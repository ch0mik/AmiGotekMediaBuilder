using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Naming;

namespace AmiGotekMediaBuilder.Core.Grouping;

public static partial class ReleaseGrouper
{
    private const double NearDuplicateRatio = 0.90;
    private static readonly IReadOnlyDictionary<string, int> Roman = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["i"] = 1, ["ii"] = 2, ["iii"] = 3, ["iv"] = 4, ["v"] = 5,
        ["vi"] = 6, ["vii"] = 7, ["viii"] = 8, ["ix"] = 9, ["x"] = 10
    };

    public static IReadOnlyList<ReleaseGroup> Group(IEnumerable<ParsedRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var input = records.ToArray();
        // A directory is a game folder only when its filenames describe one
        // title. Collection directories such as AGA or 0-9 contain many titles
        // and must never become one release.
        var collectionDirectories = input
            .Where(record => GetDirectoryGroupKey(record) is not null)
            .GroupBy(record => GetDirectoryGroupKey(record)!, StringComparer.OrdinalIgnoreCase)
            .Where(directory => directory.Select(record => NormalizeTitle(record.Title ?? string.Empty))
                .Where(title => title.Length > 0 && !IsGenericDiskTitle(title))
                .Distinct(StringComparer.Ordinal).Take(2).Count() > 1)
            .Select(directory => directory.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var groups = input
            .Select(record => (Record: record, Key: BuildGroupingKey(record, collectionDirectories)))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => SplitReleaseVariants(g.Key, g.Select(item => item.Record).ToArray()))
            .Select(partition =>
            {
                var allRecords = partition.Records;
                var first = partition.Anchor ?? allRecords.FirstOrDefault(record => record.DiskNumber == 1 &&
                    !string.IsNullOrWhiteSpace(record.Group)) ??
                    allRecords.FirstOrDefault(record => !string.IsNullOrWhiteSpace(record.Group)) ??
                    allRecords[0];
                var directoryFolder = collectionDirectories.Contains(GetDirectoryGroupKey(first) ?? string.Empty)
                    ? null : GetDirectoryFolder(first);
                var result = new ReleaseGroup
                {
                    // A subdirectory is an explicit game boundary. Its name
                    // is therefore the authoritative title when filenames
                    // inside it use generic disk labels such as "Atlantis -
                    // 01.adf" (the folder may carry the full release name).
                    ReleaseKey = partition.Key, Title = directoryFolder is null ? first.Title : CanonicalDirectoryTitle(directoryFolder), Edition = first.Edition,
                    Group = first.Group, Chipset = first.Chipset, Language = first.Language,
                    Version = first.Version, AltMarker = first.AltMarker, Extension = first.Extension,
                    SourceSha256 = first.SourceSha256, Folder = directoryFolder,
                    OutputVariant = partition.OutputVariant,
                    // Export uses canonical TOSEC names. Parsed markers retain
                    // their role in disk ordering and output filenames.
                    UseSequentialDiskNames = true,
                    IsDemoscene = allRecords.Any(r => r.IsDemoscene)
                };
                result.Records.AddRange(allRecords);
                result.Disks.AddRange(allRecords.Where(r => !r.SpecialDisk).OrderBy(r => r.DiskNumber ?? int.MaxValue));
                result.Specials.AddRange(allRecords.Where(r => r.SpecialDisk));
                result.IsComplete = IsComplete(result.Disks);
                if (partition.Ambiguous)
                    AppendReason(result, "Multiple alternative disks could not be paired safely; manual review required.");
                return result;
            }).ToList();
        DisambiguateOutputNames(groups);
        FlagNearDuplicates(groups);
        foreach (var group in groups)
        {
            var ordered = group.Disks.OrderBy(disk => disk.DiskNumber ?? int.MaxValue)
                .Concat(group.Specials.OrderBy(disk => disk.SpecialRole, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            var outputNames = ordered.Select((disk, index) =>
                ReleaseNamer.GetDiskFilename(group, disk, index, ordered.Length));
            if (outputNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ordered.Length)
                AppendReason(group, "Multiple images would receive the same export filename; manual review required.");

            var missingDisks = GetMissingDeclaredDisks(group.Disks);
            if (missingDisks.Count > 0)
            {
                var expected = group.Disks
                    .Where(disk => disk.TotalDisks.HasValue)
                    .Max(disk => disk.TotalDisks!.Value);
                AppendReason(group,
                    $"Incomplete TOSEC set: expected disks 1-{expected}; missing {FormatDiskNumbers(missingDisks)}. " +
                    "Release skipped; no partial set is exported.");
            }

            if (!group.HasMainDisk && group.Specials.Count > 0)
            {
                var roles = string.Join(", ", group.Specials.Select(s => s.SpecialRole).Where(r => r is not null).Distinct().OrderBy(r => r));
                AppendReason(group, $"Incomplete set: only special disk(s) present ({roles}), no determinable main game disk. Quarantined; not guessed into a game folder.");
            }
            else if (!group.HasMainDisk)
                AppendReason(group, "No main disk and no special disk resolved.");
        }
        return groups;
    }

    private sealed record GroupPartition(string Key, ParsedRecord[] Records,
        string? OutputVariant = null, bool Ambiguous = false, ParsedRecord? Anchor = null);

    private static IEnumerable<GroupPartition> SplitReleaseVariants(string key, ParsedRecord[] records)
    {
        // An explicit single-game folder remains authoritative. All other
        // release groups may contain alternate dumps of the same disk number.
        if (key.StartsWith("directory:", StringComparison.Ordinal) ||
            key.StartsWith("demoscene:directory:", StringComparison.Ordinal) ||
            records.Length < 2)
        {
            yield return new(key, records);
            yield break;
        }

        var mainDisks = records.Where(record => !record.SpecialDisk).ToArray();
        var duplicate = mainDisks.GroupBy(record => record.DiskNumber)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key ?? int.MaxValue)
            .FirstOrDefault();
        if (duplicate is null)
        {
            yield return new(key, records);
            yield break;
        }

        var anchors = duplicate.OrderBy(record => record.SourceFilename, StringComparer.OrdinalIgnoreCase).ToArray();
        var assigned = new HashSet<ParsedRecord>();
        foreach (var anchor in anchors)
        {
            var selected = new List<ParsedRecord> { anchor };
            var ambiguous = false;
            foreach (var diskSet in mainDisks.Except(anchors)
                         .GroupBy(record => record.DiskNumber))
            {
                if (diskSet.Count() == 1)
                {
                    selected.Add(diskSet.First());
                    continue;
                }

                var matching = diskSet.Where(record => !string.IsNullOrWhiteSpace(anchor.Group) &&
                    string.Equals(record.Group, anchor.Group, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 1)
                    selected.Add(matching[0]);
                else
                {
                    var common = diskSet.Where(record => string.IsNullOrWhiteSpace(record.Group)).ToArray();
                    if (common.Length == 1) selected.Add(common[0]);
                    else ambiguous = true;
                }
            }
            selected.AddRange(records.Where(record => record.SpecialDisk));
            foreach (var record in selected) assigned.Add(record);
            var variant = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(anchor.SourceFilename)))
                .ToLowerInvariant()[..8];
            yield return new($"{key}:variant:{variant}", selected.ToArray(), variant, ambiguous, anchor);
        }
        foreach (var unassigned in records.Where(record => !assigned.Contains(record)))
        {
            var variant = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unassigned.SourceFilename)))
                .ToLowerInvariant()[..8];
            yield return new($"{key}:unpaired:{variant}", [unassigned], variant, Ambiguous: true,
                Anchor: unassigned);
        }
    }

    private static void DisambiguateOutputNames(List<ReleaseGroup> groups)
    {
        foreach (var collision in groups.GroupBy(ReleaseNamer.GetBasename, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var group in collision)
            {
                var tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(group.ReleaseKey)))
                    .ToLowerInvariant()[..12];
                group.OutputVariant = string.IsNullOrWhiteSpace(group.OutputVariant)
                    ? tag : $"{group.OutputVariant}-{tag}";
            }
        }
    }

    private static string BuildGroupingKey(ParsedRecord record, HashSet<string> collectionDirectories)
    {
        // A relative subdirectory is an explicit collection boundary: all
        // images directly in that directory belong to one game, even when a
        // dump uses inconsistent or non-standard filenames. Alphabetic index
        // directories (A-Z) are the collection layout, not game boundaries;
        // files there continue to use TOSEC/filename identity.
        var directory = GetDirectoryGroupKey(record);
        if (directory is not null && collectionDirectories.Contains(directory))
        {
            var collectionDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))
                .ToLowerInvariant();
            // Crack tags are often present on disk 1 only. Keep its companion
            // disks together, while retaining distinct titles and versions.
            var identity = string.Join('|', NormalizeTitle(record.Title ?? string.Empty),
                NormalizeTitle(record.Edition ?? string.Empty), NormalizeTitle(record.Version ?? string.Empty),
                record.Extension.ToLowerInvariant());
            return $"collection:{collectionDigest}:{identity}";
        }
        // Preserve existing game keys for cache/catalog compatibility. Only
        // demoscene records get a namespace of their own, preventing a demo
        // in the optional intake from colliding with a game of the same name.
        if (record.IsDemoscene && directory is null)
            return $"demoscene:release:{record.ReleaseKey}";
        if (directory is null) return $"release:{record.ReleaseKey}";

        // Keep absolute source paths out of catalog keys and online provider
        // requests while retaining a collision-resistant directory identity.
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))
            .ToLowerInvariant();
        return record.IsDemoscene ? $"demoscene:directory:{digest}" : $"directory:{digest}";
    }

    private static string? GetDirectoryGroupKey(ParsedRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SourceDirectory)) return null;
        var relativeDirectory = NormalizePath(record.SourceDirectory);
        if (relativeDirectory.Length == 0 || IsAlphabeticIndexDirectory(relativeDirectory)) return null;

        // ZIP entries need the archive in the key, otherwise a common entry
        // folder name (for example "ADF") could merge unrelated archives.
        if (!string.IsNullOrWhiteSpace(record.SourcePath))
        {
            var marker = record.SourcePath.IndexOf("::", StringComparison.Ordinal);
            if (marker >= 0)
                return $"{NormalizePath(record.SourcePath[..marker])}::{relativeDirectory}";

            var parent = Path.GetDirectoryName(record.SourcePath);
            if (!string.IsNullOrWhiteSpace(parent))
                return NormalizePath(parent);
        }
        return relativeDirectory;
    }

    private static string? GetDirectoryFolder(ParsedRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.SourceDirectory)) return null;
        var normalized = NormalizePath(record.SourceDirectory).TrimEnd('/');
        if (normalized.Length == 0 || IsAlphabeticIndexDirectory(normalized)) return null;
        var separator = normalized.LastIndexOf('/');
        var folder = separator >= 0 ? normalized[(separator + 1)..] : normalized;
        return folder.Length == 0 ? null : folder;
    }

    private static bool IsAlphabeticIndexDirectory(string normalizedDirectory)
    {
        var separator = normalizedDirectory.LastIndexOf('/');
        var folder = separator >= 0 ? normalizedDirectory[(separator + 1)..] : normalizedDirectory;
        return folder.Length == 1 && folder[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim('/');

    private static bool IsGenericDiskTitle(string title) =>
        title is "boot" or "data" or "program" or "save" or "intro" or "utility" or
            "character" or "companion" ||
        (title.StartsWith("disk", StringComparison.Ordinal) && title[4..].All(char.IsDigit)) ||
        (title.StartsWith("disc", StringComparison.Ordinal) && title[4..].All(char.IsDigit));

    private static string CanonicalDirectoryTitle(string folder)
    {
        // TOSEC/crack collections often use a compact folder such as
        // "FateOfAtlantis-FFAS" while the files inside are named only
        // "Atlantis - 01.adf". Keep the raw folder for the export path, but
        // use a readable title for NFOs and online searches.
        var title = DirectoryReleaseTagRegex().Replace(folder, string.Empty);
        title = CamelCaseBoundaryRegex().Replace(title, "$1 $2");
        title = AcronymBoundaryRegex().Replace(title, "$1 $2");
        title = title.Replace('_', ' ').Replace('-', ' ');
        return string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsComplete(IReadOnlyList<ParsedRecord> disks)
    {
        if (disks.Count == 0) return false;
        return GetMissingDeclaredDisks(disks).Count == 0;
    }

    private static IReadOnlyList<int> GetMissingDeclaredDisks(IReadOnlyList<ParsedRecord> disks)
    {
        var totals = disks.Where(disk => disk.TotalDisks.HasValue)
            .Select(disk => disk.TotalDisks!.Value).ToArray();
        if (totals.Length == 0) return [];
        var expected = totals.Max();
        var have = disks.Where(disk => disk.DiskNumber.HasValue)
            .Select(disk => disk.DiskNumber!.Value).ToHashSet();
        return Enumerable.Range(1, expected).Where(number => !have.Contains(number)).ToArray();
    }

    private static string FormatDiskNumbers(IReadOnlyList<int> disks) =>
        disks.Count == 1 ? $"disk {disks[0]}" : $"disks {string.Join(", ", disks)}";

    private static void FlagNearDuplicates(List<ReleaseGroup> groups)
    {
        var titled = groups.Where(g => !string.IsNullOrWhiteSpace(g.Title))
            .Select(group => (Group: group, Normalized: NormalizeTitle(group.Title!)))
            .ToArray();
        for (var i = 0; i < titled.Length; i++)
        {
            var normalized = titled[i].Normalized;
            for (var j = i + 1; j < titled.Length; j++)
            {
                var other = titled[j].Normalized;
                if (normalized.Length == 0 || other.Length == 0 || normalized == other ||
                    Math.Abs(normalized.Length - other.Length) >
                    (1 - NearDuplicateRatio) * Math.Max(normalized.Length, other.Length) ||
                    Similarity(normalized, other) < NearDuplicateRatio) continue;
                AppendReason(titled[i].Group, $"Near-duplicate spelling of the same game: source spelling variant(s) '{titled[i].Group.Title}', '{titled[j].Group.Title}' match closely but differ. Human review required; not auto-merged.");
                AppendReason(titled[j].Group, $"Near-duplicate spelling of the same game: source spelling variant(s) '{titled[i].Group.Title}', '{titled[j].Group.Title}' match closely but differ. Human review required; not auto-merged.");
            }
        }
    }

    private static void AppendReason(ReleaseGroup group, string reason) =>
        group.QuarantineReason = group.QuarantineReason is null ? reason : $"{group.QuarantineReason} | {reason}";

    private static string NormalizeTitle(string value)
    {
        var compact = NonWordRegex().Replace(value.ToLowerInvariant(), "");
        var match = TrailingOrdinalRegex().Match(compact);
        if (!match.Success) return compact;
        var token = match.Groups[2].Value;
        if (!int.TryParse(token, out var number) && !Roman.TryGetValue(token, out number)) return compact;
        return $"{match.Groups[1].Value}{number}";
    }

    private static double Similarity(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        var distance = previous[right.Length];
        return 1.0 - (double)distance / Math.Max(left.Length, right.Length);
    }

    [GeneratedRegex(@"[\W_]+")] private static partial Regex NonWordRegex();
    [GeneratedRegex(@"^(.*?)(\d+|i{1,3}v?i{0,3}|iv|v?i|ix|x|vi{0,3})$")] private static partial Regex TrailingOrdinalRegex();
    [GeneratedRegex(@"(?:[-_ ]+)(?:[A-Z][A-Z0-9]{1,7})$")] private static partial Regex DirectoryReleaseTagRegex();
    [GeneratedRegex(@"([a-z0-9])([A-Z])")] private static partial Regex CamelCaseBoundaryRegex();
    [GeneratedRegex(@"([A-Z])([A-Z][a-z])")] private static partial Regex AcronymBoundaryRegex();
}

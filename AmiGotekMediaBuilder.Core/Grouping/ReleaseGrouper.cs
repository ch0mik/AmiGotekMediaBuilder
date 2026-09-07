using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;

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
        var groups = records
            .Select(record => (Record: record, Key: BuildGroupingKey(record)))
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First().Record;
                var directoryFolder = GetDirectoryFolder(first);
                var allRecords = g.Select(item => item.Record).ToArray();
                var result = new ReleaseGroup
                {
                    // A subdirectory is an explicit game boundary. Its name
                    // is therefore the authoritative title when filenames
                    // inside it use generic disk labels such as "Atlantis -
                    // 01.adf" (the folder may carry the full release name).
                    ReleaseKey = g.Key, Title = directoryFolder is null ? first.Title : CanonicalDirectoryTitle(directoryFolder), Edition = first.Edition,
                    Group = first.Group, Chipset = first.Chipset, Language = first.Language,
                    Version = first.Version, AltMarker = first.AltMarker, Extension = first.Extension,
                    SourceSha256 = first.SourceSha256, Folder = directoryFolder,
                    // Export always uses compact Gotek names (Game-1, Game-2,
                    // Game-Save). TOSEC markers are retained only as parsed
                    // input metadata and for ordering.
                    UseSequentialDiskNames = true,
                    IsDemoscene = allRecords.Any(r => r.IsDemoscene)
                };
                result.Records.AddRange(allRecords);
                result.Disks.AddRange(allRecords.Where(r => !r.SpecialDisk).OrderBy(r => r.DiskNumber ?? int.MaxValue));
                result.Specials.AddRange(allRecords.Where(r => r.SpecialDisk));
                result.IsComplete = IsComplete(result.Disks);
                return result;
            }).ToList();
        FlagNearDuplicates(groups);
        foreach (var group in groups)
        {
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

    private static string BuildGroupingKey(ParsedRecord record)
    {
        // A relative subdirectory is an explicit collection boundary: all
        // images directly in that directory belong to one game, even when a
        // dump uses inconsistent or non-standard filenames. Alphabetic index
        // directories (A-Z) are the collection layout, not game boundaries;
        // files there continue to use TOSEC/filename identity.
        var directory = GetDirectoryGroupKey(record);
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
        var totals = disks.Where(d => d.TotalDisks.HasValue).Select(d => d.TotalDisks!.Value).ToArray();
        if (totals.Length == 0) return true;
        var expected = totals.Max();
        var have = disks.Where(d => d.DiskNumber.HasValue).Select(d => d.DiskNumber!.Value).ToHashSet();
        return Enumerable.Range(1, expected).All(have.Contains);
    }

    private static void FlagNearDuplicates(List<ReleaseGroup> groups)
    {
        var titled = groups.Where(g => !string.IsNullOrWhiteSpace(g.Title)).ToArray();
        for (var i = 0; i < titled.Length; i++)
        {
            var normalized = NormalizeTitle(titled[i].Title!);
            for (var j = i + 1; j < titled.Length; j++)
            {
                var other = NormalizeTitle(titled[j].Title!);
                if (normalized.Length == 0 || other.Length == 0 || normalized == other ||
                    Similarity(normalized, other) < NearDuplicateRatio) continue;
                AppendReason(titled[i], $"Near-duplicate spelling of the same game: source spelling variant(s) '{titled[i].Title}', '{titled[j].Title}' match closely but differ. Human review required; not auto-merged.");
                AppendReason(titled[j], $"Near-duplicate spelling of the same game: source spelling variant(s) '{titled[i].Title}', '{titled[j].Title}' match closely but differ. Human review required; not auto-merged.");
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

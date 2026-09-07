using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Parsing;

public static partial class FilenameParser
{
    private static readonly IReadOnlyDictionary<string, string> SpecialRoles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["boot"] = "boot", ["character"] = "character", ["char"] = "character",
            ["save"] = "save", ["intro"] = "intro", ["utility"] = "utility",
            ["util"] = "utility", ["companion"] = "companion"
        };
    private static readonly IReadOnlyDictionary<char, int> LetterOrdinals =
        Enumerable.Range(0, 26).ToDictionary(i => (char)('A' + i), i => i + 1);

    public static ParsedRecord Parse(string filename)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        var sourceDirectory = GetSourceDirectory(filename);
        var name = System.IO.Path.GetFileName(filename);
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var extension = System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant();
        var record = new ParsedRecord
        {
            SourceFilename = name,
            Extension = extension,
            SourceDirectory = sourceDirectory
        };

        var parens = ParenRegex().Matches(stem).Select(m => m.Groups[1].Value).ToList();
        var brackets = BracketRegex().Matches(stem).Select(m => m.Groups[1].Value).ToList();
        parens.RemoveAll(t => DiskNumberRegex().IsMatch($"({t})"));
        var bare = ParenRegex().Replace(stem, " ");
        bare = BracketRegex().Replace(bare, " ").Trim(' ', '_', '-');

        var dashNumber = DashNumberRegex().Match(bare);
        if (dashNumber.Success)
        {
            record.DiskNumber = int.Parse(dashNumber.Groups["number"].Value);
            record.DashNumbered = true;
            record.DiskNumberWidth = dashNumber.Groups["number"].Value.Length;
            bare = bare[..dashNumber.Index].Trim(' ', '_', '-');
        }

        var letter = DiskLetterRegex().Match(stem);
        if (letter.Success && !DiskNumberRegex().IsMatch(stem))
        {
            if (LetterOrdinals.TryGetValue(char.ToUpperInvariant(letter.Groups[1].Value[0]), out var ordinal))
                record.DiskNumber = ordinal;
            bare = DiskLetterRegex().Replace(bare, "").Trim(' ', '_', '-');
        }

        ParseParentheses(record, parens);
        ParseBrackets(record, brackets);
        var numericDisk = DiskNumberRegex().Match(stem);
        if (numericDisk.Success)
        {
            record.DiskNumber = int.Parse(numericDisk.Groups[1].Value);
            record.TotalDisks = int.Parse(numericDisk.Groups[2].Value);
        }

        var tokens = TokenRegex().Split(bare).Where(t => t.Length > 0).ToList();
        var role = tokens.Select(t => (Token: t, Found: SpecialRoles.TryGetValue(t, out var value) ? value : null))
            .FirstOrDefault(x => x.Found is not null);
        if (role.Found is not null)
        {
            if (record.DiskNumber is null)
            {
                record.SpecialDisk = true;
                record.SpecialRole = role.Found;
                tokens.Remove(role.Token);
            }
        }

        var title = string.Join(' ', tokens).Trim();
        var edition = EditionRegex().Match(title);
        if (edition.Success)
        {
            record.Edition = edition.Groups[1].Value.Trim();
            title = title[..edition.Index].Trim();
        }
        var normalizedTitle = SeparatorRegex().Replace(title, " ").Trim();
        record.Title = normalizedTitle.Length > 0 ? normalizedTitle : null;
        record.ReleaseKey = BuildReleaseKey(record);
        record.GroupKey = record.ReleaseKey;
        return record;
    }

    private static void ParseParentheses(ParsedRecord record, IEnumerable<string> tags)
    {
        foreach (var raw in tags)
        {
            var tag = raw.Trim();
            if (YearRegex().IsMatch(tag)) record.Year = tag;
            else if (ChipsetRegex().IsMatch(tag)) record.Chipset = record.Chipset is null ? tag.ToUpperInvariant() : $"{record.Chipset}/{tag.ToUpperInvariant()}";
            else if (LanguageRegex().IsMatch(tag)) record.Language = tag.ToUpperInvariant();
            else if (VersionRegex().IsMatch(tag)) record.Version = tag;
            else if (MediaLabelRegex().IsMatch(tag)) record.MediaLabel = CanonicalMediaLabel(tag);
            else if (tag.Length > 0) record.Publisher = record.Publisher is null ? tag : $"{record.Publisher} / {tag}";
        }
    }

    private static string CanonicalMediaLabel(string value) => value.Trim().ToLowerInvariant() switch
    {
        "data" => "Data",
        "program" => "Program",
        "character" => "Character",
        "bonus" => "Bonus",
        "bonus disc" => "Bonus Disc",
        "side a" => "Side A",
        "side b" => "Side B",
        "disk a" or "disc a" => "Disk A",
        "disk b" or "disc b" => "Disk B",
        _ => value.Trim()
    };

    private static void ParseBrackets(ParsedRecord record, IEnumerable<string> tags)
    {
        foreach (var raw in tags)
        {
            var tag = raw.Trim();
            var lower = tag.ToLowerInvariant();
            if (lower.StartsWith("cr ")) record.Group = tag[3..].Trim();
            else if ((lower.StartsWith("p ") || lower == "p") && record.Group is null && tag.Length > 2) record.Group = tag[2..].Trim();
            else if (lower == "t" || (lower.StartsWith("t ") && tag.Length <= 12)) record.Trainer = true;
            else if (AltRegex().IsMatch(lower)) record.AltMarker = lower;
        }
    }

    private static string BuildReleaseKey(ParsedRecord r) => string.Join('|', new[] { r.Title, r.Edition, r.Group, r.Chipset, r.Language, r.Version, r.AltMarker }.Select(Normalize));
    private static string Normalize(string? value) => NonAlphaNumericRegex().Replace((value ?? "").ToLowerInvariant(), "");

    private static string? GetSourceDirectory(string filename)
    {
        // Parse both Windows and archive-style slash separators regardless of
        // the host platform. The scanner passes relative intake names here.
        var normalized = filename.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        if (separator <= 0) return null;
        var directory = normalized[..separator].Trim('/');
        return directory.Length == 0 ? null : directory;
    }

    [GeneratedRegex(@"\(([^()]*)\)")] private static partial Regex ParenRegex();
    [GeneratedRegex(@"\[([^\[\]]*)\]")] private static partial Regex BracketRegex();
    [GeneratedRegex(@"\(Disk\s+(\d+)\s+of\s+(\d+)\)", RegexOptions.IgnoreCase)] private static partial Regex DiskNumberRegex();
    [GeneratedRegex(@"\s*-\s*(?<number>\d{1,3})\s*$")] private static partial Regex DashNumberRegex();
    [GeneratedRegex(@"Disk[_ ]?([A-Za-z])\b", RegexOptions.IgnoreCase)] private static partial Regex DiskLetterRegex();
    [GeneratedRegex(@"([A-Za-z0-9']+\s+Edition)\s*$", RegexOptions.IgnoreCase)] private static partial Regex EditionRegex();
    [GeneratedRegex(@"[_\s\-]+")] private static partial Regex TokenRegex();
    [GeneratedRegex(@"[_\-]+")] private static partial Regex SeparatorRegex();
    [GeneratedRegex(@"^\d{3}[xX]$|^\d{4}$")] private static partial Regex YearRegex();
    [GeneratedRegex(@"^(AGA|CD32|CDTV|ECS|OCS|PPC|RTG|PPC[/\\-]RTG|NTSC|PAL|M\d+)$", RegexOptions.IgnoreCase)] private static partial Regex ChipsetRegex();
    [GeneratedRegex(@"^(data|program|character|bonus(?:\s+disc)?|side\s+[ab]|(?:disk|disc)\s+[ab])$", RegexOptions.IgnoreCase)] private static partial Regex MediaLabelRegex();
    [GeneratedRegex(@"^[A-Za-z]{2}$")] private static partial Regex LanguageRegex();
    [GeneratedRegex(@"^(v\d[\d.]*[a-z]?|\d+\.\d+[a-z]?)$", RegexOptions.IgnoreCase)] private static partial Regex VersionRegex();
    [GeneratedRegex(@"^a\d*$", RegexOptions.IgnoreCase)] private static partial Regex AltRegex();
    [GeneratedRegex("[^a-z0-9]")] private static partial Regex NonAlphaNumericRegex();
}

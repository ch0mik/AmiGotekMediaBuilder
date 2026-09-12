using System.Text;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Naming;

public static class ReleaseNamer
{
    public static string GetBasename(ReleaseGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var parts = new List<string> { Clean(group.Folder ?? group.Title) };
        if (!string.IsNullOrWhiteSpace(group.Folder))
            return Sanitize(parts[0]);
        if (!string.IsNullOrWhiteSpace(group.Edition)) parts.Add(group.Edition);
        if (!string.IsNullOrWhiteSpace(group.Chipset)) parts.Add(group.Chipset);
        if (!string.IsNullOrWhiteSpace(group.Group)) parts.Add($"cr {group.Group}");
        if (!string.IsNullOrWhiteSpace(group.Language)) parts.Add($"lang {group.Language}");
        if (!string.IsNullOrWhiteSpace(group.Version)) parts.Add($"ver {group.Version}");
        if (!string.IsNullOrWhiteSpace(group.AltMarker)) parts.Add($"alt {group.AltMarker}");
        return Sanitize(string.Join(' ', parts));
    }

    /// <summary>
    /// Returns the canonical TOSEC filename for one image in a release set.
    /// The GTi recognises TOSEC disk markers, so keep them in the exported
    /// library instead of flattening images to the legacy Game-1 convention.
    /// </summary>
    public static string GetDiskFilename(ReleaseGroup group, ParsedRecord disk, int ordinal, int setCount)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(disk);
        if (ordinal < 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        if (setCount < 1) throw new ArgumentOutOfRangeException(nameof(setCount));

        var basename = GetBasename(group);
        var extension = CleanExtension(disk.Extension, group.Extension);

        if (disk.SpecialDisk && !string.IsNullOrWhiteSpace(disk.SpecialRole))
        {
            basename = $"{basename} ({CanonicalSpecialRole(disk.SpecialRole)} Disk)";
        }
        else
        {
            var marker = GetDiskMarker(group, disk, ordinal, setCount);
            if (marker is not null)
                basename += $" {marker}";
        }
        return Sanitize($"{basename}.{extension}");
    }

    /// <summary>Formats a TOSEC multi-image marker, such as (Disk 2 of 2).</summary>
    public static string? GetDiskMarker(ReleaseGroup group, ParsedRecord disk, int ordinal, int setCount)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(disk);
        if (disk.SpecialDisk) return null;

        var declaredTotal = group.Records
            .Concat(group.Disks)
            .Concat(group.Specials)
            .Select(record => record.TotalDisks)
            .Where(total => total is > 1)
            .Select(total => total!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (declaredTotal <= 1 && disk.TotalDisks is > 1)
            declaredTotal = disk.TotalDisks.Value;
        if (declaredTotal <= 1 && group.Disks.Count > 1)
            declaredTotal = group.Disks.Count;
        if (declaredTotal <= 1) return null;

        var number = disk.DiskNumber.GetValueOrDefault(ordinal + 1);
        if (number < 1 || number > declaredTotal)
            number = ordinal + 1;
        if (number < 1 || number > declaredTotal) return null;

        // TOSEC uses one digit through nine disks and a two-digit field from
        // ten onward. Keep enough width for totals greater than 99 as well.
        var width = declaredTotal >= 10
            ? Math.Max(2, declaredTotal.ToString().Length)
            : 1;
        var marker = $"(Disk {number.ToString($"D{width}")} of {declaredTotal.ToString($"D{width}")})";
        if (!string.IsNullOrWhiteSpace(disk.MediaLabel))
            marker += $"({CanonicalMediaLabel(disk.MediaLabel)})";
        return marker;
    }

    private static string Clean(string? value) => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    private static string CleanExtension(string? value, string fallback)
    {
        var extension = string.IsNullOrWhiteSpace(value) ? fallback : value;
        extension = extension.Trim().TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 ? "adf" : extension;
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

    private static string CanonicalSpecialRole(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length == 0
            ? "Special"
            : char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
            builder.Append(char.IsLetterOrDigit(character) || character is ' ' or '.' or '-' or '[' or ']' or '(' or ')' ? character : '_');
        // Windows trims trailing dots from directory components, while later
        // file operations still receive the original component. Remove them
        // here so a TOSEC title such as "Gettin' Tired of..." has one stable
        // path on every platform.
        var sanitized = builder.ToString().Trim().TrimEnd('.', ' ').Replace("  ", " ");
        return sanitized.Length == 0 ? "Unknown" : sanitized;
    }
}

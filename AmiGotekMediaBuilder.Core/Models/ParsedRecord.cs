namespace AmiGotekMediaBuilder.Core.Models;

/// <summary>Metadata recovered from one disk-image filename.</summary>
public sealed class ParsedRecord
{
    public required string SourceFilename { get; init; }
    public required string Extension { get; init; }
    public string? SourcePath { get; set; }
    /// <summary>
    /// Directory portion of the relative intake name. A non-empty value
    /// normally means that the image came from a subdirectory treated as a
    /// single game container by the grouper. A final one-letter A-Z component
    /// is reserved for alphabetic collection indexes and is not a game
    /// boundary.
    /// </summary>
    public string? SourceDirectory { get; set; }
    /// <summary>Public content hash used by hash-based online providers.</summary>
    public string? SourceSha256 { get; set; }
    /// <summary>True when the image came from the dedicated demoscene intake.</summary>
    public bool IsDemoscene { get; set; }
    public string? Title { get; set; }
    public string? Year { get; set; }
    public string? Publisher { get; set; }
    public string? Chipset { get; set; }
    public string? Language { get; set; }
    public string? Version { get; set; }
    public string? Group { get; set; }
    public bool Trainer { get; set; }
    public string? AltMarker { get; set; }
    public string? Edition { get; set; }
    /// <summary>TOSEC media label that follows a disk marker, for example Data or Program.</summary>
    public string? MediaLabel { get; set; }
    public int? DiskNumber { get; set; }
    public int? TotalDisks { get; set; }
    /// <summary>True when the source uses a dash-numbered name such as "Game - 01".</summary>
    public bool DashNumbered { get; set; }
    /// <summary>Number of digits used by a dash-numbered source name.</summary>
    public int? DiskNumberWidth { get; set; }
    public bool SpecialDisk { get; set; }
    public string? SpecialRole { get; set; }
    public string GroupKey { get; set; } = "";
    public string ReleaseKey { get; set; } = "";
}

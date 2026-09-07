namespace AmiGotekMediaBuilder.Core.Models;

/// <summary>A clustered release and its ordered main/special disks.</summary>
public sealed class ReleaseGroup
{
    public required string ReleaseKey { get; init; }
    /// <summary>Stable multisystem namespace; Amiga is the current default.</summary>
    public string SystemId { get; init; } = "amiga";
    /// <summary>Content namespace such as game, demo or tool.</summary>
    public string ContentType { get; init; } = "";
    public string? Title { get; init; }
    public string? Edition { get; init; }
    public string? Group { get; init; }
    public string? Chipset { get; init; }
    public string? Language { get; init; }
    public string? Version { get; init; }
    public string? AltMarker { get; init; }
    public required string Extension { get; init; }
    /// <summary>Hash of the first disk, used only for public provider lookup.</summary>
    public string? SourceSha256 { get; init; }
    public List<ParsedRecord> Records { get; } = [];
    public List<ParsedRecord> Disks { get; } = [];
    public List<ParsedRecord> Specials { get; } = [];
    public bool IsComplete { get; set; }
    public bool HasMainDisk => Disks.Count > 0;
    public string? QuarantineReason { get; set; }
    public string? Folder { get; set; }
    /// <summary>
    /// Export uses simple sequential names (Game-1.adf, Game-2.adf,
    /// Game-Save.adf) while retaining TOSEC parsing data.
    /// </summary>
    public bool UseSequentialDiskNames { get; set; }
    /// <summary>True when all records belong to the dedicated demoscene intake.</summary>
    public bool IsDemoscene { get; set; }
}

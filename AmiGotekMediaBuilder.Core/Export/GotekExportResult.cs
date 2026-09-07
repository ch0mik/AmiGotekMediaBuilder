namespace AmiGotekMediaBuilder.Core.Export;

public sealed class GotekExportResult
{
    public required string RunId { get; init; }
    public required string StagingRoot { get; init; }
    public bool ExportGateOpen { get; init; }
    public string ExportGateReason { get; init; } = "";
    public int ReleasesExported { get; set; }
    public List<string> FilesWritten { get; } = [];
    public List<string> FilesUnchanged { get; } = [];
    public List<string> Conflicts { get; } = [];
    public List<string> SkippedQuarantined { get; } = [];
    public List<string> Errors { get; } = [];
}

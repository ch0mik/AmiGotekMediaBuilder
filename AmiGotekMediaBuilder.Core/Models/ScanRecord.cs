namespace AmiGotekMediaBuilder.Core.Models;

/// <summary>One immutable intake file record. The intake is read-only.</summary>
public sealed record ScanRecord(
    string Path,
    string Filename,
    long Size,
    string Sha256,
    DateTimeOffset ScannedAt);

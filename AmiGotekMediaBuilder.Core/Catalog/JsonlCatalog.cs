using System.Text.Json;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Catalog;

public sealed class JsonlCatalog(string catalogDirectory)
{
    private readonly string _directory = Path.GetFullPath(catalogDirectory);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public int WriteScans(IEnumerable<ScanRecord> records)
    {
        var target = Prepare("scan.jsonl");
        var existing = Read(target)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Where(x => (x.TryGetProperty("path", out _) || x.TryGetProperty("filename", out _)) &&
                        x.TryGetProperty("sha256", out _))
            .Select(ScanIdentity)
            .ToHashSet();
        var count = 0;
        using var writer = new StreamWriter(target, append: true);
        foreach (var record in records)
        {
            if (!existing.Add((record.Path, record.Sha256))) continue;
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                path = record.Path, filename = record.Filename, size = record.Size,
                sha256 = record.Sha256, scanned_at = record.ScannedAt
            }, JsonOptions));
            count++;
        }
        return count;
    }

    private static (string? Path, string? Sha256) ScanIdentity(JsonElement element)
    {
        var path = element.TryGetProperty("path", out var pathElement)
            ? pathElement.GetString()
            : element.GetProperty("filename").GetString();
        return (path, element.GetProperty("sha256").GetString());
    }

    public int WriteParses(IEnumerable<ParsedRecord> records)
    {
        var target = Prepare("parse.jsonl");
        var existing = Read(target).Select(line => JsonDocument.Parse(line).RootElement)
            .Where(x => x.TryGetProperty("source_path", out _) || x.TryGetProperty("source_filename", out _))
            .Select(ParseIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var count = 0;
        using var writer = new StreamWriter(target, append: true);
        foreach (var record in records)
        {
            var identity = record.SourcePath ??
                           $"{record.SourceDirectory}|{record.SourceFilename}";
            if (!existing.Add(identity)) continue;
            writer.WriteLine(JsonSerializer.Serialize(record, JsonOptions));
            count++;
        }
        return count;
    }

    private static string ParseIdentity(JsonElement element)
    {
        if (element.TryGetProperty("source_path", out var path) &&
            path.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(path.GetString()))
            return path.GetString()!;
        var directory = element.TryGetProperty("source_directory", out var dir) &&
                        dir.ValueKind == JsonValueKind.String
            ? dir.GetString()
            : null;
        var filename = element.TryGetProperty("source_filename", out var name)
            ? name.GetString()
            : null;
        return $"{directory}|{filename}";
    }

    public int WriteGroups(IEnumerable<ReleaseGroup> groups, string runId, DateTimeOffset? writtenAt = null)
    {
        var target = Prepare("groups.jsonl");
        var count = 0;
        using var writer = new StreamWriter(target, append: true);
        foreach (var group in groups)
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                release_key = group.ReleaseKey, system_id = group.SystemId,
                content_type = group.ContentType,
                title = group.Title, edition = group.Edition,
                group = group.Group, chipset = group.Chipset, language = group.Language,
                version = group.Version, alt_marker = group.AltMarker, ext = group.Extension,
                source_sha256 = group.SourceSha256,
                is_complete = group.IsComplete, has_main_disk = group.HasMainDisk,
                quarantine_reason = group.QuarantineReason, folder = group.Folder,
                sequential_disk_names = group.UseSequentialDiskNames,
                is_demoscene = group.IsDemoscene,
                disk_count = group.Disks.Count, special_count = group.Specials.Count,
                record_filenames = group.Records.Select(r => r.SourceFilename).ToArray(),
                run_id = runId, written_at = writtenAt ?? DateTimeOffset.UtcNow
            }, JsonOptions));
            count++;
        }
        return count;
    }

    private string Prepare(string filename)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, filename);
    }

    private static IEnumerable<string> Read(string path) =>
        File.Exists(path) ? File.ReadLines(path).Where(line => !string.IsNullOrWhiteSpace(line)) : [];
}

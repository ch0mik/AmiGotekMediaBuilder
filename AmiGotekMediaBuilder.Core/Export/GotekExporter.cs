using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Naming;

namespace AmiGotekMediaBuilder.Core.Export;

public static partial class GotekExporter
{
    /// <summary>Describes the release currently handled by the exporter.</summary>
    public sealed record ExportProgress(
        int Current,
        int Total,
        string ReleaseKey,
        string Title,
        string Phase = "export");

    public static (bool Open, string Reason) CheckGate(bool upstreamTaskClosed, int? artworkWidth, int? artworkHeight)
    {
        if (!upstreamTaskClosed)
            return (false, "Gotek export BLOCKED: upstream Gotek requirements verification is not closed.");
        if (!artworkWidth.HasValue || artworkWidth <= 0 || !artworkHeight.HasValue || artworkHeight <= 0)
            return (false, "Gotek export BLOCKED: verified artwork dimensions unresolved.");
        return (true, "export gate open");
    }

    public static GotekExportResult Export(
        IEnumerable<ReleaseGroup> groups,
        string originalDirectory,
        string stagingDirectory,
        string runId,
        bool upstreamTaskClosed,
        int? verifiedArtworkWidth,
        int? verifiedArtworkHeight,
        bool verifyOnly = false,
        string? nfoDirectory = null,
        string? artworkProcessedDirectory = null,
        string? artworkOriginalDirectory = null,
        IProgress<ExportProgress>? progress = null,
        string? rtfmDirectory = null,
        bool exportToDestinationRoot = false,
        CancellationToken cancellationToken = default)
    {
        var safeRunId = ValidateRunId(runId);
        var parent = Path.GetFullPath(stagingDirectory);
        var stagingRoot = exportToDestinationRoot ? parent : Path.GetFullPath(Path.Combine(parent, safeRunId));
        if (!exportToDestinationRoot && !IsWithin(stagingRoot, parent))
            throw new InvalidOperationException("refusing to export outside staging directory");
        var gate = CheckGate(upstreamTaskClosed, verifiedArtworkWidth, verifiedArtworkHeight);
        var result = new GotekExportResult
        {
            RunId = safeRunId, StagingRoot = stagingRoot,
            ExportGateOpen = gate.Open, ExportGateReason = gate.Reason
        };
        if (!gate.Open)
        {
            result.Errors.Add(gate.Reason);
            return result;
        }

        var releaseGroups = groups.ToArray();
        foreach (var group in releaseGroups)
        {
            var branch = string.Equals(group.Extension, "dsk", StringComparison.OrdinalIgnoreCase) ? "DSK" : "ADF";
            Directory.CreateDirectory(Path.Combine(stagingRoot, branch, GetCategory(group)));
        }
        for (var index = 0; index < releaseGroups.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = releaseGroups[index];
            progress?.Report(new ExportProgress(index + 1, releaseGroups.Length,
                group.ReleaseKey, group.Title ?? group.ReleaseKey));
            if (group.QuarantineReason is not null)
            {
                result.SkippedQuarantined.Add(group.ReleaseKey);
                continue;
            }
            var ordered = group.Disks
                .OrderBy(d => d.DiskNumber ?? int.MaxValue)
                .Concat(group.Specials.OrderBy(d => d.SpecialRole, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (ordered.Length == 0) continue;
            var basename = ReleaseNamer.GetBasename(group);
            var branch = string.Equals(group.Extension, "dsk", StringComparison.OrdinalIgnoreCase) ? "DSK" : "ADF";
            var folder = Path.Combine(stagingRoot, branch, GetCategory(group), basename);
            for (var i = 0; i < ordered.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diskName = ReleaseNamer.GetDiskFilename(group, ordered[i], i, ordered.Length);
                var source = ordered[i].SourcePath ?? Path.Combine(originalDirectory, ordered[i].SourceFilename);
                var destination = Path.Combine(folder, diskName);
                Copy(source, destination, verifyOnly, result, cancellationToken);
            }
            var first = group.Records.FirstOrDefault();
            var generatedNfo = GotekNfoRenderer.Render(
                group.Title, first?.Year, first?.Publisher, group.Group);
            var nfoPath = nfoDirectory is null ? null :
                Path.Combine(nfoDirectory, $"{basename}.nfo");
            var nfo = nfoPath is not null && File.Exists(nfoPath)
                ? File.ReadAllText(nfoPath)
                : generatedNfo;
            WriteBytes(Encoding.UTF8.GetBytes(nfo), Path.Combine(folder, $"{basename}.nfo"), verifyOnly, result);

            var rtfmPath = rtfmDirectory is null ? null : Path.Combine(rtfmDirectory, $"{basename}.rtfm");
            if (rtfmPath is not null && File.Exists(rtfmPath))
                WriteBytes(File.ReadAllBytes(rtfmPath), Path.Combine(folder, $"{basename}.rtfm"), verifyOnly, result);

            // Prefer acquired artwork, but write the embedded diskette only
            // into the export folder when none exists. Never materialize this
            // fallback in the artwork cache: a later run must retry scraping.
            var artwork = group.IsDemoscene ? null :
                FindArtwork(basename, artworkProcessedDirectory, artworkOriginalDirectory) ??
                FindArtwork(basename, artworkOriginalDirectory, artworkOriginalDirectory);
            if (artwork is not null)
                WriteBytes(File.ReadAllBytes(artwork), Path.Combine(folder, Path.GetFileName(artwork)), verifyOnly, result);
            else
                WriteBytes(DefaultArtworkService.GetExportFallbackBytes(), Path.Combine(folder, $"{basename}.jpg"), verifyOnly, result);
            result.ReleasesExported++;
        }
        return result;
    }

    private static string GetCategory(ReleaseGroup group) => group.IsDemoscene ? "Demoscene" : "Games";

    private static void Copy(string source, string destination, bool verifyOnly, GotekExportResult result,
        CancellationToken cancellationToken)
    {
        if (!TryReadSource(source, out var bytes, cancellationToken))
        {
            result.Conflicts.Add($"source missing for {Path.GetFileName(source)}");
            return;
        }
        WriteBytes(bytes, destination, verifyOnly, result);
    }

    private static bool TryReadSource(string source, out byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var marker = source.IndexOf("::", StringComparison.Ordinal);
        if (marker < 0)
        {
            if (!File.Exists(source)) { bytes = []; return false; }
            bytes = File.ReadAllBytes(source);
            return true;
        }
        var archive = source[..marker];
        var entryName = source[(marker + 2)..];
        if (!File.Exists(archive)) { bytes = []; return false; }
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.GetEntry(entryName);
        if (entry is null) { bytes = []; return false; }
        using var stream = entry.Open();
        using var output = new MemoryStream();
        stream.CopyTo(output);
        cancellationToken.ThrowIfCancellationRequested();
        bytes = output.ToArray();
        return true;
    }

    private static string? FindArtwork(string basename, string? directory, string? provenanceDirectory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return null;
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
        {
            var candidate = Path.Combine(directory, basename + extension);
            if (File.Exists(candidate) && !IsDemosceneArtwork(candidate, basename, provenanceDirectory))
                return candidate;
        }
        return null;
    }

    private static bool HasArtworkCandidate(string basename, string? processedDirectory, string? originalDirectory)
    {
        foreach (var directory in new[] { processedDirectory, originalDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
            {
                var candidate = Path.Combine(directory, basename + extension);
                if (File.Exists(candidate) && !IsDemosceneArtwork(candidate, basename, originalDirectory))
                    return true;
            }
        }
        return false;
    }

    private static bool IsDemosceneArtwork(string candidate, string basename, string? provenanceDirectory)
    {
        // DemosceneArtworkService stores provenance beside the original image;
        // the processed copy therefore needs the original directory checked as
        // well. This also protects exports from stale artwork generated by an
        // older build where Pouët was in the default provider chain.
        var sidecars = new List<string> { candidate + ".source.json" };
        if (!string.IsNullOrWhiteSpace(provenanceDirectory) && Directory.Exists(provenanceDirectory))
        {
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
                sidecars.Add(Path.Combine(provenanceDirectory, basename + extension + ".source.json"));
        }

        foreach (var sidecar in sidecars.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(sidecar)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(sidecar));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (HasDemosceneValue(root, "catalog") || HasDemosceneValue(root, "provider"))
                    return true;
            }
            catch (JsonException)
            {
                // A damaged sidecar must not make a valid image disappear.
            }
            catch (IOException)
            {
                // Treat transient/unreadable provenance as unknown.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat inaccessible provenance as unknown.
            }
        }
        return false;
    }

    private static bool HasDemosceneValue(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString();
        return string.Equals(text, "pouet", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(text, "demozoo", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteBytes(byte[] bytes, string destination, bool verifyOnly, GotekExportResult result)
    {
        if (File.Exists(destination))
        {
            if (File.ReadAllBytes(destination).AsSpan().SequenceEqual(bytes))
                result.FilesUnchanged.Add(destination);
            else
                result.Conflicts.Add(destination);
            return;
        }
        if (verifyOnly) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, bytes);
        result.FilesWritten.Add(destination);
    }

    private static string ValidateRunId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !RunIdRegex().IsMatch(value) || value is "." or "..")
            throw new ArgumentException("runId must be one safe path component", nameof(value));
        return value;
    }

    private static bool IsWithin(string candidate, string parent) =>
        candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")] private static partial Regex RunIdRegex();
}

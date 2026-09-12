using System.Security.Cryptography;
using System.Text.Json;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Demoscene;

public enum DemosceneArtworkStatus { Downloaded, AlreadyPresent, Skipped, Failed }

public sealed record DemosceneArtworkResult(
    string PouetId,
    string Title,
    DemosceneArtworkStatus Status,
    string? OriginalPath,
    string? ProcessedPath,
    string? Error);

/// <summary>Downloads and caches demoscene thumbnails without modifying the disk-image intake.</summary>
public sealed class DemosceneArtworkService : IDisposable
{
    private const long MaxArtworkBytes = 12 * 1024 * 1024;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public DemosceneArtworkService(SafeHttpClient? client = null)
    {
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public async Task<IReadOnlyList<DemosceneArtworkResult>> DownloadAsync(
        IEnumerable<DemosceneProduction> productions,
        string originalDirectory,
        string processedDirectory,
        CancellationToken cancellationToken = default,
        IProgress<DemosceneArtworkResult>? progress = null,
        int requestDelayMilliseconds = 0)
    {
        ArgumentNullException.ThrowIfNull(productions);
        Directory.CreateDirectory(originalDirectory);
        Directory.CreateDirectory(processedDirectory);
        var results = new List<DemosceneArtworkResult>();
        requestDelayMilliseconds = Math.Clamp(requestDelayMilliseconds, 0, 60_000);
        foreach (var production in productions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DownloadOneAsync(production, originalDirectory, processedDirectory, cancellationToken);
            results.Add(result);
            progress?.Report(result);
            if (requestDelayMilliseconds > 0)
                await Task.Delay(requestDelayMilliseconds, cancellationToken);
        }
        return results;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private async Task<DemosceneArtworkResult> DownloadOneAsync(
        DemosceneProduction production,
        string originalDirectory,
        string processedDirectory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(production.ArtworkUrl))
            return new(production.PouetId, production.Title, DemosceneArtworkStatus.Skipped, null, null, "no artwork URL");
        byte[] bytes;
        try
        {
            bytes = await _client.GetBytesFollowingRedirectsAsync(production.ArtworkUrl, MaxArtworkBytes, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new(production.PouetId, production.Title, DemosceneArtworkStatus.Failed, null, null, ex.Message);
        }
        var extension = DetectExtension(bytes, production.ArtworkUrl);
        if (extension is null)
            return new(production.PouetId, production.Title, DemosceneArtworkStatus.Skipped, null, null, "unsupported image format");
        var prefix = production.Catalog.Equals(DemosceneCatalogs.Pouet, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : SafeSlug(production.Catalog) + "-";
        var basename = $"{prefix}{production.PouetId}-{SafeSlug(production.Title)}";
        var originalPath = Path.Combine(originalDirectory, basename + extension);
        var processedPath = Path.Combine(processedDirectory, basename + extension);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (File.Exists(originalPath) && File.Exists(processedPath))
        {
            try
            {
                var existingHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(originalPath, cancellationToken))).ToLowerInvariant();
                if (existingHash.Equals(hash, StringComparison.OrdinalIgnoreCase))
                    return new(production.PouetId, production.Title, DemosceneArtworkStatus.AlreadyPresent, originalPath, processedPath, null);
            }
            catch (IOException) { }
        }
        try
        {
            await WriteAtomicAsync(originalPath, bytes, cancellationToken);
            await WriteAtomicAsync(processedPath, bytes, cancellationToken);
            var provenance = new
            {
                source = production.SourceUrl,
                catalog = production.Catalog,
                source_id = production.PouetId,
                linked_pouet_id = production.LinkedPouetId,
                pouet_id = production.PouetId,
                title = production.Title,
                artwork_url = production.ArtworkUrl,
                retrieved_at = DateTimeOffset.UtcNow,
                sha256 = hash,
                bytes = bytes.LongLength,
                original_path = originalPath,
                processed_path = processedPath
            };
            var provenanceJson = JsonSerializer.Serialize(provenance,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(originalPath + ".source.json", provenanceJson, cancellationToken);
            // Keep provenance beside both copies. The processed thumbnail is
            // normally consumed by the demoscene UI, but this marker also
            // prevents accidental reuse if a caller scans that directory as
            // an artwork source in the future.
            await File.WriteAllTextAsync(processedPath + ".source.json", provenanceJson, cancellationToken);
            return new(production.PouetId, production.Title, DemosceneArtworkStatus.Downloaded, originalPath, processedPath, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new(production.PouetId, production.Title, DemosceneArtworkStatus.Failed, null, null, ex.Message);
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static string? DetectExtension(byte[] bytes, string url)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47) return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff) return ".jpg";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
        var extension = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        return extension is ".jpg" or ".jpeg" ? ".jpg" : extension is ".png" ? ".png" : extension is ".webp" ? ".webp" : null;
    }

    private static string SafeSlug(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var slug = new string(chars).Trim('.', ' ');
        return slug.Length > 100 ? slug[..100].TrimEnd(' ', '.') : slug;
    }
}

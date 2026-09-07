using System.Security.Cryptography;
using System.Text.Json;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Naming;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

public sealed record ArtworkArtifact(
    string OriginalPath,
    string ProcessedPath,
    bool Downloaded,
    string Provider,
    string Url);

/// <summary>
/// Downloads provider artwork into the managed asset cache and creates a
/// deterministic Gotek-friendly copy. The original bytes are preserved. Image
/// conversion/resizing is deliberately not required: Gotek accepts the
/// provider's JPG/PNG/WEBP media and this keeps the Core project dependency-free.
/// </summary>
public sealed class ArtworkDownloader(SafeHttpClient? client = null) : IDisposable
{
    private const int MaxDownloadBytes = 12_000_000;
    private readonly SafeHttpClient _client = client ?? new SafeHttpClient();
    private readonly bool _ownsClient = client is null;

    public async Task<ArtworkArtifact?> DownloadAsync(
        MetadataRecord metadata,
        ReleaseGroup group,
        string originalDirectory,
        string processedDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(metadata.ArtworkUrl) &&
            string.IsNullOrWhiteSpace(metadata.ArtworkPath)) return null;
        // Demoscene thumbnails have their own cache and provenance model. A
        // Pouët/Demozoo record must never be persisted by this game-artwork
        // downloader, even if a caller accidentally passes it here.
        if (IsDemosceneProvider(metadata.ArtworkProvider) ||
            IsDemosceneProvider(metadata.Provider)) return null;

        var basename = ReleaseNamer.GetBasename(group);
        Directory.CreateDirectory(originalDirectory);
        Directory.CreateDirectory(processedDirectory);

        var existing = FindExistingMaster(originalDirectory, basename);
        byte[] bytes;
        string extension;
        var downloaded = false;
        if (existing is not null)
        {
            bytes = await File.ReadAllBytesAsync(existing, cancellationToken);
            try
            {
                // Do not keep reusing an HTML challenge saved by an older
                // build under an image extension.
                extension = DetectExtension(bytes, existing);
            }
            catch (InvalidDataException)
            {
                existing = null;
                bytes = [];
                extension = string.Empty;
            }
        }
        else
        {
            bytes = [];
            extension = string.Empty;
        }

        var localArtworkPath = string.IsNullOrWhiteSpace(metadata.ArtworkPath)
            ? null
            : Path.GetFullPath(metadata.ArtworkPath);
        var usableLocalArtwork = localArtworkPath is not null &&
                                 File.Exists(localArtworkPath) &&
                                 !IsDemosceneArtwork(localArtworkPath);
        if (existing is null && usableLocalArtwork)
        {
            bytes = await File.ReadAllBytesAsync(localArtworkPath!, cancellationToken);
            if (bytes.Length == 0) throw new InvalidOperationException("local artwork file is empty");
            extension = DetectExtension(bytes, localArtworkPath!);
            existing = Path.Combine(originalDirectory, basename + extension);
            AtomicWrite(existing, bytes);
            downloaded = true;
        }
        else if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(metadata.ArtworkUrl)) return null;
            // Public image CDNs commonly redirect to a versioned asset or a
            // different host. Follow only validated, bounded redirects.
            bytes = await _client.GetBytesFollowingRedirectsAsync(
                metadata.ArtworkUrl!, MaxDownloadBytes, cancellationToken);
            if (bytes.Length == 0) throw new InvalidOperationException("artwork download returned an empty body");
            extension = DetectExtension(bytes, metadata.ArtworkUrl!);
            existing = Path.Combine(originalDirectory, basename + extension);
            AtomicWrite(existing, bytes);
            downloaded = true;
        }

        var processedExtension = extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : extension;
        var processedPath = Path.Combine(processedDirectory, basename + processedExtension);
        var processedBytes = bytes;
        if (!File.Exists(processedPath) || !File.ReadAllBytes(processedPath).AsSpan().SequenceEqual(processedBytes))
            AtomicWrite(processedPath, processedBytes);

        var sidecar = existing + ".source.json";
        var provenance = new
        {
            image_url = metadata.ArtworkUrl,
            image_path = metadata.ArtworkPath,
            source_page = metadata.ArtworkSourceUrl,
            provider = metadata.ArtworkProvider ?? metadata.Provider,
            retrieved_at = DateTimeOffset.UtcNow,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes = bytes.Length,
            processed_path = processedPath
        };
        AtomicWrite(sidecar, JsonSerializer.SerializeToUtf8Bytes(provenance, new JsonSerializerOptions { WriteIndented = true }));
        return new ArtworkArtifact(existing, processedPath, downloaded,
            metadata.ArtworkProvider ?? metadata.Provider,
            metadata.ArtworkUrl ?? metadata.ArtworkPath ?? string.Empty);
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static string? FindExistingMaster(string directory, string basename)
    {
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
        {
            var candidate = Path.Combine(directory, basename + extension);
            if (File.Exists(candidate) && !IsDemosceneArtwork(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsDemosceneArtwork(string imagePath)
    {
        var sidecar = imagePath + ".source.json";
        if (!File.Exists(sidecar)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(sidecar));
            var root = document.RootElement;
            return HasDemosceneValue(root, "provider") || HasDemosceneValue(root, "catalog");
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool HasDemosceneValue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        (text.Equals("pouet", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("demozoo", StringComparison.OrdinalIgnoreCase));

    private static string DetectExtension(byte[] bytes, string url)
    {
        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return ".png";
        if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xd8) return ".jpg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8.ToArray()) && bytes[8..12].SequenceEqual("WEBP"u8.ToArray())) return ".webp";
        if (bytes.Length >= 6 &&
            (bytes[..6].SequenceEqual("GIF87a"u8.ToArray()) || bytes[..6].SequenceEqual("GIF89a"u8.ToArray()))) return ".gif";
        throw new InvalidDataException("artwork response is not a recognized JPG, PNG, WEBP or GIF image");
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsDemosceneProvider(string? provider) =>
        string.Equals(provider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, "demozoo", StringComparison.OrdinalIgnoreCase);
}

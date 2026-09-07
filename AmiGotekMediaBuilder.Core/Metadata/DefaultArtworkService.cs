using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Naming;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Supplies a local game-thumbnail when all metadata providers return no
/// artwork. The image is embedded in the Core assembly, so offline builds and
/// machines without network access still produce a visible companion image.
/// Demoscene groups are intentionally excluded; they have a separate artwork
/// cache and provenance model.
/// </summary>
public static class DefaultArtworkService
{
    public const string ProviderId = "default-artwork";
    public const string ResourceName = "default-game-artwork.jpg";
    private static readonly Lazy<byte[]> EmbeddedBytes = new(ReadResource);

    public static ArtworkArtifact? Ensure(
        ReleaseGroup group,
        string originalDirectory,
        string processedDirectory)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.IsDemoscene) return null;
        if (string.IsNullOrWhiteSpace(originalDirectory) || string.IsNullOrWhiteSpace(processedDirectory))
            return null;

        var basename = ReleaseNamer.GetBasename(group);
        Directory.CreateDirectory(originalDirectory);
        Directory.CreateDirectory(processedDirectory);
        var originalPath = Path.Combine(originalDirectory, basename + ".jpg");
        var processedPath = Path.Combine(processedDirectory, basename + ".jpg");
        var bytes = EmbeddedBytes.Value;
        AtomicWriteIfDifferent(originalPath, bytes);
        AtomicWriteIfDifferent(processedPath, bytes);

        var sidecar = originalPath + ".source.json";
        var provenance = new
        {
            image_url = (string?)null,
            image_path = (string?)null,
            source_page = (string?)null,
            provider = ProviderId,
            retrieved_at = DateTimeOffset.UtcNow,
            sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes = bytes.Length,
            processed_path = processedPath
        };
        AtomicWriteIfDifferent(sidecar,
            JsonSerializer.SerializeToUtf8Bytes(provenance,
                new JsonSerializerOptions { WriteIndented = true }));
        return new ArtworkArtifact(originalPath, processedPath, false, ProviderId,
            "embedded://" + ResourceName);
    }

    private static byte[] ReadResource()
    {
        var assembly = typeof(DefaultArtworkService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("." + ResourceName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            throw new InvalidOperationException($"Embedded artwork resource '{ResourceName}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded artwork resource '{resourceName}' could not be opened.");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static void AtomicWriteIfDifferent(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            try
            {
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }
}

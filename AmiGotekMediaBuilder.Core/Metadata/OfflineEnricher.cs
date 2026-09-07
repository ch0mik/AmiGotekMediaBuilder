using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Catalog;
using AmiGotekMediaBuilder.Core.Models;
using System.Text.Json;

namespace AmiGotekMediaBuilder.Core.Metadata;

public sealed class OfflineEnricher(IMetadataProvider? provider = null)
{
    private readonly IMetadataProvider _provider = provider ?? new FilenameMetadataProvider();
    private readonly FilenameMetadataProvider _filenameFallback = new();
    public int ArtworkFallbackUsed { get; private set; }

    public IReadOnlyList<MetadataRecord> Enrich(
        IEnumerable<ReleaseGroup> groups,
        string cacheDirectory,
        string? nfoDirectory = null,
        string? catalogDatabasePath = null)
    {
        var cache = new MetadataCache(cacheDirectory);
        var catalog = string.IsNullOrWhiteSpace(catalogDatabasePath)
            ? null
            : new SqliteCatalogStore(catalogDatabasePath);
        var results = new List<MetadataRecord>();
        var artworkOriginalDirectory = nfoDirectory is null
            ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nfoDirectory))!, "artwork-original");
        var artworkProcessedDirectory = nfoDirectory is null
            ? null
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nfoDirectory))!, "artwork-processed");
        foreach (var group in groups)
        {
            var cached = catalog?.ReadMetadata(group, CatalogNamespace.For(group));
            var legacy = cache.Read(group.ReleaseKey);
            if (catalog is not null && legacy is not null)
                catalog.WriteMetadata(group, legacy, CatalogNamespace.For(group), includeHash: false);
            var legacyAllowed = catalog is null || !HasSourceHash(group);
            var record = cached ?? (legacyAllowed ? legacy : null) ?? _provider.Resolve(group);
            if (record is null) continue;
            if (!group.IsDemoscene && IsDemosceneRecord(record))
                record = _filenameFallback.Resolve(group) ?? record;
            if (group.IsDemoscene || IsDemosceneRecord(record))
                record = record with { ArtworkUrl = null, ArtworkPath = null, ArtworkSourceUrl = null, ArtworkProvider = null };
            if (!group.IsDemoscene && group.Folder is not null && IsLegacyDirectoryTitle(record))
                record = record with { Title = group.Title ?? group.Folder };

            ArtworkArtifact? defaultArtwork = null;
            if (!group.IsDemoscene && artworkOriginalDirectory is not null && artworkProcessedDirectory is not null &&
                !HasManagedArtwork(group, record, artworkOriginalDirectory, artworkProcessedDirectory))
            {
                defaultArtwork = DefaultArtworkService.Ensure(
                    group, artworkOriginalDirectory, artworkProcessedDirectory);
                if (defaultArtwork is not null)
                {
                    ArtworkFallbackUsed++;
                    record = record with
                    {
                        ArtworkPath = defaultArtwork.OriginalPath,
                        ArtworkProvider = DefaultArtworkService.ProviderId,
                        ArtworkSourceUrl = defaultArtwork.Url
                    };
                }
            }
            cache.Write(record);
            catalog?.WriteMetadata(group, record, CatalogNamespace.For(group));
            if (defaultArtwork is not null)
                catalog?.WriteArtwork(group, defaultArtwork, CatalogNamespace.For(group));
            results.Add(record);
            if (nfoDirectory is not null)
            {
                Directory.CreateDirectory(nfoDirectory);
                var basename = Naming.ReleaseNamer.GetBasename(group);
                var nfo = GotekNfoRenderer.Render(record.Title, record.Year, record.Publisher, record.Description);
                File.WriteAllText(Path.Combine(nfoDirectory, $"{basename}.nfo"), nfo);
            }
        }
        return results;
    }

    private static bool IsDemosceneRecord(MetadataRecord record) =>
        string.Equals(record.ArtworkProvider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.ArtworkProvider, "demozoo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "demozoo", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyDirectoryTitle(MetadataRecord record) =>
        string.Equals(record.Provider, "offline-filename", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "demozoo", StringComparison.OrdinalIgnoreCase);

    private static bool HasManagedArtwork(
        ReleaseGroup group,
        MetadataRecord record,
        string originalDirectory,
        string processedDirectory)
    {
        var basename = Naming.ReleaseNamer.GetBasename(group);
        if (!string.IsNullOrWhiteSpace(record.ArtworkPath) && File.Exists(record.ArtworkPath) &&
            !IsDemosceneArtworkPath(record.ArtworkPath, originalDirectory, basename))
            return true;
        foreach (var directory in new[] { originalDirectory, processedDirectory })
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
            {
                var candidate = Path.Combine(directory, basename + extension);
                if (File.Exists(candidate) &&
                    !IsDemosceneArtworkPath(candidate, originalDirectory, basename)) return true;
            }
        return false;
    }

    private static bool IsDemosceneArtworkPath(
        string path, string? provenanceDirectory = null, string? basename = null)
    {
        var sidecars = new List<string> { path + ".source.json" };
        // Processed artwork is a copy and therefore has no sidecar of its
        // own. Check the matching original sidecar before treating it as a
        // valid game artwork candidate.
        if (!string.IsNullOrWhiteSpace(provenanceDirectory) &&
            !string.IsNullOrWhiteSpace(basename))
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
                if (HasDemosceneValue(root, "provider") || HasDemosceneValue(root, "catalog"))
                    return true;
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool HasDemosceneValue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        (text.Equals("pouet", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("demozoo", StringComparison.OrdinalIgnoreCase));

    private static bool HasSourceHash(ReleaseGroup group) =>
        group.SourceSha256 is { Length: > 0 } ||
        group.Records.Any(record => record.SourceSha256 is { Length: > 0 });
}

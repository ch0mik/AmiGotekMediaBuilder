using AmiGotekMediaBuilder.Core.Catalog;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>Shared endpoint options retained for the separate demoscene provider.</summary>
public sealed record OnlineProviderOptions(
    string Id,
    string BaseUrl,
    string SearchPath = "/search?title={title}&platform=amiga",
    int MaxResponseBytes = 1_000_000);

public interface IAsyncMetadataProvider
{
    string Id { get; }
    Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default);
}

/// <summary>Describes the release currently being resolved by online enrichment.</summary>
public sealed record OnlineEnrichmentProgress(
    int Current,
    int Total,
    string ReleaseKey,
    string Title,
    string Phase,
    string? Provider = null,
    string? Error = null);

public sealed class OnlineMetadataChain(IEnumerable<IAsyncMetadataProvider> providers)
{
    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group,
        CancellationToken cancellationToken = default,
        Action<string>? providerStarted = null,
        Action<string, string>? providerFailed = null,
        Action<string, MetadataRecord?>? providerCompleted = null)
    {
        var results = await ResolveAllAsync(group, cancellationToken, providerStarted,
            providerFailed, providerCompleted);
        return Merge(results);
    }

    public async Task<IReadOnlyList<MetadataRecord>> ResolveAllAsync(
        ReleaseGroup group,
        CancellationToken cancellationToken = default,
        Action<string>? providerStarted = null,
        Action<string, string>? providerFailed = null,
        Action<string, MetadataRecord?>? providerCompleted = null)
    {
        var matches = new List<MetadataRecord>();
        foreach (var provider in providers)
        {
            providerStarted?.Invoke(provider.Id);
            try
            {
                var result = await provider.ResolveAsync(group, cancellationToken);
                providerCompleted?.Invoke(provider.Id, result);
                if (result is null) continue;
                matches.Add(result);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                providerFailed?.Invoke(provider.Id, ex.GetBaseException().Message);
            }
        }
        return matches;
    }

    internal static MetadataRecord? Merge(IEnumerable<MetadataRecord> records)
    {
        MetadataRecord? merged = null;
        foreach (var candidate in records)
        {
            if (merged is null) { merged = candidate; continue; }
            merged = merged with
            {
                Year = merged.Year ?? candidate.Year,
                Publisher = merged.Publisher ?? candidate.Publisher,
                Description = merged.Description ?? candidate.Description,
                ArtworkUrl = merged.ArtworkUrl ?? candidate.ArtworkUrl,
                ArtworkPath = merged.ArtworkPath ?? candidate.ArtworkPath,
                ArtworkSourceUrl = merged.ArtworkSourceUrl ?? candidate.ArtworkSourceUrl,
                ArtworkProvider = merged.ArtworkProvider ?? candidate.ArtworkProvider
            };
        }
        return merged;
    }
}

public sealed class HybridMetadataEnricher(
    IEnumerable<IAsyncMetadataProvider> providers,
    bool allowDemosceneArtwork = false)
{
    private readonly IReadOnlyList<IAsyncMetadataProvider> _providers = providers.ToArray();
    private readonly bool _allowDemosceneArtwork = allowDemosceneArtwork;
    public int ArtworkDownloaded { get; private set; }
    public int ArtworkFallbackUsed { get; private set; }
    public int ArtworkFailed { get; private set; }
    public int CatalogCacheHits { get; private set; }
    public int CatalogCacheMisses { get; private set; }

    public async Task<IReadOnlyList<MetadataRecord>> EnrichAsync(
        IEnumerable<ReleaseGroup> groups, string cacheDirectory, string nfoDirectory,
        CancellationToken cancellationToken = default,
        IProgress<OnlineEnrichmentProgress>? progress = null,
        string? catalogDatabasePath = null)
    {
        var cache = new MetadataCache(cacheDirectory);
        var catalog = string.IsNullOrWhiteSpace(catalogDatabasePath)
            ? null
            : new SqliteCatalogStore(catalogDatabasePath);
        var fallback = new FilenameMetadataProvider();
        var chain = new OnlineMetadataChain(_providers);
        var output = new List<MetadataRecord>();
        using var artwork = new ArtworkDownloader();
        var artworkOriginalDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nfoDirectory))!, "artwork-original");
        var artworkProcessedDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(nfoDirectory))!, "artwork-processed");
        var releaseGroups = groups.ToArray();
        for (var index = 0; index < releaseGroups.Length; index++)
        {
            var group = releaseGroups[index];
            var current = index + 1;
            var title = group.Title ?? group.ReleaseKey;
            progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title, "metadata"));
            var cached = catalog?.ReadMetadata(group, CatalogNamespace.For(group), includeOffline: false);
            if (cached is not null)
                CatalogCacheHits++;
            else
                CatalogCacheMisses++;
            var legacy = cache.Read(group.ReleaseKey);
            if (catalog is not null && legacy is not null)
                // Import legacy JSON without binding it to a new hash. This
                // keeps migration safe when a filename is reused for another
                // image while still preserving the old release-key record.
                catalog.WriteMetadata(group, legacy, CatalogNamespace.For(group), includeHash: false);
            var legacyAllowed = catalog is null || !HasSourceHash(group);
            var cachedCandidate = cached ??
                (legacyAllowed && legacy is not null && !IsOfflineRecord(legacy) ? legacy : null);
            // A successful provider record is a completed lookup even when no
            // artwork was found. Keep it in the persistent catalog and do not
            // scrape the same game again on every build. Offline filename
            // fallbacks are excluded by ReadMetadata(includeOffline: false),
            // so games that have never matched a provider can still be retried.
            var staleDemosceneCache = !group.IsDemoscene && cachedCandidate is not null &&
                                      IsDemosceneArtwork(cachedCandidate);
            var staleRemovedProviderCache = !group.IsDemoscene && cachedCandidate is not null &&
                                            IsRemovedGameProvider(cachedCandidate);
            var staleProviderCache = staleDemosceneCache || staleRemovedProviderCache;
            var cachedUsable = cachedCandidate is not null && !staleProviderCache &&
                               HasUsableCachedArtworkReference(cachedCandidate);
            MetadataRecord? record = cachedUsable ? cachedCandidate : null;
            IReadOnlyList<MetadataRecord> providerRecords = [];
            if (record is null)
            {
                var providerOutcomes = new List<string>();
                providerRecords = await chain.ResolveAllAsync(group, cancellationToken,
                    provider => progress?.Report(new(current, releaseGroups.Length,
                        group.ReleaseKey, title, "metadata", provider)),
                    (provider, error) => providerOutcomes.Add($"{provider}: error ({ShortProviderError(error)})"),
                    (provider, result) => providerOutcomes.Add(result is null
                        ? $"{provider}: no match"
                        : HasArtwork(result) ? $"{provider}: match + artwork" : $"{provider}: match, no artwork"));
                var refreshed = OnlineMetadataChain.Merge(providerRecords);
                if (providerOutcomes.Count > 0)
                    progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                        "provider-summary", Error: string.Join("; ", providerOutcomes)));
                if (!staleProviderCache && cachedCandidate is not null && refreshed is not null)
                {
                    record = OnlineMetadataChain.Merge([cachedCandidate, refreshed]);
                }
                else if (staleProviderCache)
                {
                    // A previous version allowed Pouët/Demozoo records into
                    // the game cache. Never carry that title/description into
                    // the game pipeline; use a clean online result or the
                    // filename fallback instead.
                    record = refreshed ?? fallback.Resolve(group);
                }
                else
                    record = cachedCandidate ?? refreshed ?? fallback.Resolve(group);
            }
            if (record is null) continue;
            // Older caches may contain a complete Pouët record from when that
            // provider was part of the default game chain. Do not carry its
            // demoscene description/title into a game NFO; rebuild a clean
            // filename fallback instead.
            if (!group.IsDemoscene && IsDemosceneArtwork(record))
                record = fallback.Resolve(group) ?? record;
            if (group.IsDemoscene || (!_allowDemosceneArtwork && IsDemosceneArtwork(record)))
                record = record with { ArtworkUrl = null, ArtworkPath = null, ArtworkSourceUrl = null, ArtworkProvider = null };
            if (!group.IsDemoscene && group.Folder is not null && IsLegacyDirectoryTitle(record))
                record = record with { Title = group.Title ?? group.Folder };

            // Online enrichment owns artwork acquisition. Offline mode never
            // enters this class, so it cannot accidentally make network calls.
            ArtworkArtifact? artifact = null;
            if (!group.IsDemoscene)
            {
                var candidates = new List<MetadataRecord>();
                if (cachedCandidate is not null && !staleProviderCache && HasArtwork(cachedCandidate))
                    candidates.Add(cachedCandidate);
                candidates.AddRange(providerRecords.Where(HasArtwork));
                if (candidates.Count == 0 && HasArtwork(record)) candidates.Add(record);

                foreach (var candidate in candidates
                             .DistinctBy(item => item.ArtworkUrl ?? item.ArtworkPath,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                            "artwork", candidate.ArtworkProvider ?? candidate.Provider));
                        artifact = await artwork.DownloadAsync(
                            candidate, group, artworkOriginalDirectory,
                            artworkProcessedDirectory, cancellationToken);
                        if (artifact is null) continue;
                        ArtworkDownloaded++;
                        record = record with
                        {
                            ArtworkUrl = candidate.ArtworkUrl,
                            ArtworkPath = artifact.ProcessedPath,
                            ArtworkSourceUrl = candidate.ArtworkSourceUrl,
                            ArtworkProvider = candidate.ArtworkProvider ?? candidate.Provider
                        };
                        break;
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        ArtworkFailed++;
                        progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                            "artwork-error", candidate.ArtworkProvider ?? candidate.Provider,
                            ex.GetBaseException().Message));
                    }
                }

                // Do not create an artwork-cache fallback when providers miss.
                // The exporter writes its diskette image only into output so
                // subsequent online runs retry artwork discovery.
                if (artifact is null)
                {
                    record = record with { ArtworkUrl = null, ArtworkPath = null,
                        ArtworkSourceUrl = null, ArtworkProvider = null };
                }
            }

            cache.Write(record);
            catalog?.WriteMetadata(group, record, CatalogNamespace.For(group));
            if (artifact is not null)
                catalog?.WriteArtwork(group, artifact, CatalogNamespace.For(group));
            output.Add(record);
            Directory.CreateDirectory(nfoDirectory);
            var basename = Naming.ReleaseNamer.GetBasename(group);
            var nfo = Export.GotekNfoRenderer.Render(record.Title, record.Year, record.Publisher, record.Description);
            File.WriteAllText(Path.Combine(nfoDirectory, $"{basename}.nfo"), nfo);
            progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                "completed", record.Provider));
        }
        return output;
    }

    private static bool IsDemosceneArtwork(MetadataRecord record) =>
        string.Equals(record.ArtworkProvider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.ArtworkProvider, "demozoo", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "demozoo", StringComparison.OrdinalIgnoreCase);

    private static bool IsOfflineRecord(MetadataRecord record) =>
        string.Equals(record.Provider, "offline-filename", StringComparison.OrdinalIgnoreCase);

    private static bool IsRemovedGameProvider(MetadataRecord record) =>
        IsRemovedGameProvider(record.Provider) || IsRemovedGameProvider(record.ArtworkProvider);

    private static bool IsRemovedGameProvider(string? provider) => provider is not null &&
        (provider.Equals("hasheous", StringComparison.OrdinalIgnoreCase) ||
         provider.Equals("playmatch", StringComparison.OrdinalIgnoreCase) ||
         provider.Equals("hall-of-light", StringComparison.OrdinalIgnoreCase) ||
         provider.Equals("wikipedia", StringComparison.OrdinalIgnoreCase));

    private static bool HasUsableCachedArtworkReference(MetadataRecord record)
    {
        // Metadata-only results are valid cache entries. A missing local image
        // is stale only when there is no saved URL from which it can be restored.
        if (string.IsNullOrWhiteSpace(record.ArtworkPath)) return true;
        return File.Exists(record.ArtworkPath) || !string.IsNullOrWhiteSpace(record.ArtworkUrl);
    }

    private static bool HasArtwork(MetadataRecord record) =>
        !string.IsNullOrWhiteSpace(record.ArtworkUrl) ||
        !string.IsNullOrWhiteSpace(record.ArtworkPath);

    private static string ShortProviderError(string error)
    {
        var marker = error.IndexOf("Response status code does not indicate success:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return error;
        var status = error[(marker + "Response status code does not indicate success:".Length)..].Trim();
        return status.Length > 80 ? status[..80] : status;
    }

    private static bool HasSourceHash(ReleaseGroup group) =>
        group.SourceSha256 is { Length: > 0 } ||
        group.Records.Any(record => record.SourceSha256 is { Length: > 0 });

    private static bool IsLegacyDirectoryTitle(MetadataRecord record) =>
        string.Equals(record.Provider, "offline-filename", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "demozoo", StringComparison.OrdinalIgnoreCase);
}

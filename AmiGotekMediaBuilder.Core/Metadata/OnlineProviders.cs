using System.Net;
using System.Text.Json;
using AmiGotekMediaBuilder.Core.Catalog;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

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

/// <summary>
/// Configurable adapter for public JSON metadata gateways.
/// </summary>
public class JsonMetadataProvider(OnlineProviderOptions options, SafeHttpClient? client = null)
    : IAsyncMetadataProvider, IDisposable
{
    private readonly SafeHttpClient _client = client ?? new SafeHttpClient();
    private readonly bool _ownsClient = client is null;
    public string Id => options.Id;

    public virtual async Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        var title = group.Title ?? "";
        if (options.SearchPath.Contains("{sha256}", StringComparison.Ordinal) &&
            string.IsNullOrWhiteSpace(group.SourceSha256))
            return null;
        var path = options.SearchPath.Replace("{title}", Uri.EscapeDataString(title), StringComparison.Ordinal)
            .Replace("{release_key}", Uri.EscapeDataString(group.ReleaseKey), StringComparison.Ordinal)
            .Replace("{sha256}", Uri.EscapeDataString(group.SourceSha256 ?? ""), StringComparison.Ordinal)
            .Replace("{systemeid}", "23", StringComparison.Ordinal);

        var url = new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString();
        var bytes = await _client.GetBytesAsync(url, options.MaxResponseBytes, cancellationToken);
        var result = ParseResponse(bytes, group, title);
        return result is { ArtworkUrl: not null, ArtworkSourceUrl: null }
            ? result with { ArtworkSourceUrl = RedactQuery(url) }
            : result;
    }

    protected MetadataRecord? ParseResponse(byte[] bytes, ReleaseGroup group, string title)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (IsExplicitMiss(document.RootElement)) return null;
            var item = SelectBestItem(document.RootElement, title);
            if (item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
            if (IsExplicitMiss(item)) return null;
            var canonical = StringValue(item, "canonical_title", "title", "name", "game") ?? title;
            var artworkUrl = ArtworkUrl(item) ?? ArtworkUrl(document.RootElement);
            var sourceUrl = StringValue(item, "artwork_source_url", "source_url", "website", "url");
            if (sourceUrl is not null) sourceUrl = RedactQuery(sourceUrl);
            return CreateRecord(group, canonical,
                YearValue(item),
                StringValue(item, "publisher", "company", "developer"),
                StringValue(item, "description", "summary", "plot"), artworkUrl, sourceUrl);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    private static JsonElement SelectBestItem(JsonElement root, string title)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
        foreach (var property in new[] { "results", "games", "data", "items" })
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().FirstOrDefault(x => x.ValueKind == JsonValueKind.Object);
        foreach (var property in new[] { "result", "game", "data", "item" })
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        return root;
    }

    private static bool IsExplicitMiss(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("found", out var found) &&
        found.ValueKind == JsonValueKind.False;

    private static string? StringValue(JsonElement item, params string[] names)
    {
        foreach (var name in names)
            if (item.TryGetProperty(name, out var value))
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Number => value.ToString(),
                    _ => null
                };
        return null;
    }

    private static string? YearValue(JsonElement item)
    {
        if (!item.TryGetProperty("year", out _) && !item.TryGetProperty("release_year", out _) &&
            !item.TryGetProperty("date", out _)) return null;
        var raw = StringValue(item, "year", "release_year", "date");
        if (long.TryParse(raw, out var number) && number > 100_000_000)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(number).Year.ToString(); }
            catch (ArgumentOutOfRangeException) { }
        }
        return raw;
    }

    /// <summary>
    /// Reads the common artwork shapes used by scraper gateways and provider
    /// APIs (artwork_url/image_url, cover.url, images[], screenshots[]).
    /// Values are normalized to absolute HTTPS/HTTP URLs. Invalid schemes are
    /// ignored and therefore can never reach the downloader.
    /// </summary>
    private static string? ArtworkUrl(JsonElement item)
    {
        if (item.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        foreach (var name in new[]
        {
            "artwork_url", "image_url", "cover_url", "boxart_url", "thumbnail_url",
            "artwork", "image", "cover", "boxart", "front_cover"
        })
        {
            if (!item.TryGetProperty(name, out var value)) continue;
            var candidate = UrlValue(value);
            if (candidate is not null) return candidate;
        }

        foreach (var name in new[] { "artwork_urls", "images", "screenshots", "media", "assets" })
        {
            if (!item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array) continue;
            foreach (var entry in value.EnumerateArray())
            {
                var candidate = UrlValue(entry);
                if (candidate is not null) return candidate;
            }
        }
        return null;
    }

    protected MetadataRecord CreateRecord(ReleaseGroup group, string title, string? year,
        string? publisher, string? description, string? artworkUrl, string? sourceUrl) =>
        new(group.ReleaseKey, title, year, publisher, description, Id, DateTimeOffset.UtcNow)
        {
            ArtworkUrl = artworkUrl,
            ArtworkSourceUrl = sourceUrl,
            ArtworkProvider = artworkUrl is null ? null : Id
        };

    private static string RedactQuery(string url)
    {
        var marker = url.IndexOf('?', StringComparison.Ordinal);
        return marker >= 0 ? url[..marker] : url;
    }

    private static string? UrlValue(JsonElement value)
    {
        var raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => StringValue(value, "url", "image_url", "src", "href", "path"),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)) return null;

        return raw;
    }
}

public sealed class HasheousProvider(OnlineProviderOptions options, SafeHttpClient? client = null)
    : JsonMetadataProvider(options with { Id = "hasheous" }, client);
public sealed class PlaymatchProvider(OnlineProviderOptions options, SafeHttpClient? client = null)
    : JsonMetadataProvider(options with { Id = "playmatch" }, client);

public sealed class OnlineMetadataChain(IEnumerable<IAsyncMetadataProvider> providers)
{
    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group,
        CancellationToken cancellationToken = default,
        Action<string>? providerStarted = null,
        Action<string, string>? providerFailed = null)
    {
        MetadataRecord? firstMatch = null;
        foreach (var provider in providers)
        {
            providerStarted?.Invoke(provider.Id);
            try
            {
                var result = await provider.ResolveAsync(group, cancellationToken);
                if (result is null) continue;
                // Keep metadata precedence, but allow a later scraper to
                // supply artwork when an earlier provider returned text only.
                firstMatch ??= result;
                if (!string.IsNullOrWhiteSpace(result.ArtworkUrl) ||
                    !string.IsNullOrWhiteSpace(result.ArtworkPath)) return result;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                providerFailed?.Invoke(provider.Id, ex.GetBaseException().Message);
            }
        }
        return firstMatch;
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
            // A metadata cache hit is not a completed artwork lookup. Retry
            // providers for records without artwork, and for our own fallback,
            // so a later online build can replace the placeholder with a real
            // provider image.
            var staleDemosceneCache = !group.IsDemoscene && cachedCandidate is not null &&
                                      IsDemosceneArtwork(cachedCandidate);
            var cachedUsable = cachedCandidate is not null && !staleDemosceneCache &&
                               !NeedsOnlineArtworkRefresh(cachedCandidate);
            MetadataRecord? record = cachedUsable ? cachedCandidate : null;
            if (record is null)
            {
                var refreshed = await chain.ResolveAsync(group, cancellationToken,
                    provider => progress?.Report(new(current, releaseGroups.Length,
                        group.ReleaseKey, title, "metadata", provider)),
                    (provider, error) => progress?.Report(new(current, releaseGroups.Length,
                        group.ReleaseKey, title, "provider-error", provider, error)));
                if (!staleDemosceneCache && cachedCandidate is not null &&
                    refreshed is not null && HasArtwork(refreshed))
                {
                    // Keep the cached metadata precedence while accepting a
                    // newly discovered provider image.
                    record = cachedCandidate with
                    {
                        ArtworkUrl = refreshed.ArtworkUrl,
                        ArtworkPath = refreshed.ArtworkPath,
                        ArtworkSourceUrl = refreshed.ArtworkSourceUrl,
                        ArtworkProvider = refreshed.ArtworkProvider
                    };
                }
                else if (staleDemosceneCache)
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
                try
                {
                    progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                        "artwork", record.ArtworkProvider ?? record.Provider));
                    if (string.Equals(record.ArtworkProvider, DefaultArtworkService.ProviderId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        artifact = DefaultArtworkService.Ensure(
                            group, artworkOriginalDirectory, artworkProcessedDirectory);
                    }
                    else if (!string.IsNullOrWhiteSpace(record.ArtworkUrl) ||
                             !string.IsNullOrWhiteSpace(record.ArtworkPath))
                    {
                        artifact = await artwork.DownloadAsync(
                            record, group, artworkOriginalDirectory,
                            artworkProcessedDirectory, cancellationToken);
                        if (artifact is not null) ArtworkDownloaded++;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    ArtworkFailed++;
                    progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                        "artwork-error", record.ArtworkProvider ?? record.Provider,
                        ex.GetBaseException().Message));
                }

                // Every game receives a local thumbnail, even when all public
                // providers miss, rate-limit, or return metadata without an
                // image. This is deliberately never applied to demoscene data.
                if (artifact is null)
                {
                    try
                    {
                        artifact = DefaultArtworkService.Ensure(
                            group, artworkOriginalDirectory, artworkProcessedDirectory);
                        if (artifact is not null)
                        {
                            ArtworkFallbackUsed++;
                            record = record with
                            {
                                ArtworkUrl = null,
                                ArtworkPath = artifact.OriginalPath,
                                ArtworkSourceUrl = artifact.Url,
                                ArtworkProvider = DefaultArtworkService.ProviderId
                            };
                            progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                                "artwork", DefaultArtworkService.ProviderId));
                        }
                    }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                    {
                        ArtworkFailed++;
                        progress?.Report(new(current, releaseGroups.Length, group.ReleaseKey, title,
                            "fallback-error", DefaultArtworkService.ProviderId,
                            ex.GetBaseException().Message));
                    }
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

    private static bool NeedsOnlineArtworkRefresh(MetadataRecord record) =>
        string.Equals(record.ArtworkProvider, DefaultArtworkService.ProviderId,
            StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(record.ArtworkPath) && !File.Exists(record.ArtworkPath)) ||
        (string.IsNullOrWhiteSpace(record.ArtworkUrl) &&
         string.IsNullOrWhiteSpace(record.ArtworkPath));

    private static bool HasArtwork(MetadataRecord record) =>
        !string.IsNullOrWhiteSpace(record.ArtworkUrl) ||
        !string.IsNullOrWhiteSpace(record.ArtworkPath);

    private static bool HasSourceHash(ReleaseGroup group) =>
        group.SourceSha256 is { Length: > 0 } ||
        group.Records.Any(record => record.SourceSha256 is { Length: > 0 });

    private static bool IsLegacyDirectoryTitle(MetadataRecord record) =>
        string.Equals(record.Provider, "offline-filename", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "pouet", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(record.Provider, "demozoo", StringComparison.OrdinalIgnoreCase);
}

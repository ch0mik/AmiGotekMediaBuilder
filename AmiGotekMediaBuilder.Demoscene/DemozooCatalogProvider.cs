using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Demoscene;

public sealed record DemozooCatalogOptions(
    string BaseUrl = "https://demozoo.org",
    int MaxResponseBytes = 4_000_000,
    int MaxConcurrency = 2,
    TimeSpan? RequestDelay = null);

/// <summary>
/// Reads Amiga demo productions from Demozoo. The public productions list
/// uses platform IDs 5 (OCS/ECS), 6 (AGA) and 26 (PPC/RTG), with production
/// type 1 representing Demo.
/// </summary>
public sealed class DemozooCatalogProvider : IDisposable
{
    private const int OcsEcsPlatformId = 5;
    private const int AgaPlatformId = 6;
    private const int PpcRtgPlatformId = 26;
    private const int DemoProductionTypeId = 1;

    private readonly DemozooCatalogOptions _options;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public DemozooCatalogProvider(DemozooCatalogOptions? options = null, SafeHttpClient? client = null)
    {
        _options = options ?? new DemozooCatalogOptions();
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public async Task<IReadOnlyList<DemosceneProduction>> BrowseAsync(
        DemosceneQuery query,
        CancellationToken cancellationToken = default,
        IProgress<DemosceneProduction>? progress = null)
    {
        var normalized = query.Normalize();
        // This provider is intentionally scoped to the Demozoo Demo type.
        // Other types continue to be served by Pouët and existing providers.
        if (!string.IsNullOrWhiteSpace(normalized.Type) &&
            !normalized.Type.Equals("demo", StringComparison.OrdinalIgnoreCase))
            return [];

        var requestedPlatforms = normalized.Platform == DemoscenePlatform.None
            ? DemoscenePlatform.OcsEcs | DemoscenePlatform.Aga | DemoscenePlatform.PpcRtg
            : normalized.Platform;
        var platformList = DemoscenePlatforms.Enumerate(requestedPlatforms).ToArray();
        if (platformList.Length == 0) return [];
        var quotas = platformList
            .Select((platform, index) => new
            {
                platform,
                quota = normalized.MaxItems / platformList.Length +
                        (index < normalized.MaxItems % platformList.Length ? 1 : 0)
            })
            .ToDictionary(x => x.platform, x => x.quota);
        var counts = platformList.ToDictionary(platform => platform, _ => 0);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<DemosceneProduction>();
        var delay = _options.RequestDelay ?? TimeSpan.FromMilliseconds(350);

        for (var page = 1; page <= normalized.MaxPages && output.Count < normalized.MaxItems; page++)
        {
            var pageHadRows = false;
            foreach (var platform in platformList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = BuildListUrl(platform, page);
                IReadOnlyList<DemosceneProduction> rows;
                try
                {
                    var bytes = await _client.GetBytesAsync(url, _options.MaxResponseBytes, cancellationToken);
                    rows = ParseListPage(bytes, url);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (rows.Count > 0) pageHadRows = true;
                var remaining = quotas[platform] - counts[platform];
                if (remaining <= 0) continue;
                var candidates = rows
                    .Where(row => row.Supports(normalized.Platform))
                    .Where(row => seen.Add(row.PouetId))
                    .Take(Math.Min(remaining, normalized.MaxItems - output.Count))
                    .ToArray();
                foreach (var production in await LoadDetailsBatchAsync(candidates, delay, cancellationToken))
                {
                    if (production is null || !production.Supports(normalized.Platform) ||
                        !string.Equals(production.Type, "demo", StringComparison.OrdinalIgnoreCase) ||
                        output.Count >= normalized.MaxItems)
                        continue;
                    output.Add(production);
                    counts[platform]++;
                    progress?.Report(production);
                }
            }

            if (!pageHadRows) break;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
        }
        return output;
    }

    public async Task<DemosceneProduction?> GetProductionAsync(
        string demozooId,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(demozooId ?? string.Empty, "^\\d+$"))
            throw new ArgumentException("Demozoo production ID must be numeric.", nameof(demozooId));
        var source = ProductionUrl(demozooId!);
        var bytes = await _client.GetBytesAsync(source, _options.MaxResponseBytes, cancellationToken);
        return DemozooHtmlParser.ParseProduction(bytes, source, demozooId!);
    }

    public static IReadOnlyList<DemosceneProduction> ParseListPage(byte[] bytes, string sourceUrl) =>
        DemozooHtmlParser.ParseListPage(bytes, sourceUrl);

    public static DemosceneProduction? ParseProduction(byte[] bytes, string sourceUrl, string demozooId) =>
        DemozooHtmlParser.ParseProduction(bytes, sourceUrl, demozooId);

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private async Task<IReadOnlyList<DemosceneProduction?>> LoadDetailsBatchAsync(
        IReadOnlyList<DemosceneProduction> rows,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return [];
        using var gate = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrency, 1, 8));
        var tasks = rows.Select(async row =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                DemosceneProduction? production;
                try
                {
                    var bytes = await _client.GetBytesAsync(row.SourceUrl, _options.MaxResponseBytes, cancellationToken);
                    production = DemozooHtmlParser.ParseProduction(bytes, row.SourceUrl, row.PouetId) ?? row;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    production = row;
                }
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                return production;
            }
            finally { gate.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    private string BuildListUrl(DemoscenePlatform platform, int page) =>
        new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"),
            $"productions/?platform={PlatformId(platform)}&production_type={DemoProductionTypeId}&page={page}").ToString();

    private string ProductionUrl(string id) =>
        new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), $"productions/{id}/").ToString();

    private static int PlatformId(DemoscenePlatform platform) => platform switch
    {
        DemoscenePlatform.OcsEcs => OcsEcsPlatformId,
        DemoscenePlatform.Aga => AgaPlatformId,
        DemoscenePlatform.PpcRtg => PpcRtgPlatformId,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "A concrete platform is required.")
    };
}

internal static class DemozooHtmlParser
{
    private static readonly Regex ProductionLinkRegex = new(
        "<a\\b[^>]*href=[\\\"'](?:(?:https?:)?//[^/]+)?/?productions/(?<id>\\d+)/?[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorRegex = new(
        "<a\\b[^>]*href=[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>(?<label>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(
        "<h1\\b[^>]*>(?<value>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(
        "<title\\b[^>]*>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(
        "<img\\b[^>]*src=[\\\"'](?<src>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MetaRegex = new(
        "<meta\\b[^>]*(?:property|name)=[\\\"'](?<key>[^\\\"']+)[\\\"'][^>]*content=[\\\"'](?<value>[^\\\"']*)[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StripRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptRegex = new(
        "<(script|style)\\b.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);
    private static readonly Regex YearRegex = new("\\b(?<year>19\\d{2}|20\\d{2})\\b", RegexOptions.Compiled);
    private static readonly Regex ReleaseDateRegex = new(
        "released?\\s+(?:\\d{1,2}\\s+)?[A-Za-z]+\\s+(?<year>19\\d{2}|20\\d{2})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InlineByRegex = new(
        "^(?<title>.+?)\\s+by\\s+(?<group>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ByHeadingRegex = new(
        "<h[2-4]\\b[^>]*>\\s*by\\s+(?<group>.*?)</h[2-4]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex PouetLinkRegex = new(
        "pouet\\.net/(?:prod\\.php\\?[^\\\"']*which=|productions/)(?<id>\\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<DemosceneProduction> ParseListPage(byte[] bytes, string sourceUrl)
    {
        var html = WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes));
        var rows = new List<DemosceneProduction>();
        foreach (Match match in ProductionLinkRegex.Matches(html))
        {
            var start = Math.Max(0, match.Index - 450);
            var length = Math.Min(html.Length - start, match.Length + 900);
            var context = Text(html.Substring(start, length));
            var platform = DemoscenePlatforms.ParseLabels(context);
            if (platform == DemoscenePlatform.None) continue;
            var id = match.Groups["id"].Value;
            var title = Text(match.Groups["title"].Value);
            var year = YearRegex.Match(context).Groups["year"].Value;
            var group = InlineByRegex.Match(context).Groups["group"].Value.Trim();
            var production = new DemosceneProduction(
                id,
                title,
                group.Length == 0 ? null : group,
                year.Length == 0 ? null : year,
                "demo",
                platform,
                ProductionUrl(sourceUrl, id),
                null,
                null,
                [])
            {
                Catalog = DemosceneCatalogs.Demozoo
            };
            rows.Add(production);
        }
        return rows
            .GroupBy(production => production.PouetId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    public static DemosceneProduction? ParseProduction(byte[] bytes, string sourceUrl, string demozooId)
    {
        var html = WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes));
        var visible = Text(ScriptRegex.Replace(html, " "));
        var platforms = DemoscenePlatforms.ParseLabels(visible);
        if (platforms == DemoscenePlatform.None) return null;

        var title = Text(HeadingRegex.Match(html).Groups["value"].Value);
        if (title.Length == 0)
            title = Text(TitleRegex.Match(html).Groups["value"].Value);
        title = Regex.Replace(title, @"\s+-\s+Demozoo\s*$", "", RegexOptions.IgnoreCase).Trim();
        if (title.Length == 0) title = $"Demozoo production {demozooId}";

        var by = InlineByRegex.Match(title);
        var group = by.Success
            ? by.Groups["group"].Value.Trim()
            : Text(ByHeadingRegex.Match(html).Groups["group"].Value);
        if (group.Length == 0) group = null;
        if (by.Success) title = by.Groups["title"].Value.Trim();
        var year = ReleaseDateRegex.Match(visible).Groups["year"].Value;
        if (year.Length == 0) year = YearRegex.Match(visible).Groups["year"].Value;
        var artwork = NormalizeUrl(Meta(html, "og:image"), sourceUrl) ??
                      NormalizeUrl(FirstImage(html), sourceUrl);
        var description = Meta(html, "description");
        var downloads = AnchorRegex.Matches(html)
            .Cast<Match>()
            .Select(match => PouetCatalogProvider.ClassifyDownload(
                NormalizeUrl(WebUtility.HtmlDecode(match.Groups["href"].Value.Trim()), sourceUrl) ?? string.Empty,
                Text(match.Groups["label"].Value)))
            .Where(link => link is not null)
            .Select(link => link!)
            .GroupBy(link => link.Url, StringComparer.OrdinalIgnoreCase)
            .Select(grouped => grouped.First())
            .ToArray();
        var linkedPouet = PouetLinkRegex.Match(html).Groups["id"].Value;
        return new DemosceneProduction(
            demozooId,
            title,
            group,
            year.Length == 0 ? null : year,
            Regex.IsMatch(visible, @"\bdemo\b", RegexOptions.IgnoreCase)
                ? "demo"
                : FirstKnownType(visible) ?? "demo",
            platforms,
            sourceUrl,
            string.IsNullOrWhiteSpace(description) ? "Demozoo demo" : Text(description),
            artwork,
            downloads)
        {
            Catalog = DemosceneCatalogs.Demozoo,
            LinkedPouetId = string.IsNullOrWhiteSpace(linkedPouet) ? null : linkedPouet
        };
    }

    private static string? Meta(string html, string key)
    {
        foreach (Match match in MetaRegex.Matches(html))
            if (match.Groups["key"].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(match.Groups["value"].Value.Trim());
        return null;
    }

    private static string? FirstImage(string html)
    {
        foreach (Match match in ImageRegex.Matches(html))
        {
            var source = match.Groups["src"].Value;
            if (source.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                source.Contains("avatar", StringComparison.OrdinalIgnoreCase))
                continue;
            return source;
        }
        return null;
    }

    private static string? NormalizeUrl(string? value, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https" or "ftp")
            return absolute.ToString();
        return Uri.TryCreate(new Uri(sourceUrl), value, out var relative) &&
               relative.Scheme is "http" or "https" or "ftp"
            ? relative.ToString()
            : null;
    }

    private static string Text(string value)
    {
        var stripped = StripRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(WebUtility.HtmlDecode(stripped), " ").Trim();
    }

    private static string? FirstKnownType(string text)
    {
        foreach (var type in new[] { "demopack", "diskmag", "musicdisk", "invitation", "slideshow", "intro", "demo", "4k", "64k", "256b", "wild", "game", "tool" })
            if (Regex.IsMatch(text, $"\\b{Regex.Escape(type)}\\b", RegexOptions.IgnoreCase))
                return type;
        return null;
    }

    private static string ProductionUrl(string sourceUrl, string id)
    {
        var source = new Uri(sourceUrl);
        return new Uri($"{source.Scheme}://{source.Host}{(source.IsDefaultPort ? string.Empty : ":" + source.Port)}/productions/{id}/").ToString();
    }
}

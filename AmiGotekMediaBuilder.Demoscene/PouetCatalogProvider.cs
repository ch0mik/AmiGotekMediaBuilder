using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Demoscene;

public sealed record PouetCatalogOptions(
    string BaseUrl = "https://www.pouet.net",
    int MaxResponseBytes = 3_000_000,
    int MaxConcurrency = 2,
    TimeSpan? RequestDelay = null);

/// <summary>
/// Reads Pouët's public production lists and production pages. This provider
/// only discovers metadata and candidate links; binary downloads are handled
/// by <see cref="DemosceneDownloadService"/>.
/// </summary>
public sealed class PouetCatalogProvider : IDisposable
{
    private static readonly string[] KnownTypes =
    ["demo", "intro", "demopack", "diskmag", "4k", "64k", "256b", "musicdisk", "invitation", "wild", "slideshow", "game", "tool"];

    private readonly PouetCatalogOptions _options;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public PouetCatalogProvider(PouetCatalogOptions? options = null, SafeHttpClient? client = null)
    {
        _options = options ?? new PouetCatalogOptions();
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public async Task<IReadOnlyList<DemosceneProduction>> BrowseAsync(
        DemosceneQuery query,
        CancellationToken cancellationToken = default,
        IProgress<DemosceneProduction>? progress = null)
    {
        var normalized = query.Normalize();
        var requestedPlatforms = normalized.Platform == DemoscenePlatform.None
            ? DemoscenePlatform.OcsEcs | DemoscenePlatform.Aga | DemoscenePlatform.PpcRtg
            : normalized.Platform;
        var platformList = DemoscenePlatforms.Enumerate(requestedPlatforms).ToArray();
        var platformQuotas = platformList
            .Select((platform, index) => new
            {
                platform,
                quota = normalized.MaxItems / platformList.Length +
                        (index < normalized.MaxItems % platformList.Length ? 1 : 0)
            })
            .ToDictionary(x => x.platform, x => x.quota);
        var platformCounts = platformList.ToDictionary(p => p, _ => 0);
        var output = new List<DemosceneProduction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var delay = _options.RequestDelay ?? TimeSpan.FromMilliseconds(350);

        for (var page = 1; page <= normalized.MaxPages && output.Count < normalized.MaxItems; page++)
        {
            var pageHadRows = false;
            foreach (var platform in platformList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = BuildListUrl(platform, normalized, page);
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
                var remainingForPlatform = platformQuotas[platform] - platformCounts[platform];
                if (remainingForPlatform <= 0) continue;
                var candidates = rows
                    .Where(row => seen.Add(row.PouetId) && Matches(row, normalized))
                    .Take(Math.Min(remainingForPlatform, Math.Max(0, normalized.MaxItems - output.Count)))
                    .ToArray();
                foreach (var production in await LoadDetailsBatchAsync(candidates, delay, cancellationToken))
                {
                    if (production is null || !Matches(production, normalized) || output.Count >= normalized.MaxItems) continue;
                    output.Add(production);
                    platformCounts[platform]++;
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
        string pouetId,
        CancellationToken cancellationToken = default)
    {
        if (!Regex.IsMatch(pouetId ?? string.Empty, "^\\d+$"))
            throw new ArgumentException("Pouët production ID must be numeric.", nameof(pouetId));
        var source = ProductionUrlForBase(pouetId!);
        var bytes = await _client.GetBytesAsync(source, _options.MaxResponseBytes, cancellationToken);
        return PouetHtmlParser.ParseProduction(bytes, source, pouetId!);
    }

    public static IReadOnlyList<DemosceneProduction> ParseListPage(byte[] bytes, string sourceUrl) =>
        PouetHtmlParser.ParseListPage(bytes, sourceUrl);

    public static DemosceneDownloadLink? ClassifyDownload(string url, string? label = null) =>
        PouetHtmlParser.ClassifyDownload(url, label);

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private async Task<DemosceneProduction?> LoadDetailsAsync(
        DemosceneProduction row,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _client.GetBytesAsync(row.SourceUrl, _options.MaxResponseBytes, cancellationToken);
            return PouetHtmlParser.ParseProduction(bytes, row.SourceUrl, row.PouetId) ?? row;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return row;
        }
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
                var production = await LoadDetailsAsync(row, cancellationToken);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                return production;
            }
            finally { gate.Release(); }
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    private string BuildListUrl(DemoscenePlatform platform, DemosceneQuery query, int page)
    {
        var parameters = new List<string>
        {
            $"page={page}",
            $"platform%5B0%5D={Uri.EscapeDataString(platform.ToPouetLabel())}"
        };
        if (!string.IsNullOrWhiteSpace(query.Type))
            parameters.Add($"type%5B0%5D={Uri.EscapeDataString(query.Type)}");
        return new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"),
            "prodlist.php?" + string.Join('&', parameters)).ToString();
    }

    private string ProductionUrlForBase(string id)
    {
        var builder = new UriBuilder(_options.BaseUrl.TrimEnd('/') + "/prod.php")
        {
            Query = $"which={id}"
        };
        return builder.Uri.ToString();
    }

    private static string ProductionUrl(string id) => $"https://www.pouet.net/prod.php?which={id}";

    private static bool Matches(DemosceneProduction production, DemosceneQuery query)
    {
        if (!production.Supports(query.Platform)) return false;
        if (!string.IsNullOrWhiteSpace(query.Type) &&
            !string.Equals(production.Type, query.Type, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(query.Search) &&
            !production.Title.Contains(query.Search, StringComparison.OrdinalIgnoreCase) &&
            !(production.Group?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ?? false)) return false;
        if (!int.TryParse(production.Year, out var year))
            return !query.YearFrom.HasValue && !query.YearTo.HasValue;
        return (!query.YearFrom.HasValue || year >= query.YearFrom.Value) &&
               (!query.YearTo.HasValue || year <= query.YearTo.Value);
    }
}

internal static class PouetHtmlParser
{
    private static readonly Regex RowRegex = new("<tr\\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ProductionLinkRegex = new("<a\\b[^>]*href=[\\\"'](?:(?:https?:)?//[^/]+)?/?prod\\.php\\?which=(?<id>\\d+)[^\\\"']*[\\\"'][^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorRegex = new("<a\\b[^>]*href=[\\\"'](?<href>[^\\\"']+)[\\\"'][^>]*>(?<label>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new("<img\\b[^>]*src=[\\\"'](?<src>[^\\\"']+)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MetaRegex = new("<meta\\b[^>]*(?:property|name)=[\\\"'](?<key>[^\\\"']+)[\\\"'][^>]*content=[\\\"'](?<value>[^\\\"']*)[\\\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new("<title\\b[^>]*>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ReleaseDateRegex = new("release\\s+date.{0,180}?(?<year>19\\d{2}|20\\d{2})", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex YearRegex = new("\\b(?<year>19\\d{2}|20\\d{2})\\b", RegexOptions.Compiled);
    private static readonly Regex ByRegex = new("^(?<title>.+?)\\s+by\\s+(?<group>.+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StripRegex = new("<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptRegex = new("<(script|style)\\b.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    public static IReadOnlyList<DemosceneProduction> ParseListPage(byte[] bytes, string sourceUrl)
    {
        var html = WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes));
        var rows = new List<DemosceneProduction>();
        foreach (Match rowMatch in RowRegex.Matches(html))
        {
            var row = rowMatch.Groups["row"].Value;
            var link = ProductionLinkRegex.Match(row);
            if (!link.Success) continue;
            var text = Text(row);
            var platforms = DemoscenePlatforms.ParseLabels(text);
            if (platforms == DemoscenePlatform.None) continue;
            var id = link.Groups["id"].Value;
            var title = Text(link.Groups["title"].Value);
            var type = FirstKnownType(text);
            var year = YearRegex.Match(text).Groups["year"].Value;
            var productUrl = ProductionUrl(id, sourceUrl);
            rows.Add(new DemosceneProduction(id, title, null,
                year.Length == 0 ? null : year, type, platforms,
                productUrl, null, null, []));
        }
        return rows;
    }

    public static DemosceneProduction? ParseProduction(byte[] bytes, string sourceUrl, string pouetId)
    {
        var html = WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes));
        var visible = Text(ScriptRegex.Replace(html, " "));
        if (!Regex.IsMatch(visible, "\\bAmiga\\b", RegexOptions.IgnoreCase)) return null;

        var pageTitle = Meta(html, "og:title") ?? TitleRegex.Match(html).Groups["value"].Value;
        pageTitle = Text(pageTitle);
        pageTitle = Regex.Replace(pageTitle, @"\s*::\s*pou[eë]t.*$", "", RegexOptions.IgnoreCase).Trim();
        if (pageTitle.Length == 0) pageTitle = $"Pouët production {pouetId}";
        var by = ByRegex.Match(pageTitle);
        var title = by.Success ? by.Groups["title"].Value.Trim() : pageTitle;
        var group = by.Success ? by.Groups["group"].Value.Trim() : null;
        var platform = DemoscenePlatforms.ParseLabels(visible);
        var type = FirstKnownType(visible);
        var year = ReleaseDateRegex.Match(visible).Groups["year"].Value;
        if (year.Length == 0) year = YearRegex.Match(visible).Groups["year"].Value;
        var artwork = NormalizeUrl(Meta(html, "og:image"), sourceUrl) ??
                      NormalizeUrl(FirstImage(html), sourceUrl);
        var description = Meta(html, "description");
        if (string.IsNullOrWhiteSpace(description))
            description = type is null ? "Pouët production" : $"Pouët {type}";
        var downloads = AnchorRegex.Matches(html)
            .Cast<Match>()
            .Select(m => ClassifyDownload(
                NormalizeUrl(WebUtility.HtmlDecode(m.Groups["href"].Value.Trim()), sourceUrl) ?? string.Empty,
                Text(m.Groups["label"].Value)))
            .Where(l => l is not null)
            .Select(l => l!)
            .GroupBy(l => l.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
        return new DemosceneProduction(pouetId, WebUtility.HtmlDecode(title), group,
            year.Length == 0 ? null : year, type, platform, sourceUrl,
            Text(description), artwork, downloads);
    }

    public static DemosceneDownloadLink? ClassifyDownload(string url, string? label)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp"))
            return null;
        var path = uri.AbsolutePath.ToLowerInvariant();
        var text = $"{path} {label}".ToLowerInvariant();
        var hasKnownExtension = path.EndsWith(".adf") || path.EndsWith(".dsk") ||
                                path.EndsWith(".zip") || path.EndsWith(".gz") ||
                                path.EndsWith(".adz") || path.EndsWith(".7z") ||
                                path.EndsWith(".lha") || path.EndsWith(".dms") ||
                                path.EndsWith(".rom");
        var isCandidate = hasKnownExtension || text.Contains("download") || text.Contains("mirror") ||
                          text.Contains("release") || text.Contains("disk") ||
                          text.Contains("adf") || text.Contains("dsk") ||
                          text.Contains("archive") || text.Contains("image");
        if (!isCandidate) return null;
        var format = path.EndsWith(".adf") ? DemosceneAssetFormat.Adf :
            path.EndsWith(".dsk") ? DemosceneAssetFormat.Dsk :
            path.EndsWith(".zip") ? DemosceneAssetFormat.Zip :
            path.EndsWith(".gz") || path.EndsWith(".adz") ? DemosceneAssetFormat.Gzip :
            path.EndsWith(".7z") ? DemosceneAssetFormat.SevenZip :
            path.EndsWith(".lha") ? DemosceneAssetFormat.Lha :
            path.EndsWith(".dms") ? DemosceneAssetFormat.Dms :
            path.EndsWith(".rom") ? DemosceneAssetFormat.Rom :
            DemosceneAssetFormat.Unknown;
        var diskMatch = Regex.Match(text, @"(?:disk|disc)\s*#?\s*(?<n>\d+)(?:\s+of\s+(?<total>\d+))?", RegexOptions.IgnoreCase);
        var disk = diskMatch.Groups["n"].Value;
        var total = diskMatch.Groups["total"].Value;
        return new DemosceneDownloadLink(url, Text(label ?? string.Empty), format,
            Path.GetFileName(uri.LocalPath), int.TryParse(disk, out var number) ? number : null,
            int.TryParse(total, out var totalNumber) ? totalNumber : null);
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
                source.Contains("avatar", StringComparison.OrdinalIgnoreCase)) continue;
            return source;
        }
        return null;
    }

    private static string? NormalizeUrl(string? value, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.ToString();
        return Uri.TryCreate(new Uri(sourceUrl), value, out var relative) && relative.Scheme is "http" or "https" or "ftp" ? relative.ToString() : null;
    }

    private static string Text(string value)
    {
        var stripped = StripRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(WebUtility.HtmlDecode(stripped), " ").Trim();
    }

    private static string? FirstKnownType(string text)
    {
        foreach (var type in new[] { "demopack", "diskmag", "musicdisk", "invitation", "slideshow", "intro", "demo", "4k", "64k", "256b", "wild", "game", "tool" })
            if (Regex.IsMatch(text, $"\\b{Regex.Escape(type)}\\b", RegexOptions.IgnoreCase)) return type;
        return null;
    }

    private static string ProductionUrl(string id, string sourceUrl)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
        {
            var builder = new UriBuilder(source.Scheme, source.Host, source.Port)
            {
                Path = "/prod.php",
                Query = $"which={id}"
            };
            return builder.Uri.ToString();
        }
        return $"https://www.pouet.net/prod.php?which={id}";
    }
}

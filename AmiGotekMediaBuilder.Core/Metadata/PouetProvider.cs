using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Pouët production scraper. Pouët has no public JSON API, so this provider
/// reads the public search result and a small number of production pages. It
/// only accepts pages that mention Amiga and never downloads release binaries.
/// </summary>
public sealed partial class PouetProvider(OnlineProviderOptions options, SafeHttpClient? client = null)
    : IAsyncMetadataProvider, IDisposable
{
    private const int MaxCandidates = 8;
    private readonly SafeHttpClient _client = client ?? new SafeHttpClient();
    private readonly bool _ownsClient = client is null;
    public string Id => "pouet";

    public async Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        var title = (group.Title ?? string.Empty).Trim();
        if (title.Length == 0) return null;
        var path = options.SearchPath
            .Replace("{title}", Uri.EscapeDataString(title), StringComparison.Ordinal)
            .Replace("{query}", Uri.EscapeDataString(title), StringComparison.Ordinal);
        var searchUrl = new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), path.TrimStart('/')).ToString();
        var search = Encoding.UTF8.GetString(await _client.GetBytesAsync(searchUrl, options.MaxResponseBytes, cancellationToken));
        var candidates = ProductionIds(search).Take(MaxCandidates).ToArray();
        if (candidates.Length == 0) return null;

        MetadataRecord? best = null;
        var bestScore = 0.0;
        foreach (var id in candidates)
        {
            var pageUrl = new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), $"prod.php?which={id}").ToString();
            string page;
            try
            {
                page = Encoding.UTF8.GetString(await _client.GetBytesAsync(pageUrl, options.MaxResponseBytes, cancellationToken));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            var parsed = ParseProduction(page, pageUrl, group.ReleaseKey, title);
            if (parsed is null) continue;
            var score = Similarity(Normalize(title), Normalize(parsed.Title));
            if (score > bestScore)
            {
                bestScore = score;
                best = parsed;
            }
        }
        return bestScore >= 0.55 ? best : null;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static IEnumerable<string> ProductionIds(string html)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in ProdLinkRegex().Matches(html))
        {
            var id = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (seen.Add(id)) yield return id;
        }
    }

    private static MetadataRecord? ParseProduction(string html, string sourceUrl, string releaseKey, string requestedTitle)
    {
        var decoded = WebUtility.HtmlDecode(html);
        if (!Regex.IsMatch(decoded, @"\bAmiga\b", RegexOptions.IgnoreCase)) return null;
        var pageTitle = HtmlValue(decoded, "og:title") ?? HtmlTagValue(decoded, "title") ?? requestedTitle;
        pageTitle = Regex.Replace(pageTitle, @"\s*::\s*pou[eë]t.*$", "", RegexOptions.IgnoreCase).Trim();
        var by = Regex.Match(pageTitle, @"^(?<title>.+?)\s+by\s+(?<group>.+)$", RegexOptions.IgnoreCase);
        var canonical = by.Success ? by.Groups["title"].Value.Trim() : pageTitle;
        var sceneGroup = by.Success ? by.Groups["group"].Value.Trim() : null;
        var image = HtmlValue(decoded, "og:image") ?? FirstImage(decoded);
        var year = ReleaseYear(decoded);
        var type = Regex.Match(decoded, @"\b(demo|intro|demopack|diskmag|4k|64k|256b|musicdisk|invitation)\b", RegexOptions.IgnoreCase).Value;
        var description = type.Length == 0 ? "Pouët production" : $"Pouët {type}";
        return new MetadataRecord(releaseKey, WebUtility.HtmlDecode(canonical), year, sceneGroup,
            description, "pouet", DateTimeOffset.UtcNow)
        {
            ArtworkUrl = NormalizeUrl(image, sourceUrl),
            ArtworkSourceUrl = sourceUrl,
            ArtworkProvider = image is null ? null : "pouet"
        };
    }

    private static string? HtmlValue(string html, string property)
    {
        var match = Regex.Match(html,
            $"<meta\\s+[^>]*property=[\\\"']{Regex.Escape(property)}[\\\"'][^>]*content=[\\\"'](?<v>[^\\\"']+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success) return WebUtility.HtmlDecode(match.Groups["v"].Value.Trim());
        match = Regex.Match(html,
            $"<meta\\s+[^>]*content=[\\\"'](?<v>[^\\\"']+)[\\\"'][^>]*property=[\\\"']{Regex.Escape(property)}[\\\"']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value.Trim()) : null;
    }

    private static string? HtmlTagValue(string html, string tag)
    {
        var match = Regex.Match(html, $"<{tag}\\b[^>]*>(?<v>.*?)</{tag}>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? WebUtility.HtmlDecode(Regex.Replace(match.Groups["v"].Value, "<.*?>", "").Trim()) : null;
    }

    private static string? FirstImage(string html)
    {
        foreach (Match match in ImageRegex().Matches(html))
        {
            var url = WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            if (url.Contains("logo", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("avatar", StringComparison.OrdinalIgnoreCase)) continue;
            return url;
        }
        return null;
    }

    private static string? ReleaseYear(string html)
    {
        var match = Regex.Match(html, @"release\s+date.{0,160}?(?<year>19\d{2}|20\d{2})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (match.Success) return match.Groups["year"].Value;
        match = Regex.Match(html, @"\b(19\d{2}|20\d{2})\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? NormalizeUrl(string? value, string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.ToString();
        return Uri.TryCreate(new Uri(sourceUrl), value, out var relative) && relative.Scheme is "http" or "https" ? relative.ToString() : null;
    }

    private static string Normalize(string value) => NonAlphaNumericRegex().Replace(value.ToLowerInvariant(), "");

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        if (left == right) return 1;
        if (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)) return 0.92;
        var distance = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) distance[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            var previous = distance[0]; distance[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var current = distance[j];
                distance[j] = Math.Min(Math.Min(distance[j] + 1, distance[j - 1] + 1), previous + (left[i - 1] == right[j - 1] ? 0 : 1));
                previous = current;
            }
        }
        return 1.0 - (double)distance[right.Length] / Math.Max(left.Length, right.Length);
    }

    [GeneratedRegex(@"(?:https?:)?//[^""'<>\s]+/prod\.php\?which=(\d+)|/?prod\.php\?which=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProdLinkRegex();

    [GeneratedRegex(@"<img\b[^>]*?src=[""']([^""']+)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageRegex();

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlphaNumericRegex();
}

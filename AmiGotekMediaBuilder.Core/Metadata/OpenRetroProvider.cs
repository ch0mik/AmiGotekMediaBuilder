using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Keyless OpenRetro scraper for Amiga games. OpenRetro exposes a public HTML
/// search and edit page; the provider intentionally reads only those pages and
/// never requires an account or an API token.
/// </summary>
public sealed class OpenRetroProvider : IAsyncMetadataProvider, IDisposable
{
    private static readonly Regex SearchLinkRegex = new(
        "<a\\s+[^>]*href=[\\\"'](?<href>/[^\\\"']+)[\\\"'][^>]*>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FieldRegex = new(
        "<tr\\b[^>]*>\\s*<t[dh]\\b[^>]*>\\s*(?<key>[^<]+?)\\s*</t[dh]>\\s*<t[dh]\\b[^>]*>(?<value>.*?)</t[dh]>\\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageFieldRegex = new(
        "<tr\\b[^>]*>\\s*<t[dh]\\b[^>]*>\\s*(?<key>[^<]+?)\\s*</t[dh]>\\s*<t[dh]\\b[^>]*>.*?/image/(?<hash>[0-9a-f]{40})",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private readonly string _baseUrl;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public OpenRetroProvider(string baseUrl = "https://openretro.org", SafeHttpClient? client = null)
    {
        _baseUrl = (string.IsNullOrWhiteSpace(baseUrl) ? "https://openretro.org" : baseUrl).TrimEnd('/');
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public string Id => "openretro";

    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var title = group.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var searchUrl = $"{_baseUrl}/browse?q={Uri.EscapeDataString(title)}";
        var searchBytes = await _client.GetBytesAsync(searchUrl, 2_000_000, cancellationToken);
        var candidates = ParseSearch(Encoding.UTF8.GetString(searchBytes), title);
        foreach (var candidate in candidates.Take(5))
        {
            var detailUrl = _baseUrl + candidate.Path.TrimEnd('/') + "/edit";
            try
            {
                var detailBytes = await _client.GetBytesAsync(detailUrl, 3_000_000, cancellationToken);
                var result = ParseDetails(Encoding.UTF8.GetString(detailBytes), group, detailUrl, candidate.Title);
                if (result is not null) return result;
            }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                // A stale result should not prevent trying the next candidate.
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    internal static IReadOnlyList<(string Path, string Title)> ParseSearch(string html, string requestedTitle)
    {
        var result = new List<(string Path, string Title, double Score)>();
        foreach (Match match in SearchLinkRegex.Matches(html))
        {
            var path = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            if (!path.StartsWith("/amiga/", StringComparison.OrdinalIgnoreCase) ||
                path.Contains("/edit", StringComparison.OrdinalIgnoreCase)) continue;
            var title = CleanText(match.Groups["body"].Value);
            if (title.Length == 0) continue;
            result.Add((path, title, Similarity(requestedTitle, title)));
        }
        return result.OrderByDescending(x => x.Score)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Select(x => (x.Path, x.Title)).ToArray();
    }

    internal static MetadataRecord? ParseDetails(
        string html, ReleaseGroup group, string sourceUrl, string? searchTitle = null)
    {
        var title = Field(html, "game_name") ?? Field(html, "title") ?? searchTitle ?? group.Title;
        if (string.IsNullOrWhiteSpace(title)) return null;
        title = CleanText(title);
        var description = Field(html, "__long_description") ?? Field(html, "description") ??
            Field(html, "comment") ?? Field(html, "__short_description");
        var year = Field(html, "year") ?? Field(html, "release_year");
        var publisher = Field(html, "publisher") ?? Field(html, "company");
        var developer = Field(html, "developer") ?? Field(html, "author");
        var imageHash = ImageField(html, "front_sha1") ?? ImageField(html, "cover_sha1") ??
            ImageField(html, "screen1_sha1");
        var artwork = imageHash is null ? null : $"https://openretro.org/image/{imageHash}?s=512";
        // Keep a custom mirror usable in tests and installations. The hash is
        // data from the page, not a user-controlled URL.
        if (artwork is not null && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source))
            artwork = $"{source.Scheme}://{source.Host}{(source.IsDefaultPort ? "" : ":" + source.Port)}/image/{imageHash}?s=512";

        return new MetadataRecord(group.ReleaseKey, title, CleanNullable(year),
            CleanNullable(publisher ?? developer), CleanNullable(description), "openretro", DateTimeOffset.UtcNow)
        {
            ArtworkUrl = artwork,
            ArtworkSourceUrl = artwork is null ? null : RedactQuery(sourceUrl),
            ArtworkProvider = artwork is null ? null : "openretro"
        };
    }

    private static string? Field(string html, string key)
    {
        foreach (Match match in FieldRegex.Matches(html))
        {
            var candidate = CleanText(match.Groups["key"].Value).Trim().ToLowerInvariant();
            if (!candidate.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return CleanText(match.Groups["value"].Value);
        }
        return null;
    }

    private static string? ImageField(string html, string key)
    {
        foreach (Match match in ImageFieldRegex.Matches(html))
        {
            var candidate = CleanText(match.Groups["key"].Value).Trim().ToLowerInvariant();
            if (candidate.Equals(key, StringComparison.OrdinalIgnoreCase))
                return match.Groups["hash"].Value.ToLowerInvariant();
        }
        // The public page has changed its table wrappers a few times. Keep a
        // permissive fallback that still requires the field name and a
        // 40-character SHA-1 image path.
        var fallback = Regex.Match(html,
            $"{Regex.Escape(key)}(?:(?!<tr\\b).)*?/image/(?<hash>[0-9a-f]{{40}})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (fallback.Success) return fallback.Groups["hash"].Value.ToLowerInvariant();
        return null;
    }

    private static string CleanText(string value)
    {
        var withBreaks = Regex.Replace(value, "<\\s*br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(TagRegex.Replace(withBreaks, " "))
            .Replace('\u00a0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static string? CleanNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : CleanText(value);

    private static string RedactQuery(string value)
    {
        var index = value.IndexOf('?', StringComparison.Ordinal);
        return index >= 0 ? value[..index] : value;
    }

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Equals(b, StringComparison.Ordinal)) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.85;
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return (double)at.Intersect(bt, StringComparer.Ordinal).Count() / Math.Max(at.Count, bt.Count);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant().Replace('&', ' ').Split(
            [' ', '\t', '\r', '\n', '-', '_', ':', '/', '(', ')', '[', ']', ',', '.'],
            StringSplitOptions.RemoveEmptyEntries));
}

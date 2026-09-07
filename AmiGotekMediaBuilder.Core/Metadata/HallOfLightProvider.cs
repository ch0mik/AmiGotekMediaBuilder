using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Options for the Hall of Light (amiga.abime.net) HTML provider.
/// The endpoint and response limits are configurable so parser tests can use
/// a local fixture without contacting the public site.
/// </summary>
public sealed record HallOfLightProviderOptions(
    string BaseUrl = "https://amiga.abime.net",
    string SearchPath = "/games/list/?gamename={title}",
    int MaxResponseBytes = 2_000_000,
    int MaxCandidates = 5,
    int RequestDelayMilliseconds = 750);

/// <summary>
/// Resolves game metadata and artwork from Hall of Light. This provider is
/// deliberately game-only: demoscene records are owned by their separate
/// Pouët/Demozoo pipeline and are never sent to Hall of Light.
/// </summary>
public sealed class HallOfLightProvider : IAsyncMetadataProvider, IDisposable
{
    private static readonly Regex AnchorRegex = new(
        "<a\\b(?<attrs>[^>]*)>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(
        "<img\\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HeadingRegex = new(
        "<h1\\b[^>]*>(?<value>.*?)</h1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TableRowRegex = new(
        "<tr\\b[^>]*>\\s*<t[hd]\\b[^>]*>(?<key>.*?)</t[hd]>\\s*<t[hd]\\b[^>]*>(?<value>.*?)</t[hd]>\\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex MetaRegex = new(
        "<meta\\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex AttributeRegex = new(
        "(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*[\\\"'](?<value>.*?)[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly HallOfLightProviderOptions _options;
    private readonly string _baseUrl;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public HallOfLightProvider(
        HallOfLightProviderOptions? options = null,
        SafeHttpClient? client = null)
    {
        _options = options ?? new HallOfLightProviderOptions();
        _baseUrl = (string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? "https://amiga.abime.net"
            : _options.BaseUrl).TrimEnd('/');
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public string Id => "hall-of-light";

    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (group.IsDemoscene) return null;

        var title = group.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var searchPath = _options.SearchPath
            .Replace("{title}", Uri.EscapeDataString(title), StringComparison.Ordinal);
        var searchUrl = BuildUrl(searchPath);
        var searchBytes = await _client.GetBytesAsync(
            searchUrl, Math.Max(1, _options.MaxResponseBytes), cancellationToken);
        if (LooksLikeBotChallenge(searchBytes)) return null;

        var searchHtml = Encoding.UTF8.GetString(searchBytes);
        var candidates = ParseSearch(searchHtml, title)
            .Where(candidate => candidate.Score >= 0.45)
            .Take(Math.Clamp(_options.MaxCandidates, 1, 50))
            .ToArray();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DelayAsync(cancellationToken);
            var detailUrl = BuildUrl(candidate.Path);
            var detailBytes = await _client.GetBytesAsync(
                detailUrl, Math.Max(1, _options.MaxResponseBytes), cancellationToken);
            if (LooksLikeBotChallenge(detailBytes)) return null;

            var record = ParseDetails(
                Encoding.UTF8.GetString(detailBytes), group, detailUrl, candidate.Title);
            if (record is not null) return record;
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        var milliseconds = Math.Clamp(_options.RequestDelayMilliseconds, 0, 60_000);
        if (milliseconds > 0)
            await Task.Delay(milliseconds, cancellationToken);
    }

    private string BuildUrl(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute))
            return absolute.ToString();
        return new Uri(new Uri(_baseUrl + "/"), path.TrimStart('/')).ToString();
    }

    private static IReadOnlyList<SearchCandidate> ParseSearch(string html, string requestedTitle)
    {
        var candidates = new List<SearchCandidate>();
        foreach (Match match in AnchorRegex.Matches(html))
        {
            var attrs = match.Groups["attrs"].Value;
            var path = Attribute(attrs, "href");
            if (string.IsNullOrWhiteSpace(path) || !IsGamePath(path)) continue;

            var title = CleanText(match.Groups["body"].Value);
            if (title.Length == 0)
                title = CleanText(Attribute(attrs, "title") ?? string.Empty);
            if (title.Length == 0) continue;

            candidates.Add(new SearchCandidate(path, title, Similarity(requestedTitle, title)));
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static MetadataRecord? ParseDetails(
        string html,
        ReleaseGroup group,
        string sourceUrl,
        string? searchTitle = null)
    {
        var title = MetaContent(html, "og:title", "twitter:title") ??
                    HeadingRegex.Match(html).Groups["value"].Value;
        title = CleanTitle(title);
        if (title.Length == 0)
            title = CleanText(searchTitle ?? string.Empty);
        if (title.Length == 0) return null;
        if (Similarity(group.Title ?? string.Empty, title) < 0.45) return null;

        var year = TableField(html, "year of the first release", "first release", "release year", "year");
        var publisher = TableField(html, "publisher", "company");
        var developer = TableField(html, "developer", "author");
        var description = MetaContent(html, "description", "og:description") ??
                          TableField(html, "description", "comment", "plot");
        var artwork = ExtractArtworkUrl(html, sourceUrl);

        return new MetadataRecord(
            group.ReleaseKey,
            title,
            CleanNullable(year),
            CleanNullable(publisher ?? developer),
            CleanNullable(description),
            "hall-of-light",
            DateTimeOffset.UtcNow)
        {
            ArtworkUrl = artwork,
            ArtworkSourceUrl = artwork is null ? null : sourceUrl,
            ArtworkProvider = artwork is null ? null : "hall-of-light"
        };
    }

    private static string? ExtractArtworkUrl(string html, string sourceUrl)
    {
        var candidates = new List<(string Url, int Score)>();
        var openGraph = MetaContent(html, "og:image", "twitter:image");
        if (openGraph is not null)
            candidates.Add((openGraph, 80));

        foreach (Match match in ImageRegex.Matches(html))
        {
            var attrs = match.Groups["attrs"].Value;
            var raw = Attribute(attrs, "src") ?? Attribute(attrs, "data-src") ??
                      Attribute(attrs, "data-original");
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var context = string.Join(' ',
                Attribute(attrs, "alt"), Attribute(attrs, "class"),
                Attribute(attrs, "id"), Attribute(attrs, "title"));
            var normalizedContext = Normalize(context);
            var score = 10;
            if (ContainsAny(normalizedContext, "front", "cover", "box", "packaging")) score += 100;
            if (ContainsAny(normalizedContext, "screenshot", "screen", "gameplay")) score += 50;
            if (ContainsAny(normalizedContext, "logo", "avatar", "icon", "button")) score -= 100;
            candidates.Add((raw, score));
        }

        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            var url = ResolveUrl(candidate.Url, sourceUrl);
            if (url is not null) return url;
        }
        return null;
    }

    private static string? TableField(string html, params string[] names)
    {
        foreach (Match match in TableRowRegex.Matches(html))
        {
            var key = Normalize(CleanText(match.Groups["key"].Value));
            if (names.Any(name => key.Equals(Normalize(name), StringComparison.Ordinal)))
                return CleanText(match.Groups["value"].Value);
        }
        return null;
    }

    private static string? MetaContent(string html, params string[] names)
    {
        foreach (Match match in MetaRegex.Matches(html))
        {
            var attrs = match.Groups["attrs"].Value;
            var key = Attribute(attrs, "property") ?? Attribute(attrs, "name");
            if (key is null || !names.Any(name => key.Equals(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            var value = Attribute(attrs, "content");
            if (!string.IsNullOrWhiteSpace(value)) return CleanText(value);
        }
        return null;
    }

    private static string? ResolveUrl(string raw, string sourceUrl)
    {
        raw = WebUtility.HtmlDecode(raw.Trim());
        if (raw.StartsWith("//", StringComparison.Ordinal)) raw = "https:" + raw;
        if (!Uri.TryCreate(new Uri(sourceUrl), raw, out var resolved) ||
            resolved.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(resolved.Host))
            return null;
        return resolved.ToString();
    }

    private static bool IsGamePath(string path) =>
        path.StartsWith("/games/view/", StringComparison.OrdinalIgnoreCase) &&
        path.Length > "/games/view/".Length;

    private static bool LooksLikeBotChallenge(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Contains("Making sure you&#39;re not a bot", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Making sure you're not a bot", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("anubis_challenge", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("id=\"anubis-main\"", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Attribute(string attrs, string name)
    {
        foreach (Match match in AttributeRegex.Matches(attrs))
            if (match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return WebUtility.HtmlDecode(match.Groups["value"].Value);
        return null;
    }

    private static string CleanTitle(string? value)
    {
        var title = CleanText(value ?? string.Empty);
        foreach (var suffix in new[] { " | Hall of Light", " - Hall of Light", " — Hall of Light" })
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                title = title[..^suffix.Length].Trim();
        return title;
    }

    private static string CleanText(string value)
    {
        var withBreaks = Regex.Replace(value, "<\\s*br\\s*/?>", " ", RegexOptions.IgnoreCase);
        return WebUtility.HtmlDecode(TagRegex.Replace(withBreaks, " "))
            .Replace('\u00a0', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
    }

    private static string? CleanNullable(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : CleanText(value);

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.Ordinal));

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Equals(b, StringComparison.Ordinal)) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.9;
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return (double)at.Intersect(bt, StringComparer.Ordinal).Count() / Math.Max(at.Count, bt.Count);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var withoutMarks = new string(decomposed
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) !=
                        System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        return string.Join(' ', withoutMarks.ToLowerInvariant().Split(
            [' ', '\t', '\r', '\n', '-', '_', ':', '/', '(', ')', '[', ']', ',', '.', '\'', '"'],
            StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record SearchCandidate(string Path, string Title, double Score);
}

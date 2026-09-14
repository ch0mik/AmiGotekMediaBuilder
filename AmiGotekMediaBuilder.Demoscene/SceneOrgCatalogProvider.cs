using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Demoscene;

/// <summary>Searches the public scene.org archive for downloadable Amiga demo images.</summary>
public sealed class SceneOrgCatalogProvider : IDisposable
{
    private readonly string _baseUrl;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public SceneOrgCatalogProvider(string baseUrl = "https://files.scene.org", SafeHttpClient? client = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    /// <summary>
    /// scene.org has no bounded "latest Amiga demos" API, so this provider is
    /// intentionally title-search based. It searches both the root archive
    /// and party archive because the service indexes both under one endpoint.
    /// </summary>
    public async Task<IReadOnlyList<DemosceneProduction>> SearchAsync(
        string? search, int maxItems = 50, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(search)) return [];
        var limit = Math.Clamp(maxItems, 1, 100);
        var results = new List<DemosceneProduction>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 5 && results.Count < limit; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = $"{_baseUrl}/search/?q={Uri.EscapeDataString(search.Trim())}" +
                (page == 1 ? string.Empty : $"&page={page}");
            var bytes = await _client.GetBytesAsync(url, cancellationToken: cancellationToken);
            var pageResults = ParseSearchPage(bytes, _baseUrl);
            if (pageResults.Count == 0) break;
            foreach (var production in pageResults)
            {
                if (seen.Add(production.PouetId)) results.Add(production);
                if (results.Count == limit) break;
            }
        }
        return results;
    }

    public static IReadOnlyList<DemosceneProduction> ParseSearchPage(byte[] bytes, string baseUrl = "https://files.scene.org")
    {
        var html = WebUtility.HtmlDecode(Encoding.UTF8.GetString(bytes));
        var output = new List<DemosceneProduction>();
        var entries = ListItemRegex.Matches(html).Cast<Match>().ToArray();
        // Keep the parser usable with compact fixtures and future markup that
        // omits the file-list item wrapper.
        if (entries.Length == 0)
            entries = [new MatchWrapper(html).Match];
        foreach (var entry in entries)
        {
            var body = entry.Groups["body"].Success ? entry.Groups["body"].Value : entry.Value;
            var isAmigaEntry = entry.Groups["attrs"].Value.Contains("amiga", StringComparison.OrdinalIgnoreCase) ||
                               entries.Length == 1 && !entry.Groups["body"].Success;
            foreach (Match match in ViewLinkRegex.Matches(body))
            {
                var path = Uri.UnescapeDataString(match.Groups["path"].Value).TrimStart('/');
                if (!IsAmigaDemoPath(path, isAmigaEntry)) continue;
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrWhiteSpace(fileName)) continue;
                var downloadUrl = baseUrl.TrimEnd('/') + "/get/" + string.Join('/', path.Split('/')
                    .Select(Uri.EscapeDataString));
                var link = PouetCatalogProvider.ClassifyDownload(downloadUrl, fileName);
                if (link is null) continue;
                if (link.Format is not (DemosceneAssetFormat.Adf or DemosceneAssetFormat.Dsk or
                    DemosceneAssetFormat.Zip or DemosceneAssetFormat.Gzip)) continue;

                var title = Path.GetFileNameWithoutExtension(fileName).Replace('_', ' ').Trim();
                if (title.Length == 0) continue;
                var year = YearRegex.Match(path).Groups["year"].Value;
                var sourceUrl = baseUrl.TrimEnd('/') + "/view/" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
                output.Add(new DemosceneProduction(
                    StableId(path), title, null, year.Length == 0 ? null : year, "demo",
                    DemoscenePlatform.OcsEcs | DemoscenePlatform.Aga | DemoscenePlatform.PpcRtg,
                    sourceUrl, "scene.org archive", null, [link])
                {
                    Catalog = DemosceneCatalogs.SceneOrg
                });
            }
        }
        return output.GroupBy(item => item.PouetId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToArray();
    }

    private static bool IsAmigaDemoPath(string path, bool isAmigaEntry) =>
        path.StartsWith("mirrors/amigascne/groups/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("mirrors/amigascne/demos/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("parties/", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("amiga", StringComparison.OrdinalIgnoreCase) ||
        isAmigaEntry && (path.StartsWith("demos/", StringComparison.OrdinalIgnoreCase) ||
                          path.StartsWith("parties/", StringComparison.OrdinalIgnoreCase));

    private static string StableId(string path) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..16];

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static readonly Regex ViewLinkRegex = new(
        "href=[\\\"'](?:https?:)?//files\\.scene\\.org/view/(?<path>[^\\\"'#?]+)|href=[\\\"']/view/(?<path>[^\\\"'#?]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ListItemRegex = new(
        "<li\\b(?<attrs>[^>]*)>(?<body>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex YearRegex = new(@"(?:^|/)(?<year>19\d{2}|20\d{2})(?:/|$)", RegexOptions.Compiled);

    private sealed class MatchWrapper(string value)
    {
        public Match Match { get; } = Regex.Match(value, ".*", RegexOptions.Singleline);
    }
}

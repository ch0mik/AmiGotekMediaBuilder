using System.Text.Json;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Public, keyless artwork fallback for ordinary Amiga games.  Wikipedia's
/// search API is used only when the Online switch is enabled; it is never
/// consulted by an offline build.  The provider deliberately requires an
/// Amiga/video-game relevance signal so a similarly named person or film does
/// not become game artwork.
/// </summary>
public sealed class WikipediaProvider : IAsyncMetadataProvider, IDisposable
{
    private readonly string _baseUrl;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public WikipediaProvider(string baseUrl = "https://en.wikipedia.org", SafeHttpClient? client = null)
    {
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://en.wikipedia.org" : baseUrl.TrimEnd('/');
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public string Id => "wikipedia";

    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        var title = group.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;

        var query = Uri.EscapeDataString($"\"{title}\" Amiga video game");
        var url = $"{_baseUrl}/w/api.php?action=query&format=json&formatversion=2" +
                  $"&generator=search&gsrsearch={query}&gsrlimit=8" +
                  "&prop=extracts%7Cpageimages%7Cinfo&exintro=1&explaintext=1" +
                  "&piprop=original%7Cthumbnail&pithumbsize=1200&inprop=url";
        var bytes = await _client.GetBytesAsync(url, 2_000_000, cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("query", out var queryElement) ||
                !queryElement.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Array)
                return null;

            JsonElement best = default;
            var bestScore = 0d;
            foreach (var page in pages.EnumerateArray())
            {
                if (page.ValueKind != JsonValueKind.Object) continue;
                var pageTitle = StringValue(page, "title") ?? string.Empty;
                var extract = StringValue(page, "extract") ?? string.Empty;
                var haystack = (pageTitle + " " + extract).ToLowerInvariant();
                if (!haystack.Contains("amiga", StringComparison.Ordinal) ||
                    !ContainsGameSignal(haystack)) continue;

                var score = Similarity(title, pageTitle);
                if (haystack.Contains("video game", StringComparison.Ordinal)) score += 0.15;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = page;
                }
            }

            if (best.ValueKind != JsonValueKind.Object || bestScore < 0.45) return null;
            var canonical = StringValue(best, "title") ?? title;
            var artwork = ArtworkUrl(best);
            var source = StringValue(best, "fullurl");
            return new MetadataRecord(group.ReleaseKey, canonical, null, null,
                StringValue(best, "extract"), Id, DateTimeOffset.UtcNow)
            {
                ArtworkUrl = artwork,
                ArtworkSourceUrl = artwork is null ? null : source,
                ArtworkProvider = artwork is null ? null : Id
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private static bool ContainsGameSignal(string haystack) =>
        haystack.Contains("video game", StringComparison.Ordinal) ||
        haystack.Contains("computer game", StringComparison.Ordinal) ||
        haystack.Contains("amiga game", StringComparison.Ordinal) ||
        (haystack.Contains("game", StringComparison.Ordinal) &&
         (haystack.Contains("developed", StringComparison.Ordinal) ||
          haystack.Contains("released", StringComparison.Ordinal) ||
          haystack.Contains("adventure game", StringComparison.Ordinal)));

    private static string? StringValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ArtworkUrl(JsonElement page)
    {
        foreach (var propertyName in new[] { "original", "thumbnail" })
        {
            if (!page.TryGetProperty(propertyName, out var image) || image.ValueKind != JsonValueKind.Object)
                continue;
            var value = StringValue(image, "source");
            if (NormalizeUrl(value) is { } normalized) return normalized;
        }
        return null;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https" && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString()
            : null;
    }

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Equals(b, StringComparison.Ordinal)) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.85;

        var leftTokens = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var rightTokens = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var overlap = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        return (double)overlap / Math.Max(leftTokens.Count, rightTokens.Count);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.ToLowerInvariant().Split(
            [' ', '\t', '\r', '\n', '-', '_', ':', '/', '(', ')', '[', ']', ',', '.'],
            StringSplitOptions.RemoveEmptyEntries));
}

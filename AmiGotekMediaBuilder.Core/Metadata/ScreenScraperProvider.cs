using System.Security.Cryptography;
using System.Text.Json;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

public sealed record ScreenScraperOptions(
    string DeveloperId,
    string DeveloperPassword,
    string? UserId = null,
    string? UserPassword = null,
    string BaseUrl = "https://api.screenscraper.fr/api2",
    int AmigaSystemId = 64)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(DeveloperId) && !string.IsNullOrWhiteSpace(DeveloperPassword);

    public static ScreenScraperOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("SCREENSCRAPER_DEV_ID") ?? string.Empty,
        Environment.GetEnvironmentVariable("SCREENSCRAPER_DEV_PASSWORD") ?? string.Empty,
        Environment.GetEnvironmentVariable("SCREENSCRAPER_USER"),
        Environment.GetEnvironmentVariable("SCREENSCRAPER_PASSWORD"),
        Environment.GetEnvironmentVariable("SCREENSCRAPER_BASE_URL") ??
        "https://api.screenscraper.fr/api2");
}

/// <summary>
/// ScreenScraper API v2 adapter. It identifies the first disk by SHA-1 and
/// MD5 whenever the source is available and falls back to a strict title
/// search only when a file identity cannot be calculated.
/// </summary>
public sealed class ScreenScraperProvider : IAsyncMetadataProvider, IDisposable
{
    private readonly ScreenScraperOptions _options;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public ScreenScraperProvider(ScreenScraperOptions options, SafeHttpClient? client = null)
    {
        _options = options;
        _client = client ?? new SafeHttpClient(minimumRequestInterval: TimeSpan.FromMilliseconds(1100));
        _ownsClient = client is null;
    }

    public string Id => "screenscraper";

    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured || group.IsDemoscene) return null;
        var source = group.Records.FirstOrDefault(record =>
                         !string.IsNullOrWhiteSpace(record.SourcePath) && File.Exists(record.SourcePath))
                     ?? group.Records.FirstOrDefault();

        var query = Credentials();
        query["output"] = "json";
        query["systemeid"] = _options.AmigaSystemId.ToString();
        if (source?.SourcePath is { Length: > 0 } path && File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, cancellationToken));
            stream.Position = 0;
            var md5 = Convert.ToHexString(await MD5.HashDataAsync(stream, cancellationToken));
            query["sha1"] = sha1;
            query["md5"] = md5;
            query["romnom"] = source.SourceFilename ?? Path.GetFileName(path);
            query["romtaille"] = (source.SourceSize ?? new FileInfo(path).Length).ToString();
            query["romtype"] = "rom";
            return Parse(await GetJson("jeuInfos.php", query, cancellationToken), group, requireExactTitle: false);
        }

        if (string.IsNullOrWhiteSpace(group.Title)) return null;
        query["recherche"] = group.Title;
        return Parse(await GetJson("jeuRecherche.php", query, cancellationToken), group, requireExactTitle: true);
    }

    private Dictionary<string, string> Credentials()
    {
        var result = new Dictionary<string, string>
        {
            ["devid"] = _options.DeveloperId,
            ["devpassword"] = _options.DeveloperPassword,
            ["softname"] = "AmiGotekMediaBuilder"
        };
        if (!string.IsNullOrWhiteSpace(_options.UserId)) result["ssid"] = _options.UserId;
        if (!string.IsNullOrWhiteSpace(_options.UserPassword)) result["sspassword"] = _options.UserPassword;
        return result;
    }

    private async Task<byte[]> GetJson(string endpoint, Dictionary<string, string> query,
        CancellationToken cancellationToken)
    {
        var parameters = string.Join("&", query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return await _client.GetBytesAsync(
            $"{_options.BaseUrl.TrimEnd('/')}/{endpoint}?{parameters}", 5_000_000, cancellationToken);
    }

    internal static MetadataRecord? Parse(byte[] json, ReleaseGroup group, bool requireExactTitle)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var games = FindGames(document.RootElement).ToArray();
            var requested = GameTitleMatcher.Normalize(group.Title ?? string.Empty);
            var ranked = games.Select(game => (Game: game,
                    Score: GameTitleMatcher.Score(group.Title ?? string.Empty, Text(game, "nom") ?? Name(game))))
                .OrderByDescending(item => item.Score);
            var selected = ranked.FirstOrDefault();
            if (selected.Game.ValueKind != JsonValueKind.Object) return null;
            if (requireExactTitle && selected.Score < 0.91) return null;

            var title = RegionalText(selected.Game, "noms", "nom", "nom_en", "nom_wor", "nom_eu", "nom_ss")
                        ?? Text(selected.Game, "nom") ?? group.Title;
            if (string.IsNullOrWhiteSpace(title)) return null;
            var date = RegionalText(selected.Game, "dates", "date", "date_wor", "date_eu", "date_us");
            var year = date is { Length: >= 4 } ? date[..4] : null;
            var description = RegionalText(selected.Game, "synopsis", "synopsis", "synopsis_en", "synopsis_wor");
            var publisher = Text(selected.Game, "editeur") ?? Text(selected.Game, "developpeur");
            var artwork = FindArtwork(selected.Game);
            var id = Text(selected.Game, "id");
            return new MetadataRecord(group.ReleaseKey, title, year, publisher, description,
                "screenscraper", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = artwork,
                ArtworkProvider = artwork is null ? null : "screenscraper",
                ArtworkSourceUrl = id is null ? null : $"https://www.screenscraper.fr/gameinfos.php?gameid={id}"
            };
        }
        catch (JsonException) { return null; }
    }

    private static IEnumerable<JsonElement> FindGames(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("jeu", out var game) && game.ValueKind == JsonValueKind.Object)
                yield return game;
            if (element.TryGetProperty("jeux", out var games))
            {
                if (games.ValueKind == JsonValueKind.Array)
                    foreach (var gameItem in games.EnumerateArray()) if (gameItem.ValueKind == JsonValueKind.Object) yield return gameItem;
                else if (games.ValueKind == JsonValueKind.Object)
                    foreach (var gameProperty in games.EnumerateObject()) if (gameProperty.Value.ValueKind == JsonValueKind.Object) yield return gameProperty.Value;
            }
            foreach (var property in element.EnumerateObject())
                foreach (var nested in FindGames(property.Value)) yield return nested;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var nested in FindGames(item)) yield return nested;
    }

    private static string Name(JsonElement game) =>
        RegionalText(game, "noms", "nom", "nom_en", "nom_wor", "nom_eu", "nom_ss") ?? string.Empty;

    private static string? RegionalText(JsonElement item, string container, params string[] priorities)
    {
        if (!item.TryGetProperty(container, out var value)) return null;
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in priorities)
            if (Text(value, name) is { Length: > 0 } text) return text;
        return value.EnumerateObject().Select(property => Scalar(property.Value))
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
    }

    private static string? FindArtwork(JsonElement game)
    {
        var priorities = new[]
        {
            "media_boitier_2d_eu", "media_boitier_2d_wor", "media_boitier_2d_us",
            "media_boitier_2d_ss", "media_screenshot"
        };
        foreach (var name in priorities)
            if (FindProperty(game, name) is { } candidate && IsHttp(candidate)) return candidate;
        return null;
    }

    private static string? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return Scalar(property.Value);
                if (FindProperty(property.Value, name) is { } nested) return nested;
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (FindProperty(item, name) is { } nested) return nested;
        return null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? Scalar(value) : null;
    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.ToString(), _ => null
    };
    private static bool IsHttp(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                                                 uri.Scheme is "http" or "https";
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

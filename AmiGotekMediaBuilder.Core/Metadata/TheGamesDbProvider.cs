using System.Text.Json;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Metadata;

public sealed record TheGamesDbOptions(
    string ApiKey,
    string BaseUrl = "https://api.thegamesdb.net/v1",
    int AmigaPlatformId = 4911);

/// <summary>Strict-title TheGamesDB v1 adapter, enabled only when an API key is configured.</summary>
public sealed class TheGamesDbProvider : IAsyncMetadataProvider, IDisposable
{
    private readonly TheGamesDbOptions _options;
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public TheGamesDbProvider(TheGamesDbOptions options, SafeHttpClient? client = null)
    {
        _options = options;
        _client = client ?? new SafeHttpClient(minimumRequestInterval: TimeSpan.FromMilliseconds(250));
        _ownsClient = client is null;
    }

    public string Id => "thegamesdb";

    public async Task<MetadataRecord?> ResolveAsync(
        ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        if (group.IsDemoscene || string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(group.Title)) return null;
        var url = $"{_options.BaseUrl.TrimEnd('/')}/Games/ByGameName" +
                  $"?apikey={Uri.EscapeDataString(_options.ApiKey)}" +
                  $"&name={Uri.EscapeDataString(group.Title)}" +
                  $"&filter%5Bplatform%5D={_options.AmigaPlatformId}" +
                  "&include=boxart";
        var bytes = await _client.GetBytesAsync(url, 5_000_000, cancellationToken);
        return Parse(bytes, group);
    }

    internal static MetadataRecord? Parse(byte[] bytes, ReleaseGroup group)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
                return null;
            var best = games.EnumerateArray()
                .Where(game => game.ValueKind == JsonValueKind.Object)
                .Select(game => (Game: game, Score: GameTitleMatcher.Score(
                    group.Title ?? string.Empty, Text(game, "game_title") ?? Text(game, "name") ?? string.Empty)))
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();
            if (best.Game.ValueKind != JsonValueKind.Object || best.Score < 0.91) return null;

            var id = Text(best.Game, "id");
            var title = Text(best.Game, "game_title") ?? Text(best.Game, "name") ?? group.Title!;
            var release = Text(best.Game, "release_date");
            var year = release is { Length: >= 4 } ? release[..4] : null;
            var publisher = FirstText(best.Game, "publishers") ?? Text(best.Game, "publisher");
            var description = Text(best.Game, "overview");
            var artwork = id is null ? null : BoxArt(document.RootElement, id);
            return new MetadataRecord(group.ReleaseKey, title, year, publisher, description,
                "thegamesdb", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = artwork,
                ArtworkProvider = artwork is null ? null : "thegamesdb",
                ArtworkSourceUrl = id is null ? null : $"https://thegamesdb.net/game.php?id={id}"
            };
        }
        catch (JsonException) { return null; }
    }

    private static string? BoxArt(JsonElement root, string id)
    {
        if (!root.TryGetProperty("include", out var include) ||
            !include.TryGetProperty("boxart", out var boxart)) return null;
        var baseUrl = boxart.TryGetProperty("base_url", out var bases)
            ? Text(bases, "original") ?? Text(bases, "large") ?? Text(bases, "medium") : null;
        if (baseUrl is null || !boxart.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(id, out var images) ||
            images.ValueKind != JsonValueKind.Array) return null;
        var selected = images.EnumerateArray()
            .Where(image => image.ValueKind == JsonValueKind.Object)
            .OrderByDescending(image => string.Equals(Text(image, "side"), "front", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        var filename = Text(selected, "filename");
        if (filename is null) return null;
        return baseUrl.TrimEnd('/') + "/" + filename.TrimStart('/');
    }

    private static string? Text(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.ToString(), _ => null
        };
    }

    private static string? FirstText(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String) return entry.GetString();
            if (entry.ValueKind == JsonValueKind.Object &&
                (Text(entry, "name") ?? Text(entry, "publisher")) is { } text) return text;
        }
        return null;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Creates the credential-free online provider chain. Public endpoints are
/// deliberately part of the application defaults; no API key, password or
/// token is read, stored or shipped with the source code.
/// </summary>
public static class OnlineProviderFactory
{
    public static IReadOnlyList<IAsyncMetadataProvider> CreateDefault(
        bool includeDemoscene = false, string? gameBaseDatabasePath = null)
    {
        var providers = new List<IAsyncMetadataProvider>
        {
            // Hash lookup works without an API key on the public Hasheous host.
            new HasheousProvider(new OnlineProviderOptions(
                "hasheous", "https://hasheous.org", "/Lookup/ByHash/sha256/{sha256}")),
            // The public Playmatch instance exposes read-only identification.
            new PlaymatchProvider(new OnlineProviderOptions(
                "playmatch", "https://playmatch.retrorealm.dev", "/api/v1/identify/ids?sha256={sha256}")),
            // OpenRetro is a public, Amiga-focused source with cover and
            // screenshot artwork; it does not require credentials.
            new OpenRetroProvider(Environment.GetEnvironmentVariable("OPENRETRO_BASE_URL") ?? "https://openretro.org"),
            // GameBase is local-only. When no path is configured this provider
            // is a harmless no-op, while configured SQLite files are searched
            // before the generic Wikipedia fallback.
            new GameBaseProvider(gameBaseDatabasePath),
            // Hall of Light is an Amiga-focused public game catalogue. The
            // provider is game-only and returns no result for demoscene groups.
            new HallOfLightProvider(new HallOfLightProviderOptions(
                Environment.GetEnvironmentVariable("HALL_OF_LIGHT_BASE_URL") ?? "https://amiga.abime.net",
                Environment.GetEnvironmentVariable("HALL_OF_LIGHT_SEARCH_PATH") ??
                "/games/list/?gamename={title}",
                MaxResponseBytes: 2_000_000,
                MaxCandidates: 5,
                RequestDelayMilliseconds: 750)),
            // Keyless title search and artwork fallback.
            new WikipediaProvider()
        };

        // Pouët is public and specifically covers demoscene productions. It is
        // only added when a caller explicitly requests that separate pipeline;
        // the normal game chain stays isolated from demoscene artwork.
        if (includeDemoscene)
        {
            var pouetBase = Environment.GetEnvironmentVariable("POUET_BASE_URL");
            providers.Add(new PouetProvider(new OnlineProviderOptions(
                "pouet", string.IsNullOrWhiteSpace(pouetBase) ? "https://www.pouet.net" : pouetBase,
                Environment.GetEnvironmentVariable("POUET_SEARCH_PATH") ??
                "/search.php?type=prod&what={title}")));
        }

        return providers;
    }

    public static IReadOnlyList<string> DefaultProviderIds { get; } =
        ["hasheous", "playmatch", "openretro", "gamebase", "hall-of-light", "wikipedia"];
}

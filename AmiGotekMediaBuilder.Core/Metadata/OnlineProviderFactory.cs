namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Creates the game-scraping pipeline.  The ordering follows the same
/// "best identity first, fill the gaps afterwards" model used by ARGDB and
/// Skyscraper: local/hash-aware sources precede title-only sources.
/// </summary>
public static class OnlineProviderFactory
{
    public static IReadOnlyList<IAsyncMetadataProvider> CreateDefault(
        bool includeDemoscene = false, string? gameBaseDatabasePath = null)
    {
        var providers = new List<IAsyncMetadataProvider>
        {
            new GameBaseProvider(gameBaseDatabasePath),
        };

        var screenScraper = ScreenScraperOptions.FromEnvironment();
        if (screenScraper.IsConfigured)
            providers.Add(new ScreenScraperProvider(screenScraper));

        providers.Add(new OpenRetroProvider(
            Environment.GetEnvironmentVariable("OPENRETRO_BASE_URL") ?? "https://openretro.org"));

        var theGamesDbKey = Environment.GetEnvironmentVariable("THEGAMESDB_API_KEY");
        if (!string.IsNullOrWhiteSpace(theGamesDbKey))
            providers.Add(new TheGamesDbProvider(new TheGamesDbOptions(ApiKey: theGamesDbKey)));

        // A keyless, artwork-only final source.  The actual image is validated
        // by ArtworkDownloader; a missing CDN object therefore becomes a miss
        // and never poisons the cache.
        providers.Add(new LibretroAmigaArtworkProvider());

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

    public static IReadOnlyList<string> DefaultProviderIds
    {
        get
        {
            var ids = new List<string> { "gamebase" };
            if (ScreenScraperOptions.FromEnvironment().IsConfigured) ids.Add("screenscraper");
            ids.Add("openretro");
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("THEGAMESDB_API_KEY")))
                ids.Add("thegamesdb");
            ids.Add("libretro");
            return ids;
        }
    }
}

using System.Net;
using System.Net.Http;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class OnlineProviderTests
{
    [Fact]
    public void DefaultChainUsesDedicatedAmigaGameSources()
    {
        var ids = OnlineProviderFactory.CreateDefault().Select(provider => provider.Id).ToArray();
        Assert.Equal("gamebase", ids[0]);
        Assert.Contains("openretro", ids);
        Assert.Equal("libretro", ids[^1]);
        Assert.DoesNotContain("hasheous", ids);
        Assert.DoesNotContain("playmatch", ids);
        Assert.DoesNotContain("hall-of-light", ids);
        Assert.DoesNotContain("wikipedia", ids);
    }

    [Fact]
    public void PouetIsOptInForRegularGameMetadata()
    {
        Assert.DoesNotContain(OnlineProviderFactory.CreateDefault(), p => p.Id.Equals("pouet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(OnlineProviderFactory.CreateDefault(includeDemoscene: true), p => p.Id.Equals("pouet", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RegularEnrichmentStripsPouetArtworkFromAnOldCacheEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var group = new ReleaseGroup { ReleaseKey = "old-pouet", Title = "Canonical Game", Folder = "Canonical-FFAS", Extension = "adf" };
            var cached = new MetadataRecord(group.ReleaseKey, "Wrong Atlantis", "1990", null, "Pouët demo",
                "pouet", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://images.example/demo.jpg",
                ArtworkSourceUrl = "https://www.pouet.net/prod.php?which=7",
                ArtworkProvider = "pouet"
            };
            var cache = new MetadataCache(root);
            cache.Write(cached);

            var records = await new HybridMetadataEnricher([]).EnrichAsync(
                [group], root, Path.Combine(root, "nfo"));

            var result = Assert.Single(records);
            Assert.Null(result.ArtworkUrl);
            Assert.Null(result.ArtworkProvider);
            Assert.Null(result.ArtworkPath);
            Assert.Equal("Canonical Game", result.Title);
            Assert.Equal("offline-filename", result.Provider);
            Assert.Null(result.Description);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OfflineEnrichmentDoesNotReusePouetDescription()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-offline-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var group = new ReleaseGroup
            {
                ReleaseKey = "offline-pouet", Title = "Fate Of Atlantis", Folder = "FateOfAtlantis-FFAS", Extension = "adf"
            };
            new MetadataCache(root).Write(new MetadataRecord(group.ReleaseKey, "Atlantis", "1993", "Albion Crew",
                "Pouët demo", "pouet", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://images.example/demo.jpg", ArtworkProvider = "pouet"
            });

            var records = new OfflineEnricher().Enrich([group], root, Path.Combine(root, "nfo"));

            var result = Assert.Single(records);
            Assert.Equal("Fate Of Atlantis", result.Title);
            Assert.Equal("offline-filename", result.Provider);
            Assert.Null(result.Description);
            Assert.Null(result.ArtworkUrl);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OfflineEnrichmentReplacesStaleDemosceneArtworkWithGameFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-offline-stale-artwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            var nfo = Path.Combine(root, "assets", "nfo");
            var originalArtwork = Path.Combine(root, "assets", "artwork-original");
            var processedArtwork = Path.Combine(root, "assets", "artwork-processed");
            Directory.CreateDirectory(originalArtwork);
            Directory.CreateDirectory(processedArtwork);
            var basename = "FateOfAtlantis-FFAS";
            var staleOriginal = Path.Combine(originalArtwork, basename + ".jpg");
            var staleProcessed = Path.Combine(processedArtwork, basename + ".jpg");
            File.WriteAllBytes(staleOriginal, [0xff, 0xd8, 0xaa, 0xd9]);
            File.WriteAllBytes(staleProcessed, [0xff, 0xd8, 0xaa, 0xd9]);
            File.WriteAllText(staleOriginal + ".source.json", "{\"catalog\":\"pouet\"}");

            var group = new ReleaseGroup
            {
                ReleaseKey = "offline-stale-artwork", Title = "Fate Of Atlantis", Folder = basename, Extension = "adf"
            };
            var result = Assert.Single(new OfflineEnricher().Enrich(
                [group], Path.Combine(root, "catalog"), nfo));

            Assert.Null(result.ArtworkProvider);
            Assert.Null(result.ArtworkPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OnlineEnrichmentRefreshesStaleDemosceneCacheForGames()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-online-stale-artwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cacheDirectory = Path.Combine(root, "catalog");
            var nfo = Path.Combine(root, "assets", "nfo");
            var stale = new MetadataRecord("refresh-artwork", "Old demo", null, null,
                "Pouët", "pouet", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://content.pouet.net/old.jpg",
                ArtworkProvider = "pouet"
            };
            new MetadataCache(cacheDirectory).Write(stale);

            var localArtwork = Path.Combine(root, "provider-cover.png");
            await File.WriteAllBytesAsync(localArtwork, [137, 80, 78, 71, 13, 10, 26, 10]);
            var provider = new CountingArtworkProvider(localArtwork);
            var group = new ReleaseGroup
            {
                ReleaseKey = "refresh-artwork", Title = "Real Game", Folder = "RealGame", Extension = "adf"
            };

            var result = Assert.Single(await new HybridMetadataEnricher([provider]).EnrichAsync(
                [group], cacheDirectory, nfo));

            Assert.Equal(1, provider.Calls);
            Assert.Equal("test-provider", result.Provider);
            Assert.Equal("test-provider", result.ArtworkProvider);
            Assert.Contains("Real metadata", result.Description);
            Assert.NotNull(result.ArtworkPath);
            Assert.True(File.Exists(result.ArtworkPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenRetroParsesAmigaGameAndFrontArtwork()
    {
        const string hash = "63dd0bbbefeabd33d23e1f825f3d2ea360e15fd4";
        var handler = new RoutingHandler(
            _ => "<a href=\"/amiga/fire-and-brimstone\"><img src=\"/image/x\">Fire and Brimstone <span>Amiga</span></a>",
            _ => $"<table><tr><td>game_name</td><td>Fire and Brimstone</td></tr>" +
                 $"<tr><td>front_sha1</td><td><a href=\"/image/{hash}\">image</a></td></tr>" +
                 "<tr><td>publisher</td><td>Firebird</td></tr><tr><td>year</td><td>1990</td></tr>" +
                 "<tr><td>__long_description</td><td>A Norse adventure.</td></tr></table>");
        using var client = new SafeHttpClient(handler: handler);
        using var provider = new OpenRetroProvider("https://example.test", client);
        var group = new ReleaseGroup { ReleaseKey = "fire", Title = "Fire and Brimstone", Extension = "adf" };

        var result = await provider.ResolveAsync(group);

        Assert.NotNull(result);
        Assert.Equal("Fire and Brimstone", result!.Title);
        Assert.Equal("1990", result.Year);
        Assert.Equal("Firebird", result.Publisher);
        Assert.Equal("openretro", result.Provider);
        Assert.Equal($"https://example.test/image/{hash}?s=512", result.ArtworkUrl);
    }

    [Fact]
    public async Task ProviderChainUsesLaterArtworkMatchWhenFirstOnlyHasText()
    {
        var group = new ReleaseGroup { ReleaseKey = "example", Title = "Example Game", Extension = "adf" };
        var first = new StubProvider(new MetadataRecord("example", "Example Game", "1992", null, "text", "first", DateTimeOffset.UtcNow));
        var second = new StubProvider(new MetadataRecord("example", "Example Game", "1992", null, "text", "second", DateTimeOffset.UtcNow)
        {
            ArtworkUrl = "https://images.example/example.jpg",
            ArtworkProvider = "second"
        });

        var result = await new OnlineMetadataChain(new IAsyncMetadataProvider[] { first, second }).ResolveAsync(group);

        Assert.Equal("first", result!.Provider);
        Assert.Equal("second", result.ArtworkProvider);
        Assert.Equal("https://images.example/example.jpg", result.ArtworkUrl);
    }

    [Fact]
    public async Task ProviderChainReportsEveryProviderTriedUntilArtworkIsFound()
    {
        var group = new ReleaseGroup { ReleaseKey = "example", Title = "Example Game", Extension = "adf" };
        var first = new StubProvider(new MetadataRecord("example", "Example Game", null, null, null, "first", DateTimeOffset.UtcNow));
        var second = new StubProvider(new MetadataRecord("example", "Example Game", null, null, null, "second", DateTimeOffset.UtcNow)
        {
            ArtworkUrl = "https://images.example/example.jpg", ArtworkProvider = "second"
        });
        var third = new StubProvider(new MetadataRecord("example", "Example Game", "1992", "Acme", null,
            "third", DateTimeOffset.UtcNow));
        var reported = new List<string>();

        await new OnlineMetadataChain([first, second, third]).ResolveAsync(group,
            providerCompleted: (provider, result) => reported.Add($"{provider}:{(result is null ? "miss" : "match")}"));

        Assert.Equal(["first:match", "second:match", "third:match"], reported);
    }

    [Fact]
    public async Task ArtworkFailureFallsThroughToNextProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-fallthrough-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var invalid = Path.Combine(root, "invalid.jpg");
            var valid = Path.Combine(root, "valid.png");
            await File.WriteAllTextAsync(invalid, "not an image");
            await File.WriteAllBytesAsync(valid, [137, 80, 78, 71, 13, 10, 26, 10]);
            var group = new ReleaseGroup { ReleaseKey = "fallback", Title = "Fallback Game", Extension = "adf" };
            var first = new CountingArtworkProvider(invalid, "broken");
            var second = new CountingArtworkProvider(valid, "working");

            var result = Assert.Single(await new HybridMetadataEnricher([first, second]).EnrichAsync(
                [group], Path.Combine(root, "metadata"), Path.Combine(root, "assets", "nfo")));

            Assert.Equal("working", result.ArtworkProvider);
            Assert.NotNull(result.ArtworkPath);
            Assert.True(File.Exists(result.ArtworkPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemovedProviderCacheIsReplacedByNewValidatedArtwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-retired-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var metadataDirectory = Path.Combine(root, "catalog");
            var nfoDirectory = Path.Combine(root, "assets", "nfo");
            var originalDirectory = Path.Combine(root, "assets", "artwork-original");
            Directory.CreateDirectory(originalDirectory);
            var oldArtwork = Path.Combine(originalDirectory, "Cache Game.jpg");
            await File.WriteAllBytesAsync(oldArtwork, [0xff, 0xd8, 0xff]);
            await File.WriteAllTextAsync(oldArtwork + ".source.json", "{\"provider\":\"wikipedia\"}");
            new MetadataCache(metadataDirectory).Write(new MetadataRecord(
                "cache-game", "Wrong cached title", null, null, null, "wikipedia", DateTimeOffset.UtcNow)
            {
                ArtworkPath = oldArtwork, ArtworkProvider = "wikipedia"
            });
            var replacement = Path.Combine(root, "replacement.png");
            await File.WriteAllBytesAsync(replacement, [137, 80, 78, 71, 13, 10, 26, 10]);
            var group = new ReleaseGroup { ReleaseKey = "cache-game", Title = "Cache Game", Extension = "adf" };

            var result = Assert.Single(await new HybridMetadataEnricher([
                new CountingArtworkProvider(replacement, "new-source")
            ]).EnrichAsync([group], metadataDirectory, nfoDirectory));

            Assert.Equal("new-source", result.Provider);
            Assert.Equal("new-source", result.ArtworkProvider);
            Assert.EndsWith(".png", result.ArtworkPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(oldArtwork + ".superseded"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScreenScraperUsesFileHashesAndParsesAmigaCover()
    {
        var root = Path.Combine(Path.GetTempPath(), "screenscraper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var disk = Path.Combine(root, "Lotus.adf");
            await File.WriteAllBytesAsync(disk, [1, 2, 3, 4]);
            Uri? requested = null;
            const string json = """
                {"response":{"jeu":{"id":"42","nom":"Lotus Esprit Turbo Challenge",
                "noms":{"nom_en":"Lotus Esprit Turbo Challenge"},"dates":{"date_wor":"1990-01-01"},
                "editeur":"Gremlin","synopsis":{"synopsis_en":"A racing game."},
                "medias":{"media_boitiers_2d":{"media_boitier_2d_eu":"https://img.example/lotus.png"}}}}}
                """;
            using var client = new SafeHttpClient(handler: new CapturingJsonHandler(json, uri => requested = uri));
            using var provider = new ScreenScraperProvider(new ScreenScraperOptions(
                "dev", "secret", BaseUrl: "https://example.test/api2"), client);
            var group = new ReleaseGroup { ReleaseKey = "lotus", Title = "Lotus Esprit Turbo Challenge", Extension = "adf" };
            group.Records.Add(new ParsedRecord { SourceFilename = "Lotus.adf", SourcePath = disk,
                SourceSize = 4, Extension = "adf" });

            var result = await provider.ResolveAsync(group);

            Assert.NotNull(result);
            Assert.Equal("1990", result!.Year);
            Assert.Equal("https://img.example/lotus.png", result.ArtworkUrl);
            Assert.Contains("sha1=", requested!.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("md5=", requested.Query, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("systemeid=64", requested.Query, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheGamesDbRejectsLooseTitleAndParsesFrontBoxArt()
    {
        const string json = """
            {"data":{"games":[{"id":7,"game_title":"The Settlers","release_date":"1993-01-01",
            "overview":"Build a settlement.","publishers":["Blue Byte"]}]},
            "include":{"boxart":{"base_url":{"original":"https://cdn.example/original"},
            "data":{"7":[{"side":"front","filename":"boxart/7.jpg"}]}}}}
            """;
        using var client = new SafeHttpClient(handler: new JsonHandler(json));
        using var provider = new TheGamesDbProvider(new TheGamesDbOptions("key", "https://example.test/v1"), client);
        var group = new ReleaseGroup { ReleaseKey = "settlers", Title = "Settlers, The", Extension = "adf" };

        var result = await provider.ResolveAsync(group);

        Assert.NotNull(result);
        Assert.Equal("Blue Byte", result!.Publisher);
        Assert.Equal("https://cdn.example/original/boxart/7.jpg", result.ArtworkUrl);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
    }

    private sealed class CapturingJsonHandler(string json, Action<Uri> capture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            capture(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class RoutingHandler(
        Func<Uri, string> search, Func<Uri, string> detail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri?.AbsolutePath.EndsWith("/edit", StringComparison.Ordinal) == true
                ? detail(request.RequestUri!) : search(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
        }
    }

    private sealed class StubProvider(MetadataRecord result) : IAsyncMetadataProvider
    {
        public string Id => result.Provider;
        public Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default) => Task.FromResult<MetadataRecord?>(result);
    }

    private sealed class CountingArtworkProvider(string artworkPath, string id = "test-provider") : IAsyncMetadataProvider
    {
        public string Id => id;
        public int Calls { get; private set; }

        public Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<MetadataRecord?>(new MetadataRecord(
                group.ReleaseKey, "Real Game", "1992", "Acme", "Real metadata",
                Id, DateTimeOffset.UtcNow)
            {
                ArtworkPath = artworkPath,
                ArtworkProvider = Id
            });
        }
    }
}

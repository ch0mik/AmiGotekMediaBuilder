using System.Net;
using System.Net.Http;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class OnlineProviderTests
{
    [Fact]
    public void DefaultChainContainsOnlyCredentialFreeProviders()
    {
        Assert.Equal(
            new[] { "hasheous", "playmatch", "openretro", "gamebase", "hall-of-light", "wikipedia" },
            OnlineProviderFactory.CreateDefault().Select(provider => provider.Id));
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
            Assert.Equal(DefaultArtworkService.ProviderId, result.ArtworkProvider);
            Assert.True(result.ArtworkPath is not null && File.Exists(result.ArtworkPath));
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

            Assert.Equal(DefaultArtworkService.ProviderId, result.ArtworkProvider);
            Assert.NotNull(result.ArtworkPath);
            Assert.True(File.Exists(result.ArtworkPath));
            Assert.True(new FileInfo(result.ArtworkPath!).Length > 1_000);
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
    public async Task ParsesGenericJsonResultThroughSafeTransport()
    {
        using var client = new SafeHttpClient(handler: new JsonHandler("""{"results":[{"title":"Example Game","year":1992,"publisher":"Acme"}]}"""));
        var provider = new PlaymatchProvider(new OnlineProviderOptions("ignored", "https://example.test"), client);
        var group = new ReleaseGroup { ReleaseKey = "example", Title = "Example Game", Extension = "adf" };
        var result = await provider.ResolveAsync(group);
        Assert.NotNull(result);
        Assert.Equal("Example Game", result!.Title);
        Assert.Equal("1992", result.Year);
        Assert.Equal("playmatch", result.Provider);
    }

    [Fact]
    public async Task ParsesArtworkUrlFromCommonCoverShape()
    {
        using var client = new SafeHttpClient(handler: new JsonHandler(
            "{\"results\":[{\"name\":\"Example Game\",\"cover\":{\"url\":\"//images.example/cover/abc.jpg\"}}]}"));
        var provider = new JsonMetadataProvider(new OnlineProviderOptions("public-gateway", "https://example.test"), client);
        var group = new ReleaseGroup { ReleaseKey = "example", Title = "Example Game", Extension = "adf" };

        var result = await provider.ResolveAsync(group);

        Assert.Equal("https://images.example/cover/abc.jpg", result!.ArtworkUrl);
        Assert.Equal("public-gateway", result.ArtworkProvider);
    }

    [Fact]
    public async Task WikipediaProviderReturnsRelevantAmigaArtwork()
    {
        using var client = new SafeHttpClient(handler: new JsonHandler(
            "{\"query\":{\"pages\":[{\"pageid\":1,\"title\":\"Example Game\",\"extract\":\"Example Game is an adventure game released for the Amiga home computer.\",\"fullurl\":\"https://en.wikipedia.org/wiki/Example_Game\",\"original\":{\"source\":\"https://upload.wikimedia.org/example.jpg\"}}]}}"));
        using var provider = new WikipediaProvider("https://example.test", client);
        var group = new ReleaseGroup { ReleaseKey = "example", Title = "Example Game", Extension = "adf" };

        var result = await provider.ResolveAsync(group);

        Assert.NotNull(result);
        Assert.Equal("Example Game", result!.Title);
        Assert.Equal("wikipedia", result.Provider);
        Assert.Equal("https://upload.wikimedia.org/example.jpg", result.ArtworkUrl);
        Assert.Equal("https://en.wikipedia.org/wiki/Example_Game", result.ArtworkSourceUrl);
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

        Assert.Equal("second", result!.Provider);
        Assert.Equal("https://images.example/example.jpg", result.ArtworkUrl);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json)
            });
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

    private sealed class CountingArtworkProvider(string artworkPath) : IAsyncMetadataProvider
    {
        public string Id => "test-provider";
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

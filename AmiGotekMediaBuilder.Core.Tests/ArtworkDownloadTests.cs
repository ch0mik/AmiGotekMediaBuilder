using System.Net;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class ArtworkDownloadTests
{
    [Fact]
    public async Task OnlineMetadataArtworkIsPreservedAndCopiedToProcessedCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var bytes = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
            using var client = new SafeHttpClient(handler: new BytesHandler(bytes));
            using var downloader = new ArtworkDownloader(client);
            var group = new ReleaseGroup { ReleaseKey = "star|", Title = "Star Voyage", Extension = "adf" };
            var metadata = new MetadataRecord("star|", "Star Voyage", "1992", null, null, "public-catalog", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://images.example/star.jpg",
                ArtworkProvider = "public-catalog"
            };

            var result = await downloader.DownloadAsync(metadata, group,
                Path.Combine(root, "original"), Path.Combine(root, "processed"));

            Assert.NotNull(result);
            Assert.True(File.Exists(result!.OriginalPath));
            Assert.True(File.Exists(result.ProcessedPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.OriginalPath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.ProcessedPath));
            Assert.True(File.Exists(result.OriginalPath + ".source.json"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DemosceneArtworkIsNotWrittenToGameArtworkCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var client = new SafeHttpClient(handler: new BytesHandler([0xff, 0xd8, 0xff, 0xd9]));
            using var downloader = new ArtworkDownloader(client);
            var group = new ReleaseGroup { ReleaseKey = "demo|", Title = "Scene Demo", Extension = "adf" };
            var metadata = new MetadataRecord("demo|", "Scene Demo", "1992", null, null, "pouet", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://images.example/demo.jpg",
                ArtworkProvider = "pouet"
            };

            var result = await downloader.DownloadAsync(metadata, group,
                Path.Combine(root, "original"), Path.Combine(root, "processed"));

            Assert.Null(result);
            Assert.False(Directory.Exists(Path.Combine(root, "original")));
            Assert.False(Directory.Exists(Path.Combine(root, "processed")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LocalGameBaseArtworkIsCopiedWithoutHttp()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "cover.png");
            var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            await File.WriteAllBytesAsync(source, bytes);
            using var downloader = new ArtworkDownloader(new SafeHttpClient(handler: new BytesHandler([])));
            var group = new ReleaseGroup { ReleaseKey = "gamebase", Title = "GameBase Game", Extension = "adf" };
            var metadata = new MetadataRecord("gamebase", "GameBase Game", null, null, null, "gamebase", DateTimeOffset.UtcNow)
            {
                ArtworkPath = source,
                ArtworkProvider = "gamebase"
            };

            var result = await downloader.DownloadAsync(metadata, group,
                Path.Combine(root, "original"), Path.Combine(root, "processed"));

            Assert.NotNull(result);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result!.OriginalPath));
            Assert.Equal(source, result.Url);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DefaultArtworkIsMaterializedForGamesButNeverForDemoscene()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-default-artwork-" + Guid.NewGuid().ToString("N"));
        try
        {
            var game = new ReleaseGroup { ReleaseKey = "fallback|", Title = "Fallback Game", Extension = "adf" };
            var gameResult = DefaultArtworkService.Ensure(game,
                Path.Combine(root, "original"), Path.Combine(root, "processed"));

            Assert.NotNull(gameResult);
            Assert.Equal(DefaultArtworkService.ProviderId, gameResult!.Provider);
            Assert.True(new FileInfo(gameResult.OriginalPath).Length > 1000);
            Assert.True(File.Exists(gameResult.ProcessedPath));
            Assert.True(File.Exists(gameResult.OriginalPath + ".source.json"));

            var demo = new ReleaseGroup
            {
                ReleaseKey = "demo|",
                Title = "Fallback Demo",
                Extension = "adf",
                IsDemoscene = true
            };
            Assert.Null(DefaultArtworkService.Ensure(demo,
                Path.Combine(root, "demo-original"), Path.Combine(root, "demo-processed")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtworkDownloaderFollowsValidatedRedirects()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-redirect-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new SafeHttpClient(handler: new RedirectArtworkHandler());
            using var downloader = new ArtworkDownloader(client);
            var group = new ReleaseGroup { ReleaseKey = "redirect|", Title = "Redirect Game", Extension = "adf" };
            var metadata = new MetadataRecord(group.ReleaseKey, group.Title!, null, null, null,
                "public-catalog", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://example.test/redirect.jpg",
                ArtworkProvider = "public-catalog"
            };

            var result = await downloader.DownloadAsync(metadata, group,
                Path.Combine(root, "original"), Path.Combine(root, "processed"));

            Assert.NotNull(result);
            Assert.Equal(new byte[] { 0xff, 0xd8, 0xff, 0xd9 },
                await File.ReadAllBytesAsync(result!.OriginalPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtworkDownloaderRejectsHtmlReturnedForImageUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-invalid-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new SafeHttpClient(handler: new BytesHandler(
                System.Text.Encoding.UTF8.GetBytes("<html>challenge</html>")));
            using var downloader = new ArtworkDownloader(client);
            var group = new ReleaseGroup { ReleaseKey = "invalid|", Title = "Invalid Image", Extension = "adf" };
            var metadata = new MetadataRecord(group.ReleaseKey, group.Title!, null, null, null,
                "public-catalog", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://example.test/challenge.jpg",
                ArtworkProvider = "public-catalog"
            };

            await Assert.ThrowsAsync<InvalidDataException>(() => downloader.DownloadAsync(metadata, group,
                Path.Combine(root, "original"), Path.Combine(root, "processed")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DemosceneArtworkInOldCacheIsNotReusedForGameDownload()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-artwork-stale-demo-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = Path.Combine(root, "original");
            var processed = Path.Combine(root, "processed");
            Directory.CreateDirectory(original);
            var stale = Path.Combine(original, "Mixed Game.jpg");
            await File.WriteAllBytesAsync(stale, [0xff, 0xd8, 0xaa, 0xd9]);
            await File.WriteAllTextAsync(stale + ".source.json", "{\"provider\":\"pouet\"}");

            using var client = new SafeHttpClient(handler: new BytesHandler([0xff, 0xd8, 0xbb, 0xd9]));
            using var downloader = new ArtworkDownloader(client);
            var group = new ReleaseGroup { ReleaseKey = "mixed|", Title = "Mixed Game", Extension = "adf" };
            var metadata = new MetadataRecord(group.ReleaseKey, group.Title!, null, null, null,
                "wikipedia", DateTimeOffset.UtcNow)
            {
                ArtworkUrl = "https://example.test/mixed.jpg",
                ArtworkProvider = "wikipedia"
            };

            var result = await downloader.DownloadAsync(metadata, group, original, processed);

            Assert.NotNull(result);
            Assert.Equal(new byte[] { 0xff, 0xd8, 0xbb, 0xd9 },
                await File.ReadAllBytesAsync(result!.OriginalPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
    }

    private sealed class RedirectArtworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/redirect.jpg", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://example.test/final.jpg") }
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9])
            });
        }
    }
}

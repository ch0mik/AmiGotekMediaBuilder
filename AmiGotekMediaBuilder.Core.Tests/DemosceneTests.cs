using System.IO.Compression;
using System.Net;
using System.Text;
using AmiGotekMediaBuilder.Demoscene;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class DemosceneTests
{
    [Fact]
    public void RootSourceExcludesManagedDemosceneDirectoryFromIntake()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-config-" + Guid.NewGuid().ToString("N"));
        var config = AmiGotekMediaBuilder.Core.Configuration.PathConfig.Create(root);
        Assert.Equal(Path.Combine(config.DemosceneDirectory, "downloads"), config.DemosceneDownloadDirectory);
        Assert.Contains(config.DemosceneDirectory, config.IntakeExcludedDirectories);
    }

    [Fact]
    public void DemosceneDirectoryRoundTripsThroughTomlConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.toml");
        try
        {
            var original = AmiGotekMediaBuilder.Core.Configuration.PathConfig.Create(root,
                demosceneDirectory: Path.Combine(root, "scene-data"),
                gameBaseDatabasePath: Path.Combine(root, "gamebase.sqlite"));
            AmiGotekMediaBuilder.Core.Configuration.PathConfigLoader.Write(path, original);
            var loaded = AmiGotekMediaBuilder.Core.Configuration.PathConfigLoader.Load(path);
            Assert.Equal(original.DemosceneDirectory, loaded.DemosceneDirectory);
            Assert.Equal(original.GameBaseDatabasePath, loaded.GameBaseDatabasePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CatalogBrowsesPlatformFilteredRowsAndLoadsDetails()
    {
        using var client = new SafeHttpClient(handler: new PouetHandler());
        using var provider = new PouetCatalogProvider(new PouetCatalogOptions(
            "https://example.test", RequestDelay: TimeSpan.Zero), client);

        var results = await provider.BrowseAsync(new DemosceneQuery(
            DemoscenePlatform.Aga, MaxPages: 1, MaxItems: 5));

        var production = Assert.Single(results);
        Assert.Equal("42", production.PouetId);
        Assert.Equal("Example Demo", production.Title);
        Assert.Equal(DemoscenePlatform.Aga | DemoscenePlatform.PpcRtg, production.Platforms);
        Assert.Equal("https://images.example/thumb.jpg", production.ArtworkUrl);
        Assert.Contains(production.Downloads, d => d.Format == DemosceneAssetFormat.Zip);
    }

    [Fact]
    public void DemozooParsesAmigaDemoDownloadsAndPouetReference()
    {
        var html = """
            <html><head>
              <title>Example Demo - Demozoo</title>
              <meta property="og:image" content="https://media.demozoo.org/thumb.jpg">
            </head><body>
              <h1>Example Demo</h1>
              <h3>by <a href="/groups/7/">Group</a></h3>
              <p>Released 12 May 1999</p>
              <p>Amiga AGA, Amiga OCS/ECS - Demo</p>
              <a href="https://files.example/demo.adf">Download</a>
              <a href="https://www.pouet.net/prod.php?which=42">Pouët</a>
            </body></html>
            """;

        var production = DemozooCatalogProvider.ParseProduction(
            Encoding.UTF8.GetBytes(html), "https://demozoo.org/productions/123/", "123");

        Assert.NotNull(production);
        Assert.Equal("123", production.PouetId);
        Assert.Equal(DemosceneCatalogs.Demozoo, production.Catalog);
        Assert.Equal("42", production.LinkedPouetId);
        Assert.Equal("Group", production.Group);
        Assert.Equal(DemoscenePlatform.Aga | DemoscenePlatform.OcsEcs, production.Platforms);
        Assert.Equal("demo", production.Type);
        Assert.Contains(production.Downloads, d => d.Format == DemosceneAssetFormat.Adf);
    }

    [Fact]
    public void DemozooListParsesDemoRows()
    {
        var html = "<table><tr><td><a href='/productions/123/'>Example Demo</a> " +
                   "Amiga PPC/RTG - Demo 1998</td></tr></table>";

        var production = Assert.Single(DemozooCatalogProvider.ParseListPage(
            Encoding.UTF8.GetBytes(html), "https://demozoo.org/productions/?platform=26&production_type=1"));

        Assert.Equal("123", production.PouetId);
        Assert.Equal(DemoscenePlatform.PpcRtg, production.Platforms);
        Assert.Equal("1998", production.Year);
        Assert.Equal(DemosceneCatalogs.Demozoo, production.Catalog);
    }

    [Fact]
    public void CatalogMergerPrefersPouetAndDoesNotDuplicateLinkedDemozooRecord()
    {
        var pouet = new DemosceneProduction("42", "Example Demo", "Group", "1999", "demo",
            DemoscenePlatform.Aga, "https://www.pouet.net/prod.php?which=42", "Pouët description", null, []);
        var demozoo = new DemosceneProduction("123", "Example Demo", "Group", "1999", "demo",
            DemoscenePlatform.Aga, "https://demozoo.org/productions/123/", "Demozoo description", null,
            [new DemosceneDownloadLink("https://files.example/demo.adf", "download", DemosceneAssetFormat.Adf)])
        {
            Catalog = DemosceneCatalogs.Demozoo,
            LinkedPouetId = "42"
        };

        var merged = Assert.Single(DemosceneCatalogMerger.Merge([pouet], [demozoo]));

        Assert.Equal(DemosceneCatalogs.Pouet, merged.Catalog);
        Assert.Equal("42", merged.PouetId);
        Assert.Contains(merged.Downloads, link => link.Url.EndsWith("demo.adf", StringComparison.Ordinal));
    }

    [Fact]
    public void CatalogMergerSortsProductionsNewestFirst()
    {
        var old = new DemosceneProduction("1", "Older Demo", null, "1999", "demo",
            DemoscenePlatform.OcsEcs, "https://www.pouet.net/prod.php?which=1", null, null, []);
        var newest = new DemosceneProduction("2", "Newest Demo", null, "2024", "demo",
            DemoscenePlatform.OcsEcs, "https://www.pouet.net/prod.php?which=2", null, null, []);
        var undated = new DemosceneProduction("3", "Undated Demo", null, null, "demo",
            DemoscenePlatform.OcsEcs, "https://www.pouet.net/prod.php?which=3", null, null, []);

        var result = DemosceneCatalogMerger.Merge([old, undated, newest]);

        Assert.Equal(["2", "1", "3"], result.Select(production => production.PouetId));
    }

    [Fact]
    public async Task DemozooProviderBrowsesOnlyDemoTypeForSelectedPlatform()
    {
        using var client = new SafeHttpClient(handler: new DemozooHandler());
        using var provider = new DemozooCatalogProvider(new DemozooCatalogOptions(
            "https://demozoo.test", RequestDelay: TimeSpan.Zero), client);

        var results = await provider.BrowseAsync(new DemosceneQuery(
            DemoscenePlatform.PpcRtg, MaxPages: 1, MaxItems: 5));

        var production = Assert.Single(results);
        Assert.Equal("99", production.PouetId);
        Assert.Equal(DemosceneCatalogs.Demozoo, production.Catalog);
        Assert.Equal(DemoscenePlatform.PpcRtg, production.Platforms);
    }

    [Fact]
    public async Task BatchDownloaderExtractsZipAndWritesProvenance()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-demoscene-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var disk = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
            var archive = CreateZip(disk);
            using var client = new SafeHttpClient(handler: new DownloadHandler(archive));
            using var service = new DemosceneDownloadService(client);
            var production = new DemosceneProduction("42", "Example Demo", "Group", "1999", "demo",
                DemoscenePlatform.Aga, "https://example.test/prod.php?which=42", "Description", null,
                [new DemosceneDownloadLink("https://example.test/demo.zip", "download", DemosceneAssetFormat.Zip, "demo.zip")]);

            var result = await service.DownloadAsync([production], new DemosceneDownloadOptions(root,
                MaxConcurrency: 1, RequestDelayMilliseconds: 0));

            Assert.Equal(1, result.Downloaded);
            var output = Assert.Single(Directory.EnumerateFiles(root, "*.adf", SearchOption.AllDirectories));
            Assert.EndsWith("Example Demo (Disk 1 of 2)(Data).adf", output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(disk, await File.ReadAllBytesAsync(output));
            Assert.True(File.Exists(output + ".source.json"));
            Assert.True(File.Exists(Path.Combine(root, "manifest.jsonl")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DownloaderInfersExtensionlessAdfPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-demoscene-infer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var disk = new byte[1024];
            using var client = new SafeHttpClient(handler: new DownloadHandler(disk));
            using var service = new DemosceneDownloadService(client);
            var production = new DemosceneProduction("43", "Infer Demo", null, null, "demo",
                DemoscenePlatform.OcsEcs, "https://example.test/prod.php?which=43", null, null,
                [new DemosceneDownloadLink("https://example.test/download?id=43", "download", DemosceneAssetFormat.Unknown)]);

            var result = await service.DownloadAsync([production], new DemosceneDownloadOptions(root,
                MaxConcurrency: 1, RequestDelayMilliseconds: 0));

            Assert.Equal(1, result.Downloaded);
            Assert.Single(Directory.EnumerateFiles(root, "*.adf", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task BatchDownloaderUsesGotekDemosceneCategoryWhenRequested()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-demoscene-gotek-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var disk = Enumerable.Range(0, 1024).Select(i => (byte)i).ToArray();
            using var client = new SafeHttpClient(handler: new DownloadHandler(CreateZip(disk)));
            using var service = new DemosceneDownloadService(client);
            var production = new DemosceneProduction("44", "Example Demo", null, "1999", "demo",
                DemoscenePlatform.Aga, "https://example.test/prod.php?which=44", null, null,
                [new DemosceneDownloadLink("https://example.test/demo.zip", "download", DemosceneAssetFormat.Zip, "demo.zip")]);

            var result = await service.DownloadAsync([production], new DemosceneDownloadOptions(root,
                MaxConcurrency: 1, RequestDelayMilliseconds: 0, GotekExportLayout: true));

            Assert.Equal(1, result.Downloaded);
            Assert.True(File.Exists(Path.Combine(root, "ADF", "Demoscene", "Example Demo",
                "Example Demo (Disk 1 of 2)(Data).adf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ArtworkServiceCachesPouetThumbnailSeparately()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-demoscene-art-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
            using var client = new SafeHttpClient(handler: new DownloadHandler(image));
            using var service = new DemosceneArtworkService(client);
            var production = new DemosceneProduction("7", "Thumbnail Demo", null, "2001", "demo",
                DemoscenePlatform.OcsEcs, "https://example.test/prod.php?which=7", "desc",
                "https://images.example/shot.jpg", []);
            var original = Path.Combine(root, "original");
            var processed = Path.Combine(root, "processed");

            var first = await service.DownloadAsync([production], original, processed);
            var second = await service.DownloadAsync([production], original, processed);

            Assert.Equal(DemosceneArtworkStatus.Downloaded, Assert.Single(first).Status);
            Assert.Equal(DemosceneArtworkStatus.AlreadyPresent, Assert.Single(second).Status);
            Assert.Equal(image, await File.ReadAllBytesAsync(Path.Combine(original, "7-Thumbnail Demo.jpg")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] CreateZip(byte[] disk)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry("Example Demo (Disk 1 of 2)(Data).adf").Open())
            stream.Write(disk);
        return output.ToArray();
    }

    private sealed class PouetHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Contains("prodlist", StringComparison.OrdinalIgnoreCase)
                ? "<table><tr><td>demo Amiga AGA Amiga PPC/RTG <a href='/prod.php?which=42'>Example Demo</a> Group 1999</td></tr></table>"
                : "<html><head><title>Example Demo by Group :: pouët.net</title><meta property='og:image' content='https://images.example/thumb.jpg'></head><body>Amiga AGA Amiga PPC/RTG demo release date : june 1999 <a href='/files/demo.zip'>download</a></body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") });
        }
    }

    private sealed class DownloadHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }

    private sealed class DemozooHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path.Equals("/productions/", StringComparison.OrdinalIgnoreCase)
                ? "<table><tr><td><a href='/productions/99/'>PPC Demo</a> Amiga PPC/RTG - Demo 2004</td></tr></table>"
                : "<html><head><title>PPC Demo - Demozoo</title></head><body>" +
                  "<h1>PPC Demo</h1><p>Released 1 Jan 2004</p><p>Amiga PPC/RTG - Demo</p></body></html>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
        }
    }
}

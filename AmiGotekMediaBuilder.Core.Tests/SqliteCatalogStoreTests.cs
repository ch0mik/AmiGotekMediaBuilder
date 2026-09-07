using AmiGotekMediaBuilder.Core.Catalog;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class SqliteCatalogStoreTests
{
    [Fact]
    public void ReadsMetadataByHashAndKeepsNamespacesSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-sqlite-cache-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "catalog", "catalog.db");
        try
        {
            var group = new ReleaseGroup
            {
                ReleaseKey = "release|atlantis",
                Title = "Fate of Atlantis",
                SourceSha256 = "ABCDEF1234",
                Chipset = "aga",
                Extension = "adf"
            };
            var ns = new CatalogNamespace("amiga", "game", "aga");
            var record = new MetadataRecord(group.ReleaseKey, "Indiana Jones and the Fate of Atlantis",
                "1992", "Lucasfilm", "Adventure", "hasheous", DateTimeOffset.UtcNow);

            var store = new SqliteCatalogStore(database);
            store.WriteMetadata(group, record, ns);

            var loaded = store.ReadMetadata(group, ns, includeOffline: false);
            Assert.NotNull(loaded);
            Assert.Equal(record.Title, loaded!.Title);
            Assert.Equal(record.Provider, loaded.Provider);
            Assert.True(File.Exists(database));

            Assert.Null(store.ReadMetadata(group, new CatalogNamespace("c64", "game", "aga"),
                includeOffline: false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task HybridEnrichmentUsesSqliteBeforeCallingProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-sqlite-hybrid-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "catalog.db");
        try
        {
            var group = new ReleaseGroup
            {
                ReleaseKey = "release|cached",
                Title = "Cached Game",
                SourceSha256 = "cached-sha256",
                Extension = "adf"
            };
            var cached = new MetadataRecord(group.ReleaseKey, group.Title!, "1990", null,
                "from sqlite", "test-cache", DateTimeOffset.UtcNow);
            new SqliteCatalogStore(database).WriteMetadata(group, cached);

            var provider = new CountingProvider();
            var records = await new HybridMetadataEnricher([provider]).EnrichAsync(
                [group], Path.Combine(root, "legacy"), Path.Combine(root, "nfo"),
                catalogDatabasePath: database);

            var result = Assert.Single(records);
            Assert.Equal("from sqlite", result.Description);
            // A metadata-only cache hit is still rechecked online so a later
            // build can discover artwork; cached text remains authoritative.
            Assert.Equal(1, provider.Calls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReusesArtworkPathStoredForHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-sqlite-artwork-" + Guid.NewGuid().ToString("N"));
        var database = Path.Combine(root, "catalog.db");
        var artwork = Path.Combine(root, "artwork-original.jpg");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(artwork, [0xFF, 0xD8, 0xFF, 0xD9]);
            var group = new ReleaseGroup
            {
                ReleaseKey = "release|artwork",
                Title = "Artwork Game",
                SourceSha256 = "artwork-sha256",
                Extension = "adf"
            };
            var store = new SqliteCatalogStore(database);
            store.WriteMetadata(group, new MetadataRecord(group.ReleaseKey, group.Title!, null, null,
                null, "openretro", DateTimeOffset.UtcNow));
            store.WriteArtwork(group, new ArtworkArtifact(artwork, artwork, true,
                "openretro", "https://example.test/art.jpg"));

            var loaded = store.ReadMetadata(group, includeOffline: false);
            Assert.Equal(artwork, loaded!.ArtworkPath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CountingProvider : IAsyncMetadataProvider
    {
        public string Id => "counting-provider";
        public int Calls { get; private set; }

        public Task<MetadataRecord?> ResolveAsync(
            ReleaseGroup group, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult<MetadataRecord?>(new MetadataRecord(
                group.ReleaseKey, group.Title ?? "Unknown", null, null, null, Id,
                DateTimeOffset.UtcNow));
        }
    }
}

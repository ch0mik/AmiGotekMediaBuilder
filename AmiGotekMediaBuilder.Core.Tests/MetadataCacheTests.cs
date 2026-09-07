using AmiGotekMediaBuilder.Core.Metadata;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class MetadataCacheTests
{
    [Fact]
    public void RoundTripsMetadataUsingStableCacheKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amiga-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new MetadataCache(directory);
            var record = new MetadataRecord("oil|imperium", "Oil Imperium", "1992", "ECS", "QTX", "test", DateTimeOffset.UtcNow);
            cache.Write(record);
            var loaded = cache.Read(record.ReleaseKey);
            Assert.NotNull(loaded);
            Assert.Equal(record.Title, loaded!.Title);
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PreservesArtworkProvenanceFields()
    {
        var directory = Path.Combine(Path.GetTempPath(), "amiga-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
        var original = new MetadataRecord("art|", "Artwork", "1990", null, null, "public-catalog", DateTimeOffset.UtcNow)
        {
            ArtworkUrl = "https://images.example/art.jpg",
            ArtworkSourceUrl = "https://catalog.example/games/art",
            ArtworkProvider = "public-catalog"
            };
            var cache = new MetadataCache(directory);
            cache.Write(original);
            var restored = cache.Read(original.ReleaseKey);
            Assert.Equal(original.ArtworkUrl, restored!.ArtworkUrl);
            Assert.Equal(original.ArtworkSourceUrl, restored.ArtworkSourceUrl);
            Assert.Equal(original.ArtworkProvider, restored.ArtworkProvider);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}

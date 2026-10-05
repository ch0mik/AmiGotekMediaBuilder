using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>Keyless final artwork candidate from libretro's Amiga thumbnail set.</summary>
public sealed class LibretroAmigaArtworkProvider : IAsyncMetadataProvider
{
    public string Id => "libretro";

    public Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        if (group.IsDemoscene || string.IsNullOrWhiteSpace(group.Title))
            return Task.FromResult<MetadataRecord?>(null);
        var file = Uri.EscapeDataString(group.Title.Trim() + ".png");
        var url = "https://raw.githubusercontent.com/libretro-thumbnails/Commodore_-_Amiga/master/Named_Boxarts/" + file;
        return Task.FromResult<MetadataRecord?>(new MetadataRecord(group.ReleaseKey, group.Title,
            null, null, null, Id, DateTimeOffset.UtcNow)
        {
            ArtworkUrl = url,
            ArtworkSourceUrl = "https://github.com/libretro-thumbnails/Commodore_-_Amiga",
            ArtworkProvider = Id
        });
    }
}

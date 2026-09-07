namespace AmiGotekMediaBuilder.Core.Metadata;

public sealed partial record MetadataRecord(
    string ReleaseKey,
    string Title,
    string? Year,
    string? Publisher,
    string? Description,
    string Provider,
    DateTimeOffset RetrievedAt);

// Artwork is kept as optional init-only fields so existing cache files and the
// original positional constructor remain backwards compatible.
public sealed partial record MetadataRecord
{
    /// <summary>Direct image URL returned by the selected online provider.</summary>
    public string? ArtworkUrl { get; init; }

    /// <summary>Page or API URL that supplied the image URL.</summary>
    public string? ArtworkSourceUrl { get; init; }

    /// <summary>Provider that supplied the artwork (normally the metadata provider).</summary>
    public string? ArtworkProvider { get; init; }

    /// <summary>
    /// Local image path supplied by a database-backed provider such as
    /// GameBase. It is mutually exclusive with <see cref="ArtworkUrl"/> in
    /// normal records, but both fields remain optional for cache compatibility.
    /// </summary>
    public string? ArtworkPath { get; init; }
}

public interface IMetadataProvider
{
    string Id { get; }
    MetadataRecord? Resolve(Models.ReleaseGroup group);
}

/// <summary>Offline provider: only facts already present in parsed filenames.</summary>
public sealed class FilenameMetadataProvider : IMetadataProvider
{
    public string Id => "offline-filename";

    public MetadataRecord? Resolve(Models.ReleaseGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var first = group.Records.FirstOrDefault();
        if (first is null && string.IsNullOrWhiteSpace(group.Title)) return null;
        var title = string.IsNullOrWhiteSpace(group.Title) ? "Unknown" : group.Title.Trim();
        var details = new[] { group.Group, group.Chipset, group.Language, group.Version }
            .Where(v => !string.IsNullOrWhiteSpace(v));
        var description = string.Join(" - ", details!);
        return new MetadataRecord(group.ReleaseKey, title, first?.Year, first?.Publisher,
            description.Length == 0 ? null : description, Id, DateTimeOffset.UtcNow);
    }
}

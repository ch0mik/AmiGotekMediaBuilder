namespace AmiGotekMediaBuilder.Demoscene;

/// <summary>
/// Combines catalog results without presenting a production twice. Pouët is
/// preferred when Demozoo links back to the same Pouët production, while
/// missing Demozoo download links and metadata are retained.
/// </summary>
public static class DemosceneCatalogMerger
{
    public static IReadOnlyList<DemosceneProduction> Merge(
        params IEnumerable<DemosceneProduction>[] catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var merged = new List<DemosceneProduction>();
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
        {
            if (catalog is null) continue;
            foreach (var production in catalog)
            {
                if (production is null) continue;
                var key = IdentityKey(production);
                if (!positions.TryGetValue(key, out var index))
                {
                    positions[key] = merged.Count;
                    merged.Add(production);
                    continue;
                }
                merged[index] = MergePair(merged[index], production);
            }
        }
        return merged;
    }

    private static DemosceneProduction MergePair(
        DemosceneProduction left,
        DemosceneProduction right)
    {
        var preferred = string.Equals(left.Catalog, DemosceneCatalogs.Pouet, StringComparison.OrdinalIgnoreCase)
            ? left
            : string.Equals(right.Catalog, DemosceneCatalogs.Pouet, StringComparison.OrdinalIgnoreCase)
                ? right
                : left;
        var other = ReferenceEquals(preferred, left) ? right : left;
        var downloads = preferred.Downloads
            .Concat(other.Downloads)
            .Where(link => !string.IsNullOrWhiteSpace(link.Url))
            .GroupBy(link => link.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return preferred with
        {
            Group = preferred.Group ?? other.Group,
            Year = preferred.Year ?? other.Year,
            Type = preferred.Type ?? other.Type,
            Platforms = preferred.Platforms | other.Platforms,
            Description = preferred.Description ?? other.Description,
            ArtworkUrl = preferred.ArtworkUrl ?? other.ArtworkUrl,
            Downloads = downloads,
            LinkedPouetId = preferred.LinkedPouetId ?? other.LinkedPouetId
        };
    }

    private static string IdentityKey(DemosceneProduction production)
    {
        if (string.Equals(production.Catalog, DemosceneCatalogs.Pouet, StringComparison.OrdinalIgnoreCase))
            return "pouet:" + production.PouetId;
        if (!string.IsNullOrWhiteSpace(production.LinkedPouetId))
            return "pouet:" + production.LinkedPouetId;

        var title = Normalize(production.Title);
        var group = Normalize(production.Group);
        var year = Normalize(production.Year);
        return $"fallback:{title}|{group}|{year}|{(int)production.Platforms}";
    }

    private static string Normalize(string? value) =>
        new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}

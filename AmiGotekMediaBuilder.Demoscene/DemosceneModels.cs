namespace AmiGotekMediaBuilder.Demoscene;

public static class DemosceneCatalogs
{
    public const string Pouet = "pouet";
    public const string Demozoo = "demozoo";
    public const string SceneOrg = "sceneorg";
}

[Flags]
public enum DemoscenePlatform
{
    None = 0,
    OcsEcs = 1,
    Aga = 2,
    PpcRtg = 4
}

public static class DemoscenePlatforms
{
    public const string OcsEcsLabel = "Amiga OCS/ECS";
    public const string AgaLabel = "Amiga AGA";
    public const string PpcRtgLabel = "Amiga PPC/RTG";

    public static string ToPouetLabel(this DemoscenePlatform platform) => platform switch
    {
        DemoscenePlatform.OcsEcs => OcsEcsLabel,
        DemoscenePlatform.Aga => AgaLabel,
        DemoscenePlatform.PpcRtg => PpcRtgLabel,
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "A concrete platform is required.")
    };

    public static DemoscenePlatform Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DemoscenePlatform.None;
        var normalized = value.Trim().ToLowerInvariant().Replace('_', '-');
        return normalized switch
        {
            "ocs" or "ecs" or "ocs-ecs" or "ocs/ecs" or "amiga ocs/ecs" => DemoscenePlatform.OcsEcs,
            "aga" or "amiga aga" => DemoscenePlatform.Aga,
            "ppc" or "rtg" or "ppc-rtg" or "ppc/rtg" or "amiga ppc/rtg" => DemoscenePlatform.PpcRtg,
            "all" or "amiga" => DemoscenePlatform.OcsEcs | DemoscenePlatform.Aga | DemoscenePlatform.PpcRtg,
            _ => throw new ArgumentException($"Unknown demoscene platform '{value}'.", nameof(value))
        };
    }

    public static DemoscenePlatform ParseLabels(string text)
    {
        var flags = DemoscenePlatform.None;
        if (text.Contains(OcsEcsLabel, StringComparison.OrdinalIgnoreCase)) flags |= DemoscenePlatform.OcsEcs;
        if (text.Contains(AgaLabel, StringComparison.OrdinalIgnoreCase)) flags |= DemoscenePlatform.Aga;
        if (text.Contains(PpcRtgLabel, StringComparison.OrdinalIgnoreCase)) flags |= DemoscenePlatform.PpcRtg;
        return flags;
    }

    public static IEnumerable<DemoscenePlatform> Enumerate(DemoscenePlatform flags)
    {
        foreach (var value in new[] { DemoscenePlatform.OcsEcs, DemoscenePlatform.Aga, DemoscenePlatform.PpcRtg })
            if ((flags & value) != 0) yield return value;
    }
}

public enum DemosceneAssetFormat
{
    Unknown,
    Adf,
    Dsk,
    Zip,
    Gzip,
    SevenZip,
    Lha,
    Dms,
    Rom,
    Executable
}

public sealed record DemosceneDownloadLink(
    string Url,
    string? Label,
    DemosceneAssetFormat Format,
    string? FileName = null,
    int? DiskNumber = null,
    int? TotalDisks = null);

public sealed record DemosceneProduction(
    string PouetId,
    string Title,
    string? Group,
    string? Year,
    string? Type,
    DemoscenePlatform Platforms,
    string SourceUrl,
    string? Description,
    string? ArtworkUrl,
    IReadOnlyList<DemosceneDownloadLink> Downloads)
{
    /// <summary>Catalog that supplied this record. Existing records default to Pouët.</summary>
    public string Catalog { get; init; } = DemosceneCatalogs.Pouet;
    /// <summary>Optional Pouët ID linked from another catalog, used for cross-catalog deduplication.</summary>
    public string? LinkedPouetId { get; init; }

    public bool Supports(DemoscenePlatform requested) => requested == DemoscenePlatform.None ||
        (Platforms & requested) != 0;
}

public sealed record DemosceneQuery(
    DemoscenePlatform Platform = DemoscenePlatform.None,
    string? Type = null,
    int? YearFrom = null,
    int? YearTo = null,
    string? Search = null,
    int MaxPages = 1,
    int MaxItems = 100)
{
    public DemosceneQuery Normalize()
    {
        var pages = Math.Clamp(MaxPages, 1, 10_000);
        var items = Math.Clamp(MaxItems, 1, 100_000);
        return this with
        {
            MaxPages = pages,
            MaxItems = items,
            Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
            Type = string.IsNullOrWhiteSpace(Type) ? null : Type.Trim()
        };
    }

    /// <summary>
    /// Applies the requested filters locally as a final guard.  Catalog search
    /// endpoints do not all honour every query parameter (notably Demozoo's
    /// browse endpoint), so callers must not rely on remote filtering alone.
    /// </summary>
    public bool Matches(DemosceneProduction production)
    {
        ArgumentNullException.ThrowIfNull(production);
        var normalized = Normalize();
        if (!production.Supports(normalized.Platform)) return false;
        if (!string.IsNullOrWhiteSpace(normalized.Type) &&
            !string.Equals(production.Type, normalized.Type, StringComparison.OrdinalIgnoreCase)) return false;
        var searchableText = string.Join('\n',
            new string?[] { production.Title, production.Group, production.Year, production.Type,
             production.Description, production.SourceUrl }
            .Concat(production.Downloads.Select(link => $"{link.FileName}\n{link.Url}")));
        if (!string.IsNullOrWhiteSpace(normalized.Search) &&
            !SearchTerms(normalized.Search).All(term =>
                searchableText.Contains(term, StringComparison.OrdinalIgnoreCase))) return false;
        if (!int.TryParse(production.Year, out var year))
            return !normalized.YearFrom.HasValue && !normalized.YearTo.HasValue;
        return (!normalized.YearFrom.HasValue || year >= normalized.YearFrom.Value) &&
               (!normalized.YearTo.HasValue || year <= normalized.YearTo.Value);
    }

    /// <summary>Breaks a human query into searchable words, so that e.g.
    /// "The Black Lotus - Eon" also matches a record titled "Eon" by
    /// "The Black Lotus".</summary>
    public static IReadOnlyList<string> SearchTerms(string? search) =>
        System.Text.RegularExpressions.Regex.Matches(search ?? string.Empty, @"[\p{L}\p{N}]+").Cast<System.Text.RegularExpressions.Match>()
            .Select(match => match.Value)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}

public sealed record DemosceneDownloadOptions(
    string DestinationDirectory,
    long MaxBytesPerFile = 100 * 1024 * 1024,
    long MaxBytesTotal = 4L * 1024 * 1024 * 1024,
    int MaxConcurrency = 2,
    bool IncludeArchives = true,
    bool IncludeUnsupportedReport = true,
    bool FollowRedirects = true,
    int RequestDelayMilliseconds = 350,
    int MaxLinksPerProduction = 32,
    bool GotekExportLayout = false)
{
    public DemosceneDownloadOptions Normalize()
    {
        if (string.IsNullOrWhiteSpace(DestinationDirectory))
            throw new ArgumentException("Demoscene destination directory is required.", nameof(DestinationDirectory));
        return this with
        {
            DestinationDirectory = Path.GetFullPath(DestinationDirectory),
            MaxBytesPerFile = Math.Clamp(MaxBytesPerFile, 1024, 2L * 1024 * 1024 * 1024),
            MaxBytesTotal = Math.Clamp(MaxBytesTotal, 1024, 20L * 1024 * 1024 * 1024),
            MaxConcurrency = Math.Clamp(MaxConcurrency, 1, 8),
            RequestDelayMilliseconds = Math.Clamp(RequestDelayMilliseconds, 0, 60_000),
            MaxLinksPerProduction = Math.Clamp(MaxLinksPerProduction, 1, 256)
        };
    }
}

public enum DemosceneDownloadStatus { Downloaded, AlreadyPresent, Skipped, Failed }

public sealed record DemosceneDownloadResult(
    string PouetId,
    string Title,
    string Url,
    DemosceneDownloadStatus Status,
    string? OutputPath,
    string? Error,
    long Bytes,
    string? Sha256,
    DemosceneAssetFormat Format,
    DemoscenePlatform Platform);

public sealed class DemosceneBatchResult
{
    public List<DemosceneDownloadResult> Results { get; } = [];
    public int Downloaded => Results.Count(r => r.Status == DemosceneDownloadStatus.Downloaded);
    public int AlreadyPresent => Results.Count(r => r.Status == DemosceneDownloadStatus.AlreadyPresent);
    public int Skipped => Results.Count(r => r.Status == DemosceneDownloadStatus.Skipped);
    public int Failed => Results.Count(r => r.Status == DemosceneDownloadStatus.Failed);
    public long BytesDownloaded => Results.Where(r => r.Status == DemosceneDownloadStatus.Downloaded).Sum(r => r.Bytes);
}

public sealed record DemosceneDownloadProgress(
    int Completed,
    int Total,
    DemosceneProduction Production,
    DemosceneDownloadResult Result,
    bool IsStarting = false);

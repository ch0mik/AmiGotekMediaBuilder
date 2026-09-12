namespace AmiGotekMediaBuilder.Core.Configuration;

public sealed class PathConfigException(string message) : ArgumentException(message);

/// <summary>
/// Fully resolved library paths. When no explicit original directory is
/// configured, the library root itself is the intake source. Managed output
/// directories are then excluded from recursive scans.
/// </summary>
public sealed record PathConfig
{
    private static readonly string[] ManagedDirectoryNames =
        ["catalog", "assets", "work", "config"];

    public required string LibraryRoot { get; init; }
    public required string OriginalDirectory { get; init; }
    public required string StagingDirectory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string QuarantineDirectory { get; init; }
    public required string ApprovalsDirectory { get; init; }
    public required string ReportsDirectory { get; init; }
    public required string LogsDirectory { get; init; }
    public required string CacheDirectory { get; init; }
    /// <summary>Writable area for Pouët catalogue cache and downloaded images.</summary>
    public required string DemosceneDirectory { get; init; }
    /// <summary>Optional local GameBase SQLite database used for metadata/artwork.</summary>
    public string? GameBaseDatabasePath { get; init; }

    public string CatalogDirectory => Combine(LibraryRoot, "catalog");
    public string AssetsDirectory => Combine(LibraryRoot, "assets");
    public string ReviewDirectory => Combine(LibraryRoot, "review");
    public string RejectedDirectory => Combine(LibraryRoot, "rejected");
    public string MetadataCacheDirectory => Combine(CatalogDirectory, "metadata-cache");
    public string CuratedMetadataDirectory => Combine(CatalogDirectory, "metadata-curated");
    /// <summary>Shared multisystem SQLite cache for metadata and artwork indexes.</summary>
    public string CatalogDatabasePath => Combine(CatalogDirectory, "catalog.db");
    public string ArtworkOriginalDirectory => Combine(AssetsDirectory, "artwork-original");
    public string ArtworkProcessedDirectory => Combine(AssetsDirectory, "artwork-processed");
    public string NfoDirectory => Combine(AssetsDirectory, "nfo");
    public string RtfmDirectory => Combine(AssetsDirectory, "rtfm");
    public string DemosceneDownloadDirectory => Combine(DemosceneDirectory, "downloads");
    public string DemosceneMetadataDirectory => Combine(DemosceneDirectory, "metadata");
    public string DemosceneCacheDirectory => Combine(DemosceneDirectory, "cache");
    public string DemosceneArtworkOriginalDirectory => Combine(AssetsDirectory, "demoscene", "thumbnails-original");
    public string DemosceneArtworkProcessedDirectory => Combine(AssetsDirectory, "demoscene", "thumbnails-processed");

    /// <summary>
    /// Managed directories that must not be scanned when the library root is
    /// also the intake source. This prevents a later run from ingesting its
    /// own staging, artwork, catalog or logs.
    /// </summary>
    public IReadOnlyList<string> IntakeExcludedDirectories
    {
        get
        {
            var exclusions = new List<string>();
            if (SamePath(LibraryRoot, OriginalDirectory))
            {
                exclusions.AddRange(
                [
                    CatalogDirectory, AssetsDirectory, StagingDirectory,
                    OutputDirectory, QuarantineDirectory, ApprovalsDirectory,
                    ReportsDirectory, LogsDirectory, DemosceneDirectory,
                    ReviewDirectory, RejectedDirectory, Combine(LibraryRoot, "config")
                ]);
            }
            else
            {
                // If a source directory was previously used as the library
                // root, it can contain generated output from that run. Ignore
                // only directories that carry the managed-layout markers, so
                // a legitimate game folder named e.g. "Work" is unaffected.
                foreach (var name in ManagedDirectoryNames)
                {
                    var candidate = Combine(OriginalDirectory, name);
                    if (Directory.Exists(candidate) && LooksLikeManagedDirectory(candidate, name))
                        exclusions.Add(candidate);
                }
            }

            return exclusions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public static PathConfig Create(
        string libraryRoot,
        string? originalDirectory = null,
        string? stagingDirectory = null,
        string? outputDirectory = null,
        string? quarantineDirectory = null,
        string? approvalsDirectory = null,
        string? reportsDirectory = null,
        string? logsDirectory = null,
        string? cacheDirectory = null,
        string? demosceneDirectory = null,
        string? gameBaseDatabasePath = null)
    {
        var root = ResolveRequired(libraryRoot, nameof(libraryRoot));
        var config = new PathConfig
        {
            LibraryRoot = root,
            OriginalDirectory = string.IsNullOrWhiteSpace(originalDirectory)
                ? root
                : ResolveRequired(originalDirectory, "originalDirectory"),
            StagingDirectory = ResolveOrDefault(stagingDirectory, root, "work", "staging"),
            OutputDirectory = ResolveOrDefault(outputDirectory, root, "output"),
            QuarantineDirectory = ResolveOrDefault(quarantineDirectory, root, "unknown"),
            ApprovalsDirectory = ResolveOrDefault(approvalsDirectory, root, "config", "manual-approvals"),
            ReportsDirectory = ResolveOrDefault(reportsDirectory, root, "reports"),
            LogsDirectory = ResolveOrDefault(logsDirectory, root, "logs"),
            CacheDirectory = ResolveOrDefault(cacheDirectory, DefaultCacheDirectory()),
            DemosceneDirectory = ResolveOrDefault(demosceneDirectory, root, "demoscene"),
            GameBaseDatabasePath = string.IsNullOrWhiteSpace(gameBaseDatabasePath)
                ? null
                : ResolveRequired(gameBaseDatabasePath, nameof(gameBaseDatabasePath))
        };
        Validate(config);
        return config;
    }

    private static void Validate(PathConfig config)
    {
        var original = Normalize(config.OriginalDirectory);
        var root = Normalize(config.LibraryRoot);
        var sourceIsRoot = SamePath(root, original);
        foreach (var (name, value) in new[]
        {
            ("outputDirectory", config.OutputDirectory),
            ("stagingDirectory", config.StagingDirectory),
            ("cacheDirectory", config.CacheDirectory),
            ("quarantineDirectory", config.QuarantineDirectory),
            ("demosceneDirectory", config.DemosceneDirectory)
        })
        {
            var path = Normalize(value);
            if (SamePath(path, root))
                throw new PathConfigException($"{name} must not equal libraryRoot ({root}); source files are read-only.");
            if (!sourceIsRoot && (SamePath(path, original) || IsWithin(path, original)))
                throw new PathConfigException($"{name} must not equal or be inside originalDirectory ({original}); the original corpus is read-only.");
        }

        if (!sourceIsRoot && (SamePath(root, original) || IsWithin(root, original)))
            throw new PathConfigException($"libraryRoot ({root}) must not be inside originalDirectory ({original}).");
    }

    private static string ResolveOrDefault(string? value, string root, params string[] parts) =>
        string.IsNullOrWhiteSpace(value) ? Combine(root, parts) : ResolveRequired(value, "path");

    private static string ResolveRequired(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new PathConfigException($"{name} is required.");
        return Normalize(value);
    }

    private static string Normalize(string value)
    {
        var full = Path.GetFullPath(value);
        var pending = new Stack<string>();
        var current = new DirectoryInfo(full);

        // Resolve the nearest existing directory first, then append any
        // non-existing child components. This covers role paths such as
        // <symlink-to-original>/new-output before they are created.
        while (!current.Exists && current.Parent is not null)
        {
            pending.Push(current.Name);
            current = current.Parent;
        }

        var resolved = ResolveExistingDirectory(current);
        while (pending.Count > 0)
            resolved = Path.Combine(resolved, pending.Pop());
        return Path.GetFullPath(resolved);
    }

    private static string ResolveExistingDirectory(DirectoryInfo directory)
    {
        try
        {
            var target = directory.ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName is { Length: > 0 } path ? Path.GetFullPath(path) : directory.FullName;
        }
        catch (IOException)
        {
            // A path can be syntactically valid but not resolvable yet (for
            // example a drive root or an inaccessible parent). Keep the
            // normalized absolute path; existing links are resolved above.
            return directory.FullName;
        }
        catch (UnauthorizedAccessException)
        {
            return directory.FullName;
        }
    }
    private static string Combine(string root, params string[] parts) => Normalize(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    private static bool LooksLikeManagedDirectory(string path, string name) => name switch
    {
        "catalog" => File.Exists(Path.Combine(path, "catalog.db")) ||
                     Directory.Exists(Path.Combine(path, "metadata-cache")) ||
                     Directory.Exists(Path.Combine(path, "metadata-curated")),
        "assets" => Directory.Exists(Path.Combine(path, "artwork-original")) ||
                    Directory.Exists(Path.Combine(path, "artwork-processed")) ||
                    Directory.Exists(Path.Combine(path, "nfo")) ||
                    Directory.Exists(Path.Combine(path, "rtfm")) ||
                    Directory.Exists(Path.Combine(path, "demoscene")),
        "work" => Directory.Exists(Path.Combine(path, "staging")),
        "config" => File.Exists(Path.Combine(path, "config.toml")) ||
                    Directory.Exists(Path.Combine(path, "manual-approvals")),
        _ => false
    };

    private static bool IsWithin(string candidate, string parent)
    {
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right) =>
        StringComparer.OrdinalIgnoreCase.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static string DefaultCacheDirectory() =>
        Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "amiga-adf-library-builder");
}

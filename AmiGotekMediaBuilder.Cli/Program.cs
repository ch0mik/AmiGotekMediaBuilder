using System.Text.Json;
using AmiGotekMediaBuilder.Core.Configuration;
using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Grouping;
using AmiGotekMediaBuilder.Core.Parsing;
using AmiGotekMediaBuilder.Core.Scanning;

return await MainAsync(args);

static Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
    {
        PrintHelp();
        return Task.FromResult(0);
    }

    if (args[0].Equals("init", StringComparison.OrdinalIgnoreCase))
        return RunInit(args[1..]);
    if (args[0].Equals("config", StringComparison.OrdinalIgnoreCase))
        return RunConfig(args[1..]);
    if (args[0] is not ("scan" or "build" or "export"))
    {
        Console.Error.WriteLine($"error: unknown command '{args[0]}'");
        return Task.FromResult(2);
    }

    try
    {
        var options = ParseOptions(args[1..]);
        var config = PathConfigLoader.Load(
            options.GetValueOrDefault("config"),
            options.Where(p => p.Key is not "config" and not "json")
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        if (!Directory.Exists(config.OriginalDirectory))
        {
            Console.Error.WriteLine($"error: source directory does not exist: {config.OriginalDirectory}");
            return Task.FromResult(1);
        }

        var scans = IntakeScanner.ScanDirectories(
            [config.OriginalDirectory], config.IntakeExcludedDirectories);
        var parsed = scans.Select(s =>
        {
            var record = FilenameParser.Parse(s.Filename);
            record.SourcePath = s.Path;
            record.SourceSha256 = s.Sha256;
            return record;
        }).ToArray();
        var records = scans.Select((s, i) => new
        {
            scan = new
            {
                path = s.Path, filename = s.Filename, size = s.Size,
                sha256 = s.Sha256, scanned_at = s.ScannedAt
            },
            parsed = parsed[i]
        }).ToArray();

        if (args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
        {
            var onlineRequested = options.ContainsKey("online");
            var groups = ReleaseGrouper.Group(parsed);
            AmiGotekMediaBuilder.Core.Metadata.HybridMetadataEnricher? onlineEnricher = null;
            AmiGotekMediaBuilder.Core.Metadata.OfflineEnricher? offlineEnricher = null;
            var metadata = onlineRequested
                ? (onlineEnricher = new AmiGotekMediaBuilder.Core.Metadata.HybridMetadataEnricher(
                        AmiGotekMediaBuilder.Core.Metadata.OnlineProviderFactory.CreateDefault(
                            gameBaseDatabasePath: config.GameBaseDatabasePath)))
                    .EnrichAsync(groups, config.MetadataCacheDirectory, config.NfoDirectory,
                        catalogDatabasePath: config.CatalogDatabasePath).GetAwaiter().GetResult()
                : (offlineEnricher = new AmiGotekMediaBuilder.Core.Metadata.OfflineEnricher()).Enrich(
                    groups, config.MetadataCacheDirectory, config.NfoDirectory,
                    config.CatalogDatabasePath);
            var catalog = new AmiGotekMediaBuilder.Core.Catalog.JsonlCatalog(config.CatalogDirectory);
            var runId = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var newScans = catalog.WriteScans(scans);
            var newParses = catalog.WriteParses(parsed);
            var writtenGroups = catalog.WriteGroups(groups, runId);
            var summary = new
            {
                run_id = runId, files_scanned = scans.Count, records_parsed = parsed.Length,
                groups = groups.Count, catalog_new_scan = newScans,
                catalog_new_parse = newParses, catalog_groups_written = writtenGroups,
                metadata_records = metadata.Count, online_requested = onlineRequested,
                artwork_downloaded = onlineEnricher?.ArtworkDownloaded ?? 0,
                artwork_fallback_used = onlineEnricher?.ArtworkFallbackUsed ?? offlineEnricher?.ArtworkFallbackUsed ?? 0,
                artwork_failed = onlineEnricher?.ArtworkFailed ?? 0,
                catalog_cache_hits = onlineEnricher?.CatalogCacheHits ?? 0,
                catalog_cache_misses = onlineEnricher?.CatalogCacheMisses ?? 0,
                catalog_database = config.CatalogDatabasePath
            };
            if (options.ContainsKey("json"))
                Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
            else
            {
                Console.WriteLine($"build source: {config.OriginalDirectory}");
                Console.WriteLine($"files scanned: {scans.Count}");
                Console.WriteLine($"groups: {groups.Count}");
                Console.WriteLine($"quarantined: {groups.Count(g => g.QuarantineReason is not null)}");
                if (onlineRequested)
                    Console.WriteLine($"artwork downloaded: {onlineEnricher?.ArtworkDownloaded ?? 0} " +
                                      $"(failed: {onlineEnricher?.ArtworkFailed ?? 0})");
                Console.WriteLine($"default artwork: {onlineEnricher?.ArtworkFallbackUsed ?? offlineEnricher?.ArtworkFallbackUsed ?? 0} game(s)");
                if (onlineRequested)
                    Console.WriteLine($"catalog cache: {onlineEnricher?.CatalogCacheHits ?? 0} hit(s), " +
                                      $"{onlineEnricher?.CatalogCacheMisses ?? 0} queried");
                Console.WriteLine($"catalog database: {config.CatalogDatabasePath}");
            }
            return Task.FromResult(0);
        }

        if (args[0].Equals("export", StringComparison.OrdinalIgnoreCase))
        {
            var onlineRequested = options.ContainsKey("online");
            var acknowledged = options.ContainsKey("export-gate-acknowledged");
            var width = ParseNullableInt(options.GetValueOrDefault("verified-artwork-width"));
            var height = ParseNullableInt(options.GetValueOrDefault("verified-artwork-height"));
            var runId = options.GetValueOrDefault("run-id") ??
                DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
            var groups = ReleaseGrouper.Group(parsed);
            if (onlineRequested)
                new AmiGotekMediaBuilder.Core.Metadata.HybridMetadataEnricher(
                    AmiGotekMediaBuilder.Core.Metadata.OnlineProviderFactory.CreateDefault(
                        gameBaseDatabasePath: config.GameBaseDatabasePath))
                    .EnrichAsync(groups, config.MetadataCacheDirectory, config.NfoDirectory,
                        catalogDatabasePath: config.CatalogDatabasePath).GetAwaiter().GetResult();
            else
                new AmiGotekMediaBuilder.Core.Metadata.OfflineEnricher().Enrich(
                    groups, config.MetadataCacheDirectory, config.NfoDirectory,
                    config.CatalogDatabasePath);
            var catalog = new AmiGotekMediaBuilder.Core.Catalog.JsonlCatalog(config.CatalogDirectory);
            catalog.WriteScans(scans);
            catalog.WriteParses(parsed);
            catalog.WriteGroups(groups, runId);
            var exported = GotekExporter.Export(
                groups, config.OriginalDirectory, config.StagingDirectory, runId,
                acknowledged, width, height, options.ContainsKey("verify-only"),
                config.NfoDirectory, config.ArtworkProcessedDirectory, config.ArtworkOriginalDirectory,
                rtfmDirectory: config.RtfmDirectory);
            if (options.ContainsKey("json"))
                Console.WriteLine(JsonSerializer.Serialize(exported, new JsonSerializerOptions { WriteIndented = true }));
            else
            {
                Console.WriteLine($"export gate: {(exported.ExportGateOpen ? "OPEN" : "BLOCKED")}");
                Console.WriteLine($"staging root: {exported.StagingRoot}");
                Console.WriteLine($"releases: {exported.ReleasesExported}");
                Console.WriteLine($"files written: {exported.FilesWritten.Count}");
                Console.WriteLine($"conflicts: {exported.Conflicts.Count}");
                foreach (var error in exported.Errors) Console.WriteLine($"error: {error}");
            }
            return exported.Errors.Count == 0 && exported.Conflicts.Count == 0 ? Task.FromResult(0) : Task.FromResult(1);
        }

        if (options.ContainsKey("json"))
            Console.WriteLine(JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            Console.WriteLine($"dry-run source: {config.OriginalDirectory}");
            Console.WriteLine($"files scanned: {records.Length}");
            foreach (var item in records)
                Console.WriteLine($"- {item.scan.filename} [{item.parsed.Title ?? "Unknown"}] {item.scan.sha256}");
        }
        return Task.FromResult(0);
    }
    catch (PathConfigException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return Task.FromResult(2);
    }
    catch (DirectoryNotFoundException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return Task.FromResult(1);
    }
}

static Dictionary<string, string?> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            throw new PathConfigException($"unexpected argument: {args[i]}");
        var key = args[i][2..];
        if (key is "json" or "verify-only" or "export-gate-acknowledged" or "online")
        {
            result[key] = null;
            continue;
        }
        if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            throw new PathConfigException($"option '--{key}' requires a value");
        result[key] = args[++i];
    }
    return result;
}

static int? ParseNullableInt(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    return int.TryParse(value, out var result) ? result :
        throw new PathConfigException($"invalid integer value: {value}");
}


static Task<int> RunInit(string[] args)
{
    try
    {
        var options = ParseOptions(args);
        var root = options.GetValueOrDefault("library-root")
            ?? throw new PathConfigException("--library-root is required");
        var config = PathConfig.Create(root, options.GetValueOrDefault("original-dir"),
            options.GetValueOrDefault("staging-dir"), options.GetValueOrDefault("output-dir"),
            options.GetValueOrDefault("quarantine-dir"), options.GetValueOrDefault("approvals-dir"),
            options.GetValueOrDefault("reports-dir"), options.GetValueOrDefault("logs-dir"),
            options.GetValueOrDefault("cache-dir"), options.GetValueOrDefault("demoscene-dir"),
            options.GetValueOrDefault("gamebase-db"));
        var configPath = options.GetValueOrDefault("config") ??
            Path.Combine(config.LibraryRoot, "config", "config.toml");
        PathConfigLoader.Write(configPath, config);
        foreach (var directory in new[]
        {
            config.CatalogDirectory, config.AssetsDirectory, config.ReviewDirectory,
            config.RejectedDirectory, config.StagingDirectory, config.OutputDirectory,
            config.QuarantineDirectory, config.ApprovalsDirectory, config.ReportsDirectory,
            config.LogsDirectory, config.DemosceneDirectory, config.DemosceneDownloadDirectory,
            config.DemosceneMetadataDirectory, config.DemosceneCacheDirectory,
            config.DemosceneArtworkOriginalDirectory, config.DemosceneArtworkProcessedDirectory
        }) Directory.CreateDirectory(directory);
        _ = new AmiGotekMediaBuilder.Core.Catalog.SqliteCatalogStore(config.CatalogDatabasePath);
        Console.WriteLine($"configuration written: {Path.GetFullPath(configPath)}");
        Console.WriteLine($"library root: {config.LibraryRoot}");
        Console.WriteLine($"source directory: {config.OriginalDirectory}");
        return Task.FromResult(0);
    }
    catch (PathConfigException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return Task.FromResult(2);
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"error: could not write configuration: {ex.Message}");
        return Task.FromResult(1);
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"error: could not write configuration: {ex.Message}");
        return Task.FromResult(1);
    }
}

static Task<int> RunConfig(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("error: config requires 'show' or 'validate'");
        return Task.FromResult(2);
    }
    try
    {
        var options = ParseOptions(args[1..]);
        var config = PathConfigLoader.Load(options.GetValueOrDefault("config"),
            options.Where(p => p.Key is not "config" and not "json")
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase));
        if (args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in new Dictionary<string, string>
            {
                ["library_root"] = config.LibraryRoot, ["original_dir"] = config.OriginalDirectory,
                ["staging_dir"] = config.StagingDirectory, ["output_dir"] = config.OutputDirectory,
                ["quarantine_dir"] = config.QuarantineDirectory, ["approvals_dir"] = config.ApprovalsDirectory,
                ["reports_dir"] = config.ReportsDirectory, ["logs_dir"] = config.LogsDirectory,
                ["cache_dir"] = config.CacheDirectory, ["demoscene_dir"] = config.DemosceneDirectory,
                ["catalog_db"] = config.CatalogDatabasePath,
                ["gamebase_db"] = config.GameBaseDatabasePath ?? ""
            }) Console.WriteLine($"{pair.Key}: {pair.Value}");
            return Task.FromResult(0);
        }
        if (args[0].Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(config.OriginalDirectory))
            {
                Console.Error.WriteLine($"invalid: source directory does not exist: {config.OriginalDirectory}");
                return Task.FromResult(1);
            }
            Console.WriteLine("configuration valid");
            return Task.FromResult(0);
        }
        Console.Error.WriteLine($"error: unknown config command '{args[0]}'");
        return Task.FromResult(2);
    }
    catch (PathConfigException ex)
    {
        Console.Error.WriteLine($"invalid: {ex.Message}");
        return Task.FromResult(1);
    }
}

static void PrintHelp() => Console.WriteLine("""
    AmiGotekMediaBuilder (C# preview)

    Commands:
      scan --library-root <path> [--config <file.toml>] [--original-dir <path>] [--json]
      build --library-root <path> [--config <file.toml>] [--original-dir <path>] [--json]
        [--online]  (public Hasheous + Playmatch + OpenRetro + Hall of Light + Wikipedia;
                     optional local GameBase DB)
        [--gamebase-db <path>]  (SQLite generated from a GameBase MDB database)
      export --library-root <path> --export-gate-acknowledged
        [--run-id <id>] [--verified-artwork-width <n>] [--verified-artwork-height <n>]
        [--verify-only] [--online] [--json]
    init --library-root <path> [--config <file.toml>] [--gamebase-db <path>]
      config show|validate --config <file.toml>
    """);

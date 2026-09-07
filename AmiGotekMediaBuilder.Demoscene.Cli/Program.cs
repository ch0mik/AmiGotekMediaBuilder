using System.Text.Json;
using AmiGotekMediaBuilder.Core.Configuration;
using AmiGotekMediaBuilder.Demoscene;

return await MainAsync(args);

static Task<int> MainAsync(string[] args)
{
    if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
    {
        PrintHelp();
        return Task.FromResult(0);
    }

    var command = args[0].ToLowerInvariant();
    if (command is not ("list" or "show" or "sync"))
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
        Directory.CreateDirectory(config.DemosceneDirectory);
        var platform = ParsePlatform(options.GetValueOrDefault("platform"));
        var query = new DemosceneQuery(
            platform,
            options.GetValueOrDefault("type"),
            ParseNullableInt(options.GetValueOrDefault("year-from")),
            ParseNullableInt(options.GetValueOrDefault("year-to")),
            options.GetValueOrDefault("search"),
            ParseInt(options.GetValueOrDefault("max-pages"), options.ContainsKey("all") ? 10_000 : 1),
            ParseInt(options.GetValueOrDefault("max-items"), options.ContainsKey("all") ? 100_000 : 100));

        using var pouet = new PouetCatalogProvider(new PouetCatalogOptions(
            Environment.GetEnvironmentVariable("POUET_BASE_URL") ?? "https://www.pouet.net"));
        using var demozoo = new DemozooCatalogProvider(new DemozooCatalogOptions(
            Environment.GetEnvironmentVariable("DEMOZOO_BASE_URL") ?? "https://demozoo.org"));

        if (command == "show")
        {
            var pouetId = options.GetValueOrDefault("pouet-id");
            var demozooId = options.GetValueOrDefault("demozoo-id");
            if (pouetId is null && demozooId is null)
                throw new PathConfigException("show requires --pouet-id <id> or --demozoo-id <id>");
            var production = pouetId is not null
                ? pouet.GetProductionAsync(pouetId).GetAwaiter().GetResult()
                : demozoo.GetProductionAsync(demozooId!).GetAwaiter().GetResult();
            if (production is null)
            {
                Console.Error.WriteLine($"not found or not an Amiga production: {pouetId ?? demozooId}");
                return Task.FromResult(1);
            }
            WriteProductions([production], options.ContainsKey("json"));
            return Task.FromResult(0);
        }

        var pouetProductions = pouet.BrowseAsync(query).GetAwaiter().GetResult();
        var demozooProductions = demozoo.BrowseAsync(query).GetAwaiter().GetResult();
        var productions = DemosceneCatalogMerger.Merge(pouetProductions, demozooProductions);
        if (command == "list")
        {
            WriteProductions(productions, options.ContainsKey("json"));
            return Task.FromResult(0);
        }

        new DemosceneMetadataStore(config.DemosceneMetadataDirectory)
            .WriteAsync(productions).GetAwaiter().GetResult();
        if (!options.ContainsKey("acknowledge-downloads") && !options.ContainsKey("yes"))
        {
            Console.WriteLine($"discovered {productions.Count} production(s); no files downloaded");
            Console.WriteLine("repeat with --acknowledge-downloads to start the batch");
            return Task.FromResult(0);
        }

        var destination = options.GetValueOrDefault("destination") ?? config.DemosceneDownloadDirectory;
        PathConfig.Create(config.LibraryRoot, config.OriginalDirectory, stagingDirectory: destination);
        using var downloader = new DemosceneDownloadService();
        var progress = new Progress<DemosceneDownloadProgress>(p =>
        {
            if (!options.ContainsKey("json"))
                Console.WriteLine($"[{p.Completed}/{p.Total}] {p.Production.Title}: {p.Result.Status}");
        });
        var downloaded = downloader.DownloadAsync(productions,
            new DemosceneDownloadOptions(
                destination,
                ParseLong(options.GetValueOrDefault("max-bytes-per-file"), 100L * 1024 * 1024),
                ParseLong(options.GetValueOrDefault("max-bytes-total"), 4L * 1024 * 1024 * 1024),
                ParseInt(options.GetValueOrDefault("max-concurrency"), 2),
                IncludeArchives: true),
            progress: progress).GetAwaiter().GetResult();
        using var artwork = new DemosceneArtworkService();
        var artworkResults = artwork.DownloadAsync(productions,
            config.DemosceneArtworkOriginalDirectory,
            config.DemosceneArtworkProcessedDirectory).GetAwaiter().GetResult();
        if (options.ContainsKey("json"))
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                productions = productions.Count,
                downloads = downloaded,
                artwork_downloaded = artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Downloaded),
                artwork_already_present = artworkResults.Count(r => r.Status == DemosceneArtworkStatus.AlreadyPresent),
                artwork_failed = artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Failed),
                artwork_skipped = artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Skipped)
            }, new JsonSerializerOptions { WriteIndented = true }));
        else
            Console.WriteLine($"demoscene download: {downloaded.Downloaded} downloaded, " +
                              $"{downloaded.AlreadyPresent} already present, {downloaded.Skipped} skipped, " +
                              $"{downloaded.Failed} failed ({downloaded.BytesDownloaded} bytes); " +
                              $"artwork {artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Downloaded)} downloaded");
        return Task.FromResult(downloaded.Failed == 0 ? 0 : 1);
    }
    catch (PathConfigException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return Task.FromResult(2);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return Task.FromResult(2);
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
        if (key is "json" or "all" or "acknowledge-downloads" or "yes")
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

static DemoscenePlatform ParsePlatform(string? value) =>
    string.IsNullOrWhiteSpace(value) ? DemoscenePlatform.None : DemoscenePlatforms.Parse(value);

static int? ParseNullableInt(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    return int.TryParse(value, out var result) ? result :
        throw new PathConfigException($"invalid integer value: {value}");
}

static int ParseInt(string? value, int fallback)
{
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    return int.TryParse(value, out var result) ? result :
        throw new PathConfigException($"invalid integer value: {value}");
}

static long ParseLong(string? value, long fallback)
{
    if (string.IsNullOrWhiteSpace(value)) return fallback;
    return long.TryParse(value, out var result) ? result :
        throw new PathConfigException($"invalid integer value: {value}");
}

static void WriteProductions(IReadOnlyList<DemosceneProduction> productions, bool json)
{
    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(productions, new JsonSerializerOptions { WriteIndented = true }));
        return;
    }
    foreach (var production in productions)
    {
        var platforms = string.Join(',', DemoscenePlatforms.Enumerate(production.Platforms).Select(p => p.ToString()));
        Console.WriteLine($"{production.Catalog}:{production.PouetId}: {production.Title} [{platforms}] {production.Year ?? "n/a"} " +
                          $"downloads={production.Downloads.Count}");
    }
}

static void PrintHelp() => Console.WriteLine("""
    AmiGotekMediaBuilder Demoscene (C# preview)

    Commands:
      list [--platform ocs-ecs|aga|ppc-rtg] [--type <type>] [--year-from <yyyy>] [--year-to <yyyy>]
        [--search <text>] [--max-pages <n>] [--max-items <n>] [--config <file.toml>] [--json]
      show --pouet-id <id> [--config <file.toml>] [--json]
      show --demozoo-id <id> [--config <file.toml>] [--json]
      sync [same filters] [--all] --acknowledge-downloads [--destination <dir>]
        [--max-concurrency <n>] [--max-items <n>] [--max-pages <n>]
        [--max-bytes-per-file <n>] [--max-bytes-total <n>] [--config <file.toml>] [--json]

    Without --acknowledge-downloads, sync only discovers productions.
    Demozoo contributes only type=demo productions and de-duplicates linked Pouët records.
    """);

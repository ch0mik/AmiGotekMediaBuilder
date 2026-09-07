using AmiGotekMediaBuilder.Core.Configuration;
using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Grouping;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Naming;
using AmiGotekMediaBuilder.Core.Parsing;
using AmiGotekMediaBuilder.Core.Scanning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AmiGotekMediaBuilder.Gui;

public partial class MainWindow : Window
{
    private bool _scanCompleted;
    private bool _buildCompleted;
    private bool _loadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        LoadSettings();
        ScreenCombo.ItemsSource = GotekScreenProfile.Supported;
        ScreenCombo.SelectedItem = GotekScreenProfile.Default;
        SetWorkflowState(scanCompleted: false, buildCompleted: false);
        RefreshProviderStatus();
        CurrentActivityText.Text = "Waiting for Scan.";
        StatusText.Text = "Run Scan to enable Build.";
        AppendLog("Ready. Scan the source directory to continue.");
    }

    private void SetWorkflowState(bool scanCompleted, bool buildCompleted)
    {
        _scanCompleted = scanCompleted;
        _buildCompleted = buildCompleted && scanCompleted;
        BuildButton.IsEnabled = _scanCompleted;
        ExportButton.IsEnabled = _buildCompleted;
    }

    private void LoadSettings()
    {
        var settings = GuiSettingsStore.Load();
        _loadingSettings = true;
        try
        {
            LibraryRootText.Text = string.IsNullOrWhiteSpace(settings.SourceDirectory)
                ? Environment.GetEnvironmentVariable("AMIGA_ADF_LIBRARY_ROOT") ?? string.Empty
                : settings.SourceDirectory;
            WorkingDirectoryText.Text = string.IsNullOrWhiteSpace(settings.WorkingDirectory)
                ? string.Empty
                : NormalizeWorkingDirectory(Path.GetFullPath(settings.WorkingDirectory));
            DestinationDirectoryText.Text = settings.DestinationDirectory;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_loadingSettings) return;
        GuiSettingsStore.Save(new GuiSettings
        {
            SourceDirectory = LibraryRootText.Text?.Trim() ?? string.Empty,
            WorkingDirectory = WorkingDirectoryText.Text?.Trim() ?? string.Empty,
            DestinationDirectory = DestinationDirectoryText.Text?.Trim() ?? string.Empty
        });
    }

    private void PathChanged(object? sender, TextChangedEventArgs e)
    {
        // Changing any path invalidates the previous scan and build.
        SetWorkflowState(scanCompleted: false, buildCompleted: false);
        SaveSettings();
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text = string.IsNullOrWhiteSpace(LogText.Text)
            ? $"[{timestamp}] {message}"
            : $"{LogText.Text}{Environment.NewLine}[{timestamp}] {message}";
    }

    private void SetCurrentActivity(string message) => CurrentActivityText.Text = message;

    private static bool IsDetailedActivity(string message) =>
        message.StartsWith("Scanning (", StringComparison.Ordinal) ||
        message.StartsWith("Build scan (", StringComparison.Ordinal) ||
        message.StartsWith("Export scan (", StringComparison.Ordinal) ||
        message.StartsWith("Resolving metadata [", StringComparison.Ordinal) ||
        message.StartsWith("Downloading artwork [", StringComparison.Ordinal) ||
        message.StartsWith("Completed metadata [", StringComparison.Ordinal) ||
        message.StartsWith("Exporting [", StringComparison.Ordinal);

    private void RefreshProviderStatus()
    {
        ProviderStatusText.Text = $"Artwork providers (enabled): {string.Join(", ", OnlineProviderFactory.DefaultProviderIds.Select(DisplayProviderName))}, Default artwork";
    }

    private static string DisplayProviderName(string id) => id switch
    {
        "hasheous" => "Hasheous",
        "playmatch" => "Playmatch",
        "openretro" => "OpenRetro",
        "gamebase" => "GameBase",
        "hall-of-light" => "Hall of Light",
        "wikipedia" => "Wikipedia",
        "default-artwork" => "Default artwork",
        _ => id
    };

    private async void BrowseRoot(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select the source directory to scan");
        if (path is not null) LibraryRootText.Text = path;
    }

    private async void BrowseWorking(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select the working directory for assets and catalog");
        if (path is not null) WorkingDirectoryText.Text = path;
    }

    private async void BrowseDestination(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync("Select the export destination directory");
        if (path is not null) DestinationDirectoryText.Text = path;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return null;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private async void ScanClick(object? sender, RoutedEventArgs e)
    {
        SetWorkflowState(scanCompleted: false, buildCompleted: false);
        var completed = await RunOperation(async (config, progress, currentActivity) =>
        {
            progress.Report((-1, "Scanning source directory…"));
            var scanProgress = new Progress<IntakeScanner.ScanProgress>(update =>
                currentActivity.Report($"Scanning ({update.Phase}): {update.Path}"));
            var scans = await Task.Run(() => IntakeScanner.ScanDirectories(
                [config.OriginalDirectory], config.IntakeExcludedDirectories, scanProgress));
            progress.Report((45, $"Found {scans.Count} supported file(s)."));
            var parsed = scans.Select(s =>
            {
                var result = FilenameParser.Parse(s.Filename);
                result.SourcePath = s.Path;
                result.SourceSha256 = s.Sha256;
                return result;
            }).ToArray();
            progress.Report((75, "Grouping releases and checking names…"));
            var groups = ReleaseGrouper.Group(parsed);
            progress.Report((95, $"Prepared {groups.Count} release group(s)."));
            return ($"{scans.Count} file(s), {groups.Count} group(s), " +
                    $"{groups.Count(g => g.QuarantineReason is not null)} quarantined",
                groups.Select(g => $"{g.Title ?? "Unknown"} [{g.Extension}] " +
                                   (g.QuarantineReason ?? "ready"))
                    .Select(text => new ResultItem(text)).ToArray());
        });
        if (completed)
        {
            SetWorkflowState(scanCompleted: true, buildCompleted: false);
            AppendLog("Scan completed. Build is now enabled.");
        }
    }

    private async void BuildClick(object? sender, RoutedEventArgs e)
    {
        if (!_scanCompleted)
        {
            StatusText.Text = "Run Scan successfully before Build.";
            AppendLog("Build blocked: run Scan successfully first.");
            return;
        }

        SetWorkflowState(scanCompleted: true, buildCompleted: false);
        var completed = await BuildOperation(OnlineRequested.IsChecked == true);
        if (completed)
        {
            SetWorkflowState(scanCompleted: true, buildCompleted: true);
            AppendLog("Build completed. Export is now enabled.");
        }
    }

    private async Task<bool> BuildOperation(bool online) =>
        await RunOperation(async (config, progress, currentActivity) =>
        {
            progress.Report((-1, "Scanning source directory for build…"));
            var scanProgress = new Progress<IntakeScanner.ScanProgress>(update =>
                currentActivity.Report($"Build scan ({update.Phase}): {update.Path}"));
            var scans = await Task.Run(() => IntakeScanner.ScanDirectories(
                [config.OriginalDirectory], config.IntakeExcludedDirectories, scanProgress));
            progress.Report((25, $"Found {scans.Count} supported file(s)."));
            var parsed = scans.Select(s =>
            {
                var result = FilenameParser.Parse(s.Filename);
                result.SourcePath = s.Path;
                result.SourceSha256 = s.Sha256;
                return result;
            }).ToArray();
            progress.Report((40, "Grouping releases…"));
            var groups = ReleaseGrouper.Group(parsed);
            progress.Report(online
                ? (50, "Fetching online metadata and artwork…")
                : (50, "Writing offline metadata…"));
            var providers = OnlineProviderFactory.CreateDefault(
                gameBaseDatabasePath: config.GameBaseDatabasePath);
            HybridMetadataEnricher? enricher = null;
            OfflineEnricher? offlineEnricher = null;
            var enrichmentProgress = new Progress<OnlineEnrichmentProgress>(update =>
            {
                var phase = update.Phase switch
                {
                    "artwork" when string.Equals(update.Provider, DefaultArtworkService.ProviderId,
                        StringComparison.OrdinalIgnoreCase) => "Using default artwork",
                    "artwork" => "Downloading artwork",
                    "provider-error" => "Provider error",
                    "artwork-error" => "Artwork download error",
                    "fallback-error" => "Fallback artwork error",
                    "completed" => "Completed metadata",
                    _ => "Resolving metadata"
                };
                var provider = string.IsNullOrWhiteSpace(update.Provider)
                    ? string.Empty
                    : $" via {DisplayProviderName(update.Provider)}";
                var error = string.IsNullOrWhiteSpace(update.Error) ? string.Empty : $" — {update.Error}";
                var message = $"{phase} [{update.Current}/{update.Total}] {update.Title}{provider}{error}";
                var percent = update.Total == 0
                    ? 85
                    : 50 + 40d * update.Current / update.Total;
                progress.Report((percent, message));
            });
            var metadata = online
                ? await (enricher = new HybridMetadataEnricher(providers))
                    .EnrichAsync(groups, config.MetadataCacheDirectory, config.NfoDirectory,
                        progress: enrichmentProgress,
                        catalogDatabasePath: config.CatalogDatabasePath)
                : await Task.Run(() => (offlineEnricher = new OfflineEnricher()).Enrich(
                    groups, config.MetadataCacheDirectory, config.NfoDirectory,
                    config.CatalogDatabasePath));
            progress.Report((90, $"Build wrote {metadata.Count} metadata record(s)."));
            var metadataByKey = metadata
                .GroupBy(record => record.ReleaseKey, StringComparer.Ordinal)
                .ToDictionary(grouping => grouping.Key, grouping => grouping.Last(), StringComparer.Ordinal);
            return ($"{scans.Count} file(s), {groups.Count} group(s), " +
                    $"{metadata.Count} metadata record(s) written" +
                    $", default artwork: {(enricher?.ArtworkFallbackUsed ?? offlineEnricher?.ArtworkFallbackUsed ?? 0)}" +
                    (online ? $"; artwork downloaded: {enricher?.ArtworkDownloaded ?? 0}" : "") +
                    (online ? $", catalog cache: {enricher?.CatalogCacheHits ?? 0} hit(s), " +
                              $"{enricher?.CatalogCacheMisses ?? 0} queried" : "") +
                    (online ? $" ({config.ArtworkOriginalDirectory})" : "") +
                    (online ? ", public artwork providers enabled" : ""),
                groups.Select(g =>
                {
                    metadataByKey.TryGetValue(g.ReleaseKey, out var record);
                    var artworkPath = g.IsDemoscene ? null :
                        FindArtwork(config.ArtworkProcessedDirectory, ReleaseNamer.GetBasename(g),
                            config.ArtworkOriginalDirectory);
                    var title = $"{g.Title ?? "Unknown"} → {ReleaseNamer.GetBasename(g)}";
                    var provider = record?.ArtworkProvider is { Length: > 0 } artworkProvider
                        ? $" [{artworkProvider} artwork]"
                        : string.Empty;
                    return new ResultItem(title + provider, LoadImage(artworkPath));
                }).ToArray());
        });

    private async void ExportClick(object? sender, RoutedEventArgs e)
    {
        if (!_scanCompleted)
        {
            StatusText.Text = "Run Scan successfully before Export.";
            AppendLog("Export blocked: run Scan successfully first.");
            return;
        }

        if (!_buildCompleted)
        {
            StatusText.Text = "Run Build successfully before Export.";
            AppendLog("Export blocked: run Build successfully first.");
            return;
        }

        var screen = ScreenCombo.SelectedItem as GotekScreenProfile ?? GotekScreenProfile.Default;
        await ExportOperation(GateAcknowledged.IsChecked == true, RunId.Text ?? "gui-run", screen);
    }

    private async Task<bool> ExportOperation(bool gateAcknowledged,
        string runIdText, GotekScreenProfile screen) =>
        await RunOperation(async (config, progress, currentActivity) =>
        {
            progress.Report((-1, "Scanning source directory for export…"));
            var scanProgress = new Progress<IntakeScanner.ScanProgress>(update =>
                currentActivity.Report($"Export scan ({update.Phase}): {update.Path}"));
            var scans = await Task.Run(() => IntakeScanner.ScanDirectories(
                [config.OriginalDirectory], config.IntakeExcludedDirectories, scanProgress));
            progress.Report((30, $"Found {scans.Count} supported file(s)."));
            var parsed = scans.Select(s =>
            {
                var result = FilenameParser.Parse(s.Filename);
                result.SourcePath = s.Path;
                result.SourceSha256 = s.Sha256;
                return result;
            }).ToArray();
            progress.Report((45, "Grouping releases…"));
            var groups = ReleaseGrouper.Group(parsed);
            var stagingRunDirectory = Path.Combine(config.StagingDirectory, runIdText);
            progress.Report((60, $"Exporting {groups.Count} release group(s) to {stagingRunDirectory}…"));
            var exportProgress = new Progress<GotekExporter.ExportProgress>(update =>
            {
                var percent = update.Total == 0
                    ? 95
                    : 60 + 35d * update.Current / update.Total;
                progress.Report((percent,
                    $"Exporting [{update.Current}/{update.Total}] {update.Title}"));
            });
            var result = await Task.Run(() => GotekExporter.Export(
                groups, config.OriginalDirectory, config.StagingDirectory,
                runIdText, gateAcknowledged, screen.Width, screen.Height,
                nfoDirectory: config.NfoDirectory,
                artworkProcessedDirectory: config.ArtworkProcessedDirectory,
                artworkOriginalDirectory: config.ArtworkOriginalDirectory,
                progress: exportProgress));
            progress.Report((95, $"Wrote {result.FilesWritten.Count} file(s)."));
            return ($"Exported {result.ReleasesExported} release(s); " +
                    $"{result.FilesWritten.Count} file(s), {result.Conflicts.Count} conflict(s), " +
                    $"{result.SkippedQuarantined.Count} skipped, {result.Errors.Count} error(s); " +
                    $"screen: {screen.DisplayName}; staging: {result.StagingRoot}",
                result.FilesWritten.Concat(result.Conflicts)
                    .Concat(result.SkippedQuarantined.Select(s => $"SKIPPED: {s}"))
                    .Concat(result.Errors.Select(s => $"ERROR: {s}"))
                    .Select(text => new ResultItem(text)).ToArray());
        });

    private async Task<bool> RunOperation(
        Func<PathConfig, IProgress<(double Percent, string Message)>,
            IProgress<string>, Task<(string Summary, IReadOnlyList<ResultItem> Items)>> operation)
    {
        var progress = new Progress<(double Percent, string Message)>(update =>
        {
            OperationProgress.IsIndeterminate = update.Percent < 0;
            if (!OperationProgress.IsIndeterminate)
                OperationProgress.Value = Math.Clamp(update.Percent, 0, 100);
            SetCurrentActivity(update.Message);
            if (!IsDetailedActivity(update.Message))
                AppendLog(update.Message);
        });
        var currentActivity = new Progress<string>(SetCurrentActivity);

        LogText.Text = string.Empty;
        OperationProgress.IsIndeterminate = false;
        OperationProgress.Value = 0;
        SetCurrentActivity("Preparing operation…");
        try
        {
            var config = CreateGuiPathConfig(
                LibraryRootText.Text ?? string.Empty,
                WorkingDirectoryText.Text ?? string.Empty,
                DestinationDirectoryText.Text ?? string.Empty,
                Environment.GetEnvironmentVariable("AMIGA_ADF_GAMEBASE_DB"));
            if (!Directory.Exists(config.OriginalDirectory))
                throw new DirectoryNotFoundException($"Selected source directory does not exist: {config.OriginalDirectory}");
            AppendLog($"Library root: {config.LibraryRoot}");
            AppendLog($"Source: {config.OriginalDirectory}");
            AppendLog($"Working: {config.LibraryRoot} (assets: {config.AssetsDirectory}; catalog: {config.CatalogDirectory})");
            AppendLog($"Destination: {config.StagingDirectory}");
            StatusText.Text = "Working…";
            ResultsList.ItemsSource = null;
            var result = await Task.Run(() => operation(config, progress, currentActivity));
            ResultsList.ItemsSource = result.Items;
            StatusText.Text = result.Summary;
            OperationProgress.Value = 100;
            AppendLog(result.Summary);
            return true;
        }
        catch (Exception ex) when (ex is PathConfigException or IOException or ArgumentException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
            AppendLog($"ERROR: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolves the GUI paths. The working directory is the base for assets
    /// and catalog, while the destination directory is the staging base.
    /// When working is empty, an existing source such as
    /// <c>...\\original\\gry</c> still infers its library root automatically.
    /// </summary>
    private static PathConfig CreateGuiPathConfig(
        string selectedSource,
        string selectedWorkingDirectory,
        string selectedDestinationDirectory,
        string? gameBaseDatabasePath)
    {
        if (string.IsNullOrWhiteSpace(selectedSource))
            return PathConfig.Create(selectedSource, gameBaseDatabasePath: gameBaseDatabasePath);

        var sourcePath = Path.GetFullPath(selectedSource);
        var libraryRoot = string.IsNullOrWhiteSpace(selectedWorkingDirectory)
            ? InferLibraryRoot(sourcePath)
            : NormalizeWorkingDirectory(Path.GetFullPath(selectedWorkingDirectory));
        var originalDirectory = string.Equals(sourcePath, libraryRoot, StringComparison.OrdinalIgnoreCase)
            ? null
            : sourcePath;
        var stagingDirectory = string.IsNullOrWhiteSpace(selectedDestinationDirectory)
            ? null
            : Path.GetFullPath(selectedDestinationDirectory);

        return PathConfig.Create(libraryRoot,
            originalDirectory: originalDirectory,
            stagingDirectory: stagingDirectory,
            gameBaseDatabasePath: gameBaseDatabasePath);
    }

    private static string InferLibraryRoot(string sourcePath)
    {
        var source = new DirectoryInfo(sourcePath);

        // Check the source ancestry first.  A previous run may have created
        // catalog/assets/work inside the selected source; those directories
        // must not hide the real library root.
        for (var current = source; current is not null; current = current.Parent)
        {
            if (current.Name.Equals("original", StringComparison.OrdinalIgnoreCase) &&
                current.Parent?.Parent is not null)
                return current.Parent.FullName;
        }

        for (var current = source; current is not null; current = current.Parent)
        {
            if (HasLibraryLayout(current))
                return current.FullName;
        }

        // A new, uninitialised directory is both the source and the library
        // root, preserving the previous GUI behaviour.
        return sourcePath;
    }

    private static string NormalizeWorkingDirectory(string selectedPath)
    {
        var selected = new DirectoryInfo(selectedPath);
        var parent = selected.Parent;
        if (parent is null) return selectedPath;

        // The field represents the base containing assets/catalog/work. Users
        // often select an existing "assets" or "catalog" child instead. If
        // its parent has the managed layout, use that parent to avoid creating
        // assets\assets and catalog\catalog trees.
        var selectedName = selected.Name;
        var isManagedChild = selectedName.Equals("assets", StringComparison.OrdinalIgnoreCase) ||
                             selectedName.Equals("catalog", StringComparison.OrdinalIgnoreCase) ||
                             (selectedName.Equals("artwork-original", StringComparison.OrdinalIgnoreCase) &&
                              parent.Name.Equals("assets", StringComparison.OrdinalIgnoreCase)) ||
                             (selectedName.Equals("artwork-processed", StringComparison.OrdinalIgnoreCase) &&
                              parent.Name.Equals("assets", StringComparison.OrdinalIgnoreCase));
        var layoutRoot = selectedName.Equals("artwork-original", StringComparison.OrdinalIgnoreCase) ||
                         selectedName.Equals("artwork-processed", StringComparison.OrdinalIgnoreCase)
            ? parent.Parent
            : parent;
        if (isManagedChild && layoutRoot is not null && HasLibraryLayout(layoutRoot) &&
            (Directory.Exists(Path.Combine(layoutRoot.FullName, "original")) ||
             Directory.Exists(Path.Combine(layoutRoot.FullName, "work"))))
            return layoutRoot.FullName;

        return selectedPath;
    }

    private static bool HasLibraryLayout(DirectoryInfo directory) =>
        new[] { "catalog", "assets", "work", "config" }
            .Any(name => Directory.Exists(Path.Combine(directory.FullName, name)));

    private static string? FindArtwork(string directory, string basename, string? provenanceDirectory = null)
    {
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
        {
            var path = Path.Combine(directory, basename + extension);
            if (File.Exists(path) && !IsDemosceneArtwork(path, basename, provenanceDirectory)) return path;
        }
        return null;
    }

    private static bool IsDemosceneArtwork(string candidate, string basename, string? provenanceDirectory)
    {
        var sidecars = new List<string> { candidate + ".source.json" };
        if (!string.IsNullOrWhiteSpace(provenanceDirectory))
            foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" })
                sidecars.Add(Path.Combine(provenanceDirectory, basename + extension + ".source.json"));

        foreach (var sidecar in sidecars.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(sidecar)) continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(sidecar));
                var root = document.RootElement;
                if (HasDemosceneValue(root, "catalog") || HasDemosceneValue(root, "provider"))
                    return true;
            }
            catch (JsonException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return false;
    }

    private static bool HasDemosceneValue(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        (text.Equals("pouet", StringComparison.OrdinalIgnoreCase) ||
         text.Equals("demozoo", StringComparison.OrdinalIgnoreCase));

    private static Bitmap? LoadImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return new Bitmap(stream);
        }
        catch (Exception) { return null; }
    }

    private sealed class ResultItem
    {
        public ResultItem(string text, Bitmap? thumbnail = null)
        {
            Text = text;
            Thumbnail = thumbnail;
        }

        public string Text { get; }
        public Bitmap? Thumbnail { get; }
    }
}

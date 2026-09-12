using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Grouping;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Naming;
using AmiGotekMediaBuilder.Core.Networking;
using AmiGotekMediaBuilder.Core.Parsing;
using AmiGotekMediaBuilder.Core.Scanning;
using AmiGotekMediaBuilder.Demoscene;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AmiGotekMediaBuilder.Demoscene.Gui;

public partial class DemosceneWindow : Window
{
    private IReadOnlyList<DemosceneProduction> _productions = [];
    private IReadOnlyList<AmiGotekMediaBuilder.Core.Models.ReleaseGroup> _scannedGroups = [];
    private IReadOnlyDictionary<string, string> _artworkByProductionId = new Dictionary<string, string>();
    private CancellationTokenSource? _operationCts;
    private bool _loadingSettings;

    public DemosceneWindow() : this(string.Empty)
    {
    }

    public DemosceneWindow(string libraryRoot)
    {
        InitializeComponent();
        LoadSettings(libraryRoot);
        ScreenCombo.ItemsSource = GotekScreenProfile.Supported;
        ScreenCombo.SelectedItem = GotekScreenProfile.Default;
        CurrentActivityText.Text = "Waiting for a catalog search.";
    }

    private void LoadSettings(string libraryRoot)
    {
        var settings = DemosceneGuiSettingsStore.Load();
        _loadingSettings = true;
        try
        {
            LibraryRootText.Text = !string.IsNullOrWhiteSpace(libraryRoot)
                ? libraryRoot
                : !string.IsNullOrWhiteSpace(settings.LibraryDirectory)
                    ? settings.LibraryDirectory
                    : Environment.GetEnvironmentVariable("AMIGA_ADF_LIBRARY_ROOT") ?? string.Empty;
            ExportDirectoryText.Text = settings.ExportDirectory;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    private void PathChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        DemosceneGuiSettingsStore.Save(new DemosceneGuiSettings
        {
            LibraryDirectory = LibraryRootText.Text?.Trim() ?? string.Empty,
            ExportDirectory = ExportDirectoryText.Text?.Trim() ?? string.Empty
        });
    }

    private void AppendLog(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogText.Text = string.IsNullOrWhiteSpace(LogText.Text)
            ? $"[{timestamp}] {message}"
            : $"{LogText.Text}{Environment.NewLine}[{timestamp}] {message}";
    }

    private void SetProgress(double value, string? message = null, bool addToLog = true)
    {
        OperationProgress.IsIndeterminate = value < 0;
        if (!OperationProgress.IsIndeterminate)
            OperationProgress.Value = Math.Clamp(value, 0, 100);
        if (!string.IsNullOrWhiteSpace(message))
        {
            CurrentActivityText.Text = message;
            if (addToLog)
                AppendLog(message);
        }
    }

    private void SetCurrentActivity(string message) => CurrentActivityText.Text = message;

    private CancellationTokenSource? BeginCancellableOperation()
    {
        if (_operationCts is not null)
        {
            StatusText.Text = "Another operation is already running. Select Cancel first.";
            return null;
        }
        _operationCts = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        return _operationCts;
    }

    private void EndCancellableOperation(CancellationTokenSource operation)
    {
        if (!ReferenceEquals(_operationCts, operation)) return;
        _operationCts.Dispose();
        _operationCts = null;
        CancelButton.IsEnabled = false;
    }

    private async void BrowseRoot(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select a library folder to scan", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) LibraryRootText.Text = path;
    }

    private async void BrowseExportDirectory(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select the Gotek export directory", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) ExportDirectoryText.Text = path;
    }

    private async void ScanLibraryClick(object? sender, RoutedEventArgs e)
    {
        var source = LibraryRootText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            StatusText.Text = "Select an existing library folder before scanning.";
            return;
        }

        var operation = BeginCancellableOperation();
        if (operation is null) return;
        try
        {
            _scannedGroups = [];
            ExportScannedButton.IsEnabled = false;
            SetProgress(-1, $"Scanning {source} for ADF/DSK images…");
            var scanProgress = new UiProgress<IntakeScanner.ScanProgress>(update =>
                SetCurrentActivity($"Scanning ({update.Phase}): {update.Path}"));
            var scans = await Task.Run(() => IntakeScanner.ScanDirectory(
                source, progress: scanProgress, cancellationToken: operation.Token), operation.Token);
            SetCurrentActivity("Grouping scanned disk images…");
            _scannedGroups = ReleaseGrouper.Group(scans.Select(scan =>
            {
                var record = FilenameParser.Parse(scan.Filename);
                record.SourcePath = scan.Path;
                record.SourceSha256 = scan.Sha256;
                record.IsDemoscene = true;
                return record;
            }));
            ExportScannedButton.IsEnabled = _scannedGroups.Count > 0;
            LocalCountText.Text = $"{scans.Count} image(s), {_scannedGroups.Count} release(s)";
            LocalStatusText.Text = _scannedGroups.Count == 0
                ? "No ADF/DSK files found in the selected library folder."
                : $"Scan complete. Choose an export directory, then select Export scanned.";
            StatusText.Text = LocalStatusText.Text;
            SetProgress(100, LocalStatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Library scan cancelled.";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = ex.Message;
            AppendLog($"ERROR: {ex.Message}");
        }
        finally { EndCancellableOperation(operation); }
    }

    private void ScreenChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Avalonia can raise SelectionChanged while InitializeComponent is
        // still wiring up controls. StatusText is declared later in XAML and
        // is not available during that initial notification.
        if (StatusText is not null && ScreenCombo.SelectedItem is GotekScreenProfile screen)
            StatusText.Text = $"Preview screen: {screen.DisplayName}. Select Search catalogs to refresh thumbnails.";
    }

    private async void SearchClick(object? sender, RoutedEventArgs e)
    {
        LogText.Text = string.Empty;
        OperationProgress.Value = 0;
        var operation = BeginCancellableOperation();
        if (operation is null) return;
        try
        {
            var cancellationToken = operation.Token;
            var platform = PlatformCombo.SelectedItem is ComboBoxItem item
                ? DemoscenePlatforms.Parse(item.Tag?.ToString() ?? "all")
                : DemoscenePlatform.None;
            var screen = ScreenCombo.SelectedItem as GotekScreenProfile ?? GotekScreenProfile.Default;
            if (!int.TryParse(MaxItemsText.Text, out var maxItems) || maxItems < 1)
                throw new ArgumentException("Max items must be a positive integer.");
            AppendLog("Starting Pouët + Demozoo catalog search…");
            SetProgress(10, $"Searching for up to {maxItems} production(s)…");
            StatusText.Text = "Loading Pouët and Demozoo production lists…";
            ProductionsList.ItemsSource = null;
            // This application is dedicated to demo productions.  Keep the
            // provider filter fixed instead of exposing a redundant type box.
            var query = new DemosceneQuery(platform, Type: "demo", Search: SearchText.Text,
                MaxPages: Math.Max(1, (int)Math.Ceiling(maxItems / 50d)), MaxItems: maxItems);
            var cacheDirectory = GetDemosceneCacheDirectory();
            using var pouetClient = CreateDemosceneHttpClient();
            using var demozooClient = CreateDemosceneHttpClient();
            using var artworkClient = CreateDemosceneHttpClient();
            using var pouet = new PouetCatalogProvider(new PouetCatalogOptions(
                Environment.GetEnvironmentVariable("POUET_BASE_URL") ?? "https://www.pouet.net",
                MaxConcurrency: 2, RequestDelay: TimeSpan.Zero), pouetClient);
            using var demozoo = new DemozooCatalogProvider(new DemozooCatalogOptions(
                Environment.GetEnvironmentVariable("DEMOZOO_BASE_URL") ?? "https://demozoo.org",
                MaxConcurrency: 2, RequestDelay: TimeSpan.Zero), demozooClient);
            var pouetTask = pouet.BrowseAsync(query, cancellationToken);
            var demozooTask = demozoo.BrowseAsync(query, cancellationToken);
            SetProgress(-1, "Waiting for catalog responses…");
            var productions = DemosceneCatalogMerger.Merge(await pouetTask, await demozooTask);
            SetProgress(55, $"Merged {productions.Count} unique production(s).");
            await new DemosceneMetadataStore(Path.Combine(cacheDirectory, "metadata")).WriteAsync(productions, cancellationToken);
            SetProgress(-1, "Metadata saved; downloading thumbnails…");
            StatusText.Text = $"Found {productions.Count} production(s); downloading thumbnails…";
            using var artwork = new DemosceneArtworkService(artworkClient);
            var artworkResults = await Task.Run(() => artwork.DownloadAsync(
                productions, Path.Combine(cacheDirectory, "thumbnails-original"),
                Path.Combine(cacheDirectory, "thumbnails-processed"), cancellationToken), cancellationToken);
            SetProgress(85, $"Processed {artworkResults.Count} thumbnail result(s).");
            _productions = productions;
            _artworkByProductionId = artworkResults
                .Where(result => !string.IsNullOrWhiteSpace(result.ProcessedPath))
                .ToDictionary(result => result.PouetId, result => result.ProcessedPath!, StringComparer.Ordinal);
            var artworkById = artworkResults.ToDictionary(r => r.PouetId, StringComparer.Ordinal);
            var items = productions.Select(production =>
            {
                artworkById.TryGetValue(production.PouetId, out var art);
                return new DemosceneItem(production, LoadImage(art?.ProcessedPath), screen);
            }).ToArray();
            ProductionsList.ItemsSource = items;
            OnlineCountText.Text = $"{productions.Count} result(s)";
            StatusText.Text = $"Found {productions.Count} production(s); " +
                              $"thumbnails: {artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Downloaded)} downloaded, " +
                              $"{artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Skipped)} missing; " +
                              $"preview: {screen.Width}×{screen.Height}.";
            SetProgress(100, StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Catalog search cancelled.";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
            CurrentActivityText.Text = $"Error: {ex.Message}";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally { EndCancellableOperation(operation); }
    }

    private async void DownloadSelectedClick(object? sender, RoutedEventArgs e)
    {
        var selected = ProductionsList.SelectedItems is { } selectedItems
            ? selectedItems.OfType<DemosceneItem>().Select(i => i.Production).ToArray()
            : [];
        await DownloadAsync(selected);
    }

    private async void DownloadAllClick(object? sender, RoutedEventArgs e) => await DownloadAsync(_productions);

    private async void ExportScannedClick(object? sender, RoutedEventArgs e)
    {
        if (_scannedGroups.Count == 0)
        {
            StatusText.Text = "Scan a library folder before exporting local files.";
            return;
        }
        if (!TryGetExportDirectory(out var exportDirectory)) return;

        var operation = BeginCancellableOperation();
        if (operation is null) return;
        try
        {
            // Avalonia controls may only be read on the UI thread. Capture
            // the selected source before moving the export work to Task.Run.
            var sourceDirectory = LibraryRootText.Text!;
            var screen = ScreenCombo.SelectedItem as GotekScreenProfile ?? GotekScreenProfile.Default;
            var exportableGroups = _scannedGroups.Where(group => group.QuarantineReason is null).ToArray();
            var skippedGroups = _scannedGroups.Where(group => group.QuarantineReason is not null).ToArray();
            AppendSkippedGroupsToLog(skippedGroups);
            if (exportableGroups.Length == 0)
            {
                StatusText.Text = $"No complete release sets to export; {skippedGroups.Length} skipped.";
                SetProgress(100, StatusText.Text);
                return;
            }
            var artworkByRelease = await ScrapeScannedArtworkAsync(exportableGroups, operation.Token);
            SetProgress(-1, $"Exporting {exportableGroups.Length} scanned release(s)…");
            var progress = new UiProgress<GotekExporter.ExportProgress>(update =>
                SetCurrentActivity($"Exporting [{update.Current}/{update.Total}]: {update.Title}"));
            var result = await Task.Run(() => GotekExporter.Export(
                exportableGroups, sourceDirectory, exportDirectory, "demoscene",
                upstreamTaskClosed: true, verifiedArtworkWidth: screen.Width, verifiedArtworkHeight: screen.Height,
                progress: progress, exportToDestinationRoot: true, cancellationToken: operation.Token), operation.Token);
            var copiedArtwork = CopyScannedArtwork(exportableGroups, artworkByRelease, exportDirectory, operation.Token);
            StatusText.Text = $"Exported {result.ReleasesExported} release(s) to {exportDirectory}; " +
                              $"{result.FilesWritten.Count} file(s) written, {copiedArtwork} artwork file(s), " +
                              $"{result.Conflicts.Count} conflict(s), {skippedGroups.Length} skipped.";
            SetProgress(100, StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Local export cancelled.";
            AppendLog(StatusText.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            CurrentActivityText.Text = $"Error: {ex.Message}";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally { EndCancellableOperation(operation); }
    }

    private async Task DownloadAsync(IReadOnlyList<DemosceneProduction> productions)
    {
        if (productions.Count == 0)
        {
            StatusText.Text = "Search for productions first.";
            AppendLog("Download blocked: search for productions first.");
            return;
        }
        CancellationTokenSource? operation = null;
        try
        {
            if (!TryGetExportDirectory(out var exportDirectory)) return;
            OperationProgress.Value = 0;
            AppendLog($"Starting download of {productions.Count} production(s)…");
            StatusText.Text = $"Downloading {productions.Count} production(s)…";
            operation = BeginCancellableOperation();
            if (operation is null) return;
            var progress = new UiProgress<DemosceneDownloadProgress>(p =>
            {
                var percent = p.Total == 0 ? 100 : p.Completed * 100d / p.Total;
                var activity = $"Downloading [{p.Completed}/{p.Total}] {p.Production.Title}: {p.Result.Status}";
                SetProgress(percent, activity, addToLog: false);
                StatusText.Text = $"Downloading [{p.Completed}/{p.Total}] {p.Production.Title}: {p.Result.Status}";
            });
            using var downloader = new DemosceneDownloadService();
            var result = await downloader.DownloadAsync(productions,
                new DemosceneDownloadOptions(exportDirectory,
                    MaxConcurrency: 2, RequestDelayMilliseconds: 350, GotekExportLayout: true),
                cancellationToken: operation.Token, progress: progress);
            var copiedArtwork = CopyDownloadedArtwork(result.Results);
            StatusText.Text = operation.IsCancellationRequested
                ? "Download cancelled. Run the same batch again to resume."
                : $"Downloaded {result.Downloaded}, already present {result.AlreadyPresent}, " +
                  $"skipped {result.Skipped}, failed {result.Failed}. " +
                  $"Artwork copied: {copiedArtwork}. Directory: {exportDirectory}";
            SetProgress(100, StatusText.Text);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Download cancelled. Run the same batch again to resume.";
            CurrentActivityText.Text = StatusText.Text;
            AppendLog(StatusText.Text);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = ex.Message;
            CurrentActivityText.Text = $"Error: {ex.Message}";
            AppendLog($"ERROR: {ex.Message}");
        }
        finally
        {
            if (operation is not null) EndCancellableOperation(operation);
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private bool TryGetExportDirectory(out string exportDirectory)
    {
        exportDirectory = ExportDirectoryText.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(exportDirectory)) return true;
        StatusText.Text = "Select an export directory first.";
        AppendLog("Operation blocked: export directory is required.");
        return false;
    }

    private void AppendSkippedGroupsToLog(
        IReadOnlyList<AmiGotekMediaBuilder.Core.Models.ReleaseGroup> skippedGroups)
    {
        if (skippedGroups.Count == 0) return;

        const int previewCount = 12;
        AppendLog($"Skipping {skippedGroups.Count} quarantined release(s); partial sets are not exported.");
        foreach (var group in skippedGroups.Take(previewCount))
            AppendLog($"SKIPPED: {group.Title ?? group.ReleaseKey}: {group.QuarantineReason}");
        if (skippedGroups.Count > previewCount)
            AppendLog($"… {skippedGroups.Count - previewCount} additional quarantined release(s); see scan results for details.");
    }

    private static string GetDemosceneCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "amiga-adf-library-builder", "demoscene");

    private SafeHttpClient CreateDemosceneHttpClient() => new(
        retryProgress: new UiProgress<HttpRetryProgress>(update =>
        {
            var seconds = Math.Max(1, Math.Ceiling(update.Delay.TotalSeconds));
            SetProgress(-1, $"{update.Host}: retry in {seconds:0}s " +
                            $"(attempt {update.Attempt}/{update.MaxAttempts})", addToLog: false);
        }),
        minimumRequestInterval: TimeSpan.Zero);

    private int CopyDownloadedArtwork(IEnumerable<DemosceneDownloadResult> downloads)
    {
        var copied = 0;
        var processedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var download in downloads)
        {
            if (string.IsNullOrWhiteSpace(download.OutputPath)) continue;
            var folder = Path.GetDirectoryName(download.OutputPath);
            if (string.IsNullOrWhiteSpace(folder) || !processedFolders.Add(folder)) continue;
            if (_artworkByProductionId.TryGetValue(download.PouetId, out var artworkPath) && File.Exists(artworkPath))
            {
                var destination = Path.Combine(folder, Path.GetFileName(folder) + Path.GetExtension(artworkPath));
                File.Copy(artworkPath, destination, overwrite: true);
            }
            else
            {
                WriteExportFallbackArtwork(folder);
            }
            copied++;
        }
        return copied;
    }

    private async Task<IReadOnlyDictionary<string, string>> ScrapeScannedArtworkAsync(
        IReadOnlyList<AmiGotekMediaBuilder.Core.Models.ReleaseGroup> groups, CancellationToken cancellationToken)
    {
        var cacheDirectory = GetDemosceneCacheDirectory();
        var store = new DemosceneMetadataStore(Path.Combine(cacheDirectory, "metadata"));
        var candidates = store.ReadAll().ToList();
        using var pouetClient = CreateDemosceneHttpClient();
        using var demozooClient = CreateDemosceneHttpClient();
        using var artworkClient = CreateDemosceneHttpClient();
        using var pouet = new PouetCatalogProvider(new PouetCatalogOptions(
            Environment.GetEnvironmentVariable("POUET_BASE_URL") ?? "https://www.pouet.net",
            MaxConcurrency: 2, RequestDelay: TimeSpan.Zero), pouetClient);
        using var demozoo = new DemozooCatalogProvider(new DemozooCatalogOptions(
            Environment.GetEnvironmentVariable("DEMOZOO_BASE_URL") ?? "https://demozoo.org",
            MaxConcurrency: 2, RequestDelay: TimeSpan.Zero), demozooClient);

        var matched = new Dictionary<string, DemosceneProduction>();
        for (var index = 0; index < groups.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups[index];
            var title = group.Title ?? group.Folder ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title)) continue;
            SetProgress(-1, $"Artwork lookup [{index + 1}/{groups.Count}]: {title}", addToLog: false);
            var match = FindBestMatch(candidates, group);
            if (match is null)
            {
                var query = new DemosceneQuery(Type: "demo", Search: title, MaxPages: 1, MaxItems: 12);
                var pouetResults = await pouet.BrowseAsync(query, cancellationToken);
                var demozooResults = await demozoo.SearchAsync(title, cancellationToken: cancellationToken);
                var found = DemosceneCatalogMerger.Merge(pouetResults, demozooResults);
                match = FindBestMatch(found, group);
                if (match is not null)
                {
                    candidates.Add(match);
                    await store.WriteAsync([match]);
                }
            }
            if (match?.ArtworkUrl is not null)
                matched[group.ReleaseKey] = match;
        }

        if (matched.Count == 0) return new Dictionary<string, string>();
        SetProgress(-1, $"Downloading artwork for {matched.Count} matched release(s)…");
        using var artwork = new DemosceneArtworkService(artworkClient);
        var results = await artwork.DownloadAsync(matched.Values.DistinctBy(p => p.PouetId),
            Path.Combine(cacheDirectory, "thumbnails-original"),
            Path.Combine(cacheDirectory, "thumbnails-processed"), cancellationToken);
        var pathsById = results.Where(result => !string.IsNullOrWhiteSpace(result.ProcessedPath))
            .ToDictionary(result => result.PouetId, result => result.ProcessedPath!, StringComparer.Ordinal);
        return matched.Where(pair => pathsById.ContainsKey(pair.Value.PouetId))
            .ToDictionary(pair => pair.Key, pair => pathsById[pair.Value.PouetId], StringComparer.Ordinal);
    }

    private static DemosceneProduction? FindBestMatch(
        IEnumerable<DemosceneProduction> candidates,
        AmiGotekMediaBuilder.Core.Models.ReleaseGroup group)
    {
        var title = NormalizeTitle(group.Title ?? group.Folder ?? string.Empty);
        var releaseGroup = NormalizeTitle(group.Group ?? string.Empty);
        var year = group.Records.Select(record => record.Year).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        return candidates
            .Select(candidate => new { Candidate = candidate, Score = MatchScore(candidate, title, releaseGroup, year) })
            .Where(item => item.Score >= 60)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Candidate.ArtworkUrl is not null)
            .Select(item => item.Candidate)
            .FirstOrDefault();
    }

    private static int MatchScore(DemosceneProduction candidate, string title, string group, string? year)
    {
        var candidateTitle = NormalizeTitle(candidate.Title);
        var candidateGroup = NormalizeTitle(candidate.Group ?? string.Empty);
        var score = candidateTitle == title ? 100 :
            candidateTitle.Contains(title, StringComparison.Ordinal) || title.Contains(candidateTitle, StringComparison.Ordinal) ? 55 : 0;
        if (score == 0) return 0;
        if (group.Length > 0 && candidateGroup == group) score += 30;
        if (!string.IsNullOrWhiteSpace(year) && string.Equals(candidate.Year, year, StringComparison.OrdinalIgnoreCase)) score += 10;
        return score;
    }

    private static string NormalizeTitle(string value) => new(value.Where(char.IsLetterOrDigit)
        .Select(char.ToLowerInvariant).ToArray());

    private static int CopyScannedArtwork(
        IEnumerable<AmiGotekMediaBuilder.Core.Models.ReleaseGroup> groups,
        IReadOnlyDictionary<string, string> artworkByRelease,
        string exportDirectory, CancellationToken cancellationToken)
    {
        var copied = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var branch = group.Extension.Equals("dsk", StringComparison.OrdinalIgnoreCase) ? "DSK" : "ADF";
            var folder = Path.Combine(exportDirectory, branch, "Demoscene", ReleaseNamer.GetBasename(group));
            if (!Directory.Exists(folder)) continue;
            if (artworkByRelease.TryGetValue(group.ReleaseKey, out var artwork) && File.Exists(artwork))
            {
                var destination = Path.Combine(folder, Path.GetFileName(folder) + Path.GetExtension(artwork));
                File.Copy(artwork, destination, overwrite: true);
            }
            else
            {
                WriteExportFallbackArtwork(folder);
            }
            copied++;
        }
        return copied;
    }

    private static void WriteExportFallbackArtwork(string folder) => File.WriteAllBytes(
        Path.Combine(folder, Path.GetFileName(folder) + ".jpg"),
        DefaultArtworkService.GetExportFallbackBytes());

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

    private sealed class DemosceneItem
    {
        public DemosceneItem(DemosceneProduction production, Bitmap? thumbnail, GotekScreenProfile screen)
        {
            Production = production;
            Thumbnail = thumbnail;
            const double maxWidth = 140;
            const double maxHeight = 90;
            var scale = Math.Min(maxWidth / screen.Width, maxHeight / screen.Height);
            ThumbnailWidth = Math.Round(screen.Width * scale);
            ThumbnailHeight = Math.Round(screen.Height * scale);
            Title = production.Title;
            Description = production.Description ?? string.Empty;
            PlatformText = string.Join(", ", DemoscenePlatforms.Enumerate(production.Platforms));
            var catalog = production.Catalog.Equals(DemosceneCatalogs.Demozoo, StringComparison.OrdinalIgnoreCase)
                ? "Demozoo" : "Pouët";
            Summary = $"{catalog} #{production.PouetId}  {production.Year}  {production.Type}";
            DownloadText = production.Downloads.Count == 0
                ? "No candidate ADF/DSK link"
                : $"{production.Downloads.Count} candidate link(s): " +
                  string.Join(", ", production.Downloads.Select(d => d.Format.ToString().ToUpperInvariant()).Distinct());
        }

        public DemosceneProduction Production { get; }
        public Bitmap? Thumbnail { get; }
        public double ThumbnailWidth { get; }
        public double ThumbnailHeight { get; }
        public string Title { get; }
        public string Summary { get; }
        public string PlatformText { get; }
        public string Description { get; }
        public string DownloadText { get; }
    }

    /// <summary>Marshals worker-thread progress to Avalonia's UI dispatcher.</summary>
    private sealed class UiProgress<T>(Action<T> update) : IProgress<T>
    {
        public void Report(T value) => Dispatcher.UIThread.Post(() => update(value));
    }
}

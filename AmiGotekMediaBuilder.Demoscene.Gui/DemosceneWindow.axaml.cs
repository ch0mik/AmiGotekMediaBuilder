using AmiGotekMediaBuilder.Core.Configuration;
using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Demoscene;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AmiGotekMediaBuilder.Demoscene.Gui;

public partial class DemosceneWindow : Window
{
    private IReadOnlyList<DemosceneProduction> _productions = [];
    private CancellationTokenSource? _downloadCts;

    public DemosceneWindow() : this(string.Empty)
    {
    }

    public DemosceneWindow(string libraryRoot)
    {
        InitializeComponent();
        LibraryRootText.Text = string.IsNullOrWhiteSpace(libraryRoot)
            ? Environment.GetEnvironmentVariable("AMIGA_ADF_LIBRARY_ROOT") ?? string.Empty
            : libraryRoot;
        ScreenCombo.ItemsSource = GotekScreenProfile.Supported;
        ScreenCombo.SelectedItem = GotekScreenProfile.Default;
        CurrentActivityText.Text = "Waiting for a catalog search.";
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

    private async void BrowseRoot(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select the library root", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) LibraryRootText.Text = path;
    }

    private void ScreenChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ScreenCombo.SelectedItem is GotekScreenProfile screen)
            StatusText.Text = $"Preview screen: {screen.DisplayName}. Select Search all catalogs to refresh thumbnails.";
    }

    private async void SearchClick(object? sender, RoutedEventArgs e)
    {
        LogText.Text = string.Empty;
        OperationProgress.Value = 0;
        try
        {
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
            var query = new DemosceneQuery(platform, TypeText.Text, Search: SearchText.Text,
                MaxPages: Math.Max(1, (int)Math.Ceiling(maxItems / 50d)), MaxItems: maxItems);
            var config = PathConfig.Create(LibraryRootText.Text ?? string.Empty);
            using var pouet = new PouetCatalogProvider(new PouetCatalogOptions(
                Environment.GetEnvironmentVariable("POUET_BASE_URL") ?? "https://www.pouet.net",
                MaxConcurrency: 2, RequestDelay: TimeSpan.FromMilliseconds(250)));
            using var demozoo = new DemozooCatalogProvider(new DemozooCatalogOptions(
                Environment.GetEnvironmentVariable("DEMOZOO_BASE_URL") ?? "https://demozoo.org",
                MaxConcurrency: 2, RequestDelay: TimeSpan.FromMilliseconds(250)));
            var pouetTask = pouet.BrowseAsync(query);
            var demozooTask = demozoo.BrowseAsync(query);
            SetProgress(-1, "Waiting for catalog responses…");
            var productions = DemosceneCatalogMerger.Merge(await pouetTask, await demozooTask);
            SetProgress(55, $"Merged {productions.Count} unique production(s).");
            await new DemosceneMetadataStore(config.DemosceneMetadataDirectory).WriteAsync(productions);
            SetProgress(-1, "Metadata saved; downloading thumbnails…");
            StatusText.Text = $"Found {productions.Count} production(s); downloading thumbnails…";
            using var artwork = new DemosceneArtworkService();
            var artworkResults = await Task.Run(() => artwork.DownloadAsync(
                productions, config.DemosceneArtworkOriginalDirectory,
                config.DemosceneArtworkProcessedDirectory));
            SetProgress(85, $"Processed {artworkResults.Count} thumbnail result(s).");
            _productions = productions;
            var artworkById = artworkResults.ToDictionary(r => r.PouetId, StringComparer.Ordinal);
            var items = productions.Select(production =>
            {
                artworkById.TryGetValue(production.PouetId, out var art);
                return new DemosceneItem(production, LoadImage(art?.ProcessedPath), screen);
            }).ToArray();
            ProductionsList.ItemsSource = items;
            CountText.Text = $"{productions.Count} result(s)";
            StatusText.Text = $"Found {productions.Count} production(s); " +
                              $"thumbnails: {artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Downloaded)} downloaded, " +
                              $"{artworkResults.Count(r => r.Status == DemosceneArtworkStatus.Skipped)} missing; " +
                              $"preview: {screen.Width}×{screen.Height}.";
            SetProgress(100, StatusText.Text);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException)
        {
            StatusText.Text = ex.Message;
            CurrentActivityText.Text = $"Error: {ex.Message}";
            AppendLog($"ERROR: {ex.Message}");
        }
    }

    private async void DownloadSelectedClick(object? sender, RoutedEventArgs e)
    {
        var selected = ProductionsList.SelectedItems is { } selectedItems
            ? selectedItems.OfType<DemosceneItem>().Select(i => i.Production).ToArray()
            : [];
        await DownloadAsync(selected);
    }

    private async void DownloadAllClick(object? sender, RoutedEventArgs e) => await DownloadAsync(_productions);

    private async Task DownloadAsync(IReadOnlyList<DemosceneProduction> productions)
    {
        if (productions.Count == 0)
        {
            StatusText.Text = "Search for productions first.";
            AppendLog("Download blocked: search for productions first.");
            return;
        }
        if (DownloadAcknowledged.IsChecked != true)
        {
            StatusText.Text = "Tick 'I acknowledge external downloads' before starting a batch.";
            AppendLog("Download blocked: external download acknowledgement is required.");
            return;
        }
        try
        {
            var config = PathConfig.Create(LibraryRootText.Text ?? string.Empty);
            OperationProgress.Value = 0;
            AppendLog($"Starting download of {productions.Count} production(s)…");
            StatusText.Text = $"Downloading {productions.Count} production(s)…";
            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            CancelButton.IsEnabled = true;
            var progress = new Progress<DemosceneDownloadProgress>(p =>
            {
                var percent = p.Total == 0 ? 100 : p.Completed * 100d / p.Total;
                SetProgress(percent, $"[{p.Completed}/{p.Total}] {p.Production.Title}: {p.Result.Status}", addToLog: false);
                StatusText.Text = $"Downloading [{p.Completed}/{p.Total}] {p.Production.Title}: {p.Result.Status}";
            });
            using var downloader = new DemosceneDownloadService();
            var result = await downloader.DownloadAsync(productions,
                new DemosceneDownloadOptions(config.DemosceneDownloadDirectory,
                    MaxConcurrency: 2, RequestDelayMilliseconds: 350),
                cancellationToken: _downloadCts.Token, progress: progress);
            StatusText.Text = _downloadCts.IsCancellationRequested
                ? "Download cancelled. Run the same batch again to resume."
                : $"Downloaded {result.Downloaded}, already present {result.AlreadyPresent}, " +
                  $"skipped {result.Skipped}, failed {result.Failed}. " +
                  $"Directory: {config.DemosceneDownloadDirectory}";
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
            CancelButton.IsEnabled = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => _downloadCts?.Cancel();

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
}

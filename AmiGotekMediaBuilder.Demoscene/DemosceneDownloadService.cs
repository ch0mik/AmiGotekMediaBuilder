using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using AmiGotekMediaBuilder.Core.Networking;

namespace AmiGotekMediaBuilder.Demoscene;

/// <summary>
/// Downloads disk images referenced by demoscene productions. Only ADF/DSK
/// payloads (including ZIP/GZip containers) are written; arbitrary links are
/// reported as skipped. Provenance is stored outside GTI media folders and
/// every output is written atomically so an interrupted batch can safely be
/// resumed.
/// </summary>
public sealed class DemosceneDownloadService : IDisposable
{
    private readonly SafeHttpClient _client;
    private readonly bool _ownsClient;

    public DemosceneDownloadService(SafeHttpClient? client = null)
    {
        _client = client ?? new SafeHttpClient();
        _ownsClient = client is null;
    }

    public async Task<DemosceneBatchResult> DownloadAsync(
        IEnumerable<DemosceneProduction> productions,
        DemosceneDownloadOptions options,
        CancellationToken cancellationToken = default,
        IProgress<DemosceneDownloadProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(productions);
        ArgumentNullException.ThrowIfNull(options);
        var normalized = options.Normalize();
        var items = productions.SelectMany(p => SelectWorkItems(p, normalized)).ToArray();
        Directory.CreateDirectory(normalized.DestinationDirectory);
        var result = new DemosceneBatchResult();
        if (items.Length == 0) return result;

        var gate = new SemaphoreSlim(normalized.MaxConcurrency, normalized.MaxConcurrency);
        var resultLock = new object();
        var manifestLock = new SemaphoreSlim(1, 1);
        var byteCounter = new ByteCounter();
        var knownHashes = BuildHashIndex(normalized.DestinationDirectory);
        var completed = 0;
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var started = Volatile.Read(ref completed);
                progress?.Report(new DemosceneDownloadProgress(started, items.Length, item.Production,
                    Result(item, DemosceneDownloadStatus.Skipped, null, null, 0, null, item.Link.Format),
                    IsStarting: true));
                if (normalized.RequestDelayMilliseconds > 0)
                    await Task.Delay(normalized.RequestDelayMilliseconds, cancellationToken);
                var downloaded = await DownloadItemAsync(item, normalized, byteCounter, knownHashes, manifestLock, cancellationToken);
                lock (resultLock) result.Results.AddRange(downloaded);
                var current = Interlocked.Increment(ref completed);
                var progressResult = downloaded.LastOrDefault() ?? Result(item,
                    DemosceneDownloadStatus.Failed, null, "download produced no result", 0, null, item.Link.Format);
                progress?.Report(new DemosceneDownloadProgress(current, items.Length, item.Production, progressResult));
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Preserve completed results. Callers can invoke the same batch
            // again; existing files are recognized as AlreadyPresent.
        }
        finally
        {
            gate.Dispose();
            manifestLock.Dispose();
        }
        return result;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }

    private async Task<IReadOnlyList<DemosceneDownloadResult>> DownloadItemAsync(
        WorkItem item,
        DemosceneDownloadOptions options,
        ByteCounter totalBytes,
        ConcurrentDictionary<string, string> knownHashes,
        SemaphoreSlim manifestLock,
        CancellationToken cancellationToken)
    {
        var link = item.Link with { Url = NormalizeDownloadUrl(item.Link.Url) };
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var linkUri) ||
            linkUri.Scheme is not ("http" or "https"))
            return [Result(item, DemosceneDownloadStatus.Skipped, null,
                $"unsupported download scheme: {linkUri?.Scheme ?? "unknown"}", 0, null, link.Format)];
        if (!IsSupported(link, options.IncludeArchives))
            return [Result(item, DemosceneDownloadStatus.Skipped, null, $"unsupported format: {link.Format}", 0, null, link.Format)];

        byte[] payload;
        try
        {
            payload = options.FollowRedirects
                ? await _client.GetBytesFollowingRedirectsAsync(link.Url, options.MaxBytesPerFile, cancellationToken)
                : await _client.GetBytesAsync(link.Url, options.MaxBytesPerFile, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return [Result(item, DemosceneDownloadStatus.Failed, null, ex.Message, 0, null, link.Format)];
        }

        if (payload.LongLength > options.MaxBytesPerFile)
            return [Result(item, DemosceneDownloadStatus.Failed, null, "download exceeds per-file limit", 0, null, link.Format)];

        List<ExtractedImage> images;
        try
        {
            var effectiveLink = link.Format == DemosceneAssetFormat.Unknown
                ? link with { Format = InferFormat(payload, link) }
                : link;
            images = Expand(effectiveLink, payload, options.MaxBytesPerFile);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return [Result(item, DemosceneDownloadStatus.Failed, null, ex.Message, 0, null, link.Format)];
        }
        if (images.Count == 0)
            return [Result(item, DemosceneDownloadStatus.Skipped, null, "no ADF/DSK image found in payload", payload.LongLength, null, link.Format)];

        var results = new List<DemosceneDownloadResult>();
        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var imageHash = Convert.ToHexString(SHA256.HashData(image.Bytes)).ToLowerInvariant();
            var newTotal = Interlocked.Add(ref totalBytes.Value, image.Bytes.LongLength);
            if (newTotal > options.MaxBytesTotal)
            {
                Interlocked.Add(ref totalBytes.Value, -image.Bytes.LongLength);
                results.Add(Result(item, DemosceneDownloadStatus.Skipped, null,
                    "batch total-size limit reached", image.Bytes.LongLength, imageHash, image.Format));
                break;
            }
            var written = await StoreAsync(item.Production, item.Platform, image,
                imageHash, options.DestinationDirectory, options.GotekExportLayout,
                knownHashes, manifestLock, cancellationToken);
            results.Add(Result(item, written.Status, written.Path, written.Error,
                image.Bytes.LongLength, imageHash, image.Format));
            if (written.Status == DemosceneDownloadStatus.Failed)
                break;
        }
        return results.Count == 0
            ? [Result(item, DemosceneDownloadStatus.Failed, null, "image extraction failed", 0, null, link.Format)]
            : results;
    }

    private static IEnumerable<WorkItem> SelectWorkItems(
        DemosceneProduction production,
        DemosceneDownloadOptions options)
    {
        var availableLinks = production.Downloads
            .Where(l => !string.IsNullOrWhiteSpace(l.Url))
            .ToArray();
        // A production page often has "mirrors" and unrelated external
        // links next to a real archive. Prefer only known disk/archive links;
        // retain unknown links solely when they are the only available lead.
        var supportedLinks = availableLinks.Where(link =>
            link.Format is DemosceneAssetFormat.Adf or DemosceneAssetFormat.Dsk ||
            options.IncludeArchives && link.Format is DemosceneAssetFormat.Zip or DemosceneAssetFormat.Gzip).ToArray();
        var links = (supportedLinks.Length > 0 ? supportedLinks : availableLinks)
            .Take(options.MaxLinksPerProduction)
            .ToArray();
        var platform = PrimaryPlatform(production.Platforms);
        if (links.Length == 0)
        {
            yield return new WorkItem(production,
                new DemosceneDownloadLink(production.SourceUrl, "no download link", DemosceneAssetFormat.Unknown),
                platform);
            yield break;
        }
        var inferredTotal = links.Length > 1 ? links.Length : (int?)null;
        foreach (var pair in links.Select((link, index) => new { link, index }))
        {
            var marker = ParseDiskMarker(pair.link.FileName ?? pair.link.Label ?? string.Empty);
            var diskNumber = pair.link.DiskNumber ?? marker.Number ?? (inferredTotal is null ? null : pair.index + 1);
            var totalDisks = pair.link.TotalDisks ?? marker.Total ?? inferredTotal;
            yield return new WorkItem(production, pair.link with
            {
                DiskNumber = diskNumber,
                TotalDisks = totalDisks
            }, platform);
        }
    }

    private static bool IsSupported(DemosceneDownloadLink link, bool includeArchives)
    {
        if (link.Format is DemosceneAssetFormat.Adf or DemosceneAssetFormat.Dsk) return true;
        if (link.Format is DemosceneAssetFormat.Zip or DemosceneAssetFormat.Gzip) return includeArchives;
        return link.Format == DemosceneAssetFormat.Unknown && !string.IsNullOrWhiteSpace(link.Label);
    }

    private static string NormalizeDownloadUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("files.scene.org", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/view/", StringComparison.OrdinalIgnoreCase))
            return url;
        var builder = new UriBuilder(uri)
        {
            Path = "/get/" + uri.AbsolutePath[6..].TrimStart('/')
        };
        return builder.Uri.ToString();
    }

    private static List<ExtractedImage> Expand(
        DemosceneDownloadLink link,
        byte[] payload,
        long maxBytes)
    {
        var output = new List<ExtractedImage>();
        switch (link.Format)
        {
            case DemosceneAssetFormat.Adf:
                if (LooksLikeDisk(payload)) output.Add(new ExtractedImage(SafeFileName(link.FileName, ".adf"), payload, DemosceneAssetFormat.Adf, link.DiskNumber, link.TotalDisks) { SourceUrl = link.Url });
                break;
            case DemosceneAssetFormat.Dsk:
                if (LooksLikeDisk(payload)) output.Add(new ExtractedImage(SafeFileName(link.FileName, ".dsk"), payload, DemosceneAssetFormat.Dsk, link.DiskNumber, link.TotalDisks) { SourceUrl = link.Url });
                break;
            case DemosceneAssetFormat.Gzip:
                using (var input = new MemoryStream(payload, writable: false))
                using (var gzip = new GZipStream(input, CompressionMode.Decompress))
                using (var decompressed = ReadBounded(gzip, maxBytes))
                {
                    var extension = link.FileName?.EndsWith(".dsk.gz", StringComparison.OrdinalIgnoreCase) == true ||
                                    link.Label?.Contains("dsk", StringComparison.OrdinalIgnoreCase) == true
                        ? ".dsk" : ".adf";
                    var format = extension == ".dsk" ? DemosceneAssetFormat.Dsk : DemosceneAssetFormat.Adf;
                    var bytes = decompressed.ToArray();
                    if (LooksLikeDisk(bytes)) output.Add(new ExtractedImage(SafeFileName(link.FileName, extension), bytes, format, link.DiskNumber, link.TotalDisks) { SourceUrl = link.Url });
                }
                break;
            case DemosceneAssetFormat.Zip:
                using (var input = new MemoryStream(payload, writable: false))
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false))
                {
                    foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Take(128))
                    {
                        var extension = Path.GetExtension(entry.Name);
                        var format = extension.Equals(".dsk", StringComparison.OrdinalIgnoreCase)
                            ? DemosceneAssetFormat.Dsk
                            : extension.Equals(".adf", StringComparison.OrdinalIgnoreCase)
                                ? DemosceneAssetFormat.Adf : DemosceneAssetFormat.Unknown;
                        if (format == DemosceneAssetFormat.Unknown || entry.Length > maxBytes) continue;
                        using var stream = entry.Open();
                        using var bytes = ReadBounded(stream, maxBytes);
                        var imageBytes = bytes.ToArray();
                        var entryDisk = ParseDiskMarker(entry.Name);
                        if (LooksLikeDisk(imageBytes)) output.Add(new ExtractedImage(
                            SafeFileName(entry.Name, extension),
                            imageBytes,
                            format,
                            entryDisk.Number ?? link.DiskNumber,
                            entryDisk.Total ?? link.TotalDisks) { SourceUrl = link.Url });
                    }
                }
                break;
        }
        return NormalizeDiskSet(output);
    }

    private static DemosceneAssetFormat InferFormat(byte[] payload, DemosceneDownloadLink link)
    {
        if (payload.Length >= 4 && payload[0] == 0x50 && payload[1] == 0x4b &&
            payload[2] is 0x03 or 0x05 or 0x07 && payload[3] is 0x04 or 0x06 or 0x08)
            return DemosceneAssetFormat.Zip;
        if (payload.Length >= 2 && payload[0] == 0x1f && payload[1] == 0x8b)
            return DemosceneAssetFormat.Gzip;
        if (LooksLikeDisk(payload))
            return link.Label?.Contains("dsk", StringComparison.OrdinalIgnoreCase) == true
                ? DemosceneAssetFormat.Dsk : DemosceneAssetFormat.Adf;
        return DemosceneAssetFormat.Unknown;
    }

    private static MemoryStream ReadBounded(Stream input, long maxBytes)
    {
        var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > maxBytes)
                throw new InvalidOperationException("decompressed payload exceeds per-file limit");
            output.Write(buffer, 0, read);
        }
        output.Position = 0;
        return output;
    }

    private static bool LooksLikeDisk(byte[] bytes) =>
        bytes.Length >= 512 && bytes.Length % 512 == 0;

    private static async Task<StoredImage> StoreAsync(
        DemosceneProduction production,
        DemoscenePlatform platform,
        ExtractedImage image,
        string sha256,
        string root,
        bool gotekExportLayout,
        ConcurrentDictionary<string, string> knownHashes,
        SemaphoreSlim manifestLock,
        CancellationToken cancellationToken)
    {
        var baseName = TosecFileStem(production, image.DiskNumber, image.TotalDisks);
        var extension = image.Format == DemosceneAssetFormat.Dsk ? ".dsk" : ".adf";
        var folder = gotekExportLayout
            ? Path.Combine(root, extension.Equals(".dsk", StringComparison.OrdinalIgnoreCase) ? "DSK" : "ADF",
                "Demoscene", SafeSlug(production.Title))
            : Path.Combine(root, PlatformFolder(platform),
                SafeSlug(production.Year is null ? production.Title : $"{production.Year} {production.Title}"));
        Directory.CreateDirectory(folder);
        if (knownHashes.TryGetValue(sha256, out var existingPath))
            return new StoredImage(DemosceneDownloadStatus.AlreadyPresent, existingPath, null);
        var target = UniquePath(folder, baseName, extension, sha256, image.Bytes);
        if (target.AlreadyPresent)
        {
            knownHashes.TryAdd(sha256, target.Path);
            return new StoredImage(DemosceneDownloadStatus.AlreadyPresent, target.Path, null);
        }
        if (!knownHashes.TryAdd(sha256, target.Path))
            return new StoredImage(DemosceneDownloadStatus.AlreadyPresent, knownHashes[sha256], null);
        try
        {
            var temp = target.Path + ".part";
            await File.WriteAllBytesAsync(temp, image.Bytes, cancellationToken);
            File.Move(temp, target.Path, overwrite: false);
            var sidecar = new
            {
                source = production.SourceUrl,
                catalog = production.Catalog,
                source_id = production.PouetId,
                linked_pouet_id = production.LinkedPouetId,
                pouet_id = production.PouetId,
                title = production.Title,
                group = production.Group,
                year = production.Year,
                type = production.Type,
                description = production.Description,
                artwork_url = production.ArtworkUrl,
                platforms = DemoscenePlatforms.Enumerate(production.Platforms).Select(p => p.ToString()).ToArray(),
                download_url = image.SourceUrl,
                retrieved_at = DateTimeOffset.UtcNow,
                sha256,
                bytes = image.Bytes.LongLength,
                format = image.Format.ToString().ToLowerInvariant()
            };
            var json = JsonSerializer.Serialize(sidecar, new JsonSerializerOptions { WriteIndented = true });
            var metadataDirectory = Path.Combine(root, ".metadata");
            Directory.CreateDirectory(metadataDirectory);
            await File.WriteAllTextAsync(Path.Combine(metadataDirectory, sha256 + ".source.json"), json, cancellationToken);
            await manifestLock.WaitAsync(cancellationToken);
            try
            {
                var manifestPath = Path.Combine(root, "manifest.jsonl");
                await File.AppendAllTextAsync(manifestPath, JsonSerializer.Serialize(sidecar) + Environment.NewLine, cancellationToken);
            }
            finally { manifestLock.Release(); }
            return new StoredImage(DemosceneDownloadStatus.Downloaded, target.Path, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            knownHashes.TryRemove(new KeyValuePair<string, string>(sha256, target.Path));
            TryDelete(target.Path + ".part");
            return new StoredImage(DemosceneDownloadStatus.Failed, null, ex.Message);
        }
    }

    private static ConcurrentDictionary<string, string> BuildHashIndex(string root)
    {
        var index = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return index;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                         .Where(path => Path.GetExtension(path).Equals(".adf", StringComparison.OrdinalIgnoreCase) ||
                                        Path.GetExtension(path).Equals(".dsk", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                    index.TryAdd(hash, path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return index;
    }

    private static (string Path, bool AlreadyPresent) UniquePath(
        string folder, string baseName, string extension, string sha256, byte[] bytes)
    {
        var path = System.IO.Path.Combine(folder, baseName + extension);
        for (var index = 1; File.Exists(path); index++)
        {
            try
            {
                if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    return (path, true);
            }
            catch (IOException) { }
            path = System.IO.Path.Combine(folder, $"{baseName}-{index + 1}{extension}");
        }
        return (path, false);
    }

    private static DemosceneDownloadResult Result(
        WorkItem item, DemosceneDownloadStatus status, string? output, string? error,
        long bytes, string? sha256, DemosceneAssetFormat format) =>
        new(item.Production.PouetId, item.Production.Title, item.Link.Url,
            status, output, error, bytes, sha256, format, item.Platform);

    private static DemoscenePlatform PrimaryPlatform(DemoscenePlatform platforms) =>
        DemoscenePlatforms.Enumerate(platforms).FirstOrDefault();

    private static string PlatformFolder(DemoscenePlatform platform) => platform switch
    {
        DemoscenePlatform.OcsEcs => "ocs-ecs",
        DemoscenePlatform.Aga => "aga",
        DemoscenePlatform.PpcRtg => "ppc-rtg",
        _ => "unknown"
    };

    private static string SafeSlug(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray();
        var slug = new string(chars).Trim('.', ' ');
        return slug.Length > 100 ? slug[..100].TrimEnd(' ', '.') : slug;
    }

    private static string SafeFileName(string? value, string extension)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "disk" : Path.GetFileName(value);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.EndsWith(".adf", StringComparison.OrdinalIgnoreCase) ||
            stem.EndsWith(".dsk", StringComparison.OrdinalIgnoreCase))
            stem = Path.GetFileNameWithoutExtension(stem);
        return SafeSlug(stem) + extension;
    }

    private static string TosecFileStem(DemosceneProduction production, int? diskNumber, int? totalDisks)
    {
        var parts = new List<string> { SafeSlug(production.Title) };
        if (!string.IsNullOrWhiteSpace(production.Year)) parts.Add($"({SafeSlug(production.Year)})");
        if (!string.IsNullOrWhiteSpace(production.Group)) parts.Add($"({SafeSlug(production.Group)})");
        var baseName = string.Concat(parts);
        if (diskNumber is not { } disk || totalDisks is not { } count || count <= 1 || disk < 1 || disk > count)
            return baseName;
        var width = count >= 10 ? Math.Max(2, count.ToString().Length) : 1;
        return $"{baseName} (Disk {disk.ToString($"D{width}")} of {count.ToString($"D{width}")})";
    }

    private static (int? Number, int? Total) ParseDiskMarker(string name)
    {
        var marker = System.Text.RegularExpressions.Regex.Match(
            name,
            @"\(Disk\s+(?<number>\d+)\s+of\s+(?<total>\d+)\)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (marker.Success)
            return (
                int.TryParse(marker.Groups["number"].Value, out var number) ? number : null,
                int.TryParse(marker.Groups["total"].Value, out var total) ? total : null);
        var numeric = System.Text.RegularExpressions.Regex.Match(name, @"(?:disk|disc)[ _-]?(?<number>\d+)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (numeric.Success && int.TryParse(numeric.Groups["number"].Value, out var numericNumber))
            return (numericNumber, null);
        var letter = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(name), @"(?<letter>[A-Z])$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return letter.Success ? (char.ToUpperInvariant(letter.Groups["letter"].Value[0]) - 'A' + 1, null) : (null, null);
    }

    private static List<ExtractedImage> NormalizeDiskSet(List<ExtractedImage> images)
    {
        if (images.Count <= 1) return images;
        var total = images.Select(image => image.TotalDisks).Where(value => value is > 1)
            .Select(value => value!.Value).DefaultIfEmpty(images.Count).Max();
        return images.Select((image, index) => image with
        {
            DiskNumber = image.DiskNumber ?? index + 1,
            TotalDisks = image.TotalDisks ?? total
        }).ToList();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private sealed record WorkItem(DemosceneProduction Production, DemosceneDownloadLink Link, DemoscenePlatform Platform);
    private sealed record ExtractedImage(string FileName, byte[] Bytes, DemosceneAssetFormat Format, int? DiskNumber, int? TotalDisks)
    {
        public string SourceUrl { get; init; } = string.Empty;
    }
    private sealed record StoredImage(DemosceneDownloadStatus Status, string? Path, string? Error);
    private sealed class ByteCounter { public long Value; }
}

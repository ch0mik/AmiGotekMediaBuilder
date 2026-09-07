using System.Security.Cryptography;
using System.IO.Compression;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Scanning;

public static class IntakeScanner
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".adf", ".dsk" };

    /// <summary>Describes the source item currently being hashed by the scanner.</summary>
    public sealed record ScanProgress(string Path, string Phase);

    public static IReadOnlyList<ScanRecord> ScanDirectory(
        string originalDirectory, IEnumerable<string>? excludedDirectories = null,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalDirectory);
        var records = new List<ScanRecord>();
        ScanDirectoryInto(new DirectoryInfo(originalDirectory), records,
            NormalizeExclusions(excludedDirectories), progress);
        return records;
    }

    /// <summary>Scans several read-only intake roots as one corpus.</summary>
    public static IReadOnlyList<ScanRecord> ScanDirectories(
        IEnumerable<string> directories, IEnumerable<string>? excludedDirectories = null,
        IProgress<ScanProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(directories);
        var records = new List<ScanRecord>();
        var exclusions = NormalizeExclusions(excludedDirectories);
        foreach (var path in directories)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            ScanDirectoryInto(new DirectoryInfo(path), records, exclusions, progress);
        }
        return records;
    }

    /// <summary>
    /// Tests whether a scanned source (including a ZIP virtual path) belongs
    /// to a configured intake directory.
    /// </summary>
    public static bool IsWithinDirectory(string sourcePath, string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var marker = sourcePath.IndexOf("::", StringComparison.Ordinal);
        var physicalPath = marker >= 0 ? sourcePath[..marker] : sourcePath;
        var candidate = Path.GetFullPath(physicalPath);
        var parent = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(parent, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(parent + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ScanDirectoryInto(
        DirectoryInfo directory, List<ScanRecord> records, IReadOnlyList<string> exclusions,
        IProgress<ScanProgress>? progress)
    {
        if (!directory.Exists)
            throw new DirectoryNotFoundException($"intake directory missing: {directory.FullName}");

        foreach (var file in EnumerateFiles(directory, exclusions)
                     .OrderBy(f => Path.GetRelativePath(directory.FullName, f.FullName), StringComparer.Ordinal))
        {
            if (SupportedExtensions.Contains(file.Extension))
            {
                progress?.Report(new ScanProgress(file.FullName, "hashing"));
                records.Add(ScanFile(file.FullName) with
                {
                    Filename = Path.GetRelativePath(directory.FullName, file.FullName)
                });
                continue;
            }
            if (!file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            progress?.Report(new ScanProgress(file.FullName, "opening ZIP"));
            records.AddRange(ScanZip(file, progress));
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(
        DirectoryInfo directory, IReadOnlyList<string> exclusions)
    {
        foreach (var file in directory.EnumerateFiles())
            yield return file;

        foreach (var child in directory.EnumerateDirectories()
                     .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (exclusions.Any(exclusion => IsWithinDirectory(child.FullName, exclusion)))
                continue;
            foreach (var file in EnumerateFiles(child, exclusions))
                yield return file;
        }
    }

    private static IReadOnlyList<string> NormalizeExclusions(IEnumerable<string>? directories) =>
        (directories ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<ScanRecord> ScanZip(
        FileInfo archive, IProgress<ScanProgress>? progress = null)
    {
        using var zip = ZipFile.OpenRead(archive.FullName);
        var records = new List<ScanRecord>();
        foreach (var entry in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name) &&
                     SupportedExtensions.Contains(Path.GetExtension(e.Name))))
        {
            progress?.Report(new ScanProgress(
                $"{archive.FullName}::{entry.FullName}", "hashing ZIP entry"));
            using var stream = entry.Open();
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            records.Add(new ScanRecord(
                $"{archive.FullName}::{entry.FullName}", entry.FullName, entry.Length,
                hash, DateTimeOffset.UtcNow));
        }
        return records;
    }

    public static ScanRecord ScanFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("not a regular file", file.FullName);
        using var stream = file.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        return new ScanRecord(file.FullName, file.Name, file.Length, hash, DateTimeOffset.UtcNow);
    }

    public static (bool Ok, IReadOnlyList<string> Problems) VerifyUnchanged(IEnumerable<ScanRecord> records)
    {
        var problems = new List<string>();
        foreach (var record in records)
        {
            try
            {
                var current = ScanSource(record.Path, record.Filename);
                if (current.Size != record.Size || !StringComparer.Ordinal.Equals(current.Sha256, record.Sha256))
                    problems.Add(record.Filename);
            }
            catch (IOException) { problems.Add(record.Filename); }
        }
        return (problems.Count == 0, problems);
    }

    private static ScanRecord ScanSource(string path, string filename)
    {
        var marker = path.IndexOf("::", StringComparison.Ordinal);
        if (marker < 0) return ScanFile(path);
        var archivePath = path[..marker];
        var entryName = path[(marker + 2)..];
        using var zip = ZipFile.OpenRead(archivePath);
        var entry = zip.GetEntry(entryName) ?? throw new FileNotFoundException("archive entry missing", path);
        using var stream = entry.Open();
        return new ScanRecord(path, filename, entry.Length,
            Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(), DateTimeOffset.UtcNow);
    }
}

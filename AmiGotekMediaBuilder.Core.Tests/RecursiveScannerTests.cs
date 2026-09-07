using AmiGotekMediaBuilder.Core.Configuration;
using AmiGotekMediaBuilder.Core.Scanning;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class RecursiveScannerTests
{
    [Fact]
    public void FindsImagesInNestedDirectoriesAndKeepsRelativePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Gry", "A"));
            var file = Path.Combine(root, "Gry", "A", "Example.adf");
            File.WriteAllBytes(file, [1, 2, 3]);
            var records = IntakeScanner.ScanDirectory(root);
            var record = Assert.Single(records);
            Assert.Equal(Path.Combine("Gry", "A", "Example.adf"), record.Filename);
            Assert.Equal(file, record.Path);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExcludesManagedDirectoriesWhenRootIsTheSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-scan-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "assets"));
            Directory.CreateDirectory(Path.Combine(root, "work", "staging"));
            File.WriteAllBytes(Path.Combine(root, "Game.adf"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "assets", "Game.adf"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(root, "work", "staging", "Game.adf"), [7, 8, 9]);

            var config = AmiGotekMediaBuilder.Core.Configuration.PathConfig.Create(root);
            var records = IntakeScanner.ScanDirectory(
                config.OriginalDirectory, config.IntakeExcludedDirectories);

            var record = Assert.Single(records);
            Assert.Equal("Game.adf", record.Filename);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExcludesLeftoverManagedDirectoriesInsideExplicitSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-scan-source-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "original", "Gry");
        try
        {
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(Path.Combine(source, "assets", "artwork-processed"));
            Directory.CreateDirectory(Path.Combine(source, "catalog", "metadata-cache"));
            Directory.CreateDirectory(Path.Combine(source, "work", "staging"));
            File.WriteAllBytes(Path.Combine(source, "Game.adf"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(source, "assets", "Game.adf"), [4, 5, 6]);
            File.WriteAllBytes(Path.Combine(source, "catalog", "Game.adf"), [7, 8, 9]);
            File.WriteAllBytes(Path.Combine(source, "work", "staging", "Game.adf"), [10, 11, 12]);

            var config = PathConfig.Create(root, originalDirectory: source);
            var records = IntakeScanner.ScanDirectory(
                config.OriginalDirectory, config.IntakeExcludedDirectories);

            var record = Assert.Single(records);
            Assert.Equal("Game.adf", record.Filename);
            Assert.Contains(Path.Combine(source, "assets"), config.IntakeExcludedDirectories);
            Assert.Contains(Path.Combine(source, "catalog"), config.IntakeExcludedDirectories);
            Assert.Contains(Path.Combine(source, "work"), config.IntakeExcludedDirectories);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

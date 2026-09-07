using AmiGotekMediaBuilder.Core.Grouping;
using AmiGotekMediaBuilder.Core.Naming;
using AmiGotekMediaBuilder.Core.Parsing;
using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Scanning;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class DirectoryGroupingTests
{
    [Fact]
    public void KeepsImagesFromOneSubdirectoryTogetherAndUsesSequentialNames()
    {
        var records = new[]
        {
            "Atlantis/Atlantis - 01.adf",
            "Atlantis/Atlantis - 02.adf",
            "Atlantis/Atlantis - Save.adf"
        }.Select(name =>
        {
            var record = FilenameParser.Parse(name);
            record.SourcePath = Path.Combine("C:\\library", name.Replace('/', Path.DirectorySeparatorChar));
            return record;
        });

        var group = Assert.Single(ReleaseGrouper.Group(records));

        Assert.Equal("Atlantis", group.Folder);
        Assert.DoesNotContain("C:", group.ReleaseKey, StringComparison.OrdinalIgnoreCase);
        Assert.True(group.UseSequentialDiskNames);
        Assert.Equal(2, group.Disks.Count);
        Assert.Single(group.Specials);
        Assert.Equal("Atlantis-1.adf", ReleaseNamer.GetDiskFilename(group, group.Disks[0], 0, 3));
        Assert.Equal("Atlantis-2.adf", ReleaseNamer.GetDiskFilename(group, group.Disks[1], 1, 3));
        Assert.Equal("Atlantis-Save.adf", ReleaseNamer.GetDiskFilename(group, group.Specials[0], 2, 3));
    }

    [Fact]
    public void AppliesTheSameConventionToDashNumberedFilesInIntakeRoot()
    {
        var first = FilenameParser.Parse("Atlantis - 01.adf");
        var second = FilenameParser.Parse("Atlantis - 02.adf");
        var save = FilenameParser.Parse("Atlantis - Save.adf");

        var group = Assert.Single(ReleaseGrouper.Group([first, second, save]));

        Assert.Null(group.Folder);
        Assert.True(group.UseSequentialDiskNames);
        Assert.Equal("Atlantis-1.adf", ReleaseNamer.GetDiskFilename(group, group.Disks[0], 0, 3));
        Assert.Equal("Atlantis-2.adf", ReleaseNamer.GetDiskFilename(group, group.Disks[1], 1, 3));
        Assert.Equal("Atlantis-Save.adf", ReleaseNamer.GetDiskFilename(group, group.Specials[0], 2, 3));
    }

    [Fact]
    public void UsesReadableFolderTitleWhenDiskFilenamesAreGeneric()
    {
        var record = FilenameParser.Parse("Gry/F/FateOfAtlantis-FFAS/Atlantis - 01.adf");
        record.SourcePath = Path.Combine("C:\\library", "Gry", "F", "FateOfAtlantis-FFAS", "Atlantis - 01.adf");

        var group = Assert.Single(ReleaseGrouper.Group([record]));

        Assert.Equal("FateOfAtlantis-FFAS", group.Folder);
        Assert.Equal("Fate Of Atlantis", group.Title);
    }

    [Fact]
    public void DoesNotTreatAlphabeticIndexDirectoryAsGameFolder()
    {
        var records = new[]
        {
            "Gry/A/Arkanoid.adf",
            "Gry/A/Abyss.adf"
        }.Select(name =>
        {
            var record = FilenameParser.Parse(name);
            record.SourcePath = Path.Combine("C:\\library", name.Replace('/', Path.DirectorySeparatorChar));
            return record;
        });

        var groups = ReleaseGrouper.Group(records);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.Folder));
        Assert.Contains(groups, group => string.Equals(group.Title, "Arkanoid", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, group => string.Equals(group.Title, "Abyss", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AlphabeticIndexDoesNotSplitDashNumberedSet()
    {
        var records = new[]
        {
            "Gry/S/Atlantis - 01.adf",
            "Gry/S/Atlantis - 02.adf",
            "Gry/S/Atlantis - Save.adf"
        }.Select(name =>
        {
            var record = FilenameParser.Parse(name);
            record.SourcePath = Path.Combine("C:\\library", name.Replace('/', Path.DirectorySeparatorChar));
            return record;
        });

        var group = Assert.Single(ReleaseGrouper.Group(records));

        Assert.Null(group.Folder);
        Assert.Equal("Atlantis", group.Title);
        Assert.Equal(2, group.Disks.Count);
        Assert.Single(group.Specials);
        Assert.Equal("Atlantis-1.adf", ReleaseNamer.GetDiskFilename(group, group.Disks[0], 0, 3));
        Assert.Equal("Atlantis-2.adf", ReleaseNamer.GetDiskFilename(group, group.Disks[1], 1, 3));
        Assert.Equal("Atlantis-Save.adf", ReleaseNamer.GetDiskFilename(group, group.Specials[0], 2, 3));
    }

    [Fact]
    public void ExportCreatesOneFolderForDiskSetInOneSubdirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-folder-group-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original");
        var game = Path.Combine(original, "Atlantis");
        try
        {
            Directory.CreateDirectory(game);
            foreach (var name in new[] { "Atlantis - 01.adf", "Atlantis - 02.adf", "Atlantis - Save.adf" })
                File.WriteAllBytes(Path.Combine(game, name), [1, 2, 3]);

            var groups = ReleaseGrouper.Group(IntakeScanner.ScanDirectory(original).Select(scan =>
            {
                var record = FilenameParser.Parse(scan.Filename);
                record.SourcePath = scan.Path;
                record.SourceSha256 = scan.Sha256;
                return record;
            }));
            var result = GotekExporter.Export(groups, original, Path.Combine(root, "staging"),
                "folder-run", true, 320, 240);

            Assert.Single(groups);
            Assert.Equal(1, result.ReleasesExported);
            var output = Path.Combine(result.StagingRoot, "ADF", "Atlantis");
            Assert.True(File.Exists(Path.Combine(output, "Atlantis-1.adf")));
            Assert.True(File.Exists(Path.Combine(output, "Atlantis-2.adf")));
            Assert.True(File.Exists(Path.Combine(output, "Atlantis-Save.adf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

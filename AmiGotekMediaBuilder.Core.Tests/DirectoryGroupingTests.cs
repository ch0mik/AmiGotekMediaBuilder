using AmiGotekMediaBuilder.Core.Grouping;
using AmiGotekMediaBuilder.Core.Naming;
using AmiGotekMediaBuilder.Core.Parsing;
using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Scanning;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class DirectoryGroupingTests
{
    [Fact]
    public void KeepsImagesFromOneSubdirectoryTogetherAndUsesTosecNames()
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
        Assert.Equal("Atlantis (Disk 1 of 2).adf", ReleaseNamer.GetDiskFilename(group, group.Disks[0], 0, 3));
        Assert.Equal("Atlantis (Disk 2 of 2).adf", ReleaseNamer.GetDiskFilename(group, group.Disks[1], 1, 3));
        Assert.Equal("Atlantis (Save Disk).adf", ReleaseNamer.GetDiskFilename(group, group.Specials[0], 2, 3));
    }

    [Fact]
    public void AppliesTosecConventionToDashNumberedFilesInIntakeRoot()
    {
        var first = FilenameParser.Parse("Atlantis - 01.adf");
        var second = FilenameParser.Parse("Atlantis - 02.adf");
        var save = FilenameParser.Parse("Atlantis - Save.adf");

        var group = Assert.Single(ReleaseGrouper.Group([first, second, save]));

        Assert.Null(group.Folder);
        Assert.True(group.UseSequentialDiskNames);
        Assert.Equal("Atlantis (Disk 1 of 2).adf", ReleaseNamer.GetDiskFilename(group, group.Disks[0], 0, 3));
        Assert.Equal("Atlantis (Disk 2 of 2).adf", ReleaseNamer.GetDiskFilename(group, group.Disks[1], 1, 3));
        Assert.Equal("Atlantis (Save Disk).adf", ReleaseNamer.GetDiskFilename(group, group.Specials[0], 2, 3));
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
    public void KeepsGenericBootAndDataImagesInOneNamedGameFolder()
    {
        var records = new[] { "Games/Atlantis/Boot.adf", "Games/Atlantis/Data.adf" }
            .Select(FilenameParser.Parse);

        var group = Assert.Single(ReleaseGrouper.Group(records));

        Assert.Equal("Atlantis", group.Title);
        Assert.Equal("Atlantis", group.Folder);
        Assert.Equal(2, group.Records.Count);
    }

    [Fact]
    public void QuarantinesCollidingImagesInsideAnExplicitGameFolder()
    {
        var records = new[]
        {
            "Games/Aladdin/Aladdin (Disk 1 of 2).adf",
            "Games/Aladdin/Aladdin (Disk 1 of 2)[t +3].adf",
            "Games/Aladdin/Aladdin (Disk 2 of 2).adf"
        }.Select(FilenameParser.Parse);

        var group = Assert.Single(ReleaseGrouper.Group(records));

        Assert.Contains("same export filename", group.QuarantineReason);
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
    public void SeparatesGamesInNonAlphabeticCollectionAndKeepsCompanionDisks()
    {
        var names = new[]
        {
            "AGA/1869 (1993)(Flair)(AGA)(Disk 1 of 4)(A)[cr FLT].adf",
            "AGA/1869 (1993)(Flair)(AGA)(Disk 2 of 4)(B).adf",
            "AGA/1869 (1993)(Flair)(AGA)(Disk 3 of 4)(C)[cr FLT].adf",
            "AGA/1869 (1993)(Flair)(AGA)(Disk 4 of 4)(D).adf",
            "AGA/3D Galax (1989)(Gremlin)[cr Codetapper].adf"
        };
        var records = names.Select(name =>
        {
            var record = FilenameParser.Parse(name);
            record.SourcePath = Path.Combine("C:\\library", name.Replace('/', Path.DirectorySeparatorChar));
            return record;
        });

        var groups = ReleaseGrouper.Group(records);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.Folder));
        var game = Assert.Single(groups, group => group.Title == "1869");
        Assert.Equal(4, game.Disks.Count);
        Assert.True(game.IsComplete);
        Assert.Null(game.QuarantineReason);
        Assert.Equal("AGA", game.Chipset);
        Assert.Equal("FLT", game.Group);
        Assert.Contains(groups, group => group.Title == "3D Galax");
    }

    [Theory]
    [InlineData("0-9", "1000 Miglia", "1943 The Battle of Midway")]
    [InlineData("Quarterback", "Air Warrior", "Alien Breed")]
    public void SeparatesDifferentTitlesRegardlessOfCollectionFolderName(
        string folder, string firstTitle, string secondTitle)
    {
        var records = new[] { $"{folder}/{firstTitle}.adf", $"{folder}/{secondTitle}.adf" }
            .Select(name =>
            {
                var record = FilenameParser.Parse(name);
                record.SourcePath = Path.Combine("C:\\library", name.Replace('/', Path.DirectorySeparatorChar));
                return record;
            });

        var groups = ReleaseGrouper.Group(records);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Null(group.Folder));
        Assert.DoesNotContain(groups, group => group.Title == folder);
    }

    [Fact]
    public void SeparatesAlternativeFirstDisksAndSharesUnambiguousCompanions()
    {
        var names = new[]
        {
            "AGA/Aladdin (1994)(Virgin)(AGA)(Disk 1 of 3)[cr PDY].adf",
            "AGA/Aladdin (1994)(Virgin)(AGA)(Disk 1 of 3)[cr PDY][t +3 PDY].adf",
            "AGA/Aladdin (1994)(Virgin)(AGA)(Disk 2 of 3).adf",
            "AGA/Aladdin (1994)(Virgin)(AGA)(Disk 3 of 3).adf",
            "AGA/1869 (1993)(Flair)(AGA).adf"
        };
        var records = names.Select(name =>
        {
            var record = FilenameParser.Parse(name);
            record.SourcePath = Path.Combine("C:\\library", name.Replace('/', Path.DirectorySeparatorChar));
            return record;
        });

        var groups = ReleaseGrouper.Group(records);
        var variants = groups.Where(group => group.Title == "Aladdin").ToArray();

        Assert.Equal(2, variants.Length);
        Assert.All(variants, group =>
        {
            Assert.Equal(3, group.Disks.Count);
            Assert.True(group.IsComplete);
            Assert.Null(group.QuarantineReason);
        });
        Assert.Equal(2, variants.Select(ReleaseNamer.GetBasename).Distinct().Count());
    }

    [Fact]
    public void KeepsUnpairedAlternativeImagesVisibleAsQuarantinedGroups()
    {
        var names = new[]
        {
            "AGA/Game (Disk 1 of 2)[cr A].adf",
            "AGA/Game (Disk 1 of 2)[cr B].adf",
            "AGA/Game (Disk 2 of 2)[cr C].adf",
            "AGA/Game (Disk 2 of 2)[cr D].adf",
            "AGA/Other.adf"
        };
        var records = names.Select(FilenameParser.Parse).ToArray();

        var groups = ReleaseGrouper.Group(records);

        Assert.Equal(records.Length, groups.SelectMany(group => group.Records).Distinct().Count());
        Assert.Contains(groups, group => group.QuarantineReason?.Contains("manual review", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void SeparatesAlternativeImagesInAlphabeticCollection()
    {
        var records = new[]
        {
            "A/Aladdin (Disk 1 of 2)[cr PDY].adf",
            "A/Aladdin (Disk 1 of 2)[cr PDY][t +3].adf",
            "A/Aladdin (Disk 2 of 2)[cr PDY].adf"
        }.Select(FilenameParser.Parse);

        var groups = ReleaseGrouper.Group(records);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.True(group.IsComplete));
        Assert.Equal(2, groups.Select(ReleaseNamer.GetBasename).Distinct().Count());
    }

    [Fact]
    public void GivesDifferentReleasesUniqueOutputNamesAcrossCollectionFolders()
    {
        var records = new[]
        {
            "A/Same.adf", "A/Other.adf",
            "AGA/Same.adf", "AGA/Different.adf"
        }.Select(FilenameParser.Parse);

        var groups = ReleaseGrouper.Group(records);

        Assert.Equal(groups.Count, groups.Select(ReleaseNamer.GetBasename)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
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
        Assert.Equal("Atlantis (Disk 1 of 2).adf", ReleaseNamer.GetDiskFilename(group, group.Disks[0], 0, 3));
        Assert.Equal("Atlantis (Disk 2 of 2).adf", ReleaseNamer.GetDiskFilename(group, group.Disks[1], 1, 3));
        Assert.Equal("Atlantis (Save Disk).adf", ReleaseNamer.GetDiskFilename(group, group.Specials[0], 2, 3));
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
            var output = Path.Combine(result.StagingRoot, "ADF", "Games", "Atlantis");
            Assert.True(File.Exists(Path.Combine(output, "Atlantis (Disk 1 of 2).adf")));
            Assert.True(File.Exists(Path.Combine(output, "Atlantis (Disk 2 of 2).adf")));
            Assert.True(File.Exists(Path.Combine(output, "Atlantis (Save Disk).adf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void QuarantinesIncompleteDeclaredTosecSetAndDoesNotExportIt()
    {
        var records = new[]
        {
            "Odyssey v1.0 (1991-12-28)(Alcatraz)(Disk 2 of 5).adf",
            "Odyssey v1.0 (1991-12-28)(Alcatraz)(Disk 3 of 5).adf",
            "Odyssey v1.0 (1991-12-28)(Alcatraz)(Disk 4 of 5).adf",
            "Odyssey v1.0 (1991-12-28)(Alcatraz)(Disk 5 of 5).adf"
        }.Select(FilenameParser.Parse).ToArray();
        var group = Assert.Single(ReleaseGrouper.Group(records));

        Assert.False(group.IsComplete);
        Assert.Contains("expected disks 1-5", group.QuarantineReason, StringComparison.Ordinal);
        Assert.Contains("missing disk 1", group.QuarantineReason, StringComparison.Ordinal);

        var root = Path.Combine(Path.GetTempPath(), "amiga-incomplete-set-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = GotekExporter.Export([group], root, Path.Combine(root, "staging"), "run",
                upstreamTaskClosed: true, verifiedArtworkWidth: 320, verifiedArtworkHeight: 240);

            Assert.Equal(0, result.ReleasesExported);
            Assert.Equal([group.ReleaseKey], result.SkippedQuarantined);
            Assert.Empty(result.FilesWritten);
            Assert.False(Directory.Exists(Path.Combine(result.StagingRoot, "ADF", "Games", "Odyssey v1.0")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeepsACompleteVersionOfAReleaseExportable()
    {
        var records = Enumerable.Range(1, 5).Select(number => FilenameParser.Parse(
            $"Odyssey v1.1c (1991-12-28)(Alcatraz)(Disk {number} of 5)[TP1#1].adf"));

        var group = Assert.Single(ReleaseGrouper.Group(records));

        Assert.True(group.IsComplete);
        Assert.Null(group.QuarantineReason);
        Assert.Equal(5, group.Disks.Count);
    }
}

using AmiGotekMediaBuilder.Core.Models;
using AmiGotekMediaBuilder.Core.Naming;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class ReleaseNamerTests
{
    [Fact]
    public void PreservesIdentityFieldsInBasename()
    {
        var group = new ReleaseGroup
        {
            ReleaseKey = "x", Title = "Oil Imperium", Extension = "adf",
            Chipset = "ECS", Group = "QTX", Language = "EN", Version = "1.1e"
        };

        Assert.Equal("Oil Imperium ECS cr QTX lang EN ver 1.1e", ReleaseNamer.GetBasename(group));
    }

    [Fact]
    public void SanitizesUnsafeCharacters()
    {
        var group = new ReleaseGroup { ReleaseKey = "x", Title = "Bad:*Name?", Extension = "adf" };
        Assert.Equal("Bad__Name_", ReleaseNamer.GetBasename(group));
    }

    [Fact]
    public void ConvertsTosecDiskMarkerToCompactOutputName()
    {
        var group = new ReleaseGroup
        {
            ReleaseKey = "game", Title = "Game", Extension = "adf"
        };
        var disk = new ParsedRecord
        {
            SourceFilename = "Game_(Disk 2 of 2)(Data).adf",
            Extension = "adf",
            Title = "Game",
            DiskNumber = 2,
            TotalDisks = 2,
            MediaLabel = "Data"
        };
        group.Records.Add(disk);
        group.Disks.Add(disk);

        Assert.Equal("Game-2.adf", ReleaseNamer.GetDiskFilename(group, disk, 0, 1));
    }

    [Fact]
    public void PadsTenOrMoreDisksAsRequiredByTosec()
    {
        var group = new ReleaseGroup { ReleaseKey = "game", Title = "Game", Extension = "adf" };
        var disk = new ParsedRecord
        {
            SourceFilename = "Game_(Disk 2 of 10).adf",
            Extension = "adf",
            Title = "Game",
            DiskNumber = 2,
            TotalDisks = 10
        };
        group.Records.Add(disk);

        Assert.Equal("(Disk 02 of 10)", ReleaseNamer.GetDiskMarker(group, disk, 0, 1));
    }

    [Fact]
    public void PreservesDashNumberingAndSaveNameInOneFolder()
    {
        var group = new ReleaseGroup { ReleaseKey = "atlantis", Title = "Atlantis", Extension = "adf" };
        var disk = new ParsedRecord
        {
            SourceFilename = "Atlantis - 01.adf",
            Extension = "adf",
            Title = "Atlantis",
            DiskNumber = 1,
            DashNumbered = true,
            DiskNumberWidth = 2
        };
        var save = new ParsedRecord
        {
            SourceFilename = "Atlantis - Save.adf",
            Extension = "adf",
            Title = "Atlantis",
            SpecialDisk = true,
            SpecialRole = "save"
        };
        group.Records.AddRange([disk, save]);
        group.Disks.Add(disk);
        group.Specials.Add(save);

        Assert.Equal("Atlantis-1.adf", ReleaseNamer.GetDiskFilename(group, disk, 0, 2));
        Assert.Equal("Atlantis-Save.adf", ReleaseNamer.GetDiskFilename(group, save, 1, 2));
    }
}

using AmiGotekMediaBuilder.Core.Parsing;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class FilenameParserTests
{
    [Fact]
    public void ParsesTosecTagsAndNumericDisk()
    {
        var record = FilenameParser.Parse("Oil_Imperium_(1992)(ECS)(en)(v1.1e)[cr QTX](Disk 2 of 3).adf");

        Assert.Equal("Oil Imperium", record.Title);
        Assert.Equal("1992", record.Year);
        Assert.Equal("ECS", record.Chipset);
        Assert.Equal("EN", record.Language);
        Assert.Equal("v1.1e", record.Version);
        Assert.Equal("QTX", record.Group);
        Assert.Equal(2, record.DiskNumber);
        Assert.Equal(3, record.TotalDisks);
        Assert.False(record.SpecialDisk);
    }

    [Fact]
    public void ParsesSpecialDiskAndEdition()
    {
        var record = FilenameParser.Parse("Example_Quest_III_Boot_(199x).dsk");

        Assert.Equal("Example Quest III", record.Title);
        Assert.Equal("199x", record.Year);
        Assert.True(record.SpecialDisk);
        Assert.Equal("boot", record.SpecialRole);
        Assert.Null(record.DiskNumber);
    }

    [Fact]
    public void ParsesTosecMediaLabelAfterDiskMarker()
    {
        var record = FilenameParser.Parse("Game_(1991)(Disk 2 of 2)(Data).adf");

        Assert.Equal("Game", record.Title);
        Assert.Equal(2, record.DiskNumber);
        Assert.Equal(2, record.TotalDisks);
        Assert.Equal("Data", record.MediaLabel);
        Assert.Null(record.Publisher);
    }

    [Fact]
    public void ParsesDashNumberedGameAndSaveAsOneReleaseIdentity()
    {
        var disk = FilenameParser.Parse("Atlantis - 01.adf");
        var save = FilenameParser.Parse("Atlantis - Save.adf");

        Assert.Equal("Atlantis", disk.Title);
        Assert.Equal(1, disk.DiskNumber);
        Assert.True(disk.DashNumbered);
        Assert.Equal(2, disk.DiskNumberWidth);
        Assert.Equal("Atlantis", save.Title);
        Assert.True(save.SpecialDisk);
        Assert.Equal("save", save.SpecialRole);
        Assert.Equal(disk.ReleaseKey, save.ReleaseKey);
    }
}

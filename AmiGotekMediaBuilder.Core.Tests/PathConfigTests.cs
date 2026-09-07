using AmiGotekMediaBuilder.Core.Configuration;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class PathConfigTests
{
    [Fact]
    public void UsesLibraryRootAsSourceWhenOriginalDirectoryIsAbsent()
    {
        var config = PathConfig.Create(@"C:\libraries\amiga");

        Assert.Equal(Path.GetFullPath(@"C:\libraries\amiga"), config.OriginalDirectory);
        Assert.Equal(Path.GetFullPath(@"C:\libraries\amiga\work\staging"), config.StagingDirectory);
        Assert.Equal(Path.GetFullPath(@"C:\libraries\amiga\assets\nfo"), config.NfoDirectory);
    }

    [Fact]
    public void SupportsAnExplicitSeparateSourceDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-config-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "original");
        Directory.CreateDirectory(source);
        try
        {
            var config = PathConfig.Create(root, originalDirectory: source);
            Assert.Equal(Path.GetFullPath(source), config.OriginalDirectory);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsWritableRoleInsideOriginal()
    {
        var exception = Assert.Throws<PathConfigException>(() =>
            PathConfig.Create(@"C:\libraries\amiga", originalDirectory: @"C:\immutable", outputDirectory: @"C:\immutable\output"));

        Assert.Contains("originalDirectory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}

using AmiGotekMediaBuilder.Core.Configuration;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class PathConfigSymlinkTests
{
    [Fact]
    public void RejectsRoleThatResolvesInsideOriginalThroughDirectoryLink()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-adf-link-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original");
        var outputLink = Path.Combine(root, "output-link");
        Directory.CreateDirectory(original);
        try
        {
            try { Directory.CreateSymbolicLink(outputLink, original); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            Assert.Throws<PathConfigException>(() => PathConfig.Create(root, outputDirectory: outputLink));
        }
        finally
        {
            if (Directory.Exists(outputLink)) Directory.Delete(outputLink);
            if (Directory.Exists(original)) Directory.Delete(original);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}

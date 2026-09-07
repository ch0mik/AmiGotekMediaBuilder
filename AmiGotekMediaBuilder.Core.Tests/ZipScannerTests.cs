using System.IO.Compression;
using AmiGotekMediaBuilder.Core.Scanning;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class ZipScannerTests
{
    [Fact]
    public void ScansAdfEntriesInsideZipWithoutExtracting()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-zip-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var archive = Path.Combine(root, "collection.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("Nested/Game Disk 1.adf");
                using (var stream = entry.Open())
                    stream.Write([1, 2, 3]);
                zip.CreateEntry("readme.txt");
            }
            var records = IntakeScanner.ScanDirectory(root);
            var record = Assert.Single(records);
            Assert.Equal("Nested/Game Disk 1.adf", record.Filename);
            Assert.Contains("collection.zip::Nested/Game Disk 1.adf", record.Path);
            Assert.Equal(3, record.Size);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

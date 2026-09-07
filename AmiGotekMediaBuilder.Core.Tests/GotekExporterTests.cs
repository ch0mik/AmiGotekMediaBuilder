using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class GotekExporterTests
{
    [Fact]
    public void GateStaysClosedWithoutOperatorAcknowledgement()
    {
        var result = GotekExporter.Export(
            [], @"C:\original", @"C:\staging", "run-1",
            upstreamTaskClosed: false, verifiedArtworkWidth: 320, verifiedArtworkHeight: 240);

        Assert.False(result.ExportGateOpen);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.FilesWritten);
    }

    [Fact]
    public void RejectsUnsafeRunId()
    {
        Assert.Throws<ArgumentException>(() => GotekExporter.Export(
            [], @"C:\original", @"C:\staging", @"..\escape",
            upstreamTaskClosed: true, verifiedArtworkWidth: 320, verifiedArtworkHeight: 240));
    }

    [Fact]
    public void CopiesProcessedArtworkAlongsideDiskAndNfo()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Game.adf");
            File.WriteAllBytes(source, [1, 2, 3]);
            var group = new ReleaseGroup { ReleaseKey = "game|", Title = "Game", Extension = "adf" };
            var record = new ParsedRecord { SourceFilename = "Game.adf", Extension = "adf", SourcePath = source, Title = "Game" };
            group.Records.Add(record);
            group.Disks.Add(record);
            var artworkDirectory = Path.Combine(root, "artwork");
            Directory.CreateDirectory(artworkDirectory);
            File.WriteAllBytes(Path.Combine(artworkDirectory, "Game.jpg"), [0xff, 0xd8, 0xff, 0xd9]);

            var result = GotekExporter.Export(new[] { group }, root, Path.Combine(root, "staging"), "run",
                upstreamTaskClosed: true, verifiedArtworkWidth: 320, verifiedArtworkHeight: 240,
                artworkProcessedDirectory: artworkDirectory);

            Assert.Contains(result.FilesWritten, path => path.EndsWith("Game.jpg", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReplacesDemosceneArtworkWithGameFallbackDuringExport()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "FateOfAtlantis-FFAS.adf");
            File.WriteAllBytes(source, [1, 2, 3]);
            var group = new ReleaseGroup { ReleaseKey = "fate|", Title = "FateOfAtlantis-FFAS", Extension = "adf" };
            var record = new ParsedRecord { SourceFilename = Path.GetFileName(source), Extension = "adf", SourcePath = source, Title = group.Title };
            group.Records.Add(record);
            group.Disks.Add(record);
            var originalArtwork = Path.Combine(root, "artwork-original");
            var processedArtwork = Path.Combine(root, "artwork-processed");
            Directory.CreateDirectory(originalArtwork);
            Directory.CreateDirectory(processedArtwork);
            File.WriteAllBytes(Path.Combine(processedArtwork, "FateOfAtlantis-FFAS.jpg"), [0xff, 0xd8, 0xff, 0xd9]);
            File.WriteAllText(Path.Combine(originalArtwork, "FateOfAtlantis-FFAS.jpg.source.json"),
                "{\"catalog\":\"pouet\",\"source_id\":\"123\"}");

            var result = GotekExporter.Export(new[] { group }, root, Path.Combine(root, "staging"), "run",
                upstreamTaskClosed: true, verifiedArtworkWidth: 320, verifiedArtworkHeight: 240,
                artworkProcessedDirectory: processedArtwork, artworkOriginalDirectory: originalArtwork);

            var exportedArtwork = Assert.Single(result.FilesWritten, path =>
                path.EndsWith("FateOfAtlantis-FFAS.jpg", StringComparison.OrdinalIgnoreCase));
            Assert.True(new FileInfo(exportedArtwork).Length > 1_000);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

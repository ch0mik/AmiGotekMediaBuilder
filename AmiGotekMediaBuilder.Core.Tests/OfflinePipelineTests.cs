using AmiGotekMediaBuilder.Core.Configuration;
using AmiGotekMediaBuilder.Core.Export;
using AmiGotekMediaBuilder.Core.Grouping;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Parsing;
using AmiGotekMediaBuilder.Core.Scanning;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class OfflinePipelineTests
{
    [Fact]
    public void ScansGroupsEnrichesAndExportsWithoutNetwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-e2e-" + Guid.NewGuid().ToString("N"));
        var original = Path.Combine(root, "original");
        try
        {
            Directory.CreateDirectory(original);
            File.WriteAllBytes(Path.Combine(original, "Oil_Imperium_(1992)(ECS)(Disk 1 of 2).adf"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(original, "Oil_Imperium_(1992)(ECS)(Disk 2 of 2).adf"), [4, 5, 6]);

            var config = PathConfig.Create(root, originalDirectory: original);
            var scans = IntakeScanner.ScanDirectory(config.OriginalDirectory);
            var groups = ReleaseGrouper.Group(scans.Select(s => FilenameParser.Parse(s.Filename)));
            Assert.Single(groups);
            Assert.True(groups[0].IsComplete);

            var metadata = new OfflineEnricher().Enrich(groups, config.MetadataCacheDirectory, config.NfoDirectory);
            Assert.Single(metadata);
            var exported = GotekExporter.Export(groups, config.OriginalDirectory, config.StagingDirectory,
                "integration-run", true, 320, 240, nfoDirectory: config.NfoDirectory);

            Assert.Empty(exported.Errors);
            Assert.Equal(1, exported.ReleasesExported);
            Assert.Equal(4, exported.FilesWritten.Count);
            Assert.True(File.Exists(Path.Combine(exported.StagingRoot, "ADF", "Games", "Oil Imperium ECS", "Oil Imperium ECS (Disk 1 of 2).adf")));
            Assert.True(File.Exists(Path.Combine(exported.StagingRoot, "ADF", "Games", "Oil Imperium ECS", "Oil Imperium ECS (Disk 2 of 2).adf")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

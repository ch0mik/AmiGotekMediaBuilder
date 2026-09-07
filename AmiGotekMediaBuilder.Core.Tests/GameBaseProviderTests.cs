using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using Microsoft.Data.Sqlite;

namespace AmiGotekMediaBuilder.Core.Tests;

public sealed class GameBaseProviderTests
{
    [Fact]
    public async Task MatchesTitleAndFilenameAndResolvesRelativeArtwork()
    {
        var root = Path.Combine(Path.GetTempPath(), "amiga-gamebase-" + Guid.NewGuid().ToString("N"));
        var imageDirectory = Path.Combine(root, "Screenshots");
        var database = Path.Combine(root, "gamebase.sqlite");
        Directory.CreateDirectory(imageDirectory);
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE Games (Name TEXT, Filename TEXT, Year TEXT, Publisher TEXT, Comment TEXT, Screenshot TEXT);" +
                                      "INSERT INTO Games VALUES ('Lotus Esprit Turbo Challenge','Lotus.adf','1990','Gremlin','Fast racing','lotus.png');";
                await command.ExecuteNonQueryAsync();
            }
            await File.WriteAllBytesAsync(Path.Combine(imageDirectory, "lotus.png"),
                [137, 80, 78, 71, 13, 10, 26, 10]);

            var group = new ReleaseGroup
            {
                ReleaseKey = "lotus", Title = "Lotus Esprit Turbo Challenge", Extension = "adf"
            };
            group.Records.Add(new ParsedRecord { SourceFilename = "Lotus - 01.adf", Extension = "adf" });
            var result = await new GameBaseProvider(database).ResolveAsync(group);

            Assert.NotNull(result);
            Assert.Equal("Lotus Esprit Turbo Challenge", result!.Title);
            Assert.Equal("1990", result.Year);
            Assert.Equal("Gremlin", result.Publisher);
            Assert.Equal("gamebase", result.Provider);
            Assert.Equal(Path.Combine(imageDirectory, "lotus.png"), result.ArtworkPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissingOptionalDatabaseIsANoOp()
    {
        var group = new ReleaseGroup { ReleaseKey = "missing", Title = "Unknown", Extension = "adf" };
        var result = await new GameBaseProvider(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".sqlite"))
            .ResolveAsync(group);
        Assert.Null(result);
    }
}

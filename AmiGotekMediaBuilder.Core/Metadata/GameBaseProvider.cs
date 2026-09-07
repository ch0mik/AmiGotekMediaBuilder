using System.Text.RegularExpressions;
using AmiGotekMediaBuilder.Core.Models;
using Microsoft.Data.Sqlite;

namespace AmiGotekMediaBuilder.Core.Metadata;

/// <summary>
/// Reads the local SQLite form of a GameBase database. GameBase sets are
/// distributed as MDB files; Skyscraper's mdb2sqlite helper produces the
/// portable SQLite file consumed here. No network or credentials are used.
/// </summary>
public sealed class GameBaseProvider : IAsyncMetadataProvider
{
    private static readonly string[] TitleColumns = ["name", "game_name", "gamename", "title", "long_name", "description"];
    private static readonly string[] FilenameColumns = ["filename", "file_name", "file", "game_file", "gamefile", "shortname", "basefile", "rom"];
    private static readonly string[] YearColumns = ["year", "release_year", "released", "date"];
    private static readonly string[] PublisherColumns = ["publisher", "company", "developer"];
    private static readonly string[] DeveloperColumns = ["developer", "author", "programmer"];
    private static readonly string[] DescriptionColumns = ["comment", "description", "info", "notes", "overview", "plot"];
    private static readonly string[] ArtworkColumns = [
        "screenshot", "screenshot1", "screenshot2", "screenshot3", "screenshot_file",
        "screenshotfilename", "screen_shot", "cover", "cover1", "cover2", "cover_file",
        "coverfilename", "picture", "image", "image_file", "boxart"];

    private readonly string? _databasePath;
    private readonly object _sync = new();
    private IReadOnlyList<Row>? _rows;

    public GameBaseProvider(string? databasePath = null)
    {
        _databasePath = string.IsNullOrWhiteSpace(databasePath)
            ? Environment.GetEnvironmentVariable("AMIGA_ADF_GAMEBASE_DB") ??
              Environment.GetEnvironmentVariable("GAMEBASE_DB_PATH")
            : databasePath;
        if (!string.IsNullOrWhiteSpace(_databasePath))
            _databasePath = Path.GetFullPath(_databasePath);
    }

    public string Id => "gamebase";

    public Task<MetadataRecord?> ResolveAsync(ReleaseGroup group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (string.IsNullOrWhiteSpace(_databasePath) || !File.Exists(_databasePath))
            return Task.FromResult<MetadataRecord?>(null);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var best = FindBest(group, LoadRows());
            if (best is null) return Task.FromResult<MetadataRecord?>(null);
            var title = best.Get(TitleColumns) ?? group.Title ?? "Unknown";
            var artworkPath = ResolveArtworkPath(best.Get(ArtworkColumns), title, best.Get(FilenameColumns));
            MetadataRecord result = new(group.ReleaseKey, title.Trim(),
                Clean(best.Get(YearColumns)), Clean(best.Get(PublisherColumns) ?? best.Get(DeveloperColumns)),
                Clean(best.Get(DescriptionColumns)), Id, DateTimeOffset.UtcNow)
            {
                ArtworkPath = artworkPath,
                ArtworkSourceUrl = artworkPath is null ? null : _databasePath,
                ArtworkProvider = artworkPath is null ? null : Id
            };
            return Task.FromResult<MetadataRecord?>(result);
        }
        catch (SqliteException)
        {
            // A missing/invalid optional database should not make an online
            // build fail; the remaining providers can still resolve metadata.
            return Task.FromResult<MetadataRecord?>(null);
        }
    }

    private IReadOnlyList<Row> LoadRows()
    {
        lock (_sync)
        {
            if (_rows is not null) return _rows;
            var rows = new List<Row>();
            using var connection = new SqliteConnection($"Data Source={_databasePath};Mode=ReadOnly;Cache=Shared;Pooling=False");
            connection.Open();
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            var tableNames = new List<string>();
            using (var tableReader = tables.ExecuteReader())
            {
                while (tableReader.Read()) tableNames.Add(tableReader.GetString(0));
            }
            foreach (var table in tableNames)
            {
                var columns = GetColumns(connection, table);
                if (!columns.Any(c => TitleColumns.Contains(c, StringComparer.OrdinalIgnoreCase)) ||
                    !columns.Any(c => FilenameColumns.Contains(c, StringComparer.OrdinalIgnoreCase))) continue;
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM {QuoteIdentifier(table)}";
                using var reader = command.ExecuteReader();
                var ordinal = Enumerable.Range(0, reader.FieldCount)
                    .ToDictionary(i => reader.GetName(i), i => i, StringComparer.OrdinalIgnoreCase);
                while (reader.Read())
                {
                    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in ordinal)
                        values[pair.Key] = reader.IsDBNull(pair.Value) ? null : Convert.ToString(reader.GetValue(pair.Value));
                    rows.Add(new Row(values, _databasePath!));
                    if (rows.Count >= 250_000) break;
                }
                if (rows.Count >= 250_000) break;
            }
            return _rows = rows;
        }
    }

    private static IReadOnlyList<string> GetColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)})";
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(1));
        return result;
    }

    private static Row? FindBest(ReleaseGroup group, IReadOnlyList<Row> rows)
    {
        var bestScore = 0d;
        Row? best = null;
        foreach (var row in rows)
        {
            var rowTitle = row.Get(TitleColumns);
            var rowFile = row.Get(FilenameColumns);
            if (string.IsNullOrWhiteSpace(rowTitle) && string.IsNullOrWhiteSpace(rowFile)) continue;
            var score = Similarity(group.Title, rowTitle);
            if (WildcardMatches(rowTitle, group.Title)) score = Math.Max(score, 0.95);
            foreach (var record in group.Records)
            {
                var source = NormalizeFile(record.SourceFilename);
                var candidate = NormalizeFile(rowFile);
                if (source.Length > 0 && candidate.Length > 0)
                {
                    if (source.Equals(candidate, StringComparison.Ordinal)) score = Math.Max(score, 1.2);
                    else if (source.StartsWith(candidate, StringComparison.Ordinal) || candidate.StartsWith(source, StringComparison.Ordinal))
                        score = Math.Max(score, 0.9);
                    else if (WildcardMatches(rowFile, record.SourceFilename)) score = Math.Max(score, 1.1);
                }
            }
            if (score > bestScore)
            {
                bestScore = score;
                best = row;
            }
        }
        return bestScore >= 0.45 ? best : null;
    }

    private string? ResolveArtworkPath(string? value, string title, string? filename)
    {
        var dbDirectory = Path.GetDirectoryName(_databasePath!);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (Path.IsPathRooted(value)) candidates.Add(value);
            else
            {
                candidates.Add(Path.Combine(dbDirectory!, value));
                foreach (var folder in new[] { "Screenshots", "Screenshot", "Cover", "Covers", "Pictures", "Images", "Artwork" })
                    candidates.Add(Path.Combine(dbDirectory!, folder, value));
            }
        }
        var stems = new[] { title, filename is null ? null : Path.GetFileNameWithoutExtension(filename) }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => SafeStem(x!)).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var stem in stems)
            foreach (var folder in new[] { "Screenshots", "Screenshot", "Cover", "Covers", "Pictures", "Images", "Artwork" })
                foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
                    candidates.Add(Path.Combine(dbDirectory!, folder, stem + extension));
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    private static string SafeStem(string value) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(value), "[^A-Za-z0-9._-]", "_");

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeFile(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var stem = Path.GetFileNameWithoutExtension(value).ToLowerInvariant();
        stem = Regex.Replace(stem, @"(?:disk|disc|side)\s*\d+(?:\s*(?:of|/)\s*\d+)?", "", RegexOptions.IgnoreCase);
        stem = Regex.Replace(stem, @"(?:-|_|\s)+(?:save|\d{1,2})(?:\s*(?:of|/)\s*\d+)?$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(stem, "[^a-z0-9]+", "");
    }

    private static double Similarity(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return 0;
        var a = NormalizeText(left); var b = NormalizeText(right);
        if (a == b) return 1;
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)) return 0.82;
        var at = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var bt = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return (double)at.Intersect(bt).Count() / Math.Max(at.Count, bt.Count);
    }

    private static bool WildcardMatches(string? pattern, string? value)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(value) ||
            (!pattern.Contains('*') && !pattern.Contains('?'))) return false;
        var expression = "^" + Regex.Escape(pattern.Trim())
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value.Trim(), expression, RegexOptions.IgnoreCase);
    }

    private static string NormalizeText(string value) =>
        string.Join(' ', value.ToLowerInvariant().Replace('&', ' ').Split(
            [' ', '\t', '\r', '\n', '-', '_', ':', '/', '(', ')', '[', ']', ',', '.'],
            StringSplitOptions.RemoveEmptyEntries));

    private sealed record Row(IReadOnlyDictionary<string, string?> Values, string DatabasePath)
    {
        public string? Get(IEnumerable<string> names) => names.Select(name =>
            Values.TryGetValue(name, out var value) ? value : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }
}

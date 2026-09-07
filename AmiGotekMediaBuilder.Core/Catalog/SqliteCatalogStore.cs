using System.Security.Cryptography;
using System.Text;
using AmiGotekMediaBuilder.Core.Metadata;
using AmiGotekMediaBuilder.Core.Models;
using Microsoft.Data.Sqlite;

namespace AmiGotekMediaBuilder.Core.Catalog;

/// <summary>
/// Namespace used to keep one catalog database safe for multiple systems and
/// content types. The current applications use Amiga/game and Amiga/demo,
/// while callers for other systems can provide their own namespace.
/// </summary>
public sealed record CatalogNamespace(
    string SystemId,
    string ContentType = "game",
    string Platform = "default")
{
    public static CatalogNamespace For(ReleaseGroup group) =>
        new(group.SystemId,
            string.IsNullOrWhiteSpace(group.ContentType)
                ? (group.IsDemoscene ? "demo" : "game")
                : group.ContentType,
            group.Chipset ?? "default");

    internal CatalogNamespace Normalize() => new(
        NormalizePart(SystemId, "unknown-system"),
        NormalizePart(ContentType, "game"),
        NormalizePart(Platform, "default"));

    private static string NormalizePart(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().ToLowerInvariant();
}

/// <summary>
/// Persistent, provider-aware metadata and artwork cache. Lookups are
/// content-hash-first and fall back to a release key for records that have no
/// usable hash yet.
/// </summary>
public sealed class SqliteCatalogStore
{
    private const string SchemaVersion = "1";
    private readonly string _databasePath;

    public SqliteCatalogStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Catalog database path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        EnsureSchema();
    }

    public MetadataRecord? ReadMetadata(
        ReleaseGroup group,
        CatalogNamespace? catalogNamespace = null,
        bool includeOffline = true)
    {
        ArgumentNullException.ThrowIfNull(group);
        var ns = (catalogNamespace ?? CatalogNamespace.For(group)).Normalize();
        using var connection = OpenConnection();
        // A release-key match is safe only when no content hash is available;
        // otherwise a renamed/replaced image must not inherit stale metadata.
        var keys = LookupKeys(group).ToArray();
        if (HasSourceHash(group))
            keys = keys.Where(key => key.Kind == "sha256").ToArray();
        foreach (var key in keys)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT release_key, provider, title, year, publisher, description,
                       artwork_url, artwork_source_url, artwork_provider, artwork_path,
                       retrieved_at
                FROM metadata_cache
                WHERE system_id = $system AND content_type = $content AND platform = $platform
                  AND lookup_kind = $kind AND lookup_value = $value
                  AND ($include_offline = 1 OR provider <> 'offline-filename')
                ORDER BY CASE WHEN provider = 'offline-filename' THEN 1 ELSE 0 END,
                         retrieved_at DESC
                LIMIT 1;
                """;
            Add(command, "$system", ns.SystemId);
            Add(command, "$content", ns.ContentType);
            Add(command, "$platform", ns.Platform);
            Add(command, "$kind", key.Kind);
            Add(command, "$value", key.Value);
            Add(command, "$include_offline", includeOffline ? 1 : 0);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) continue;

            var record = ReadMetadata(reader);
            var artworkPath = ReadArtworkPath(connection, ns, key);
            return artworkPath is null ? record : record with { ArtworkPath = artworkPath };
        }
        return null;
    }

    public void WriteMetadata(
        ReleaseGroup group,
        MetadataRecord record,
        CatalogNamespace? catalogNamespace = null,
        bool includeHash = true)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(record);
        var ns = (catalogNamespace ?? CatalogNamespace.For(group)).Normalize();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var keys = LookupKeys(group)
            .Where(key => includeHash || key.Kind == "release-key");
        foreach (var key in keys)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO metadata_cache
                    (system_id, content_type, platform, lookup_kind, lookup_value,
                     release_key, provider, title, year, publisher, description,
                     artwork_url, artwork_source_url, artwork_provider, artwork_path,
                     retrieved_at)
                VALUES
                    ($system, $content, $platform, $kind, $value,
                     $release_key, $provider, $title, $year, $publisher, $description,
                     $artwork_url, $artwork_source_url, $artwork_provider, $artwork_path,
                     $retrieved_at)
                ON CONFLICT(system_id, content_type, platform, lookup_kind, lookup_value, provider)
                DO UPDATE SET
                    release_key = excluded.release_key,
                    title = excluded.title,
                    year = excluded.year,
                    publisher = excluded.publisher,
                    description = excluded.description,
                    artwork_url = excluded.artwork_url,
                    artwork_source_url = excluded.artwork_source_url,
                    artwork_provider = excluded.artwork_provider,
                    artwork_path = excluded.artwork_path,
                    retrieved_at = excluded.retrieved_at;
                """;
            AddNamespace(command, ns, key);
            Add(command, "$release_key", record.ReleaseKey);
            Add(command, "$provider", record.Provider);
            Add(command, "$title", record.Title);
            Add(command, "$year", record.Year);
            Add(command, "$publisher", record.Publisher);
            Add(command, "$description", record.Description);
            Add(command, "$artwork_url", record.ArtworkUrl);
            Add(command, "$artwork_source_url", record.ArtworkSourceUrl);
            Add(command, "$artwork_provider", record.ArtworkProvider);
            Add(command, "$artwork_path", record.ArtworkPath);
            Add(command, "$retrieved_at", record.RetrievedAt.ToUniversalTime().ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void WriteArtwork(
        ReleaseGroup group,
        ArtworkArtifact artifact,
        CatalogNamespace? catalogNamespace = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(artifact);
        var ns = (catalogNamespace ?? CatalogNamespace.For(group)).Normalize();
        var artworkSha256 = FileSha256(artifact.OriginalPath);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var key in LookupKeys(group))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO artwork_cache
                    (system_id, content_type, platform, lookup_kind, lookup_value,
                     provider, source_url, original_path, processed_path,
                     artwork_sha256, retrieved_at)
                VALUES
                    ($system, $content, $platform, $kind, $value,
                     $provider, $source_url, $original_path, $processed_path,
                     $artwork_sha256, $retrieved_at)
                ON CONFLICT(system_id, content_type, platform, lookup_kind, lookup_value)
                DO UPDATE SET
                    provider = excluded.provider,
                    source_url = excluded.source_url,
                    original_path = excluded.original_path,
                    processed_path = excluded.processed_path,
                    artwork_sha256 = excluded.artwork_sha256,
                    retrieved_at = excluded.retrieved_at;
                """;
            AddNamespace(command, ns, key);
            Add(command, "$provider", artifact.Provider);
            Add(command, "$source_url", artifact.Url);
            Add(command, "$original_path", artifact.OriginalPath);
            Add(command, "$processed_path", artifact.ProcessedPath);
            Add(command, "$artwork_sha256", artworkSha256);
            Add(command, "$retrieved_at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void EnsureSchema()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS catalog_schema (
                version TEXT PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO catalog_schema(version, applied_at)
                VALUES ('1', $applied_at);

            CREATE TABLE IF NOT EXISTS metadata_cache (
                system_id TEXT NOT NULL,
                content_type TEXT NOT NULL,
                platform TEXT NOT NULL,
                lookup_kind TEXT NOT NULL,
                lookup_value TEXT NOT NULL,
                release_key TEXT NOT NULL,
                provider TEXT NOT NULL,
                title TEXT NOT NULL,
                year TEXT,
                publisher TEXT,
                description TEXT,
                artwork_url TEXT,
                artwork_source_url TEXT,
                artwork_provider TEXT,
                artwork_path TEXT,
                retrieved_at TEXT NOT NULL,
                PRIMARY KEY(system_id, content_type, platform, lookup_kind, lookup_value, provider)
            );
            CREATE INDEX IF NOT EXISTS ix_metadata_release_key
                ON metadata_cache(system_id, content_type, platform, release_key);

            CREATE TABLE IF NOT EXISTS artwork_cache (
                system_id TEXT NOT NULL,
                content_type TEXT NOT NULL,
                platform TEXT NOT NULL,
                lookup_kind TEXT NOT NULL,
                lookup_value TEXT NOT NULL,
                provider TEXT NOT NULL,
                source_url TEXT NOT NULL,
                original_path TEXT NOT NULL,
                processed_path TEXT NOT NULL,
                artwork_sha256 TEXT,
                retrieved_at TEXT NOT NULL,
                PRIMARY KEY(system_id, content_type, platform, lookup_kind, lookup_value)
            );
            CREATE INDEX IF NOT EXISTS ix_artwork_sha256 ON artwork_cache(artwork_sha256);
            """;
        Add(command, "$applied_at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            $"Data Source={_databasePath};Mode=ReadWriteCreate;Cache=Shared;Pooling=False");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static MetadataRecord ReadMetadata(SqliteDataReader reader)
    {
        var retrievedAt = DateTimeOffset.TryParse(reader.GetString(10), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        return new MetadataRecord(
            reader.GetString(0), reader.GetString(2), NullableString(reader, 3),
            NullableString(reader, 4), NullableString(reader, 5), reader.GetString(1), retrievedAt)
        {
            ArtworkUrl = NullableString(reader, 6),
            ArtworkSourceUrl = NullableString(reader, 7),
            ArtworkProvider = NullableString(reader, 8),
            ArtworkPath = NullableString(reader, 9)
        };
    }

    private static string? ReadArtworkPath(
        SqliteConnection connection, CatalogNamespace ns, LookupKey key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT original_path, processed_path
            FROM artwork_cache
            WHERE system_id = $system AND content_type = $content AND platform = $platform
              AND lookup_kind = $kind AND lookup_value = $value
            LIMIT 1;
            """;
        AddNamespace(command, ns, key);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var original = reader.IsDBNull(0) ? null : reader.GetString(0);
        if (!string.IsNullOrWhiteSpace(original) && File.Exists(original)) return original;
        var processed = reader.IsDBNull(1) ? null : reader.GetString(1);
        return !string.IsNullOrWhiteSpace(processed) && File.Exists(processed) ? processed : null;
    }

    private static IEnumerable<LookupKey> LookupKeys(ReleaseGroup group)
    {
        var hashes = group.Records.Select(record => record.SourceSha256)
            .Append(group.SourceSha256)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash!.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal);
        foreach (var hash in hashes) yield return new LookupKey("sha256", hash);
        if (!string.IsNullOrWhiteSpace(group.ReleaseKey))
            yield return new LookupKey("release-key", group.ReleaseKey);
    }

    private static bool HasSourceHash(ReleaseGroup group) =>
        group.SourceSha256 is { Length: > 0 } ||
        group.Records.Any(record => record.SourceSha256 is { Length: > 0 });

    private static string? NullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AddNamespace(SqliteCommand command, CatalogNamespace ns, LookupKey key)
    {
        Add(command, "$system", ns.SystemId);
        Add(command, "$content", ns.ContentType);
        Add(command, "$platform", ns.Platform);
        Add(command, "$kind", key.Kind);
        Add(command, "$value", key.Value);
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? FileSha256(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private sealed record LookupKey(string Kind, string Value);
}

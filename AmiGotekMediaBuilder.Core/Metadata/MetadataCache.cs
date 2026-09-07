using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AmiGotekMediaBuilder.Core.Metadata;

public sealed class MetadataCache(string cacheDirectory)
{
    private readonly string _directory = Path.GetFullPath(cacheDirectory);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public MetadataRecord? Read(string releaseKey)
    {
        var path = CachePath(releaseKey);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MetadataRecord>(File.ReadAllText(path), Options); }
        catch (JsonException) { return null; }
    }

    public void Write(MetadataRecord record)
    {
        Directory.CreateDirectory(_directory);
        var path = CachePath(record.ReleaseKey);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, Options), Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
    }

    private string CachePath(string releaseKey)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(releaseKey))).ToLowerInvariant();
        return Path.Combine(_directory, $"metadata-{digest}.json");
    }
}

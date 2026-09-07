using System.Text.Json;

namespace AmiGotekMediaBuilder.Demoscene;

/// <summary>Persistent cache for demoscene records from all configured catalogs.</summary>
public sealed class DemosceneMetadataStore(string directory)
{
    private readonly string _directory = Path.GetFullPath(directory);

    public async Task WriteAsync(IEnumerable<DemosceneProduction> productions, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        foreach (var production in productions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = string.Equals(production.Catalog, DemosceneCatalogs.Pouet, StringComparison.OrdinalIgnoreCase)
                ? production.PouetId
                : $"{SafePart(production.Catalog)}-{SafePart(production.PouetId)}";
            var path = Path.Combine(_directory, key + ".json");
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(production, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(temp, json, cancellationToken);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    public IReadOnlyList<DemosceneProduction> ReadAll()
    {
        if (!Directory.Exists(_directory)) return [];
        var records = new List<DemosceneProduction>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var record = JsonSerializer.Deserialize<DemosceneProduction>(File.ReadAllText(path));
                if (record is not null) records.Add(record);
            }
            catch (JsonException) { }
            catch (IOException) { }
        }
        return records;
    }

    private static string SafePart(string value)
    {
        var chars = value.Trim().Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}

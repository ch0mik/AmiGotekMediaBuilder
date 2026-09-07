namespace AmiGotekMediaBuilder.Core.Configuration;

/// <summary>Loads the top-level path keys used by the portable configuration.</summary>
public static class PathConfigLoader
{
    private static readonly string[] Keys =
    [
        "library_root", "original_dir", "staging_dir", "output_dir",
        "quarantine_dir", "approvals_dir", "reports_dir", "logs_dir", "cache_dir",
        "demoscene_dir", "gamebase_db"
    ];

    public static PathConfig Load(
        string? configFile = null,
        IReadOnlyDictionary<string, string?>? commandLine = null,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(configFile))
            foreach (var pair in ReadToml(configFile))
                values[pair.Key] = pair.Value;

        foreach (var key in Keys)
        {
            var envName = "AMIGA_ADF_" + key.ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(environment(envName)))
                values[key] = environment(envName);
        }

        if (commandLine is not null)
            foreach (var pair in commandLine)
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    values[pair.Key.Replace('-', '_')] = pair.Value;

        if (!values.TryGetValue("library_root", out var root) || string.IsNullOrWhiteSpace(root))
            throw new PathConfigException("No library configuration found. Provide library_root or --library-root.");

        return PathConfig.Create(
            root,
            Get(values, "original_dir"), Get(values, "staging_dir"),
            Get(values, "output_dir"), Get(values, "quarantine_dir"),
            Get(values, "approvals_dir"), Get(values, "reports_dir"),
            Get(values, "logs_dir"), Get(values, "cache_dir"),
            Get(values, "demoscene_dir"), Get(values, "gamebase_db"));
    }

    public static void Write(string path, PathConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var lines = new List<string>
        {
            $"library_root = \"{Escape(config.LibraryRoot)}\"",
            $"original_dir = \"{Escape(config.OriginalDirectory)}\"",
            $"staging_dir = \"{Escape(config.StagingDirectory)}\"",
            $"output_dir = \"{Escape(config.OutputDirectory)}\"",
            $"quarantine_dir = \"{Escape(config.QuarantineDirectory)}\"",
            $"approvals_dir = \"{Escape(config.ApprovalsDirectory)}\"",
            $"reports_dir = \"{Escape(config.ReportsDirectory)}\"",
            $"logs_dir = \"{Escape(config.LogsDirectory)}\"",
            $"cache_dir = \"{Escape(config.CacheDirectory)}\"",
            $"demoscene_dir = \"{Escape(config.DemosceneDirectory)}\""
        };
        if (!string.IsNullOrWhiteSpace(config.GameBaseDatabasePath))
            lines.Add($"gamebase_db = \"{Escape(config.GameBaseDatabasePath)}\"");
        lines.Add("");
        var text = string.Join(Environment.NewLine, lines);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, fullPath, overwrite: true);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? Get(Dictionary<string, string?> values, string key) =>
        values.GetValueOrDefault(key);

    private static IEnumerable<KeyValuePair<string, string?>> ReadToml(string path)
    {
        if (!File.Exists(path)) throw new PathConfigException($"config file not found: {path}");
        var section = "";
        foreach (var source in File.ReadLines(path))
        {
            var line = source.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                section = line.Trim('[', ']').Trim();
                continue;
            }
            if (section.Length > 0) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            if (!Keys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            var value = line[(separator + 1)..].Trim();
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0) value = value[..comment].TrimEnd();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
            yield return new(key, value);
        }
    }
}

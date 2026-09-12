using System.Text.Json;

namespace AmiGotekMediaBuilder.Demoscene.Gui;

/// <summary>Stores only local, non-sensitive folder preferences for the demoscene GUI.</summary>
internal sealed class DemosceneGuiSettings
{
    public string LibraryDirectory { get; set; } = string.Empty;
    public string ExportDirectory { get; set; } = string.Empty;
}

internal static class DemosceneGuiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AmiGotekMediaBuilder", "demoscene-gui-settings.json");

    public static DemosceneGuiSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new DemosceneGuiSettings();
            return JsonSerializer.Deserialize<DemosceneGuiSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new DemosceneGuiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new DemosceneGuiSettings();
        }
    }

    public static void Save(DemosceneGuiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Folder preferences must never block the GUI workflow.
        }
    }
}

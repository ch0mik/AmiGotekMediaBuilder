using System.Text.Json;

namespace AmiGotekMediaBuilder.Gui;

/// <summary>
/// Stores non-sensitive GUI path preferences. Provider credentials are never
/// read or written here.
/// </summary>
internal sealed class GuiSettings
{
    public string SourceDirectory { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string DestinationDirectory { get; set; } = string.Empty;
}

internal static class GuiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AmiGotekMediaBuilder", "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new GuiSettings();
            return JsonSerializer.Deserialize<GuiSettings>(
                File.ReadAllText(SettingsPath), JsonOptions) ?? new GuiSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unavailable preference file must not prevent the
            // application from starting.
            return new GuiSettings();
        }
    }

    public static void Save(GuiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are best effort; a read-only profile should not
            // block scanning, building or exporting.
        }
    }
}

using System.Text.Json;

namespace XPilot.Desktop;

/// <summary>User preferences, stored in %APPDATA%\XPilot\settings.json.</summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ControlPreset { get; set; } = "Modern";
    /// <summary>Optional per-action key overrides, e.g. { "Fire": ["J", "Space"] }.</summary>
    public Dictionary<string, string[]>? CustomBindings { get; set; }
    public bool Fullscreen { get; set; }
    public float Volume { get; set; } = 0.7f;
    public string PlayerName { get; set; } = "Player";
    public string Mode { get; set; } = "Dogfight";
    public string? LastDogfightMap { get; set; }
    public string? LastRaceMap { get; set; }
    public int Bots { get; set; } = 3;
    public string Difficulty { get; set; } = "Normal";

    public static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XPilot", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read settings: {ex.Message}");
        }
        var settings = new Settings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}

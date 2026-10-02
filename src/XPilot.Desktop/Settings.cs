using System.Text.Json;
using System.Text.Json.Serialization;

namespace XPilot.Desktop;

/// <summary>User preferences, stored in %APPDATA%\XPilot\settings.json.</summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string ControlPreset { get; set; } = "Modern";
    /// <summary>Optional per-action key overrides, e.g. { "Fire": ["J", "Space"] }.</summary>
    public Dictionary<string, string[]>? CustomBindings { get; set; }
    public bool Fullscreen { get; set; }
    public bool VSync { get; set; } = true;
    public bool Antialiasing { get; set; } = true;
    /// <summary>0 turns screen shake off, 1 is full strength.</summary>
    public float ScreenShake { get; set; } = 1f;
    /// <summary>Low, Normal or High.</summary>
    public string Particles { get; set; } = "Normal";
    public bool ShowFps { get; set; }
    public bool ShowShipNames { get; set; } = true;
    /// <summary>Sound effects volume (named before there was music, so older settings files keep their level).</summary>
    public float Volume { get; set; } = 0.7f;
    /// <summary>Music volume, independent of the effects; 0 turns the music off.</summary>
    public float MusicVolume { get; set; } = 0.5f;
    public bool MuteInBackground { get; set; } = true;
    public string PlayerName { get; set; } = "Player";
    public string Mode { get; set; } = "Dogfight";
    public string? LastDogfightMap { get; set; }
    public string? LastRaceMap { get; set; }
    public string? LastBallMap { get; set; }
    public int Bots { get; set; } = 3;
    public string Difficulty { get; set; } = "Normal";

    // Match rules for local and hosted games.
    /// <summary>Dogfight kills to win; 0 means no limit.</summary>
    public int ScoreLimit { get; set; } = 10;
    /// <summary>Ball captures to win; 0 means no limit.</summary>
    public int CaptureLimit { get; set; } = 3;
    /// <summary>Minutes per match: -1 uses the mode's default, 0 means no limit.</summary>
    public int TimeLimitMinutes { get; set; } = -1;
    /// <summary>Race laps; 0 uses the map's own lap count.</summary>
    public int Laps { get; set; }

    [JsonIgnore]
    public float? TimeLimitSeconds => TimeLimitMinutes < 0 ? null : TimeLimitMinutes * 60f;
    [JsonIgnore]
    public int? LapsOrDefault => Laps > 0 ? Laps : null;
    [JsonIgnore]
    public float ParticleDensity => Particles switch { "Low" => 0.4f, "High" => 1.6f, _ => 1f };
    /// <summary>The last address typed on the join screen.</summary>
    public string LastAddress { get; set; } = "";
    /// <summary>UDP port for hosting a game.</summary>
    public int HostPort { get; set; } = Net.Protocol.DefaultPort;
    public const string DefaultMasterServer = "xpilot.hjemmelaga.online";

    /// <summary>
    /// "host[:port]" of the master server for the internet game list. Null (also what older settings files
    /// have) means <see cref="DefaultMasterServer"/>; an empty string means LAN only.
    /// </summary>
    public string? MasterServer { get; set; }

    /// <summary>Announce games hosted from the menu on the internet list. Off by default: it shows the host's address.</summary>
    public bool ListHostedGames { get; set; }

    [JsonIgnore]
    public string? EffectiveMasterServer => MasterServer switch
    {
        null => DefaultMasterServer,
        var m when string.IsNullOrWhiteSpace(m) => null,
        var m => m.Trim(),
    };

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

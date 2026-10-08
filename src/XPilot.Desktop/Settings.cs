using System.Text.Json;
using System.Text.Json.Serialization;
using XPilot.Core;
using XPilot.Core.Maps;

namespace XPilot.Desktop;

/// <summary>User preferences, stored in %APPDATA%\XPilot\settings.json.</summary>
public sealed class Settings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    /// <summary>
    /// 1: <see cref="MasterServer"/> null means the public default (before, it meant LAN only), and hosted games
    /// are only listed with <see cref="ListHostedGames"/>.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>The settings format the file was written in; files from before versioning read as 0.</summary>
    public int Version { get; set; }

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
    /// <summary>The last map played in each of the newer modes, by mode name.</summary>
    public Dictionary<string, string>? LastMaps { get; set; }
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
    /// <summary>Team dogfight kills to win; 0 means no limit.</summary>
    public int TeamScoreLimit { get; set; } = 20;
    /// <summary>Lives in last pilot standing.</summary>
    public int Lives { get; set; } = 3;
    /// <summary>King of the hill seconds to win; 0 means no limit.</summary>
    public int HillScoreLimit { get; set; } = 60;
    /// <summary>Physics and random events for local and hosted games.</summary>
    public RuleOptions Rules { get; set; } = new();

    public string? LastMap(GameModeKind mode) => mode switch
    {
        GameModeKind.Dogfight => LastDogfightMap,
        GameModeKind.Race => LastRaceMap,
        GameModeKind.Ball => LastBallMap,
        _ => LastMaps?.GetValueOrDefault(mode.ToString()),
    };

    public void SetLastMap(GameModeKind mode, string name)
    {
        switch (mode)
        {
            case GameModeKind.Dogfight: LastDogfightMap = name; break;
            case GameModeKind.Race: LastRaceMap = name; break;
            case GameModeKind.Ball: LastBallMap = name; break;
            default: (LastMaps ??= [])[mode.ToString()] = name; break;
        }
    }

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
                var loaded = Parse(File.ReadAllText(FilePath));
                if (loaded.Migrated) loaded.Save();
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read settings: {ex.Message}");
        }
        var settings = new Settings { Version = CurrentVersion };
        settings.Save();
        return settings;
    }

    [JsonIgnore]
    private bool Migrated { get; set; }

    /// <summary>Reads settings JSON, bringing older files up to <see cref="CurrentVersion"/> without changing what they meant.</summary>
    public static Settings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
        if (settings.Version < 1)
        {
            // Before the public default, no master server meant LAN only, and a master server you set also
            // listed the games you hosted. Keep both.
            if (string.IsNullOrWhiteSpace(settings.MasterServer)) settings.MasterServer = "";
            else settings.ListHostedGames = true;
        }
        settings.Migrated = settings.Version < CurrentVersion;
        settings.Version = CurrentVersion;
        return settings;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, ToJson());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}

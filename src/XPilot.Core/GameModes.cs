using XPilot.Core.Maps;

namespace XPilot.Core;

/// <summary>Names of the game modes, and which maps each one can be played on.</summary>
public static class GameModes
{
    /// <summary>Every mode, in the order the menu cycles through them.</summary>
    public static readonly GameModeKind[] All =
    [
        GameModeKind.Dogfight, GameModeKind.TeamDogfight, GameModeKind.Elimination, GameModeKind.KingOfTheHill,
        GameModeKind.Race, GameModeKind.Ball,
    ];

    /// <summary>
    /// Race and ball modes need maps made for them. The other modes are about shooting, so they play on dogfight
    /// maps; the team modes and king of the hill also play on ball maps, which have room for two teams.
    /// </summary>
    public static bool Supports(GameModeKind mode, Map map) => mode switch
    {
        GameModeKind.Race or GameModeKind.Ball or GameModeKind.Dogfight => map.Mode == mode,
        _ => map.Mode is GameModeKind.Dogfight or GameModeKind.Ball && map.Bases.Count >= 2,
    };

    public static bool IsTeamMode(GameModeKind mode) => mode is GameModeKind.Ball or GameModeKind.TeamDogfight;

    public static string Name(GameModeKind mode) => mode switch
    {
        GameModeKind.Race => "Race",
        GameModeKind.Ball => "Capture the Ball",
        GameModeKind.TeamDogfight => "Team Dogfight",
        GameModeKind.Elimination => "Last Pilot Standing",
        GameModeKind.KingOfTheHill => "King of the Hill",
        _ => "Dogfight",
    };

    /// <summary>Parses a mode name as typed on a command line: the enum name or a short alias.</summary>
    public static GameModeKind? Parse(string text) => text.Trim().ToLowerInvariant() switch
    {
        "dogfight" or "ffa" => GameModeKind.Dogfight,
        "race" => GameModeKind.Race,
        "ball" or "ctb" => GameModeKind.Ball,
        "team" or "teams" or "tdm" or "teamdogfight" => GameModeKind.TeamDogfight,
        "elimination" or "lms" or "lastpilot" => GameModeKind.Elimination,
        "koth" or "hill" or "kingofthehill" => GameModeKind.KingOfTheHill,
        _ => null,
    };
}

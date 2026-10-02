using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>Mode-specific logic: spawning, scoring, and when the match ends.</summary>
public interface IGameRules
{
    GameModeKind Mode { get; }
    bool WeaponsEnabled { get; }
    /// <summary>When true ships can only turn (e.g. race countdown).</summary>
    bool ControlsLocked { get; }
    bool IsOver { get; }
    float RespawnDelay { get; }

    /// <summary>Places all ships for the start of the match.</summary>
    void Initialize(World world);
    /// <summary>Called once per tick after physics.</summary>
    void Update(World world);
    void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause);
    void Respawn(World world, Ship ship);
    /// <summary>Ships ordered from first to last place.</summary>
    IReadOnlyList<Ship> GetStandings(World world);

    /// <summary>Called when a ship joins a match in progress. It is dead until it respawns on the next tick.</summary>
    void OnShipJoined(World world, Ship ship)
    {
    }

    /// <summary>Writes the mode's own state (scores, timers, match over) so a network client can mirror it.</summary>
    void WriteState(BinaryWriter writer);
    void ReadState(BinaryReader reader);
}

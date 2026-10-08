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
    /// <summary>Ships play in Red and Blue teams that can't hurt each other.</summary>
    bool IsTeamGame => false;

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

    /// <summary>A ship that is out of the match for good and won't respawn.</summary>
    bool IsEliminated(Ship ship) => false;

    /// <summary>Writes the mode's own state (scores, timers, match over) so a network client can mirror it.</summary>
    void WriteState(BinaryWriter writer);
    void ReadState(BinaryReader reader);
}

/// <summary>Rules where Red and Blue score as teams.</summary>
public interface ITeamRules : IGameRules
{
    int TeamScore(int team);
    /// <summary>Points a team needs to win; 0 means no limit.</summary>
    int TeamScoreLimit { get; }
    /// <summary>The winning team once the match is over, or <see cref="Teams.None"/> for a draw.</summary>
    int WinningTeam { get; }
    float TimeLimit { get; }
    float TimeRemaining(World world);
}

/// <summary>Helpers shared by the modes.</summary>
public static class RuleHelpers
{
    /// <summary>The base from <paramref name="bases"/> farthest from living ships that count as threats.</summary>
    public static System.Numerics.Vector2 SafestBase(World world, Ship ship, IReadOnlyList<System.Numerics.Vector2> bases, Func<Ship, bool> isThreat)
    {
        var map = world.Map;
        var best = bases[0];
        float bestScore = float.MinValue;
        foreach (var b in bases)
        {
            float nearest = 2000f;
            foreach (var other in world.Ships)
            {
                if (other == ship || !other.Alive || !isThreat(other)) continue;
                nearest = MathF.Min(nearest, map.Distance(b, other.Position));
            }
            float score = nearest + (float)world.Rng.NextDouble() * 50f;
            if (score > bestScore)
            {
                bestScore = score;
                best = b;
            }
        }
        return best;
    }

    /// <summary>Spreads ships over random bases for the start of a free-for-all match.</summary>
    public static void SpawnAtRandomBases(World world)
    {
        var bases = world.Map.Bases;
        var order = Enumerable.Range(0, bases.Count).OrderBy(_ => world.Rng.Next()).ToList();
        for (int i = 0; i < world.Ships.Count; i++)
        {
            var pos = bases[order[i % order.Count]];
            world.SpawnShip(world.Ships[i], pos, world.ChooseSpawnHeading(pos));
        }
    }
}

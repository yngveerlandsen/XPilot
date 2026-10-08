using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>
/// Last pilot standing: everyone starts with the same number of lives, and a pilot who loses them all is out.
/// The match ends when one pilot is left, or at the time limit with the most lives winning.
/// </summary>
/// <remarks>
/// Lives are kept in <see cref="Ship.Score"/>, and the place a pilot finished in once they are out in
/// <see cref="Ship.Place"/>, so network snapshots carry both without anything new.
/// </remarks>
public sealed class EliminationRules(int lives = 3, float timeLimit = 600f) : IGameRules
{
    private int _eliminatedCount;

    public int Lives { get; } = Math.Max(1, lives);
    /// <summary>Seconds; 0 means no limit.</summary>
    public float TimeLimit { get; } = timeLimit;

    public GameModeKind Mode => GameModeKind.Elimination;
    public bool WeaponsEnabled => true;
    public bool ControlsLocked => false;
    public bool IsOver { get; private set; }
    public float RespawnDelay => 3f;

    public float TimeRemaining(World world) => TimeLimit > 0 ? MathF.Max(0f, TimeLimit - world.Time) : float.PositiveInfinity;

    public bool IsEliminated(Ship ship) => ship.Score <= 0;

    /// <summary>Pilots still in the match.</summary>
    public int Remaining(World world) => world.Ships.Count(s => !IsEliminated(s));

    public void Initialize(World world)
    {
        foreach (var ship in world.Ships)
        {
            ship.Score = Lives;
            ship.Place = 0;
        }
        RuleHelpers.SpawnAtRandomBases(world);
    }

    /// <summary>A latecomer gets as many lives as the pilot doing worst, so joining never beats staying alive.</summary>
    public void OnShipJoined(World world, Ship ship)
    {
        var alive = world.Ships.Where(s => s != ship && !IsEliminated(s)).ToList();
        ship.Score = alive.Count > 0 ? alive.Min(s => s.Score) : Lives;
        ship.Place = 0;
    }

    public void Update(World world)
    {
        if (IsOver) return;
        bool timeUp = TimeLimit > 0 && world.Time >= TimeLimit;
        if (timeUp || (world.Ships.Count >= 2 && Remaining(world) <= 1)) End(world);
    }

    public void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause)
    {
        if (killer != null && killer != victim) killer.Kills++;
        if (IsEliminated(victim)) return;
        victim.Score--;
        if (!IsEliminated(victim)) return;
        _eliminatedCount++;
        // Out first finishes last.
        victim.Place = world.Ships.Count - _eliminatedCount + 1;
        world.Emit(new GameEvent(GameEventType.ShipEliminated, victim.Id, killer?.Id ?? -1, victim.Position, Value: Remaining(world)));
    }

    public void Respawn(World world, Ship ship)
    {
        if (IsEliminated(ship)) return;
        var pos = RuleHelpers.SafestBase(world, ship, world.Map.Bases, _ => true);
        world.SpawnShip(ship, pos, world.ChooseSpawnHeading(pos));
    }

    public IReadOnlyList<Ship> GetStandings(World world) => world.Ships
        .OrderByDescending(s => s.Score)
        .ThenBy(s => s.Place)
        .ThenByDescending(s => s.Kills)
        .ThenBy(s => s.Deaths)
        .ThenBy(s => s.Id)
        .ToList();

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(IsOver);
        writer.Write((short)_eliminatedCount);
    }

    public void ReadState(BinaryReader reader)
    {
        IsOver = reader.ReadBoolean();
        _eliminatedCount = reader.ReadInt16();
    }

    private void End(World world)
    {
        IsOver = true;
        world.Emit(new GameEvent(GameEventType.MatchOver));
    }
}

using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>Free-for-all: +1 per kill, -1 for crashing or shooting yourself.</summary>
public sealed class DogfightRules(int scoreLimit = 10, float timeLimit = 300f) : IGameRules
{
    public int ScoreLimit { get; } = scoreLimit;
    /// <summary>Seconds; 0 means no limit.</summary>
    public float TimeLimit { get; } = timeLimit;

    public GameModeKind Mode => GameModeKind.Dogfight;
    public bool WeaponsEnabled => true;
    public bool ControlsLocked => false;
    public bool IsOver { get; private set; }
    public float RespawnDelay => 2f;

    public float TimeRemaining(World world) => TimeLimit > 0 ? MathF.Max(0f, TimeLimit - world.Time) : float.PositiveInfinity;

    public void Initialize(World world)
    {
        var bases = world.Map.Bases;
        var order = Enumerable.Range(0, bases.Count).OrderBy(_ => world.Rng.Next()).ToList();
        for (int i = 0; i < world.Ships.Count; i++)
        {
            var pos = bases[order[i % order.Count]];
            world.SpawnShip(world.Ships[i], pos, world.ChooseSpawnHeading(pos));
        }
    }

    public void Update(World world)
    {
        if (!IsOver && TimeLimit > 0 && world.Time >= TimeLimit) End(world);
    }

    public void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause)
    {
        if (killer != null && killer != victim)
        {
            killer.Kills++;
            killer.Score++;
            if (!IsOver && ScoreLimit > 0 && killer.Score >= ScoreLimit) End(world);
        }
        else
        {
            victim.Score--;
        }
    }

    /// <summary>Respawns at the base farthest from the other living ships.</summary>
    public void Respawn(World world, Ship ship)
    {
        var map = world.Map;
        var best = map.Bases[0];
        float bestScore = float.MinValue;
        foreach (var b in map.Bases)
        {
            float nearest = 1e9f;
            foreach (var other in world.Ships)
            {
                if (other == ship || !other.Alive) continue;
                nearest = MathF.Min(nearest, map.Distance(b, other.Position));
            }
            float score = MathF.Min(nearest, 2000f) + (float)world.Rng.NextDouble() * 50f;
            if (score > bestScore)
            {
                bestScore = score;
                best = b;
            }
        }
        world.SpawnShip(ship, best, world.ChooseSpawnHeading(best));
    }

    public IReadOnlyList<Ship> GetStandings(World world) => world.Ships
        .OrderByDescending(s => s.Score)
        .ThenByDescending(s => s.Kills)
        .ThenBy(s => s.Deaths)
        .ThenBy(s => s.Id)
        .ToList();

    private void End(World world)
    {
        IsOver = true;
        world.Emit(new GameEvent(GameEventType.MatchOver));
    }
}

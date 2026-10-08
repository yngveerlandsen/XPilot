using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>
/// Hold the hill: a pilot alone inside the zone scores a point a second, and nobody scores while it is
/// contested. The hill moves somewhere else every <see cref="MoveInterval"/> seconds.
/// </summary>
public sealed class KingOfTheHillRules(int scoreLimit = 60, float timeLimit = 300f) : IGameRules
{
    public const float HillRadius = 140f;
    public const float MoveInterval = 40f;
    /// <summary><see cref="Holder"/> when two or more pilots are in the zone.</summary>
    public const int Contested = -2;

    private readonly List<Vector2> _spots = [];
    private readonly Dictionary<int, float> _held = [];

    /// <summary>Points (seconds on the hill) needed to win; 0 means no limit.</summary>
    public int ScoreLimit { get; } = scoreLimit;
    /// <summary>Seconds; 0 means no limit.</summary>
    public float TimeLimit { get; } = timeLimit;
    public Vector2 Hill { get; private set; }
    /// <summary>When the hill moves next, in match seconds.</summary>
    public float NextMove { get; private set; }
    /// <summary>The ship alone on the hill, -1 when it is empty, or <see cref="Contested"/>.</summary>
    public int Holder { get; private set; } = -1;

    public GameModeKind Mode => GameModeKind.KingOfTheHill;
    public bool WeaponsEnabled => true;
    public bool ControlsLocked => false;
    public bool IsOver { get; private set; }
    public float RespawnDelay => 3f;

    public float TimeRemaining(World world) => TimeLimit > 0 ? MathF.Max(0f, TimeLimit - world.Time) : float.PositiveInfinity;

    public bool OnHill(World world, Ship ship) => ship.Alive && world.Map.Distance(ship.Position, Hill) < HillRadius;

    public void Initialize(World world)
    {
        FindSpots(world);
        Hill = PickSpot(world, null);
        NextMove = MoveInterval;
        RuleHelpers.SpawnAtRandomBases(world);
    }

    public void Update(World world)
    {
        if (IsOver) return;
        if (world.Time >= NextMove)
        {
            Hill = PickSpot(world, Hill);
            NextMove = world.Time + MoveInterval;
            world.Emit(new GameEvent(GameEventType.HillMoved, Position: Hill));
        }

        Ship? holder = null;
        int count = 0;
        foreach (var s in world.Ships)
        {
            if (!OnHill(world, s)) continue;
            holder = s;
            count++;
        }
        int previous = Holder;
        Holder = count == 0 ? -1 : count > 1 ? Contested : holder!.Id;
        if (Holder != previous && holder != null && count == 1) world.Emit(new GameEvent(GameEventType.HillTaken, holder.Id, Position: Hill));

        if (count == 1)
        {
            float held = _held.GetValueOrDefault(holder!.Id) + GameConfig.Dt;
            _held[holder.Id] = held;
            holder.Score = (int)held;
            if (ScoreLimit > 0 && holder.Score >= ScoreLimit)
            {
                End(world);
                return;
            }
        }
        if (TimeLimit > 0 && world.Time >= TimeLimit) End(world);
    }

    public void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause)
    {
        if (killer != null && killer != victim) killer.Kills++;
    }

    public void Respawn(World world, Ship ship)
    {
        var pos = RuleHelpers.SafestBase(world, ship, world.Map.Bases, _ => true);
        world.SpawnShip(ship, pos, world.ChooseSpawnHeading(pos));
    }

    public IReadOnlyList<Ship> GetStandings(World world) => world.Ships
        .OrderByDescending(s => s.Score)
        .ThenByDescending(s => s.Kills)
        .ThenBy(s => s.Deaths)
        .ThenBy(s => s.Id)
        .ToList();

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(IsOver);
        writer.Write(Hill.X);
        writer.Write(Hill.Y);
        writer.Write(NextMove);
        writer.Write(Holder);
    }

    public void ReadState(BinaryReader reader)
    {
        IsOver = reader.ReadBoolean();
        Hill = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        NextMove = reader.ReadSingle();
        Holder = reader.ReadInt32();
    }

    /// <summary>
    /// Open places for the hill, away from the bases. Only spots in sight of a base, or of a spot already found,
    /// count, so the hill never ends up in a pocket nobody can fly into.
    /// </summary>
    private void FindSpots(World world)
    {
        var map = world.Map;
        _spots.Clear();
        var reachable = new List<Vector2>(map.Bases);
        float clearance = HillRadius * 0.45f;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < 300; i++)
            {
                var p = new Vector2((float)world.Rng.NextDouble() * map.PixelWidth, (float)world.Rng.NextDouble() * map.PixelHeight);
                if (map.CircleOverlapsWall(p, clearance)) continue;
                if (map.Bases.Any(b => map.Distance(b, p) < HillRadius + 60f)) continue;
                if (!reachable.Any(r => map.Distance(r, p) < 900f && map.SegmentClear(r, p, world.Config.ShipRadius))) continue;
                reachable.Add(p);
                _spots.Add(p);
            }
        }
        if (_spots.Count == 0) _spots.AddRange(map.FuelStations.Count > 0 ? map.FuelStations : map.Bases);
    }

    /// <summary>A random spot, preferring ones well away from the current hill.</summary>
    private Vector2 PickSpot(World world, Vector2? current)
    {
        if (_spots.Count == 0) return current ?? world.Map.Bases[0];
        if (current is not { } from) return _spots[world.Rng.Next(_spots.Count)];
        var far = _spots.OrderByDescending(s => world.Map.Distance(s, from)).Take(Math.Max(1, _spots.Count / 2)).ToList();
        return far[world.Rng.Next(far.Count)];
    }

    private void End(World world)
    {
        IsOver = true;
        world.Emit(new GameEvent(GameEventType.MatchOver));
    }
}

using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>
/// Red vs Blue. Tow the enemy ball into your own treasure to score. A dropped ball returns home when a
/// teammate touches it, or by itself after <see cref="ReturnTime"/>. Teammates cannot hurt each other.
/// </summary>
public sealed class BallRules(int captureLimit = 3, float timeLimit = 600f) : IGameRules
{
    /// <summary>How close the ball must come to the carrier's treasure to score.</summary>
    public const float CaptureRadius = 56f;
    public const float ReturnTime = 20f;
    public const int CapturePoints = 3;

    private readonly int[] _teamScores = new int[2];

    public int CaptureLimit { get; } = captureLimit;
    /// <summary>Seconds; 0 means no limit.</summary>
    public float TimeLimit { get; } = timeLimit;
    /// <summary>The winning team once the match is over, or <see cref="Teams.None"/> for a draw.</summary>
    public int WinningTeam { get; private set; } = Teams.None;

    public GameModeKind Mode => GameModeKind.Ball;
    public bool WeaponsEnabled => true;
    public bool ControlsLocked => false;
    public bool IsOver { get; private set; }
    public float RespawnDelay => 4.5f;

    public int TeamScore(int team) => team is Teams.Red or Teams.Blue ? _teamScores[team] : 0;

    public float TimeRemaining(World world) => TimeLimit > 0 ? MathF.Max(0f, TimeLimit - world.Time) : float.PositiveInfinity;

    public void Initialize(World world)
    {
        var map = world.Map;
        world.Balls.Clear();
        foreach (var t in map.Treasures) world.Balls.Add(new Ball(t.Team, t.Position));

        var used = new Dictionary<int, int>();
        foreach (var ship in world.Ships)
        {
            var bases = map.TeamBases(ship.Team);
            int k = used.GetValueOrDefault(ship.Team);
            used[ship.Team] = k + 1;
            var pos = bases[k % bases.Count];
            world.SpawnShip(ship, pos, SpawnHeading(world, ship, pos));
        }
    }

    public void Update(World world)
    {
        var map = world.Map;
        foreach (var ball in world.Balls)
        {
            if (ball.State == BallState.Carried)
            {
                var carrier = world.GetShip(ball.CarrierId);
                if (carrier == null || carrier.Team == ball.Team || map.TreasureOf(carrier.Team) is not { } goal) continue;
                if (map.Distance(ball.Position, goal) < CaptureRadius) Capture(world, carrier, ball);
            }
            else if (ball.State == BallState.Loose)
            {
                if (ball.LooseTime >= ReturnTime)
                {
                    Return(world, ball, null);
                    continue;
                }
                float touch = world.Config.ShipRadius + world.Config.BallRadius + 8f;
                foreach (var s in world.Ships)
                {
                    if (s.Alive && s.Team == ball.Team && map.Distance(s.Position, ball.Position) < touch)
                    {
                        Return(world, ball, s);
                        break;
                    }
                }
            }
        }

        if (!IsOver && TimeLimit > 0 && world.Time >= TimeLimit) End(world);
    }

    private void Capture(World world, Ship carrier, Ball ball)
    {
        _teamScores[carrier.Team]++;
        carrier.Score += CapturePoints;
        var at = ball.Position;
        ball.ResetHome();
        world.Emit(new GameEvent(GameEventType.BallCaptured, carrier.Id, Position: at, Value: ball.Team));
        if (!IsOver && CaptureLimit > 0 && _teamScores[carrier.Team] >= CaptureLimit) End(world);
    }

    private static void Return(World world, Ball ball, Ship? by)
    {
        ball.ResetHome();
        world.Emit(new GameEvent(GameEventType.BallReturned, by?.Id ?? -1, Position: ball.Home, Value: ball.Team));
    }

    public void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause)
    {
        if (killer != null && killer != victim)
        {
            killer.Kills++;
            killer.Score++;
        }
        else
        {
            victim.Score--;
        }
    }

    /// <summary>Respawns at the team base farthest from living enemies.</summary>
    public void Respawn(World world, Ship ship)
    {
        var map = world.Map;
        var bases = map.TeamBases(ship.Team);
        var best = bases[0];
        float bestScore = float.MinValue;
        foreach (var b in bases)
        {
            float nearest = 2000f;
            foreach (var other in world.Ships)
            {
                if (!other.Alive || other.Team == ship.Team) continue;
                nearest = MathF.Min(nearest, map.Distance(b, other.Position));
            }
            float score = nearest + (float)world.Rng.NextDouble() * 50f;
            if (score > bestScore)
            {
                bestScore = score;
                best = b;
            }
        }
        world.SpawnShip(ship, best, SpawnHeading(world, ship, best));
    }

    /// <summary>Face the enemy treasure if there is room to fly that way.</summary>
    private static float SpawnHeading(World world, Ship ship, System.Numerics.Vector2 pos)
    {
        var target = world.Map.TreasureOf(Teams.Opponent(ship.Team));
        return world.ChooseSpawnHeading(pos, target is { } t ? world.Map.Delta(pos, t) : null);
    }

    public IReadOnlyList<Ship> GetStandings(World world) => world.Ships
        .OrderByDescending(s => TeamScore(s.Team))
        .ThenBy(s => s.Team)
        .ThenByDescending(s => s.Score)
        .ThenByDescending(s => s.Kills)
        .ThenBy(s => s.Deaths)
        .ThenBy(s => s.Id)
        .ToList();

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(IsOver);
        writer.Write((sbyte)WinningTeam);
        writer.Write(_teamScores[Teams.Red]);
        writer.Write(_teamScores[Teams.Blue]);
    }

    public void ReadState(BinaryReader reader)
    {
        IsOver = reader.ReadBoolean();
        WinningTeam = reader.ReadSByte();
        _teamScores[Teams.Red] = reader.ReadInt32();
        _teamScores[Teams.Blue] = reader.ReadInt32();
    }

    private void End(World world)
    {
        IsOver = true;
        int red = _teamScores[Teams.Red], blue = _teamScores[Teams.Blue];
        WinningTeam = red > blue ? Teams.Red : blue > red ? Teams.Blue : Teams.None;
        world.Emit(new GameEvent(GameEventType.MatchOver, Value: WinningTeam));
    }
}

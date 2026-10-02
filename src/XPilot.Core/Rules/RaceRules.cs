using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Core.Rules;

/// <summary>
/// Pass checkpoints 1..N in order; passing the last one completes a lap. Weapons are off, ships
/// respawn at their last checkpoint, and the race ends when everyone finishes or a grace period
/// after the winner runs out.
/// </summary>
public sealed class RaceRules(int laps) : IGameRules
{
    public const float CountdownSeconds = 3f;

    private int _finishedCount;
    private float? _firstFinishTime;
    private int _lastCountdown = -1;

    public int Laps { get; } = Math.Max(1, laps);
    public float GraceAfterFirstFinish { get; init; } = 30f;

    public GameModeKind Mode => GameModeKind.Race;
    public bool WeaponsEnabled => false;
    public bool ControlsLocked { get; private set; } = true;
    public bool IsOver { get; private set; }
    public float RespawnDelay => 2.5f;

    /// <summary>Seconds left of the grace period after the first finisher, if it has started.</summary>
    public float? GraceRemaining(World world) =>
        _firstFinishTime is { } t ? MathF.Max(0f, GraceAfterFirstFinish - (world.Time - t)) : null;

    public void Initialize(World world)
    {
        var map = world.Map;
        for (int i = 0; i < world.Ships.Count; i++)
        {
            var ship = world.Ships[i];
            var pos = map.Bases[i % map.Bases.Count];
            world.SpawnShip(ship, pos, world.ChooseSpawnHeading(pos, map.Delta(pos, map.Checkpoints[0])));
            ship.NextCheckpoint = 0;
            ship.LastCheckpoint = -1;
            ship.Lap = 0;
            ship.LapStartTime = CountdownSeconds;
        }
        ControlsLocked = true;
        _lastCountdown = (int)CountdownSeconds;
        world.Emit(new GameEvent(GameEventType.CountdownTick, Value: _lastCountdown));
    }

    public void Update(World world)
    {
        float t = world.Time;
        if (ControlsLocked)
        {
            if (t >= CountdownSeconds)
            {
                ControlsLocked = false;
                world.Emit(new GameEvent(GameEventType.RaceStarted));
            }
            else
            {
                int remaining = (int)MathF.Ceiling(CountdownSeconds - t);
                if (remaining != _lastCountdown)
                {
                    _lastCountdown = remaining;
                    world.Emit(new GameEvent(GameEventType.CountdownTick, Value: remaining));
                }
            }
            return;
        }

        var map = world.Map;
        foreach (var s in world.Ships)
        {
            if (!s.Alive || s.Finished) continue;
            if (map.Distance(s.Position, map.Checkpoints[s.NextCheckpoint]) <= map.CheckpointRadius)
            {
                PassCheckpoint(world, s);
            }
        }

        if (!IsOver)
        {
            bool allFinished = world.Ships.All(s => s.Finished);
            bool graceOver = _firstFinishTime is { } first && t - first >= GraceAfterFirstFinish;
            if (allFinished || graceOver)
            {
                IsOver = true;
                world.Emit(new GameEvent(GameEventType.MatchOver));
            }
        }
    }

    private void PassCheckpoint(World world, Ship s)
    {
        int count = world.Map.Checkpoints.Count;
        int index = s.NextCheckpoint;
        s.LastCheckpoint = index;
        s.NextCheckpoint = (index + 1) % count;
        world.Emit(new GameEvent(GameEventType.CheckpointPassed, s.Id, Position: world.Map.Checkpoints[index], Value: index));
        if (index != count - 1) return;

        float t = world.Time;
        float lapTime = t - s.LapStartTime;
        s.LapTimes.Add(lapTime);
        s.LapStartTime = t;
        s.Lap++;
        world.Emit(new GameEvent(GameEventType.LapCompleted, s.Id, Value: lapTime));

        if (s.Lap >= Laps)
        {
            s.Finished = true;
            s.FinishTime = t - CountdownSeconds;
            s.Place = ++_finishedCount;
            _firstFinishTime ??= t;
            world.Emit(new GameEvent(GameEventType.ShipFinished, s.Id, Value: s.Place));
        }
    }

    public void OnShipDestroyed(World world, Ship victim, Ship? killer, DeathCause cause)
    {
    }

    public void OnShipJoined(World world, Ship ship)
    {
        ship.NextCheckpoint = 0;
        ship.LastCheckpoint = -1;
        ship.Lap = 0;
        ship.LapStartTime = MathF.Max(world.Time, CountdownSeconds);
    }

    public void WriteState(BinaryWriter writer)
    {
        writer.Write(IsOver);
        writer.Write(ControlsLocked);
        writer.Write(_finishedCount);
        writer.Write(_firstFinishTime ?? -1f);
        writer.Write(_lastCountdown);
    }

    public void ReadState(BinaryReader reader)
    {
        IsOver = reader.ReadBoolean();
        ControlsLocked = reader.ReadBoolean();
        _finishedCount = reader.ReadInt32();
        float first = reader.ReadSingle();
        _firstFinishTime = first >= 0f ? first : null;
        _lastCountdown = reader.ReadInt32();
    }

    public void Respawn(World world, Ship ship)
    {
        var map = world.Map;
        var pos = ship.LastCheckpoint >= 0 ? map.Checkpoints[ship.LastCheckpoint] : map.Bases[ship.Id % map.Bases.Count];
        var toNext = map.Delta(pos, map.Checkpoints[ship.NextCheckpoint]);
        world.SpawnShip(ship, pos, world.ChooseSpawnHeading(pos, toNext));
    }

    /// <summary>Number of checkpoints passed in total; higher is further ahead.</summary>
    public static int Progress(World world, Ship s) => s.Lap * world.Map.Checkpoints.Count + s.NextCheckpoint;

    public IReadOnlyList<Ship> GetStandings(World world)
    {
        var map = world.Map;
        return world.Ships
            .OrderBy(s => s.Finished ? 0 : 1)
            .ThenBy(s => s.Finished ? s.Place : 0)
            .ThenByDescending(s => Progress(world, s))
            .ThenBy(s => map.Distance(s.Position, map.Checkpoints[s.NextCheckpoint]))
            .ThenBy(s => s.Id)
            .ToList();
    }
}

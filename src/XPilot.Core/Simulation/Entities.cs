using System.Numerics;

namespace XPilot.Core.Simulation;

/// <summary>
/// Per-tick control state for one ship. Humans, bots and (later) network peers all produce this,
/// so the simulation never needs to know who is flying.
/// </summary>
public struct ShipInput
{
    /// <summary>-1 turns counter-clockwise (left), +1 clockwise (right).</summary>
    public float Turn;
    public bool Thrust;
    public bool Fire;
    public bool Shield;
}

public sealed class Ship(int id, string name, bool isBot, int colorIndex)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public bool IsBot { get; } = isBot;
    public int ColorIndex { get; } = colorIndex;

    public Vector2 Position;
    public Vector2 PrevPosition;
    public Vector2 Velocity;
    public float Heading;
    public float PrevHeading;

    public float Fuel;
    public bool Shield;
    public bool Thrusting;
    public bool Refueling;
    public bool Alive;
    public float RespawnTimer;
    public float FireCooldown;
    public float SpawnProtection;

    public int Kills;
    public int Deaths;
    public int Score;

    // Race progress
    public int NextCheckpoint;
    public int LastCheckpoint = -1;
    public int Lap;
    public float LapStartTime;
    public List<float> LapTimes { get; } = [];
    public bool Finished;
    public float FinishTime;
    public int Place;

    public float BestLap => LapTimes.Count > 0 ? LapTimes.Min() : float.NaN;
    public bool IsProtected => Shield || SpawnProtection > 0f;
}

public sealed class Bullet
{
    public Vector2 Position;
    public Vector2 PrevPosition;
    public Vector2 Velocity;
    public float Age;
    public int OwnerId;
    public bool Dead;
}

public enum GameEventType
{
    ShipSpawned,
    ShipFired,
    ShipDestroyed,
    WallBounce,
    ShieldHit,
    BulletHitWall,
    CheckpointPassed,
    LapCompleted,
    ShipFinished,
    CountdownTick,
    RaceStarted,
    MatchOver,
}

public enum DeathCause { None, Bullet, Wall, Collision }

/// <summary>Something that happened during a tick, for the UI, sound and kill feed.</summary>
public readonly record struct GameEvent(
    GameEventType Type,
    int ShipId = -1,
    int OtherId = -1,
    Vector2 Position = default,
    Vector2 Velocity = default,
    float Value = 0f,
    DeathCause Cause = DeathCause.None);

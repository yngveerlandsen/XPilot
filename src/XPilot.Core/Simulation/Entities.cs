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
    /// <summary>Pressing (not holding) attaches to a nearby enemy ball, or releases the towed one.</summary>
    public bool Grab;
}

public sealed class Ship(int id, string name, bool isBot, int colorIndex)
{
    public int Id { get; } = id;
    public string Name { get; set; } = name;
    /// <summary>Settable so a network client can fill in a ship it heard about before learning who flies it.</summary>
    public bool IsBot { get; set; } = isBot;
    public int ColorIndex { get; } = colorIndex;
    /// <summary><see cref="Maps.Teams.None"/> in free-for-all modes.</summary>
    public int Team = Maps.Teams.None;
    /// <summary>Grab state on the previous tick, so a held key only toggles once.</summary>
    public bool GrabHeld;

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
    /// <summary>Unique within a world, so a network client can track bullets it was told about.</summary>
    public int Id;
    public Vector2 Position;
    public Vector2 PrevPosition;
    public Vector2 Velocity;
    public float Age;
    public int OwnerId;
    public bool Dead;
}

public enum BallState { Home, Carried, Loose }

/// <summary>A team's ball. It rests in its treasure until an enemy tows it away on a rope.</summary>
public sealed class Ball(int team, Vector2 home)
{
    public int Team { get; } = team;
    public Vector2 Home { get; } = home;
    public Vector2 Position = home;
    public Vector2 PrevPosition = home;
    public Vector2 Velocity;
    public BallState State = BallState.Home;
    public int CarrierId = -1;
    /// <summary>Seconds since the ball was dropped.</summary>
    public float LooseTime;

    public void ResetHome()
    {
        State = BallState.Home;
        Position = PrevPosition = Home;
        Velocity = Vector2.Zero;
        CarrierId = -1;
        LooseTime = 0f;
    }
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
    /// <summary>ShipId grabbed the ball of team Value.</summary>
    BallGrabbed,
    /// <summary>ShipId (the former carrier) let go of the ball of team Value.</summary>
    BallDropped,
    /// <summary>ShipId scored with the ball of team Value.</summary>
    BallCaptured,
    /// <summary>The ball of team Value went home; ShipId is the teammate who returned it, or -1 on timeout.</summary>
    BallReturned,
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

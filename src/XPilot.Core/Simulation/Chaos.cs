using System.Numerics;

namespace XPilot.Core.Simulation;

/// <summary>Random events that shake up a match for a few seconds.</summary>
public enum ChaosKind : byte
{
    HeavyGravity,
    ZeroGravity,
    GravityFlip,
    ReversedControls,
    Turbo,
    RapidFire,
    RubberWalls,
    Blackout,
    ShieldJam,
    BlackHole,
    SolarWind,
    UnlimitedFuel,
    ThickAir,
}

/// <summary>How often random events happen.</summary>
public enum EventFrequency : byte { Off, Rare, Normal, Frequent, Chaos }

/// <summary>An event in progress, or announced and about to start.</summary>
/// <param name="Point">Where a black hole sits, or which way the solar wind blows (a unit vector).</param>
public readonly record struct ChaosEvent(ChaosKind Kind, float Remaining, float Duration, Vector2 Point);

/// <summary>
/// Picks and runs the random events. The server's world schedules them; a network client's world only mirrors
/// the state from snapshots (<see cref="ReadState"/>), which is enough for its prediction to feel the same
/// gravity and controls as the server.
/// </summary>
public sealed class ChaosDirector
{
    /// <summary>Seconds of warning before an event starts.</summary>
    public const float WarningTime = 3f;
    /// <summary>Strongest pull of a black hole (px/s^2), several times a map attractor's.</summary>
    public const float BlackHoleMaxAccel = 650f;
    public const float BlackHoleRange = 1100f;
    public const float BlackHoleStrength = 12_000_000f;
    public const float WindAccel = 150f;

    public static readonly ChaosKind[] AllKinds = Enum.GetValues<ChaosKind>();

    /// <summary>Every event allowed.</summary>
    public const uint AllEvents = (1u << 13) - 1;

    private readonly List<ChaosEvent> _active = [];
    private float _untilNext = -1f;

    public IReadOnlyList<ChaosEvent> Active => _active;
    /// <summary>The next event, announced <see cref="WarningTime"/> before it starts (Remaining counts down to the start).</summary>
    public ChaosEvent? Incoming { get; private set; }

    public bool Has(ChaosKind kind)
    {
        foreach (var e in _active)
        {
            if (e.Kind == kind) return true;
        }
        return false;
    }

    public ChaosEvent? Get(ChaosKind kind)
    {
        foreach (var e in _active)
        {
            if (e.Kind == kind) return e;
        }
        return null;
    }

    public static string Name(ChaosKind kind) => kind switch
    {
        ChaosKind.HeavyGravity => "Heavy Gravity",
        ChaosKind.ZeroGravity => "Zero G",
        ChaosKind.GravityFlip => "Gravity Flip",
        ChaosKind.ReversedControls => "Reversed Controls",
        ChaosKind.Turbo => "Turbo",
        ChaosKind.RapidFire => "Rapid Fire",
        ChaosKind.RubberWalls => "Rubber Walls",
        ChaosKind.Blackout => "Blackout",
        ChaosKind.ShieldJam => "Shield Jam",
        ChaosKind.BlackHole => "Black Hole",
        ChaosKind.SolarWind => "Solar Wind",
        ChaosKind.UnlimitedFuel => "Unlimited Fuel",
        _ => "Thick Air",
    };

    public static string Description(ChaosKind kind) => kind switch
    {
        ChaosKind.HeavyGravity => "Everything weighs a ton",
        ChaosKind.ZeroGravity => "Gravity is switched off",
        ChaosKind.GravityFlip => "Down is up",
        ChaosKind.ReversedControls => "Left is right and right is left",
        ChaosKind.Turbo => "Double thrust and a higher top speed",
        ChaosKind.RapidFire => "Guns fire three times as fast, for free",
        ChaosKind.RubberWalls => "Walls bounce you back unharmed",
        ChaosKind.Blackout => "The lights are out",
        ChaosKind.ShieldJam => "Shields are down",
        ChaosKind.BlackHole => "Something is pulling everyone in",
        ChaosKind.SolarWind => "A storm pushes every ship",
        ChaosKind.UnlimitedFuel => "Thrust and shoot all you like",
        _ => "Ships slow down as if under water",
    };

    /// <summary>Events that only make sense when guns are on.</summary>
    public static bool NeedsWeapons(ChaosKind kind) => kind is ChaosKind.RapidFire or ChaosKind.ShieldJam;

    /// <summary>Seconds between events at a frequency, as a range.</summary>
    public static (float Min, float Max) Gap(EventFrequency frequency) => frequency switch
    {
        EventFrequency.Rare => (50f, 80f),
        EventFrequency.Normal => (25f, 40f),
        EventFrequency.Frequent => (12f, 20f),
        EventFrequency.Chaos => (4f, 8f),
        _ => (float.PositiveInfinity, float.PositiveInfinity),
    };

    /// <summary>Counts down running events and starts new ones. Only the authoritative world calls this.</summary>
    /// <param name="paused">No new events, e.g. during a race countdown or after the match is over.</param>
    public void Update(World world, bool paused)
    {
        const float dt = GameConfig.Dt;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var e = _active[i];
            float remaining = e.Remaining - dt;
            if (remaining > 0f)
            {
                _active[i] = e with { Remaining = remaining };
                continue;
            }
            _active.RemoveAt(i);
            world.Emit(new GameEvent(GameEventType.ChaosEnded, Value: (float)e.Kind));
        }

        var config = world.Config;
        if (config.Events == EventFrequency.Off || paused) return;

        var (min, max) = Gap(config.Events);
        if (_untilNext < 0f) _untilNext = Next(world, min, max);

        if (Incoming is { } incoming)
        {
            float left = incoming.Remaining - dt;
            if (left > 0f)
            {
                Incoming = incoming with { Remaining = left };
                return;
            }
            Incoming = null;
            var started = incoming with { Remaining = incoming.Duration };
            _active.Add(started);
            world.Emit(new GameEvent(GameEventType.ChaosStarted, Position: started.Point, Value: (float)started.Kind));
            _untilNext = Next(world, min, max);
            return;
        }

        _untilNext -= dt;
        if (_untilNext > 0f) return;
        int maxActive = config.Events == EventFrequency.Chaos ? 2 : 1;
        if (_active.Count >= maxActive)
        {
            _untilNext = 1f;
            return;
        }
        if (Pick(world) is not { } kind)
        {
            _untilNext = max;
            return;
        }
        var announced = new ChaosEvent(kind, WarningTime, Math.Clamp(config.EventDuration, 1f, 600f), PickPoint(world, kind));
        Incoming = announced;
        world.Emit(new GameEvent(GameEventType.ChaosWarning, Position: announced.Point, Value: (float)kind));
    }

    /// <summary>Starts an event at once, for tests and debugging.</summary>
    public void Start(World world, ChaosKind kind, float duration = 10f, Vector2? point = null)
    {
        var e = new ChaosEvent(kind, duration, duration, Quantize(kind, point ?? PickPoint(world, kind)));
        _active.Add(e);
        world.Emit(new GameEvent(GameEventType.ChaosStarted, Position: e.Point, Value: (float)kind));
    }

    private static float Next(World world, float min, float max) => min + (float)world.Rng.NextDouble() * (max - min);

    /// <summary>A random allowed event that isn't running and doesn't clash with one that is.</summary>
    private ChaosKind? Pick(World world)
    {
        var options = new List<ChaosKind>();
        foreach (var kind in AllKinds)
        {
            if ((world.Config.EventMask & (1u << (int)kind)) == 0) continue;
            if (NeedsWeapons(kind) && !world.Rules.WeaponsEnabled) continue;
            if (Has(kind) || _active.Any(a => Clashes(a.Kind, kind))) continue;
            options.Add(kind);
        }
        return options.Count == 0 ? null : options[world.Rng.Next(options.Count)];
    }

    /// <summary>The gravity events overrule each other, so only one runs at a time.</summary>
    private static bool Clashes(ChaosKind a, ChaosKind b) => IsGravity(a) && IsGravity(b);

    private static bool IsGravity(ChaosKind kind) => kind is ChaosKind.HeavyGravity or ChaosKind.ZeroGravity or ChaosKind.GravityFlip;

    private static Vector2 PickPoint(World world, ChaosKind kind)
    {
        var map = world.Map;
        if (kind == ChaosKind.SolarWind) return Quantize(kind, MathUtil.FromAngle((float)world.Rng.NextDouble() * MathUtil.TwoPi));
        if (kind != ChaosKind.BlackHole) return Vector2.Zero;

        // Somewhere open, near where the ships are.
        var ships = world.Ships.Where(s => s.Alive).ToList();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            Vector2 p;
            if (ships.Count > 0 && attempt < 150)
            {
                var near = ships[world.Rng.Next(ships.Count)].Position;
                float angle = (float)world.Rng.NextDouble() * MathUtil.TwoPi;
                p = map.WrapPosition(near + MathUtil.FromAngle(angle) * (250f + (float)world.Rng.NextDouble() * 300f));
            }
            else
            {
                p = new Vector2((float)world.Rng.NextDouble() * map.PixelWidth, (float)world.Rng.NextDouble() * map.PixelHeight);
            }
            if (p.X < 0 || p.Y < 0 || p.X > map.PixelWidth || p.Y > map.PixelHeight) continue;
            if (!map.CircleOverlapsWall(p, 48f)) return Quantize(kind, p);
        }
        return Quantize(kind, map.Bases.Count > 0 ? map.Bases[0] : Vector2.Zero);
    }

    /// <summary>
    /// Points travel in snapshots as 16-bit numbers: whole pixels, or ten-thousandths for the wind's direction.
    /// Rounding them here gives the server exactly what its clients get, so their predictions agree.
    /// </summary>
    private static float PointScale(ChaosKind kind) => kind == ChaosKind.SolarWind ? 10000f : 1f;

    private static Vector2 Quantize(ChaosKind kind, Vector2 p)
    {
        float scale = PointScale(kind);
        return new Vector2(Math.Clamp(MathF.Round(p.X * scale), short.MinValue, short.MaxValue) / scale,
            Math.Clamp(MathF.Round(p.Y * scale), short.MinValue, short.MaxValue) / scale);
    }

    public void WriteState(BinaryWriter w)
    {
        w.Write((byte)_active.Count);
        foreach (var e in _active) Write(w, e);
        w.Write(Incoming.HasValue);
        if (Incoming is { } incoming) Write(w, incoming);
    }

    public void ReadState(BinaryReader r)
    {
        _active.Clear();
        int count = r.ReadByte();
        for (int i = 0; i < count; i++) _active.Add(Read(r));
        Incoming = r.ReadBoolean() ? Read(r) : null;
    }

    /// <summary>Nine bytes: snapshots have to fit in one packet.</summary>
    private static void Write(BinaryWriter w, ChaosEvent e)
    {
        float scale = PointScale(e.Kind);
        w.Write((byte)e.Kind);
        w.Write((ushort)Math.Clamp(MathF.Round(e.Remaining * 100f), 0f, ushort.MaxValue));
        w.Write((ushort)Math.Clamp(MathF.Round(e.Duration * 100f), 0f, ushort.MaxValue));
        w.Write((short)Math.Clamp(MathF.Round(e.Point.X * scale), short.MinValue, short.MaxValue));
        w.Write((short)Math.Clamp(MathF.Round(e.Point.Y * scale), short.MinValue, short.MaxValue));
    }

    private static ChaosEvent Read(BinaryReader r)
    {
        var kind = (ChaosKind)r.ReadByte();
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Unknown event");
        float scale = PointScale(kind);
        float remaining = r.ReadUInt16() / 100f, duration = r.ReadUInt16() / 100f;
        return new ChaosEvent(kind, remaining, duration, new Vector2(r.ReadInt16() / scale, r.ReadInt16() / scale));
    }
}

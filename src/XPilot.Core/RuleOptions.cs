using XPilot.Core.Simulation;

namespace XPilot.Core;

/// <summary>How hard walls are on ships.</summary>
public enum WallDamage { Deadly, Forgiving, Off }

/// <summary>
/// Physics and event choices for a match, in the terms the menu and command line use (percentages and
/// on/off), turned into a <see cref="GameConfig"/> by <see cref="CreateConfig"/>.
/// </summary>
public sealed class RuleOptions
{
    public int GravityPercent { get; set; } = 100;
    public int ThrustPercent { get; set; } = 100;
    public int SpeedPercent { get; set; } = 100;
    /// <summary>Fuel used by thrust, shield and shots; 0 means fuel never runs out.</summary>
    public int FuelUsagePercent { get; set; } = 100;
    public int BulletSpeedPercent { get; set; } = 100;
    public int FireRatePercent { get; set; } = 100;
    public WallDamage Walls { get; set; } = WallDamage.Deadly;
    /// <summary>Ships ramming each other hard both explode (never in races).</summary>
    public bool ShipCollisions { get; set; } = true;

    public EventFrequency Events { get; set; } = EventFrequency.Normal;
    public int EventSeconds { get; set; } = 10;
    /// <summary>Events switched off, by <see cref="ChaosKind"/> name.</summary>
    public List<string> DisabledEvents { get; set; } = [];

    public bool IsEventEnabled(ChaosKind kind) => !DisabledEvents.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase);

    public void SetEventEnabled(ChaosKind kind, bool enabled)
    {
        DisabledEvents.RemoveAll(n => string.Equals(n, kind.ToString(), StringComparison.OrdinalIgnoreCase));
        if (!enabled) DisabledEvents.Add(kind.ToString());
    }

    /// <summary>True when the physics are as the game normally plays (events aside).</summary>
    public bool IsStandardPhysics =>
        GravityPercent == 100 && ThrustPercent == 100 && SpeedPercent == 100 && FuelUsagePercent == 100 &&
        BulletSpeedPercent == 100 && FireRatePercent == 100 && Walls == WallDamage.Deadly && ShipCollisions;

    public void ResetPhysics()
    {
        var defaults = new RuleOptions();
        GravityPercent = defaults.GravityPercent;
        ThrustPercent = defaults.ThrustPercent;
        SpeedPercent = defaults.SpeedPercent;
        FuelUsagePercent = defaults.FuelUsagePercent;
        BulletSpeedPercent = defaults.BulletSpeedPercent;
        FireRatePercent = defaults.FireRatePercent;
        Walls = defaults.Walls;
        ShipCollisions = defaults.ShipCollisions;
    }

    public GameConfig CreateConfig()
    {
        var config = new GameConfig();
        float Scale(int percent) => Math.Clamp(percent, 0, 1000) / 100f;

        config.GravityScale = Scale(GravityPercent);
        config.ThrustAccel *= MathF.Max(0.1f, Scale(ThrustPercent));
        config.MaxSpeed *= MathF.Max(0.1f, Scale(SpeedPercent));
        float fuel = Scale(FuelUsagePercent);
        config.ThrustFuelPerSec *= fuel;
        config.ShieldFuelPerSec *= fuel;
        config.FireFuel *= fuel;
        config.ShieldImpactFuel *= fuel;
        config.BulletSpeed *= MathF.Max(0.1f, Scale(BulletSpeedPercent));
        config.FireCooldown /= MathF.Max(0.1f, Scale(FireRatePercent));
        config.CrashSpeed = Walls switch
        {
            WallDamage.Forgiving => config.CrashSpeed * 2f,
            WallDamage.Off => 1e9f,
            _ => config.CrashSpeed,
        };
        config.ShipCollisionsKill = ShipCollisions;

        config.Events = Events;
        config.EventDuration = Math.Clamp(EventSeconds, 1, 120);
        uint mask = 0;
        foreach (var kind in ChaosDirector.AllKinds)
        {
            if (IsEventEnabled(kind)) mask |= 1u << (int)kind;
        }
        config.EventMask = mask;
        return config;
    }

    /// <summary>A short description of what differs from a standard match, e.g. for a menu or a server log.</summary>
    public string Summary()
    {
        var parts = new List<string>();
        if (GravityPercent != 100) parts.Add($"gravity {GravityPercent}%");
        if (ThrustPercent != 100) parts.Add($"thrust {ThrustPercent}%");
        if (SpeedPercent != 100) parts.Add($"top speed {SpeedPercent}%");
        if (FuelUsagePercent != 100) parts.Add(FuelUsagePercent == 0 ? "unlimited fuel" : $"fuel use {FuelUsagePercent}%");
        if (BulletSpeedPercent != 100) parts.Add($"bullet speed {BulletSpeedPercent}%");
        if (FireRatePercent != 100) parts.Add($"fire rate {FireRatePercent}%");
        if (Walls != WallDamage.Deadly) parts.Add(Walls == WallDamage.Off ? "harmless walls" : "forgiving walls");
        if (!ShipCollisions) parts.Add("no ramming");
        parts.Add(Events == EventFrequency.Off ? "no events" : $"{Events.ToString().ToLowerInvariant()} events");
        return string.Join(", ", parts);
    }
}

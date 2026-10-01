namespace XPilot.Core;

/// <summary>Tunable simulation constants. Units are pixels and seconds.</summary>
public sealed class GameConfig
{
    public const float TickRate = 60f;
    public const float Dt = 1f / TickRate;

    public float ShipRadius { get; set; } = 12f;
    public float TurnSpeed { get; set; } = 4.2f;
    public float ThrustAccel { get; set; } = 320f;
    public float MaxSpeed { get; set; } = 650f;

    /// <summary>Wall impacts with a normal speed above this destroy an unprotected ship.</summary>
    public float CrashSpeed { get; set; } = 170f;
    public float WallRestitution { get; set; } = 0.45f;
    public float WallFriction { get; set; } = 0.85f;

    public float BulletSpeed { get; set; } = 720f;
    public float BulletLife { get; set; } = 1.3f;
    public float BulletRadius { get; set; } = 2f;
    public float FireCooldown { get; set; } = 0.16f;
    /// <summary>Own bullets cannot hit their shooter until they are this old.</summary>
    public float SelfHitGrace { get; set; } = 0.25f;

    public float MaxFuel { get; set; } = 1000f;
    public float ThrustFuelPerSec { get; set; } = 35f;
    public float ShieldFuelPerSec { get; set; } = 70f;
    public float FireFuel { get; set; } = 3f;
    public float RefuelPerSec { get; set; } = 250f;
    public float PassiveRefuelPerSec { get; set; } = 10f;
    public float RefuelRange { get; set; } = 56f;
    /// <summary>Fuel drained per unit of impact speed when a shielded ship hits a wall hard.</summary>
    public float ShieldImpactFuel { get; set; } = 0.5f;

    public float SpawnProtection { get; set; } = 2f;

    public float AttractorMaxAccel { get; set; } = 260f;
    public float AttractorRange { get; set; } = 700f;

    public bool ShipCollisionsKill { get; set; } = true;

    public GameConfig Clone() => (GameConfig)MemberwiseClone();
}

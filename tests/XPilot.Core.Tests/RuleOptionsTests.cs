using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class RuleOptionsTests
{
    [Fact]
    public void Defaults_GiveTheStandardPhysics()
    {
        var options = new RuleOptions { Events = EventFrequency.Off };
        var config = options.CreateConfig();
        var standard = new GameConfig();
        Assert.True(options.IsStandardPhysics);
        Assert.Equal(standard.ThrustAccel, config.ThrustAccel);
        Assert.Equal(standard.MaxSpeed, config.MaxSpeed);
        Assert.Equal(standard.CrashSpeed, config.CrashSpeed);
        Assert.Equal(standard.FireCooldown, config.FireCooldown);
        Assert.Equal(1f, config.GravityScale);
        Assert.Equal(ChaosDirector.AllEvents, config.EventMask);
    }

    [Fact]
    public void Percentages_ScaleThePhysics()
    {
        var config = new RuleOptions
        {
            GravityPercent = 200, ThrustPercent = 150, SpeedPercent = 50, FuelUsagePercent = 0, FireRatePercent = 200,
            Walls = WallDamage.Off, ShipCollisions = false,
        }.CreateConfig();
        var standard = new GameConfig();
        Assert.Equal(2f, config.GravityScale);
        Assert.Equal(standard.ThrustAccel * 1.5f, config.ThrustAccel);
        Assert.Equal(standard.MaxSpeed * 0.5f, config.MaxSpeed);
        Assert.Equal(0f, config.ThrustFuelPerSec);
        Assert.Equal(0f, config.FireFuel);
        Assert.Equal(standard.FireCooldown / 2f, config.FireCooldown, 5);
        Assert.True(config.CrashSpeed > 1e6f);
        Assert.False(config.ShipCollisionsKill);
    }

    [Fact]
    public void DisabledEvents_LeaveTheMask()
    {
        var options = new RuleOptions();
        options.SetEventEnabled(ChaosKind.Blackout, false);
        options.SetEventEnabled(ChaosKind.ReversedControls, false);
        options.SetEventEnabled(ChaosKind.ReversedControls, false);
        Assert.Equal(2, options.DisabledEvents.Count);
        uint mask = options.CreateConfig().EventMask;
        Assert.Equal(0u, mask & (1u << (int)ChaosKind.Blackout));
        Assert.NotEqual(0u, mask & (1u << (int)ChaosKind.Turbo));

        options.SetEventEnabled(ChaosKind.Blackout, true);
        Assert.True(options.IsEventEnabled(ChaosKind.Blackout));
    }

    [Fact]
    public void Summary_SaysWhatIsDifferent()
    {
        Assert.Equal("normal events", new RuleOptions().Summary());
        Assert.Equal("gravity 300%, unlimited fuel, no events",
            new RuleOptions { GravityPercent = 300, FuelUsagePercent = 0, Events = EventFrequency.Off }.Summary());
    }
}

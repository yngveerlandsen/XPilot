using System.Numerics;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class WeaponTests
{
    private static (World World, Ship Shooter, Ship Target) Duel()
    {
        var world = TestUtil.DogfightWorld(ships: 2);
        var shooter = world.Ships[0];
        var target = world.Ships[1];
        TestUtil.Place(shooter, new Vector2(150, 200), Vector2.Zero, heading: 0f);
        TestUtil.Place(target, new Vector2(450, 200), Vector2.Zero);
        return (world, shooter, target);
    }

    [Fact]
    public void Bullet_KillsUnshieldedShip_AndScores()
    {
        var (world, shooter, target) = Duel();
        world.Step([new ShipInput { Fire = true }]);
        TestUtil.Run(world, 60);

        Assert.False(target.Alive);
        Assert.Equal(1, shooter.Kills);
        Assert.Equal(1, shooter.Score);
        Assert.Contains(world.Ships, s => s == target && s.Deaths == 1);
    }

    [Fact]
    public void Shield_BlocksBullet()
    {
        var (world, _, target) = Duel();
        var inputs = new[] { new ShipInput { Fire = true }, new ShipInput { Shield = true } };
        world.Step(inputs);
        inputs[0] = default;
        TestUtil.Run(world, 60, inputs);

        Assert.True(target.Alive);
        Assert.Empty(world.Bullets);
    }

    [Fact]
    public void CannotFire_WhileShielded()
    {
        var (world, _, _) = Duel();
        world.Step([new ShipInput { Fire = true, Shield = true }]);
        Assert.Empty(world.Bullets);
    }

    [Fact]
    public void FireCooldown_LimitsRateOfFire()
    {
        var (world, _, _) = Duel();
        TestUtil.Run(world, 6, new ShipInput { Fire = true });
        Assert.Single(world.Bullets);
    }

    [Fact]
    public void Bullet_InheritsShipVelocity()
    {
        var (world, shooter, _) = Duel();
        shooter.Velocity = new Vector2(0, 100);
        world.Step([new ShipInput { Fire = true }]);
        var bullet = Assert.Single(world.Bullets);
        Assert.Equal(100f, bullet.Velocity.Y, 1);
        Assert.Equal(world.Config.BulletSpeed, bullet.Velocity.X, 1);
    }

    [Fact]
    public void Bullet_StopsAtWalls()
    {
        var (world, shooter, target) = Duel();
        target.Position = new Vector2(150, 100);
        shooter.Heading = MathF.PI; // towards the left wall
        world.Step([new ShipInput { Fire = true }]);
        TestUtil.Run(world, 30);
        Assert.Empty(world.Bullets);
    }
}

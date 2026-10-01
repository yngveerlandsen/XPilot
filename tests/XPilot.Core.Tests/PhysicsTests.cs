using System.Numerics;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class PhysicsTests
{
    private static readonly Vector2 Center = new(320, 200);

    [Fact]
    public void Thrust_AcceleratesAlongHeadingAndUsesFuel()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        TestUtil.Place(ship, Center, Vector2.Zero, heading: 0f);
        float fuel = ship.Fuel;

        TestUtil.Run(world, 30, new ShipInput { Thrust = true });

        float expected = world.Config.ThrustAccel * 30 * GameConfig.Dt;
        Assert.Equal(expected, ship.Velocity.X, 1);
        Assert.Equal(0f, ship.Velocity.Y, 3);
        Assert.True(ship.Fuel < fuel);
    }

    [Fact]
    public void Ship_KeepsDriftingWithoutThrust()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        TestUtil.Place(ship, Center, new Vector2(60, 0));
        TestUtil.Run(world, 60);
        Assert.Equal(60f, ship.Velocity.X, 3);
        Assert.Equal(Center.X + 60f, ship.Position.X, 1);
    }

    [Fact]
    public void Turn_RotatesAtTurnSpeed()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        TestUtil.Place(ship, Center, Vector2.Zero, heading: 0f);
        TestUtil.Run(world, 15, new ShipInput { Turn = 1f });
        Assert.Equal(world.Config.TurnSpeed * 15 * GameConfig.Dt, ship.Heading, 3);
    }

    [Fact]
    public void Speed_IsCapped()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        TestUtil.Place(ship, Center, new Vector2(world.Config.MaxSpeed, 0));
        world.Step([new ShipInput { Thrust = true }]);
        Assert.True(ship.Velocity.Length() <= world.Config.MaxSpeed + 0.01f);
    }

    [Fact]
    public void Attractor_PullsShipTowardsIt()
    {
        var world = TestUtil.DogfightWorld("""
            border: true
            ---
            _.........
            ....+.....
            ..........
            """);
        var ship = world.Ships[0];
        var attractor = world.Map.GravitySources[0].Position;
        TestUtil.Place(ship, attractor + new Vector2(100, 0), Vector2.Zero);
        world.Step([]);
        Assert.True(ship.Velocity.X < 0f);
    }

    [Fact]
    public void SlowWallContact_Bounces()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        var wallLeft = world.Map.PixelWidth - 32f;
        TestUtil.Place(ship, new Vector2(wallLeft - 14f, 200), new Vector2(100, 0));
        TestUtil.Run(world, 10);
        Assert.True(ship.Alive);
        Assert.True(ship.Velocity.X < 0f, "ship should bounce back off the wall");
        Assert.False(world.Map.CircleOverlapsWall(ship.Position, world.Config.ShipRadius - 0.5f));
    }

    [Fact]
    public void FastWallImpact_DestroysShip()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        TestUtil.Place(ship, new Vector2(world.Map.PixelWidth - 32f - 20f, 200), new Vector2(400, 0));
        TestUtil.Run(world, 10);
        Assert.False(ship.Alive);
        Assert.Equal(1, ship.Deaths);
        Assert.Equal(-1, ship.Score);
    }

    [Fact]
    public void FastWallImpact_WithShield_Survives()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        TestUtil.Place(ship, new Vector2(world.Map.PixelWidth - 32f - 20f, 200), new Vector2(400, 0));
        TestUtil.Run(world, 10, new ShipInput { Shield = true });
        Assert.True(ship.Alive);
    }

    [Fact]
    public void DeadShip_RespawnsAfterDelay()
    {
        var world = TestUtil.DogfightWorld();
        var ship = world.Ships[0];
        world.Kill(ship, null, DeathCause.Wall);
        Assert.False(ship.Alive);
        TestUtil.Run(world, (int)(world.Rules.RespawnDelay * GameConfig.TickRate) + 2);
        Assert.True(ship.Alive);
        Assert.Equal(world.Config.MaxFuel, ship.Fuel, 0);
    }
}

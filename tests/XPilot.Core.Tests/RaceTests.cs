using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class RaceTests
{
    private const string Track = """
        mode: race
        border: true
        laps: 2
        checkpoint_radius: 40
        ---
        ....................
        ._....1.....2....3..
        ....................
        """;

    private static (World World, Ship Ship, RaceRules Rules) Race()
    {
        var rules = new RaceRules(2);
        var world = new World(MapLoader.Parse(Track), new GameConfig(), rules);
        var ship = world.AddShip("Racer", false);
        world.Start();
        return (world, ship, rules);
    }

    private static void SkipCountdown(World world) =>
        TestUtil.Run(world, (int)(RaceRules.CountdownSeconds * GameConfig.TickRate) + 1);

    private static void Visit(World world, Ship ship, int checkpoint)
    {
        ship.Position = world.Map.Checkpoints[checkpoint];
        ship.Velocity = default;
        world.Step([]);
    }

    [Fact]
    public void Countdown_LocksControls()
    {
        var (world, ship, rules) = Race();
        Assert.True(rules.ControlsLocked);
        var start = ship.Position;
        TestUtil.Run(world, 30, new ShipInput { Thrust = true });
        Assert.Equal(start, ship.Position);

        SkipCountdown(world);
        Assert.False(rules.ControlsLocked);
        TestUtil.Run(world, 10, new ShipInput { Thrust = true });
        Assert.NotEqual(start, ship.Position);
    }

    [Fact]
    public void Checkpoints_MustBePassedInOrder()
    {
        var (world, ship, _) = Race();
        SkipCountdown(world);

        Visit(world, ship, 1);
        Assert.Equal(0, ship.NextCheckpoint);

        Visit(world, ship, 0);
        Visit(world, ship, 1);
        Assert.Equal(2, ship.NextCheckpoint);
        Assert.Equal(0, ship.Lap);
    }

    [Fact]
    public void PassingLastCheckpoint_CompletesLap_AndFinishesRace()
    {
        var (world, ship, rules) = Race();
        SkipCountdown(world);

        for (int cp = 0; cp < 3; cp++) Visit(world, ship, cp);
        Assert.Equal(1, ship.Lap);
        Assert.Single(ship.LapTimes);
        Assert.False(ship.Finished);

        for (int cp = 0; cp < 3; cp++) Visit(world, ship, cp);
        Assert.True(ship.Finished);
        Assert.Equal(1, ship.Place);
        Assert.True(rules.IsOver);
    }

    [Fact]
    public void Weapons_AreDisabled()
    {
        var (world, _, _) = Race();
        SkipCountdown(world);
        TestUtil.Run(world, 5, new ShipInput { Fire = true });
        Assert.Empty(world.Bullets);
    }
}

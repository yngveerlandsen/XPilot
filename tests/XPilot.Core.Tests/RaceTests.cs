using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class RaceTests
{
    // Checkpoints 13 tiles apart, further than a zone reaches, so passing one never passes the next.
    private const string Track = """
        mode: race
        border: true
        laps: 2
        ---
        ............................................
        ._............1............2............3...
        ............................................
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
    public void Checkpoints_DoNotCountThroughWalls()
    {
        // The checkpoint is well inside the zone's reach, but on the other side of a wall.
        const string walled = """
            mode: race
            border: true
            ---
            ................................
            ._....x.1..................2....
            ......x.........................
            """;
        var rules = new RaceRules(1, RaceRules.ZoneRadius(XPilot.Core.AI.BotDifficulty.Normal));
        var world = new World(MapLoader.Parse(walled), new GameConfig(), rules);
        var ship = world.AddShip("Racer", false);
        world.Start();
        SkipCountdown(world);

        // Positions relative to the checkpoint; the wall is two tiles to its left, in its row and the one below.
        var checkpoint = world.Map.Checkpoints[0];
        Vector2 Tiles(float x, float y) => checkpoint + new Vector2(x, y) * Map.TileSize;

        ship.Position = Tiles(-3, 0);
        ship.Velocity = default;
        world.Step([]);
        Assert.True(world.Map.Distance(ship.Position, checkpoint) < rules.CheckpointRadius);
        Assert.Equal(0, ship.NextCheckpoint);

        ship.Position = Tiles(-3, 1);
        world.Step([]);
        Assert.Equal(0, ship.NextCheckpoint);

        // Above the wall, with a clear view, it counts.
        ship.Position = Tiles(-2, -1);
        world.Step([]);
        Assert.Equal(1, ship.NextCheckpoint);
    }

    [Fact]
    public void CheckpointZones_ShrinkWithSkill_AndAreTheSameOnEveryMap()
    {
        float easy = RaceRules.ZoneRadius(XPilot.Core.AI.BotDifficulty.Easy);
        float normal = RaceRules.ZoneRadius(XPilot.Core.AI.BotDifficulty.Normal);
        float hard = RaceRules.ZoneRadius(XPilot.Core.AI.BotDifficulty.Hard);
        Assert.True(easy > normal && normal > hard);

        var races = TestUtil.MapFiles.Select(MapLoader.Load).Where(m => m.Mode == GameModeKind.Race).ToList();
        Assert.True(races.Count >= 4);
        foreach (var map in races)
        {
            var rules = (RaceRules)Match.CreateRules(new MatchSetup { Map = map, Difficulty = XPilot.Core.AI.BotDifficulty.Hard });
            Assert.Equal(hard, rules.CheckpointRadius);
        }
    }

    [Fact]
    public void CheckpointZone_ReachesClients_AndOlderServersStillParse()
    {
        var server = new RaceRules(3, 72f);
        var stream = new MemoryStream();
        server.WriteState(new BinaryWriter(stream));
        var client = new RaceRules(3);
        client.ReadState(new BinaryReader(new MemoryStream(stream.ToArray())));
        Assert.Equal(72f, client.CheckpointRadius);

        // A server from before the zone size was sent: the client keeps its default.
        var old = stream.ToArray()[..^sizeof(float)];
        var fromOld = new RaceRules(3);
        fromOld.ReadState(new BinaryReader(new MemoryStream(old)));
        Assert.Equal(RaceRules.ZoneRadius(XPilot.Core.AI.BotDifficulty.Normal), fromOld.CheckpointRadius);
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

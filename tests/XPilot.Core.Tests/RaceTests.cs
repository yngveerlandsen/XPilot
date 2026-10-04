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
        var world = new World(MapLoader.Parse(walled), new GameConfig(), new RaceRules(1));
        var ship = world.AddShip("Racer", false);
        world.Start();
        SkipCountdown(world);

        // Positions relative to the checkpoint; the wall is two tiles to its left, in its row and the one below.
        var checkpoint = world.Map.Checkpoints[0];
        Vector2 Tiles(float x, float y) => checkpoint + new Vector2(x, y) * Map.TileSize;

        ship.Position = Tiles(-4, 0);
        ship.Velocity = default;
        world.Step([]);
        Assert.True(world.Map.Distance(ship.Position, checkpoint) < world.Map.CheckpointRadius);
        Assert.Equal(0, ship.NextCheckpoint);

        ship.Position = Tiles(-4, 1);
        world.Step([]);
        Assert.Equal(0, ship.NextCheckpoint);

        // Above the wall, with a clear view, it counts.
        ship.Position = Tiles(-2, -1);
        world.Step([]);
        Assert.Equal(1, ship.NextCheckpoint);
    }

    public static IEnumerable<object[]> OwnRaceMaps => TestUtil.MapFiles
        .Where(f => !f.Contains("classic"))
        .Select(f => (File: f, Map: MapLoader.Load(f)))
        .Where(m => m.Map.Mode == GameModeKind.Race)
        .Select(m => new object[] { Path.GetRelativePath(TestUtil.MapsDirectory, m.File) });

    /// <summary>Wherever a ship crosses the track at a checkpoint, it is inside the zone.</summary>
    [Theory]
    [MemberData(nameof(OwnRaceMaps))]
    public void CheckpointZones_CoverTheWholeTrackWidth(string file)
    {
        var map = MapLoader.Load(Path.Combine(TestUtil.MapsDirectory, file));
        for (int i = 0; i < map.Checkpoints.Count; i++)
        {
            var c = map.Checkpoints[i];
            // The narrowest span through the checkpoint runs across the track.
            float bestSpan = float.MaxValue, far = 0f;
            for (int k = 0; k < 32; k++)
            {
                float angle = k * MathF.PI / 32f;
                var d = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
                float ahead = map.ClearDistance(c, d, 2000f, 0f), behind = map.ClearDistance(c, -d, 2000f, 0f);
                if (ahead + behind >= bestSpan) continue;
                bestSpan = ahead + behind;
                far = MathF.Max(ahead, behind);
            }
            float reach = far - new GameConfig().ShipRadius;
            Assert.True(reach <= map.CheckpointRadius, $"{file} checkpoint {i + 1}: ships can pass {reach:F0}px from its centre");
        }
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

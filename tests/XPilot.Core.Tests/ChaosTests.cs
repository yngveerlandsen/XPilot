using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class ChaosTests
{
    private static readonly Vector2 Middle = new(320, 192);

    private static (World World, Ship Ship) OneShip(GameConfig? config = null)
    {
        var world = TestUtil.DogfightWorld(config: config);
        var ship = world.Ships[0];
        TestUtil.Place(ship, Middle, Vector2.Zero);
        return (world, ship);
    }

    [Fact]
    public void ReversedControls_TurnTheOtherWay()
    {
        var (world, ship) = OneShip();
        world.Chaos.Start(world, ChaosKind.ReversedControls);
        TestUtil.Run(world, 10, new ShipInput { Turn = 1f });
        Assert.True(ship.Heading < 0f, $"turning right should turn left, heading {ship.Heading}");
    }

    [Fact]
    public void GravityEvents_ChangeTheMapsGravity()
    {
        var world = new World(TestUtil.LoadMap("caverns"), new GameConfig(), new DogfightRules());
        world.Start();
        var p = world.Map.Bases[0];
        Assert.True(world.GravityAt(p).Y > 0f, "caverns has gravity pulling down");

        world.Chaos.Start(world, ChaosKind.ZeroGravity);
        Assert.Equal(Vector2.Zero, world.GravityAt(p));
    }

    [Fact]
    public void HeavyGravity_AndGravityFlip_WorkWithoutMapGravity()
    {
        var (world, _) = OneShip();
        Assert.Equal(Vector2.Zero, world.GravityAt(Middle));

        world.Chaos.Start(world, ChaosKind.HeavyGravity);
        var heavy = world.GravityAt(Middle);
        Assert.True(heavy.Y >= 150f, $"heavy gravity pulls down hard: {heavy}");
        Assert.True(heavy.Length() < world.Config.ThrustAccel, "but the engine can still lift the ship");

        var (flipped, _) = OneShip();
        flipped.Chaos.Start(flipped, ChaosKind.GravityFlip);
        Assert.True(flipped.GravityAt(Middle).Y < -100f, "flipped gravity pulls up");
    }

    [Fact]
    public void GravityScale_MultipliesTheMapsGravity()
    {
        var map = TestUtil.LoadMap("caverns");
        var normal = new World(map, new GameConfig(), new DogfightRules());
        var double_ = new World(map, new GameConfig { GravityScale = 2f }, new DogfightRules());
        var p = map.Bases[0];
        Assert.Equal(normal.GravityAt(p).Y * 2f, double_.GravityAt(p).Y, 3);
    }

    [Fact]
    public void RubberWalls_BounceAShipThatWouldHaveCrashed()
    {
        var (world, ship) = OneShip();
        world.Chaos.Start(world, ChaosKind.RubberWalls);
        TestUtil.Place(ship, new Vector2(320, 80), new Vector2(0, -400f));
        TestUtil.Run(world, 30);
        Assert.True(ship.Alive);
        Assert.True(ship.Velocity.Y > 300f, $"bounced back almost as fast: {ship.Velocity}");
    }

    [Fact]
    public void WithoutRubberWalls_TheSameHitCrashes()
    {
        var (world, ship) = OneShip();
        TestUtil.Place(ship, new Vector2(320, 80), new Vector2(0, -400f));
        TestUtil.Run(world, 30);
        Assert.False(ship.Alive);
    }

    [Fact]
    public void ShieldJam_KeepsShieldsDown()
    {
        var (world, ship) = OneShip();
        world.Chaos.Start(world, ChaosKind.ShieldJam);
        TestUtil.Run(world, 5, new ShipInput { Shield = true });
        Assert.False(ship.Shield);
    }

    [Fact]
    public void UnlimitedFuel_BurnsNothing()
    {
        var (world, ship) = OneShip();
        world.Chaos.Start(world, ChaosKind.UnlimitedFuel);
        ship.Fuel = 500f;
        TestUtil.Run(world, 60, new ShipInput { Thrust = true, Shield = true, Turn = 1f });
        Assert.True(ship.Fuel >= 500f, $"fuel {ship.Fuel}");
    }

    [Fact]
    public void RapidFire_ShootsThreeTimesAsOften()
    {
        int Shots(bool rapid)
        {
            var (world, ship) = OneShip();
            if (rapid) world.Chaos.Start(world, ChaosKind.RapidFire);
            int shots = 0;
            for (int i = 0; i < 120; i++)
            {
                world.Step([new ShipInput { Fire = true }]);
                shots += world.Events.Count(e => e.Type == GameEventType.ShipFired);
                ship.Position = Middle;
            }
            return shots;
        }

        int normal = Shots(false), rapid = Shots(true);
        Assert.True(rapid >= normal * 2.5f, $"normal {normal}, rapid {rapid}");
    }

    [Fact]
    public void Turbo_RaisesTopSpeed_AndItEasesBackAfterwards()
    {
        var (world, ship) = OneShip(new GameConfig { MaxSpeed = 200f });
        world.Chaos.Start(world, ChaosKind.Turbo, duration: 1f);
        TestUtil.Place(ship, Middle, new Vector2(290f, 0f));
        world.Step([new ShipInput { Thrust = true }]);
        Assert.True(ship.Velocity.Length() > 290f, "turbo allows 300");

        TestUtil.Run(world, 60);
        Assert.False(world.Chaos.Has(ChaosKind.Turbo));
        TestUtil.Place(ship, Middle, new Vector2(290f, 0f));
        world.Step([default]);
        float speed = ship.Velocity.Length();
        Assert.True(speed is > 270f and < 290f, $"slows down gradually rather than snapping to 200: {speed}");
    }

    [Fact]
    public void BlackHole_PullsShipsIn_AndDestroysThemAtTheCentre()
    {
        var (world, ship) = OneShip();
        world.Chaos.Start(world, ChaosKind.BlackHole, duration: 30f, point: new Vector2(450, 192));
        var gravity = world.GravityAt(Middle);
        Assert.True(gravity.X > 100f, $"pulled towards the hole: {gravity}");

        TestUtil.Run(world, 600);
        Assert.True(ship.Deaths >= 1, "drifted in and was torn apart");
    }

    [Fact]
    public void Director_AnnouncesStartsAndEndsEvents()
    {
        var world = TestUtil.DogfightWorld(ships: 2, config: new GameConfig { Events = EventFrequency.Chaos, EventDuration = 2f });
        var seen = new List<GameEventType>();
        for (int i = 0; i < 60 * 40; i++)
        {
            world.Step([]);
            seen.AddRange(world.Events.Select(e => e.Type).Where(t => t is GameEventType.ChaosWarning or GameEventType.ChaosStarted or GameEventType.ChaosEnded));
        }
        Assert.True(seen.Count(t => t == GameEventType.ChaosStarted) >= 3, $"{seen.Count(t => t == GameEventType.ChaosStarted)} events in 40 s of chaos");
        Assert.Equal(GameEventType.ChaosWarning, seen[0]);
        Assert.Equal(GameEventType.ChaosStarted, seen[1]);
    }

    [Fact]
    public void Director_StaysQuietWhenOff()
    {
        var world = TestUtil.DogfightWorld(ships: 2);
        for (int i = 0; i < 60 * 120; i++)
        {
            world.Step([]);
            Assert.DoesNotContain(world.Events, e => e.Type == GameEventType.ChaosWarning);
        }
    }

    [Fact]
    public void Director_OnlyPicksAllowedEvents_AndSkipsGunEventsInRaces()
    {
        var config = new GameConfig { Events = EventFrequency.Chaos, EventMask = (1u << (int)ChaosKind.RapidFire) | (1u << (int)ChaosKind.Turbo) };
        var map = TestUtil.LoadMap("oval");
        var world = new World(map, config, new RaceRules(3), 4);
        world.AddShip("A", false);
        world.Start();
        var kinds = new HashSet<ChaosKind>();
        for (int i = 0; i < 60 * 60; i++)
        {
            world.Step([]);
            foreach (var e in world.Events.Where(e => e.Type == GameEventType.ChaosStarted)) kinds.Add((ChaosKind)e.Value);
        }
        Assert.Equal([ChaosKind.Turbo], kinds);
    }

    [Fact]
    public void Director_WaitsForTheRaceCountdown()
    {
        var world = new World(TestUtil.LoadMap("oval"), new GameConfig { Events = EventFrequency.Chaos }, new RaceRules(3), 4);
        world.AddShip("A", false);
        world.Start();
        for (int i = 0; i < 60 * RaceRules.CountdownSeconds - 1; i++)
        {
            world.Step([]);
            Assert.DoesNotContain(world.Events, e => e.Type == GameEventType.ChaosWarning);
        }
    }

    [Fact]
    public void State_RoundTrips_SoClientsPredictWithTheSameEffects()
    {
        var (server, _) = OneShip();
        server.Chaos.Start(server, ChaosKind.ReversedControls, 8f);
        server.Chaos.Start(server, ChaosKind.BlackHole, 5f, new Vector2(100, 100));
        var stream = new MemoryStream();
        server.Chaos.WriteState(new BinaryWriter(stream));

        var (client, _) = OneShip();
        client.Chaos.ReadState(new BinaryReader(new MemoryStream(stream.ToArray())));
        Assert.Equal(server.Chaos.Active, client.Chaos.Active);
        Assert.Equal(server.GravityAt(Middle), client.GravityAt(Middle));
    }
}

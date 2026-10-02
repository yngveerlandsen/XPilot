using System.Numerics;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;
using Xunit.Abstractions;

namespace XPilot.Core.Tests;

public class BallTests(ITestOutputHelper output)
{
    /// <summary>Red treasure on the left, Blue on the right, one base each.</summary>
    private const string Field = """
        mode: ball
        border: true
        ---
        ..............................
        ..r........................b..
        ..............................
        ..R........................B..
        ..............................
        ..............................
        """;

    private static (World World, Ship Red, Ship Blue, BallRules Rules) TwoTeams()
    {
        var rules = new BallRules(captureLimit: 2);
        var world = new World(MapLoader.Parse(Field), new GameConfig(), rules);
        var red = world.AddShip("Red", false);
        red.Team = Teams.Red;
        var blue = world.AddShip("Blue", false);
        blue.Team = Teams.Blue;
        world.Start();
        return (world, red, blue, rules);
    }

    private static Ball BallOf(World world, int team) => world.Balls.Single(b => b.Team == team);

    /// <summary>Puts the ship next to a ball, then releases and presses the grab key.</summary>
    private static void GrabNear(World world, Ship ship, Ball ball)
    {
        TestUtil.Place(ship, ball.Position + new Vector2(-40, 0), Vector2.Zero);
        var inputs = new ShipInput[world.Ships.Count];
        world.Step(inputs);
        inputs[ship.Id].Grab = true;
        world.Step(inputs);
    }

    [Fact]
    public void Parse_ReadsTreasuresAndTeamBases()
    {
        var map = MapLoader.Parse(Field);
        Assert.Equal(GameModeKind.Ball, map.Mode);
        Assert.Equal(2, map.Treasures.Count);
        Assert.Single(map.TeamBases(Teams.Red));
        Assert.Single(map.TeamBases(Teams.Blue));
        Assert.NotEqual(map.TreasureOf(Teams.Red), map.TreasureOf(Teams.Blue));
    }

    [Fact]
    public void Parse_RequiresBothTreasures()
    {
        var ex = Assert.Throws<MapFormatException>(() => MapLoader.Parse("mode: ball\n---\nr.b.R\n"));
        Assert.Contains("Blue treasure", ex.Message);
    }

    [Fact]
    public void Ships_SpawnAtTheirTeamBases()
    {
        var (world, red, blue, _) = TwoTeams();
        Assert.Equal(world.Map.TeamBases(Teams.Red)[0], red.Position);
        Assert.Equal(world.Map.TeamBases(Teams.Blue)[0], blue.Position);
        Assert.Equal(2, world.Balls.Count);
        Assert.All(world.Balls, b => Assert.Equal(BallState.Home, b.State));
    }

    [Fact]
    public void Grab_AttachesEnemyBall_ButNotOwnBall()
    {
        var (world, red, _, _) = TwoTeams();
        GrabNear(world, red, BallOf(world, Teams.Red));
        Assert.Equal(BallState.Home, BallOf(world, Teams.Red).State);

        GrabNear(world, red, BallOf(world, Teams.Blue));
        var blueBall = BallOf(world, Teams.Blue);
        Assert.Equal(BallState.Carried, blueBall.State);
        Assert.Equal(red.Id, blueBall.CarrierId);
        Assert.Contains(world.Events, e => e.Type == GameEventType.BallGrabbed);
    }

    [Fact]
    public void HoldingGrab_DoesNotReleaseImmediately()
    {
        var (world, red, _, _) = TwoTeams();
        GrabNear(world, red, BallOf(world, Teams.Blue));
        var inputs = new ShipInput[2];
        inputs[red.Id].Grab = true;
        TestUtil.Run(world, 10, inputs);
        Assert.Equal(BallState.Carried, BallOf(world, Teams.Blue).State);

        inputs[red.Id].Grab = false;
        world.Step(inputs);
        inputs[red.Id].Grab = true;
        world.Step(inputs);
        Assert.Equal(BallState.Loose, BallOf(world, Teams.Blue).State);
    }

    [Fact]
    public void Rope_DragsTheBallAlong()
    {
        var (world, red, _, _) = TwoTeams();
        var ball = BallOf(world, Teams.Blue);
        GrabNear(world, red, ball);
        red.Heading = MathF.PI; // away from the ball, towards the left
        var inputs = new ShipInput[2];
        inputs[red.Id].Thrust = true;
        TestUtil.Run(world, 60, inputs);

        Assert.True(ball.Position.X < ball.Home.X - 50f, "ball should have been pulled left");
        Assert.True(world.Map.Distance(red.Position, ball.Position) <= world.Config.BallRopeLength + 1f);
    }

    [Fact]
    public void TowingEnemyBallToOwnTreasure_Scores()
    {
        var (world, red, _, rules) = TwoTeams();
        var ball = BallOf(world, Teams.Blue);
        GrabNear(world, red, ball);

        var redTreasure = world.Map.TreasureOf(Teams.Red)!.Value;
        ball.Position = redTreasure + new Vector2(10, 0);
        red.Position = redTreasure + new Vector2(60, 0);
        world.Step([]);

        Assert.Equal(1, rules.TeamScore(Teams.Red));
        Assert.Equal(BallRules.CapturePoints, red.Score);
        Assert.Equal(BallState.Home, ball.State);
        Assert.Contains(world.Events, e => e.Type == GameEventType.BallCaptured);
    }

    [Fact]
    public void ReachingCaptureLimit_EndsMatch()
    {
        var (world, red, _, rules) = TwoTeams();
        var ball = BallOf(world, Teams.Blue);
        var redTreasure = world.Map.TreasureOf(Teams.Red)!.Value;
        for (int i = 0; i < 2; i++)
        {
            GrabNear(world, red, ball);
            ball.Position = redTreasure;
            red.Position = redTreasure + new Vector2(60, 0);
            world.Step([]);
        }
        Assert.True(rules.IsOver);
        Assert.Equal(Teams.Red, rules.WinningTeam);
    }

    [Fact]
    public void KillingTheCarrier_DropsTheBall_AndTeammateTouchReturnsIt()
    {
        var (world, red, blue, _) = TwoTeams();
        var ball = BallOf(world, Teams.Blue);
        GrabNear(world, red, ball);
        world.Kill(red, blue, DeathCause.Bullet);
        Assert.Equal(BallState.Loose, ball.State);
        Assert.Contains(world.Events, e => e.Type == GameEventType.BallDropped);

        TestUtil.Place(blue, ball.Position, Vector2.Zero);
        world.Step([]);
        Assert.Equal(BallState.Home, ball.State);
    }

    [Fact]
    public void LooseBall_ReturnsHomeAfterTimeout()
    {
        var (world, red, _, _) = TwoTeams();
        var ball = BallOf(world, Teams.Blue);
        GrabNear(world, red, ball);
        var inputs = new ShipInput[2];
        inputs[red.Id].Grab = true;
        world.Step(inputs); // still held from the grab, so no release
        inputs[red.Id].Grab = false;
        world.Step(inputs);
        inputs[red.Id].Grab = true;
        world.Step(inputs); // release
        Assert.Equal(BallState.Loose, ball.State);

        red.Position = new Vector2(400, 60); // keep away so nobody touches it
        TestUtil.Run(world, (int)(BallRules.ReturnTime * GameConfig.TickRate) + 5);
        Assert.Equal(BallState.Home, ball.State);
    }

    [Fact]
    public void Teammates_CannotShootEachOther()
    {
        var rules = new BallRules();
        var world = new World(MapLoader.Parse(Field), new GameConfig(), rules);
        var a = world.AddShip("A", false);
        var b = world.AddShip("B", false);
        a.Team = b.Team = Teams.Red;
        world.Start();
        TestUtil.Place(a, new Vector2(200, 200), Vector2.Zero, heading: 0f);
        TestUtil.Place(b, new Vector2(400, 200), Vector2.Zero);
        world.Step([new ShipInput { Fire = true }]);
        TestUtil.Run(world, 40);
        Assert.True(b.Alive);
    }

    [Fact]
    public void Match_SplitsBotsIntoTeams_PlayerOnRed()
    {
        var match = new Match(new MatchSetup { Map = TestUtil.LoadMap("bastions"), BotCount = 5, Seed = 1 });
        var ships = match.World.Ships;
        Assert.Equal(Teams.Red, match.Player!.Team);
        Assert.Equal(3, ships.Count(s => s.Team == Teams.Red));
        Assert.Equal(3, ships.Count(s => s.Team == Teams.Blue));
    }

    [Theory]
    [InlineData(BotDifficulty.Normal)]
    [InlineData(BotDifficulty.Hard)]
    public void Bots_CaptureBalls(BotDifficulty difficulty)
    {
        // Single matches vary a lot, so add up a few.
        int grabs = 0, captures = 0, returns = 0;
        foreach (int seed in new[] { 1, 2, 3 })
        {
            var match = new Match(new MatchSetup
            {
                Map = TestUtil.LoadMap("bastions"),
                IncludePlayer = false,
                BotCount = 6,
                Difficulty = difficulty,
                CaptureLimit = 0,
                TimeLimit = 0,
                Seed = seed,
            });
            for (int tick = 0; tick < 300 * GameConfig.TickRate; tick++)
            {
                match.Step();
                foreach (var e in match.World.Events)
                {
                    if (e.Type == GameEventType.BallGrabbed) grabs++;
                    if (e.Type == GameEventType.BallCaptured) captures++;
                    if (e.Type == GameEventType.BallReturned) returns++;
                }
            }
        }

        output.WriteLine($"3 matches: grabs={grabs} captures={captures} returns={returns}");
        Assert.True(captures >= 6, $"bots should capture balls (grabs={grabs}, captures={captures} in three 5-minute matches)");
    }
}

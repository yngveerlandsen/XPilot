using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

public class MatchTests
{
    private static Match ServerMatch(string map, int bots) => new(new MatchSetup
    {
        Map = TestUtil.LoadMap(map),
        IncludePlayer = false,
        BotCount = bots,
        Seed = 3,
    });

    [Fact]
    public void AddHuman_TakesABotSeat_WhenTheMapIsFull()
    {
        var match = ServerMatch("arena", 8);
        Assert.Equal(8, match.Capacity);
        Assert.Equal(8, match.Bots.Count);

        var human = match.AddHuman("Ace");
        Assert.NotNull(human);
        Assert.Equal(7, match.Bots.Count);
        Assert.Equal(8, match.World.Ships.Count);
        Assert.False(human.Alive);

        match.Step();
        Assert.True(human.Alive);
    }

    [Fact]
    public void Capacity_CanExceedTheBases_ForCrowdedMatches()
    {
        var match = new Match(new MatchSetup
        {
            Map = TestUtil.LoadMap("arena"), IncludePlayer = false, BotCount = 20, Capacity = 20, ScoreLimit = 0, TimeLimit = 0, Seed = 2,
        });
        Assert.Equal(20, match.World.Ships.Count);
        for (int i = 0; i < 600; i++) match.Step();
        Assert.True(match.World.Ships.Count(s => s.Alive) >= 10, "most of a crowd should be flying, not stuck dead");
    }

    [Fact]
    public void RemoveHuman_GivesTheSeatBackToABot()
    {
        var match = ServerMatch("arena", 8);
        var human = match.AddHuman("Ace")!;
        match.Step();
        match.RemoveHuman(human);
        Assert.Equal(8, match.Bots.Count);
        Assert.Null(match.World.GetShip(human.Id));
        Assert.DoesNotContain(human, match.World.Ships);
        match.Step();
    }

    [Fact]
    public void AddHuman_KeepsBotCount_WhenThereIsRoom()
    {
        var match = ServerMatch("arena", 3);
        match.AddHuman("A");
        match.AddHuman("B");
        Assert.Equal(3, match.Bots.Count);
        Assert.Equal(5, match.World.Ships.Count);
    }

    [Fact]
    public void AddHuman_ReturnsNull_WhenEverySeatHasAHuman()
    {
        var match = ServerMatch("oval", 0);
        for (int i = 0; i < match.Capacity; i++) Assert.NotNull(match.AddHuman($"P{i}"));
        Assert.Null(match.AddHuman("Late"));
    }

    [Fact]
    public void BallMode_SpreadsHumansAcrossTeams_AndKeepsTeamsEven()
    {
        var match = ServerMatch("bastions", 4);
        var a = match.AddHuman("A")!;
        var b = match.AddHuman("B")!;
        Assert.NotEqual(a.Team, b.Team);
        var ships = match.World.Ships;
        Assert.True(Math.Abs(ships.Count(s => s.Team == Teams.Red) - ships.Count(s => s.Team == Teams.Blue)) <= 1);

        match.SwitchTeam(a);
        Assert.Equal(b.Team, a.Team);
        Assert.True(Math.Abs(ships.Count(s => s.Team == Teams.Red) - ships.Count(s => s.Team == Teams.Blue)) <= 1);
        for (int i = 0; i < 90; i++) match.Step();
        Assert.True(a.Alive);
        Assert.Contains(match.World.Map.TeamBases(a.Team), p => Vector2.Distance(p, a.PrevPosition) < 600f);
    }

    [Fact]
    public void RaceJoiner_StartsFromTheFirstCheckpoint()
    {
        var match = ServerMatch("oval", 2);
        for (int i = 0; i < 400; i++) match.Step();
        var late = match.AddHuman("Late")!;
        match.Step();
        Assert.True(late.Alive);
        Assert.Equal(0, late.NextCheckpoint);
        Assert.Equal(0, late.Lap);
    }

    [Fact]
    public void Inputs_AreHeldUntilChanged()
    {
        var match = ServerMatch("arena", 0);
        var human = match.AddHuman("A")!;
        match.Step();
        var start = human.Position;
        match.SetInput(human, new ShipInput { Thrust = true });
        for (int i = 0; i < 30; i++) match.Step();
        Assert.True(Vector2.Distance(start, human.Position) > 20f);
    }

    [Fact]
    public void PredictShip_MatchesTheServer_ForALoneShip()
    {
        // Gravity and walls but nobody else around: prediction should track the real thing exactly.
        const string map = """
            name: Pit
            border: true
            gravity: 0 60
            ---
            ....................
            ..._................
            ....................
            ....................
            ....................
            ........xx..........
            ....................
            ....................
            ....................
            ....................
            """;
        var world = TestUtil.DogfightWorld(map);
        var real = world.Ships[0];
        var predicted = new Ship(99, "copy", false, 0);
        CopyMotion(real, predicted);

        var rng = new Random(5);
        for (int tick = 0; tick < 600; tick++)
        {
            var input = new ShipInput
            {
                Turn = (float)Math.Sin(tick * 0.05),
                Thrust = rng.NextDouble() < 0.6,
                Shield = tick % 200 > 190,
                Fire = tick % 7 == 0,
            };
            world.Step([input]);
            world.PredictShip(predicted, input, null);
            if (!real.Alive) break;
            Assert.Equal(real.Position, predicted.Position);
            Assert.Equal(real.Velocity, predicted.Velocity);
            Assert.Equal(real.Fuel, predicted.Fuel);
        }
    }

    [Fact]
    public void PredictShip_TowsTheBallLikeTheServer()
    {
        var world = new World(TestUtil.LoadMap("bastions"), new GameConfig(), new BallRules(), seed: 1);
        var ship = world.AddShip("A", false);
        ship.Team = Teams.Red;
        world.Start();
        var blue = world.Balls.First(b => b.Team == Teams.Blue);
        TestUtil.Place(ship, blue.Position + new Vector2(30, 0), Vector2.Zero);
        world.Step([new ShipInput { Grab = true }]);
        Assert.Equal(BallState.Carried, blue.State);

        var predictedShip = new Ship(99, "copy", false, 0) { Team = Teams.Red };
        CopyMotion(ship, predictedShip);
        var predictedBall = new Ball(blue.Team, blue.Home) { Position = blue.Position, Velocity = blue.Velocity, State = BallState.Carried };
        for (int tick = 0; tick < 120; tick++)
        {
            var input = new ShipInput { Thrust = true, Turn = tick < 60 ? 0.3f : -0.2f, Grab = true };
            world.Step([input]);
            world.PredictShip(predictedShip, input, predictedBall);
            Assert.Equal(ship.Position, predictedShip.Position);
            Assert.Equal(blue.Position, predictedBall.Position);
        }
    }

    private static void CopyMotion(Ship from, Ship to)
    {
        to.Position = from.Position;
        to.Velocity = from.Velocity;
        to.Heading = from.Heading;
        to.Fuel = from.Fuel;
        to.Alive = from.Alive;
        to.SpawnProtection = from.SpawnProtection;
        to.FireCooldown = from.FireCooldown;
        to.GrabHeld = from.GrabHeld;
    }

    [Fact]
    public void RulesState_RoundTrips()
    {
        var match = ServerMatch("bastions", 6);
        var rules = (BallRules)match.World.Rules;
        for (int i = 0; i < 600; i++) match.Step();

        var copy = (BallRules)Match.CreateRules(match.Setup);
        var stream = new MemoryStream();
        rules.WriteState(new BinaryWriter(stream));
        stream.Position = 0;
        copy.ReadState(new BinaryReader(stream));

        Assert.Equal(rules.TeamScore(Teams.Red), copy.TeamScore(Teams.Red));
        Assert.Equal(rules.TeamScore(Teams.Blue), copy.TeamScore(Teams.Blue));
        Assert.Equal(rules.IsOver, copy.IsOver);
    }
}

using System.Numerics;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;
using Xunit.Abstractions;

namespace XPilot.Core.Tests;

public class ModeTests(ITestOutputHelper output)
{
    private static Match BotMatch(string map, GameModeKind mode, int bots, int seed = 5, float? timeLimit = null) => new(new MatchSetup
    {
        Map = TestUtil.LoadMap(map),
        Mode = mode,
        IncludePlayer = false,
        BotCount = bots,
        Difficulty = BotDifficulty.Normal,
        TimeLimit = timeLimit,
        Seed = seed,
    });

    private static void Shoot(World world, Ship killer, Ship victim)
    {
        victim.SpawnProtection = 0f;
        victim.Shield = false;
        world.Kill(victim, killer, DeathCause.Bullet);
    }

    [Fact]
    public void Supports_MatchesModesToMaps()
    {
        var arena = TestUtil.LoadMap("arena");
        var oval = TestUtil.LoadMap("oval");
        var ballMap = TestUtil.MapFiles.Select(MapLoader.Load).First(m => m.Mode == GameModeKind.Ball);
        Assert.True(GameModes.Supports(GameModeKind.KingOfTheHill, arena));
        Assert.True(GameModes.Supports(GameModeKind.TeamDogfight, ballMap));
        Assert.False(GameModes.Supports(GameModeKind.Elimination, oval));
        Assert.False(GameModes.Supports(GameModeKind.Ball, arena));
        Assert.All(GameModes.All, mode => Assert.Contains(TestUtil.MapFiles.Select(MapLoader.Load), m => GameModes.Supports(mode, m)));
    }

    [Fact]
    public void AMode_TheMapCantPlay_FallsBackToTheMapsOwn()
    {
        var match = BotMatch("oval", GameModeKind.KingOfTheHill, 2);
        Assert.Equal(GameModeKind.Race, match.World.Rules.Mode);
    }

    [Fact]
    public void TeamDogfight_SplitsBotsIntoTeams_OnADogfightMap()
    {
        var match = BotMatch("arena", GameModeKind.TeamDogfight, 6);
        Assert.IsType<TeamDogfightRules>(match.World.Rules);
        Assert.Equal(3, match.World.Ships.Count(s => s.Team == Teams.Red));
        Assert.Equal(3, match.World.Ships.Count(s => s.Team == Teams.Blue));
    }

    [Fact]
    public void TeamDogfight_EnemyKillsScoreForTheTeam_AndEndTheMatchAtTheLimit()
    {
        var rules = new TeamDogfightRules(teamScoreLimit: 2);
        var world = new World(TestUtil.LoadMap("arena"), new GameConfig(), rules);
        var red = world.AddShip("Red", false);
        red.Team = Teams.Red;
        var blue = world.AddShip("Blue", false);
        blue.Team = Teams.Blue;
        var blue2 = world.AddShip("Blue2", false);
        blue2.Team = Teams.Blue;
        world.Start();

        Shoot(world, red, blue);
        Assert.Equal(1, rules.TeamScore(Teams.Red));
        Assert.Equal(1, red.Kills);
        Assert.False(rules.IsOver);

        Shoot(world, red, blue2);
        Assert.True(rules.IsOver);
        Assert.Equal(Teams.Red, rules.WinningTeam);
        Assert.Equal(red, rules.GetStandings(world)[0]);
    }

    [Fact]
    public void TeamDogfight_TeammatesBulletsPassThrough()
    {
        var world = new World(MapLoader.Parse(TestUtil.OpenBox), new GameConfig(), new TeamDogfightRules());
        var a = world.AddShip("A", false);
        var b = world.AddShip("B", false);
        a.Team = b.Team = Teams.Red;
        world.Start();
        TestUtil.Place(a, new Vector2(200, 200), Vector2.Zero, 0f);
        TestUtil.Place(b, new Vector2(300, 200), Vector2.Zero, 0f);
        TestUtil.Run(world, 60, new ShipInput { Fire = true }, default);
        Assert.True(b.Alive);
    }

    [Fact]
    public void Elimination_PilotsOutOfLivesStayDown_AndTheLastOneWins()
    {
        var rules = new EliminationRules(lives: 2, timeLimit: 0);
        var world = new World(TestUtil.LoadMap("arena"), new GameConfig(), rules);
        var a = world.AddShip("A", false);
        var b = world.AddShip("B", false);
        var c = world.AddShip("C", false);
        world.Start();
        Assert.All(world.Ships, s => Assert.Equal(2, s.Score));

        Shoot(world, a, b);
        Assert.False(rules.IsEliminated(b));
        TestUtil.Run(world, (int)(rules.RespawnDelay * 60) + 2);
        Assert.True(b.Alive, "one life left, so b respawns");

        Shoot(world, a, b);
        Assert.True(rules.IsEliminated(b));
        Assert.Contains(world.Events, e => e.Type == GameEventType.ShipEliminated && e.ShipId == b.Id);
        TestUtil.Run(world, 600);
        Assert.False(b.Alive, "out of lives, so b stays down");
        Assert.False(rules.IsOver);

        Shoot(world, a, c);
        TestUtil.Run(world, (int)(rules.RespawnDelay * 60) + 2);
        Shoot(world, a, c);
        world.Step([]);
        Assert.True(rules.IsOver);
        var standings = rules.GetStandings(world);
        Assert.Equal([a, c, b], standings);
        Assert.Equal(3, b.Place);
        Assert.Equal(2, c.Place);
    }

    [Fact]
    public void Elimination_ALatecomerGetsNoMoreLivesThanTheWorstOff()
    {
        var match = BotMatch("arena", GameModeKind.Elimination, 3);
        var bot = match.World.Ships[0];
        Shoot(match.World, match.World.Ships[1], bot);
        var human = match.AddHuman("Late")!;
        Assert.Equal(bot.Score, human.Score);
        Assert.True(human.Score < ((EliminationRules)match.World.Rules).Lives);
    }

    [Fact]
    public void KingOfTheHill_OnlyAPilotAloneOnTheHillScores()
    {
        var rules = new KingOfTheHillRules(scoreLimit: 3, timeLimit: 0);
        var world = new World(TestUtil.LoadMap("arena"), new GameConfig(), rules, seed: 2);
        var a = world.AddShip("A", false);
        var b = world.AddShip("B", false);
        world.Start();
        var hill = rules.Hill;
        Assert.False(world.Map.CircleOverlapsWall(hill, KingOfTheHillRules.HillRadius * 0.4f));

        TestUtil.Place(a, hill, Vector2.Zero);
        TestUtil.Place(b, hill + new Vector2(KingOfTheHillRules.HillRadius * 3f, 0), Vector2.Zero);
        HoldStill(world, a, b, hill, b.Position, 61);
        Assert.Equal(1, a.Score);
        Assert.Equal(a.Id, rules.Holder);

        HoldStill(world, a, b, hill, hill + new Vector2(30, 0), 120);
        Assert.Equal(KingOfTheHillRules.Contested, rules.Holder);
        Assert.Equal(1, a.Score);
        Assert.Equal(0, b.Score);

        HoldStill(world, a, b, hill, hill + new Vector2(KingOfTheHillRules.HillRadius * 3f, 0), 125);
        Assert.True(rules.IsOver);
        Assert.Equal(a, rules.GetStandings(world)[0]);
    }

    /// <summary>Keeps two ships parked (ignoring gravity) for a number of ticks.</summary>
    private static void HoldStill(World world, Ship a, Ship b, Vector2 aAt, Vector2 bAt, int ticks)
    {
        for (int i = 0; i < ticks; i++)
        {
            TestUtil.Place(a, aAt, Vector2.Zero);
            TestUtil.Place(b, bAt, Vector2.Zero);
            world.Step([]);
        }
    }

    [Fact]
    public void KingOfTheHill_TheHillMovesSomewhereElse()
    {
        var rules = new KingOfTheHillRules(timeLimit: 0);
        var world = new World(TestUtil.LoadMap("arena"), new GameConfig(), rules, seed: 3);
        world.AddShip("A", false);
        world.Start();
        var first = rules.Hill;
        TestUtil.Run(world, (int)(KingOfTheHillRules.MoveInterval * 60) + 2);
        Assert.NotEqual(first, rules.Hill);
        Assert.True(world.Map.Distance(first, rules.Hill) > KingOfTheHillRules.HillRadius);
    }

    [Theory]
    [InlineData("arena")]
    [InlineData("bastions")]
    [InlineData("caverns")]
    public void KingOfTheHill_BotsFindTheHillAndScore(string map)
    {
        var match = BotMatch(map, GameModeKind.KingOfTheHill, 4, timeLimit: 0);
        for (int i = 0; i < 60 * 90 && !match.World.Rules.IsOver; i++) match.Step();
        int total = match.World.Ships.Sum(s => s.Score);
        output.WriteLine($"{map}: hill points {total}, scores {string.Join(", ", match.World.Ships.Select(s => s.Score))}");
        Assert.True(total >= 20, $"bots should spend time on the hill, got {total} points in 90 s");
    }

    [Fact]
    public void Elimination_BotsPlayAMatchToTheEnd()
    {
        var match = BotMatch("arena", GameModeKind.Elimination, 4, timeLimit: 0);
        int ticks = 0;
        for (; ticks < 60 * 600 && !match.World.Rules.IsOver; ticks++) match.Step();
        output.WriteLine($"over after {ticks / 60} s");
        Assert.True(match.World.Rules.IsOver);
        Assert.Equal(1, ((EliminationRules)match.World.Rules).Remaining(match.World));
    }

    [Fact]
    public void Bots_CompensateForReversedControls_UnlessEasy()
    {
        var match = BotMatch("arena", GameModeKind.Dogfight, 4);
        match.World.Chaos.Start(match.World, ChaosKind.ReversedControls, duration: 600f);
        int crashes = 0;
        for (int i = 0; i < 60 * 60; i++)
        {
            match.Step();
            crashes += match.World.Events.Count(e => e is { Type: GameEventType.ShipDestroyed, Cause: DeathCause.Wall });
        }
        output.WriteLine($"wall crashes with reversed controls: {crashes}");
        Assert.True(crashes <= 4, $"normal bots should still fly; {crashes} crashes in a minute");
    }
}

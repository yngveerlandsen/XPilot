using System.Numerics;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;
using Xunit.Abstractions;

namespace XPilot.Core.Tests;

/// <summary>The original XPilot map format, the classic maps, and the Grand Tour.</summary>
public class XpMapTests(ITestOutputHelper output)
{
    private static string Xp(string options, params string[] rows) =>
        $"{options}\nmapWidth: {rows.Max(r => r.Length)}\nmapHeight: {rows.Length}\nmapData: \\multiline: EndOfMapdata\n" +
        string.Join("\n", rows) + "\nEndOfMapdata\n";

    [Fact]
    public void Diagonals_AreFlippedFromXPilotsYUpNaming()
    {
        // In XPilot maps q sits in corners with walls right and below, so its solid half is bottom-right.
        var map = MapLoader.Parse(Xp("mapName: T", "xxxx", "xqwx", "xasx", "x__x", "xxxx"));
        Assert.Equal(TileShape.SolidBottomRight, map.GetTile(1, 1));
        Assert.Equal(TileShape.SolidBottomLeft, map.GetTile(2, 1));
        Assert.Equal(TileShape.SolidTopRight, map.GetTile(1, 2));
        Assert.Equal(TileShape.SolidTopLeft, map.GetTile(2, 2));
    }

    [Fact]
    public void Options_CarryOver()
    {
        var map = MapLoader.Parse(Xp("mapName: Test Map\nmapAuthor: Someone\nedgeWrap: yes\ngravity: \\override: 0", "_#+ "));
        Assert.Equal("Test Map", map.Name);
        Assert.Contains("Someone", map.Description);
        Assert.True(map.Wrap);
        Assert.Equal(Vector2.Zero, map.Gravity);
        Assert.Single(map.FuelStations);
        Assert.Single(map.GravitySources);
        Assert.Equal(GameModeKind.Dogfight, map.Mode);
    }

    [Fact]
    public void DefaultGravity_PullsDown()
    {
        var map = MapLoader.Parse(Xp("mapName: T", "_  "));
        Assert.True(map.Gravity.Y > 10f && MathF.Abs(map.Gravity.X) < 0.01f, $"gravity {map.Gravity}");
    }

    [Fact]
    public void RaceCheckpoints_StartAndFinishAtA()
    {
        var map = MapLoader.Parse(Xp("timing: yes\ngravity: 0", "_A  B  C  "));
        Assert.Equal(GameModeKind.Race, map.Mode);
        Assert.Equal([Map.TileCenter(4, 0), Map.TileCenter(7, 0), Map.TileCenter(1, 0)], map.Checkpoints);
    }

    [Fact]
    public void Treasures_BelongToTheTeamWithTheNearestBase()
    {
        var map = MapLoader.Parse(Xp("teamPlay: yes", "2*      *4", "2        4"));
        Assert.Equal(GameModeKind.Ball, map.Mode);
        Assert.Equal(2, map.Treasures.Count);
        int leftBase = map.Bases.ToList().IndexOf(Map.TileCenter(0, 0));
        Assert.Equal(map.BaseTeams[leftBase], map.Treasures[0].Team);
        Assert.NotEqual(map.Treasures[0].Team, map.Treasures[1].Team);
    }

    public static IEnumerable<object[]> ClassicMaps => TestUtil.MapFiles
        .Where(f => Path.GetExtension(f) == ".xp")
        .Select(f => new object[] { Path.GetRelativePath(TestUtil.MapsDirectory, f) });

    [Theory]
    [MemberData(nameof(ClassicMaps))]
    public void ClassicMaps_ArePlayableByBots(string file)
    {
        var map = MapLoader.Load(Path.Combine(TestUtil.MapsDirectory, file));
        var match = new Match(new MatchSetup
        {
            Map = map, IncludePlayer = false, BotCount = 6, ScoreLimit = 0, CaptureLimit = 0, TimeLimit = 0, Laps = 1, Seed = 5,
        });
        int kills = 0, wallDeaths = 0, checkpoints = 0, grabs = 0;
        for (int tick = 0; tick < 60 * GameConfig.TickRate; tick++)
        {
            match.Step();
            foreach (var e in match.World.Events)
            {
                if (e is { Type: GameEventType.ShipDestroyed, Cause: DeathCause.Bullet }) kills++;
                if (e is { Type: GameEventType.ShipDestroyed, Cause: DeathCause.Wall }) wallDeaths++;
                if (e.Type == GameEventType.CheckpointPassed) checkpoints++;
                if (e.Type == GameEventType.BallGrabbed) grabs++;
            }
        }
        output.WriteLine($"{map.Name} ({map.Mode}, {map.Width}x{map.Height}): kills={kills} wall deaths={wallDeaths} checkpoints={checkpoints} grabs={grabs}");
        Assert.True(wallDeaths <= 15, $"{wallDeaths} wall deaths in a minute");
        switch (map.Mode)
        {
            case GameModeKind.Race: Assert.True(checkpoints >= 6, "race bots should be getting round"); break;
            case GameModeKind.Ball: Assert.True(grabs >= 1, "ball bots should go for the balls"); break;
            default: Assert.True(kills >= 3, "dogfight bots should find each other"); break;
        }
    }

    [Fact]
    public void ClassicTeamMaps_AreBallMaps()
    {
        foreach (var name in new[] { "tc", "blood-music2", "teamball" })
        {
            var map = TestUtil.LoadMap($"classic/{name}", ".xp");
            Assert.Equal(GameModeKind.Ball, map.Mode);
            Assert.Contains(map.Treasures, t => t.Team == Teams.Red);
            Assert.Contains(map.Treasures, t => t.Team == Teams.Blue);
        }
        Assert.Equal(13, TestUtil.LoadMap("classic/grandprix", ".xp").Checkpoints.Count);
    }

    [Fact]
    public void Latin1Files_KeepTheirNames()
    {
        Assert.Contains("Bjørn Stabell", TestUtil.LoadMap("classic/globe", ".xp").Description);
        Assert.Equal("Planet X", TestUtil.LoadMap("classic/planetx", ".xp").Name);
    }

    [Fact]
    public void GrandTour_TakesAFewMinutesALap()
    {
        var map = TestUtil.LoadMap("grand-tour", ".xp");
        Assert.Equal(GameModeKind.Race, map.Mode);
        Assert.Equal(26, map.Checkpoints.Count);

        var match = new Match(new MatchSetup { Map = map, IncludePlayer = false, BotCount = 4, Laps = 1, Seed = 9 });
        while (!match.World.Rules.IsOver && match.World.Time < 400f) match.Step();
        foreach (var s in match.World.Ships) output.WriteLine($"{s.Name}: finished={s.Finished} time={s.FinishTime:F0}s deaths={s.Deaths}");
        Assert.All(match.World.Ships, s => Assert.True(s.Finished, $"{s.Name} did not finish"));
        Assert.All(match.World.Ships, s => Assert.InRange(s.FinishTime, 120f, 300f));
    }
}

using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;
using Xunit.Abstractions;

namespace XPilot.Core.Tests;

public class BotTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> RaceMaps => TestUtil.MapFiles
        .Select(MapLoader.Load)
        .Where(m => m.Mode == GameModeKind.Race)
        .Select(m => new object[] { Path.GetFileNameWithoutExtension(m.SourcePath!) });

    public static IEnumerable<object[]> DogfightMaps => TestUtil.MapFiles
        .Select(MapLoader.Load)
        .Where(m => m.Mode == GameModeKind.Dogfight)
        .Select(m => new object[] { Path.GetFileNameWithoutExtension(m.SourcePath!) });

    [Theory]
    [MemberData(nameof(RaceMaps))]
    public void NavField_ReachesEveryCheckpointFromEveryBase(string mapName)
    {
        var map = TestUtil.LoadMap(mapName);
        var nav = new NavGrid(map);
        for (int cp = 0; cp < map.Checkpoints.Count; cp++)
        {
            var field = nav.CheckpointField(cp);
            foreach (var b in map.Bases.Concat(map.Checkpoints))
            {
                Assert.False(float.IsInfinity(field[nav.IndexOf(b)]), $"{mapName}: checkpoint {cp + 1} unreachable from {b}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(RaceMaps))]
    public void RaceBots_FinishTheRace(string mapName)
    {
        var match = new Match(new MatchSetup
        {
            Map = TestUtil.LoadMap(mapName),
            IncludePlayer = false,
            BotCount = 4,
            Difficulty = BotDifficulty.Normal,
            Laps = 2,
            Seed = 42,
        });

        int crashes = 0;
        for (int tick = 0; tick < 240 * GameConfig.TickRate && !match.World.Rules.IsOver; tick++)
        {
            match.Step();
            crashes += match.World.Events.Count(e => e.Type == GameEventType.ShipDestroyed);
        }

        foreach (var s in match.World.Ships)
        {
            output.WriteLine($"{s.Name}: finished={s.Finished} laps={s.Lap} time={s.FinishTime:F1}s best={s.BestLap:F1}s deaths={s.Deaths}");
        }
        output.WriteLine($"crashes: {crashes}");
        Assert.All(match.World.Ships, s => Assert.True(s.Finished, $"{s.Name} did not finish (lap {s.Lap}, next cp {s.NextCheckpoint + 1})"));
    }

    /// <summary>Hits (kills or shield hits) landed per bot per minute in a bots-only arena match.</summary>
    private double MeasureHitRate(BotDifficulty difficulty)
    {
        var match = new Match(new MatchSetup
        {
            Map = TestUtil.LoadMap("arena"),
            IncludePlayer = false,
            BotCount = 6,
            Difficulty = difficulty,
            ScoreLimit = 0,
            TimeLimit = 0,
            Seed = 11,
        });
        int shots = 0, hits = 0;
        for (int tick = 0; tick < 180 * GameConfig.TickRate; tick++)
        {
            match.Step();
            foreach (var e in match.World.Events)
            {
                if (e.Type == GameEventType.ShipFired) shots++;
                if (e.Type == GameEventType.ShieldHit || e is { Type: GameEventType.ShipDestroyed, Cause: DeathCause.Bullet }) hits++;
            }
        }
        double botMinutes = 3.0 * 6;
        double hitRate = hits / botMinutes;
        output.WriteLine($"{difficulty}: shots={shots} hits={hits} accuracy={(shots == 0 ? 0 : hits / (double)shots):P1} " +
                         $"shots/min/bot={shots / botMinutes:F1} hits/min/bot={hitRate:F1}");
        return hitRate;
    }

    [Fact]
    public void BotAccuracy_ScalesWithDifficulty()
    {
        double easy = MeasureHitRate(BotDifficulty.Easy);
        double normal = MeasureHitRate(BotDifficulty.Normal);
        double hard = MeasureHitRate(BotDifficulty.Hard);
        Assert.True(easy < normal && normal < hard, "hit rate should increase with difficulty");
        // Before tuning these were 17.6 (easy) and 40.9 (normal) hits per bot per minute.
        Assert.True(easy < 4, $"easy bots land {easy:F1} hits per minute");
        Assert.True(normal < 10, $"normal bots land {normal:F1} hits per minute");
    }

    [Theory]
    [MemberData(nameof(DogfightMaps))]
    public void DogfightBots_FightWithoutGettingStuckInWalls(string mapName)
    {
        var match = new Match(new MatchSetup
        {
            Map = TestUtil.LoadMap(mapName),
            IncludePlayer = false,
            BotCount = 6,
            Difficulty = BotDifficulty.Normal,
            ScoreLimit = 0,
            TimeLimit = 0,
            Seed = 7,
        });
        var world = match.World;
        int bulletKills = 0, wallDeaths = 0;
        for (int tick = 0; tick < 120 * GameConfig.TickRate; tick++)
        {
            match.Step();
            foreach (var e in world.Events.Where(e => e.Type == GameEventType.ShipDestroyed))
            {
                if (e.Cause == DeathCause.Bullet) bulletKills++;
                if (e.Cause == DeathCause.Wall) wallDeaths++;
            }
            foreach (var s in world.Ships.Where(s => s.Alive))
            {
                Assert.False(world.Map.CircleOverlapsWall(s.Position, world.Config.ShipRadius - 2f), $"{s.Name} is inside a wall at tick {tick}");
            }
        }

        foreach (var s in world.Ships) output.WriteLine($"{s.Name}: kills={s.Kills} deaths={s.Deaths} score={s.Score}");
        output.WriteLine($"bullet kills: {bulletKills}, wall deaths: {wallDeaths}");
        Assert.True(bulletKills >= 5, $"expected bots to shoot each other, got {bulletKills} kills");
        Assert.True(wallDeaths <= bulletKills, $"too many wall crashes ({wallDeaths}) compared to kills ({bulletKills})");
    }
}

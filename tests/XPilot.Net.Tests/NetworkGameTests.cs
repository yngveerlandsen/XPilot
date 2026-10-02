using System.Numerics;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;
using Xunit.Abstractions;

namespace XPilot.Net.Tests;

public class NetworkGameTests(ITestOutputHelper output)
{
    private static string MapText(string name) => File.ReadAllText(Path.Combine(TestMaps.Directory, name + ".xpm"));

    private static GameServer Server(string map = "arena", int bots = 3, ServerOptions? options = null)
    {
        options ??= new ServerOptions();
        options.BotCount = bots;
        return new GameServer(options, [MapText(map)]);
    }

    /// <summary>Thrusts and turns in a pattern that keeps a ship moving around the arena.</summary>
    private static ShipInput Weave(int frame) => new()
    {
        Turn = MathF.Sin(frame * 0.02f),
        Thrust = frame % 90 < 50,
        Fire = frame % 30 == 0,
    };

    [Fact]
    public void Clients_MirrorTheServersShipsAndNames()
    {
        var game = new SimulatedGame(Server(bots: 3), new SimulatedNetwork());
        var a = game.Join("Alice");
        var b = game.Join("Bob");
        game.Run(2);

        var serverShips = game.Server.Match.World.Ships;
        Assert.Equal(5, serverShips.Count);
        foreach (var client in new[] { a, b })
        {
            Assert.True(client.IsReady);
            Assert.Equal(serverShips.Select(s => (s.Id, s.Name)).OrderBy(x => x.Id), client.World.Ships.Select(s => (s.Id, s.Name)).OrderBy(x => x.Id));
            Assert.NotNull(client.Player);
        }
        Assert.Equal("Alice", a.Player!.Name);
        Assert.Equal("Bob", b.Player!.Name);
    }

    [Fact]
    public void RemoteShips_AreDrawnWhereTheServerHadThem()
    {
        var game = new SimulatedGame(Server(bots: 5), new SimulatedNetwork { Latency = 0.06, Jitter = 0.02, Loss = 0.05 });
        var history = new Dictionary<(int Tick, int Ship), Vector2>();
        game.AfterServerTick = () =>
        {
            var w = game.Server.Match.World;
            foreach (var s in w.Ships.Where(s => s.Alive)) history[(w.Tick, s.Id)] = s.Position;
        };
        var client = game.Join("Watcher");
        game.Run(2);

        var map = game.Server.Match.World.Map;
        var errors = new List<float>();
        game.Run(6, () =>
        {
            double rt = client.RenderTick;
            int t0 = (int)Math.Floor(rt);
            float frac = (float)(rt - t0);
            foreach (var s in client.World.Ships)
            {
                if (s == client.Player || !s.Alive) continue;
                if (!history.TryGetValue((t0, s.Id), out var p0) || !history.TryGetValue((t0 + 1, s.Id), out var p1)) continue;
                var truth = map.WrapPosition(p0 + map.Delta(p0, p1) * frac);
                // Teleports (respawns) between snapshots are not interpolated, skip those.
                if (map.Distance(p0, p1) > 50f) continue;
                errors.Add(map.Distance(truth, s.Position));
            }
        });

        errors.Sort();
        output.WriteLine($"samples={errors.Count} median={errors[errors.Count / 2]:F2}px p99={errors[(int)(errors.Count * 0.99)]:F2}px");
        Assert.True(errors.Count > 1000);
        Assert.True(errors[errors.Count / 2] < 1.5f, "remote ships should be drawn where the server had them");
        Assert.True(errors[(int)(errors.Count * 0.99)] < 12f);
    }

    [Theory]
    [InlineData(0.03, 0.0, 0.0)]
    [InlineData(0.08, 0.02, 0.05)]
    [InlineData(0.15, 0.03, 0.10)]
    public void Prediction_AgreesWithTheServer(double latency, double jitter, double loss)
    {
        var game = new SimulatedGame(Server(bots: 0), new SimulatedNetwork(seed: 4) { Latency = latency, Jitter = jitter, Loss = loss });
        var client = game.Join("Pilot", Weave);
        game.Run(1);

        var errors = new List<float>();
        int lastTick = -1;
        game.Run(10, () =>
        {
            if (client.LatestTick == lastTick) return;
            lastTick = client.LatestTick;
            if (client.PredictedShip.Alive) errors.Add(client.LastPredictionError);
        });

        errors.Sort();
        float median = errors[errors.Count / 2], p95 = errors[(int)(errors.Count * 0.95)];
        output.WriteLine($"latency={latency * 1000}ms loss={loss:P0}: snapshots={errors.Count} median={median:F3}px p95={p95:F2}px max={errors[^1]:F1}px");
        Assert.True(median < 0.5f, $"median prediction error {median:F2}px");
        Assert.True(p95 < 15f, $"95th percentile prediction error {p95:F2}px");
    }

    [Fact]
    public void PlayerInput_MovesTheShipOnTheServer()
    {
        var game = new SimulatedGame(Server(bots: 0), new SimulatedNetwork());
        var client = game.Join("Pilot", _ => new ShipInput { Thrust = true });
        game.Run(0.5);
        var ship = game.Server.Match.Humans.Single();
        var start = ship.Position;
        game.Run(0.5);
        Assert.True(Vector2.Distance(start, ship.Position) > 20f);
        Assert.True(Vector2.Distance(client.Player!.Position, ship.Position) < 60f, "the predicted ship should be just ahead of the server's");
    }

    [Fact]
    public void Bullets_FromOtherShips_ShowUpWhereTheServerHasThem()
    {
        var game = new SimulatedGame(Server(bots: 6), new SimulatedNetwork { Latency = 0.05 });
        var history = new Dictionary<(int Tick, int Bullet), Vector2>();
        game.AfterServerTick = () =>
        {
            var w = game.Server.Match.World;
            foreach (var b in w.Bullets) history[(w.Tick, b.Id)] = b.Position;
        };
        var client = game.Join("Watcher");

        int seen = 0;
        var errors = new List<float>();
        game.Run(20, () =>
        {
            if (!client.IsReady) return;
            var map = client.World.Map;
            int t0 = (int)Math.Floor(client.RenderTick);
            float frac = (float)(client.RenderTick - t0);
            foreach (var b in client.World.Bullets)
            {
                seen++;
                if (!history.TryGetValue((t0, b.Id), out var p0) || !history.TryGetValue((t0 + 1, b.Id), out var p1)) continue;
                errors.Add(map.Distance(map.WrapPosition(p0 + map.Delta(p0, p1) * frac), b.Position));
            }
        });
        output.WriteLine($"bullets drawn={seen} compared={errors.Count} max error={(errors.Count > 0 ? errors.Max() : 0):F2}px");
        Assert.True(errors.Count > 50);
        Assert.True(errors.Max() < 1f);
    }

    [Fact]
    public void Events_ReachTheClient()
    {
        var game = new SimulatedGame(Server(bots: 6), new SimulatedNetwork { Latency = 0.05, Loss = 0.1 });
        var client = game.Join("Watcher");
        int serverKills = 0, clientKills = 0;
        game.AfterServerTick = () => serverKills += game.Server.Match.World.Events.Count(e => e.Type == GameEventType.ShipDestroyed);
        game.Run(1);
        serverKills = 0;
        client.TakeEvents();
        game.Run(30, () => clientKills += client.TakeEvents().Count(e => e.Type == GameEventType.ShipDestroyed));
        game.Run(1, () => clientKills += client.TakeEvents().Count(e => e.Type == GameEventType.ShipDestroyed));
        output.WriteLine($"server deaths={serverKills} client saw={clientKills}");
        Assert.True(serverKills > 0);
        Assert.InRange(clientKills, serverKills - 2, serverKills);
    }

    [Fact]
    public void JoiningAndLeaving_TradesSeatsWithBots()
    {
        var game = new SimulatedGame(Server(bots: 8), new SimulatedNetwork());
        var a = game.Join("A");
        var b = game.Join("B");
        game.Run(1);
        var match = game.Server.Match;
        Assert.Equal(2, match.Humans.Count);
        Assert.Equal(6, match.Bots.Count);
        Assert.Equal(8, a.World.Ships.Count);

        game.Leave(b);
        game.Run(1);
        Assert.Single(match.Humans);
        Assert.Equal(7, match.Bots.Count);
        Assert.Equal(8, a.World.Ships.Count);
        Assert.DoesNotContain(a.World.Ships, s => s.Name == "B");
    }

    [Fact]
    public void FullServer_LetsLatecomersSpectate()
    {
        var game = new SimulatedGame(Server("oval", bots: 0), new SimulatedNetwork());
        int capacity = game.Server.Match.Capacity;
        for (int i = 0; i < capacity; i++) game.Join($"P{i}");
        var late = game.Join("Late");
        game.Run(1);
        Assert.True(late.IsSpectating);
        Assert.Null(late.Player);
        Assert.True(late.IsReady);
    }

    [Fact]
    public void DuplicateNames_AreMadeUnique()
    {
        var game = new SimulatedGame(Server(bots: 0), new SimulatedNetwork());
        var a = game.Join("Ace");
        var b = game.Join("Ace");
        game.Run(1);
        Assert.Equal("Ace", a.Player!.Name);
        Assert.Equal("Ace2", b.Player!.Name);
    }

    [Fact]
    public void FinishedMatch_MovesToTheNextMap()
    {
        var options = new ServerOptions { TimeLimit = 2f, IntermissionSeconds = 1f, BotCount = 2 };
        var server = new GameServer(options, [MapText("arena"), MapText("caverns")]);
        var game = new SimulatedGame(server, new SimulatedNetwork());
        var client = game.Join("A");
        game.Run(2.5);
        Assert.True(client.World.Rules.IsOver);
        Assert.True(client.Intermission > 0f);
        game.Run(1.5);
        Assert.Equal(2, server.MatchId);
        Assert.Equal(2, client.MatchCount);
        Assert.Equal("Caverns", client.World.Map.Name);
        Assert.False(client.World.Rules.IsOver);
        Assert.NotNull(client.Player);
    }

    [Fact]
    public void BallMode_TeamSwitch_AndScoresReachClients()
    {
        var game = new SimulatedGame(Server("bastions", bots: 4), new SimulatedNetwork());
        var a = game.Join("A");
        game.Run(1);
        int before = a.Player!.Team;
        a.RequestTeamSwitch();
        game.Run(1);
        Assert.Equal(Teams.Opponent(before), a.Player!.Team);
        Assert.Equal(Teams.Opponent(before), game.Server.Match.Humans[0].Team);
        Assert.Equal(2, a.World.Balls.Count);
    }

    [Fact]
    public void Chat_IsBroadcastWithTheSendersName()
    {
        var game = new SimulatedGame(Server(bots: 0), new SimulatedNetwork());
        var a = game.Join("Alice");
        var b = game.Join("Bob");
        game.Run(0.5);
        b.TakeChat();
        a.SendChat("  hello there  ");
        game.Run(0.5);
        var lines = b.TakeChat();
        Assert.Contains(new ChatLine("Alice", Teams.None, "hello there"), lines);
    }

    [Fact]
    public void RaceMode_WorksOverTheNetwork()
    {
        var game = new SimulatedGame(Server("oval", bots: 3), new SimulatedNetwork { Latency = 0.05 });
        var client = game.Join("Racer");
        var events = new List<GameEvent>();
        game.Run(6, () => events.AddRange(client.TakeEvents()));
        Assert.Contains(events, e => e.Type == GameEventType.CheckpointPassed);
        Assert.False(client.World.Rules.ControlsLocked);
    }
}

internal static class TestMaps
{
    public static string Directory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.Directory.Exists(Path.Combine(dir.FullName, "maps"))) dir = dir.Parent;
            return dir == null ? throw new DirectoryNotFoundException("maps folder not found") : Path.Combine(dir.FullName, "maps");
        }
    }
}

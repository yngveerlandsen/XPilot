using System.Diagnostics;
using System.Net;
using XPilot.Core.Simulation;

namespace XPilot.Net.Tests;

/// <summary>End to end over real UDP sockets on the loopback interface.</summary>
public class UdpTests
{
    private static GameServer NewServer(string? master = null) =>
        new(new ServerOptions { Port = 0, BotCount = 2, Name = "Loopback", MasterServer = master },
            [File.ReadAllText(Path.Combine(TestMaps.Directory, "arena.xpm"))]);

    private static bool PollUntil(Func<bool> done, Action poll, double seconds = 5)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            poll();
            if (done()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    [Fact]
    public void TwoClients_ConnectAndPlay()
    {
        using var host = new ServerHost(NewServer());
        Assert.True(host.Start());

        using var a = new ClientConnection("Alice");
        using var b = new ClientConnection("Bob");
        a.Connect($"127.0.0.1:{host.Port}");
        b.Connect($"127.0.0.1:{host.Port}");

        var clock = Stopwatch.StartNew();
        void Poll()
        {
            a.Poll();
            b.Poll();
            a.Client.Update(0.005f, new ShipInput { Thrust = true });
            b.Client.Update(0.005f, default);
        }
        Assert.True(PollUntil(() => a.Client.IsReady && b.Client.IsReady && a.Client.World.Ships.Count == 4, Poll), a.Error ?? b.Error);
        Assert.Equal(ConnectionStatus.Connected, a.Status);
        Assert.Equal("Alice", a.Client.Player!.Name);
        Assert.Contains(b.Client.World.Ships, s => s.Name == "Alice");

        // Alice thrusts; Bob should see her ship move.
        var aliceOnBob = b.Client.World.Ships.First(s => s.Name == "Alice");
        var start = aliceOnBob.Position;
        Assert.True(PollUntil(() => Vector2Distance(start, aliceOnBob.Position) > 30f, Poll));
    }

    private static float Vector2Distance(System.Numerics.Vector2 a, System.Numerics.Vector2 b) => System.Numerics.Vector2.Distance(a, b);

    [Fact]
    public void WrongVersionOrKey_IsRejected()
    {
        using var host = new ServerHost(NewServer());
        Assert.True(host.Start());
        var listener = new LiteNetLib.EventBasedNetListener();
        var net = new LiteNetLib.NetManager(listener);
        net.Start();
        bool disconnected = false;
        listener.PeerDisconnectedEvent += (_, _) => disconnected = true;
        net.Connect("127.0.0.1", host.Port, "not-xpilot");
        Assert.True(PollUntil(() => disconnected, net.PollEvents));
        Assert.Equal(0, host.Server.ClientCount);
        net.Stop();
    }

    [Fact]
    public void MasterServer_ListsServers_AndIntroducesClients()
    {
        using var master = new MasterServer();
        Assert.True(master.Start(0));
        int masterPort = master.Port;

        using var host = new ServerHost(NewServer($"127.0.0.1:{masterPort}"));
        Assert.True(host.Start());

        using var browser = new ServerBrowser($"127.0.0.1:{masterPort}");
        Assert.True(PollUntil(() => browser.Servers.Any(s => !s.OnLan && s.Info.Name == "Loopback"), () =>
        {
            master.Poll();
            browser.Poll();
        }));
        var entry = browser.Servers.First(s => s.Info.Name == "Loopback" && !s.OnLan);
        Assert.Equal(host.Port, entry.EndPoint.Port);

        using var client = new ClientConnection("Remote");
        client.ConnectViaMaster(browser.Master!, entry.ServerId!, entry.EndPoint);
        Assert.True(PollUntil(() => client.Client.IsReady, () =>
        {
            master.Poll();
            client.Poll();
            client.Client.Update(0.005f, default);
        }), client.Error);
    }

    [Fact]
    public void ServerHost_ReportsBusyPort()
    {
        using var first = new ServerHost(NewServer());
        Assert.True(first.Start());
        var options = new ServerOptions { Port = first.Port };
        using var second = new ServerHost(new GameServer(options, [File.ReadAllText(Path.Combine(TestMaps.Directory, "arena.xpm"))]));
        Assert.False(second.Start());
    }

}

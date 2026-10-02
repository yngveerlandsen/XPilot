using System.Diagnostics;
using LiteNetLib;
using XPilot.Core.Simulation;

namespace XPilot.Net.Tests;

/// <summary>Bad or hostile traffic must be dropped, not crash anything or use up memory.</summary>
public class RobustnessTests
{
    private static string Arena => File.ReadAllText(Path.Combine(TestMaps.Directory, "arena.xpm"));

    private static GameClient ClientInMatch()
    {
        var client = new GameClient((_, _) => { });
        client.Receive(new MatchStartMessage { MatchId = 1, YourShipId = 0, MapText = Arena }.Encode());
        return client;
    }

    private static byte[] Message(MessageType type, Action<BinaryWriter> write)
    {
        var m = new MessageWriter(type);
        write(m.Writer);
        return m.ToArray();
    }

    [Fact]
    public void HugeEventCount_IsIgnored()
    {
        var client = ClientInMatch();
        client.Receive(Message(MessageType.Events, w =>
        {
            w.Write(1);
            w.Write(500_000_000);
        }));
        client.Receive(Message(MessageType.Events, w =>
        {
            w.Write(1);
            w.Write(-5);
        }));
    }

    [Fact]
    public void NegativeOrOversizedCounts_AreIgnored()
    {
        var client = ClientInMatch();
        client.Receive(Message(MessageType.Roster, w =>
        {
            w.Write(1);
            w.Write((short)-3);
        }));
        client.Receive(Message(MessageType.Snapshot, w =>
        {
            w.Write(1);
            w.Write(10);
            w.Write(0);
            w.Write(-1f);
            w.Write((short)-1);
        }));
        client.Receive(Message(MessageType.Snapshot, w =>
        {
            w.Write(1);
            w.Write(10);
            w.Write(0);
            w.Write(-1f);
            w.Write((short)0);
            w.Write((byte)200);
        }));
        Assert.False(client.IsReady);
    }

    [Fact]
    public void CorruptMatchStart_IsIgnored()
    {
        var client = new GameClient((_, _) => { });
        client.Receive(Message(MessageType.MatchStart, w =>
        {
            w.Write(1);
            w.Write(0);
            w.Write("server");
            w.Write("not a map at all");
            w.Write("{ not json");
        }));
        Assert.False(client.HasMatch);
    }

    [Fact]
    public void Server_IgnoresGarbageFromClients()
    {
        var game = new SimulatedGame(new GameServer(new ServerOptions { BotCount = 0 }, [Arena]), new SimulatedNetwork());
        game.Join("A");
        game.Run(0.2);
        var connection = new FakeConnection(1);
        game.Server.Receive(connection, Message(MessageType.Input, w =>
        {
            w.Write(10);
            w.Write((byte)255);
        }));
        game.Server.Receive(connection, [(byte)MessageType.Chat, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F]);
        game.Run(0.2);
    }

    [Fact]
    public void Spectators_InputsAreNotQueued()
    {
        var server = new GameServer(new ServerOptions { BotCount = 0 }, [File.ReadAllText(Path.Combine(TestMaps.Directory, "oval.xpm"))]);
        for (int i = 0; i < server.Match.Capacity; i++) server.Connected(new FakeConnection(i), $"P{i}");
        var spectator = new FakeConnection(99);
        server.Connected(spectator, "Watcher");

        var inputs = Enumerable.Repeat(new ShipInput { Thrust = true }, 200).ToList();
        for (int seq = 200; seq < 20_000; seq += 200) server.Receive(spectator, InputMessage.Encode(seq, inputs));
        Assert.Equal(0, server.PendingInputs(spectator.Id));
        server.Tick();
    }

    [Fact]
    public void WireSizes_MatchTheDeclaredMinimums()
    {
        var ship = new MemoryStream();
        new ShipState().Write(new BinaryWriter(ship));
        Assert.Equal(ShipState.Size, ship.Length);
        var ball = new MemoryStream();
        new BallSnapshot().Write(new BinaryWriter(ball));
        Assert.Equal(BallSnapshot.Size, ball.Length);
        var removed = new MemoryStream();
        new NetEvent { Kind = NetEventKind.BulletRemoved }.Write(new BinaryWriter(removed));
        Assert.Equal(NetEvent.MinSize, removed.Length);
    }

    [Fact]
    public void MasterServer_LimitsRegistrationsPerAddress()
    {
        using var master = new MasterServer();
        Assert.True(master.Start(0));
        var sender = new NetManager(new EventBasedNetListener()) { UnconnectedMessagesEnabled = true };
        sender.Start();
        try
        {
            var target = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, master.Port);
            for (int i = 0; i < 50; i++)
            {
                var m = new MessageWriter(MessageType.MasterRegister);
                m.Writer.Write($"fake{i}");
                new ServerInfo($"Fake {i}", "Arena", "Dogfight", 0, 8, 15345).Write(m.Writer);
                sender.SendUnconnectedMessage(m.ToArray(), target);
            }
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 1)
            {
                master.Poll();
                Thread.Sleep(5);
            }
            Assert.Equal(4, master.ServerCount);
        }
        finally
        {
            sender.Stop();
        }
    }

    private sealed class FakeConnection(int id) : IConnection
    {
        public int Id => id;
        public int RoundTripMs => 0;
        public void Send(byte[] data, Delivery delivery)
        {
        }
    }
}

using System.Diagnostics;
using System.Net;
using LiteNetLib;
using XPilot.Core.Simulation;

namespace XPilot.Net.Tests;

public class ReviewFixTests
{
    private static string MapText(string name) => File.ReadAllText(Path.Combine(TestMaps.Directory, name + ".xpm"));

    [Fact]
    public void Snapshot_RoundTripsWithMoreThan255Ships()
    {
        var snap = new Snapshot();
        for (int i = 0; i < 300; i++) snap.Ships.Add(new ShipState { Id = i });
        var copy = Snapshot.Decode(MessageReader.Open(snap.Encode(), out _));
        Assert.Equal(300, copy.Ships.Count);
        Assert.Equal(299, copy.Ships[^1].Id);
    }

    [Fact]
    public void Snapshot_AckSeqCanBePatchedAfterEncoding()
    {
        var snap = new Snapshot { MatchId = 4, Tick = 1234, AckSeq = 1, Ships = [new ShipState { Id = 2, Fuel = 5 }] };
        var patched = Snapshot.WithAckSeq(snap.Encode(), 98765);
        var copy = Snapshot.Decode(MessageReader.Open(patched, out _));
        Assert.Equal(98765, copy.AckSeq);
        Assert.Equal(1234, copy.Tick);
        Assert.Equal(5, copy.Ships[0].Fuel);
    }

    [Fact]
    public void ServerInfo_TextIsClippedWhenWritten_AndRejectedWhenTooLongOnArrival()
    {
        var info = new ServerInfo(new string('n', 500), "Map", "Dogfight", 1, 8, 15345);
        var stream = new MemoryStream();
        info.Write(new BinaryWriter(stream));
        stream.Position = 0;
        Assert.Equal(ServerInfo.MaxTextLength, ServerInfo.Read(new BinaryReader(stream))!.Name.Length);

        var hostile = new MemoryStream();
        var w = new BinaryWriter(hostile);
        w.Write(Protocol.Version);
        w.Write(new string('x', 5000));
        hostile.Position = 0;
        Assert.Throws<InvalidDataException>(() => ServerInfo.Read(new BinaryReader(hostile)));
    }

    [Fact]
    public void Spectator_TakesTheSeatWhenAPlayerLeaves()
    {
        var game = new SimulatedGame(new GameServer(new ServerOptions { BotCount = 0 }, [MapText("oval")]), new SimulatedNetwork());
        var players = Enumerable.Range(0, game.Server.Match.Capacity).Select(i => game.Join($"P{i}")).ToList();
        var waiting = game.Join("Waiting", _ => new ShipInput { Thrust = true });
        game.Run(1);
        Assert.True(waiting.IsSpectating);

        game.Leave(players[0]);
        game.Run(1.5);
        Assert.False(waiting.IsSpectating);
        Assert.NotNull(waiting.Player);
        Assert.Contains(game.Server.Match.Humans, s => s.Name == "Waiting");
        Assert.Contains(players[1].World.Ships, s => s.Name == "Waiting");
    }

    [Fact]
    public void MasterServer_AnswersListRequestsAtMostOncePerInterval()
    {
        using var master = new MasterServer();
        Assert.True(master.Start(0));
        var listener = new EventBasedNetListener();
        var client = new NetManager(listener) { UnconnectedMessagesEnabled = true };
        int replies = 0;
        listener.NetworkReceiveUnconnectedEvent += (_, _, _) => replies++;
        client.Start();
        try
        {
            var target = new IPEndPoint(IPAddress.Loopback, master.Port);
            for (int i = 0; i < 10; i++) client.SendUnconnectedMessage(new MessageWriter(MessageType.MasterListRequest).ToArray(), target);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 1)
            {
                master.Poll();
                client.PollEvents();
                Thread.Sleep(5);
            }
            Assert.Equal(1, replies);
        }
        finally
        {
            client.Stop();
        }
    }
}

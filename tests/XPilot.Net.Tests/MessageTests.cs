using System.Numerics;
using XPilot.Core;
using XPilot.Core.Simulation;

namespace XPilot.Net.Tests;

public class MessageTests
{
    [Fact]
    public void Snapshot_RoundTrips()
    {
        var snap = new Snapshot
        {
            MatchId = 3,
            Tick = 12345,
            AckSeq = 678,
            Intermission = 4.5f,
            RulesState = [1, 2, 3],
            Ships =
            [
                new ShipState
                {
                    Id = 7, Team = 1, Alive = true, Shield = true, GrabHeld = true, Position = new Vector2(10.5f, 20.25f),
                    Velocity = new Vector2(-3, 4), Heading = 1.25f, Fuel = 512.5f, Kills = 4, Deaths = 2, Score = -1,
                    NextCheckpoint = 3, LastCheckpoint = -1, Lap = 2, Place = 1, LapStartTime = 33.3f, FinishTime = 99f,
                },
            ],
            Balls = [new BallSnapshot { Team = 0, State = BallState.Carried, CarrierId = 7, Position = new Vector2(1, 2), LooseTime = 3 }],
        };
        var copy = Snapshot.Decode(MessageReader.Open(snap.Encode(), out var type));
        Assert.Equal(MessageType.Snapshot, type);
        Assert.Equal(snap.Tick, copy.Tick);
        Assert.Equal(snap.AckSeq, copy.AckSeq);
        Assert.Equal(snap.Intermission, copy.Intermission);
        Assert.Equal(snap.RulesState, copy.RulesState);
        Assert.Equal(snap.Ships[0], copy.Ships[0]);
        Assert.Equal(snap.Balls[0], copy.Balls[0]);
    }

    [Fact]
    public void FullSnapshot_FitsInOnePacket()
    {
        var snap = new Snapshot { RulesState = new byte[16] };
        for (int i = 0; i < 16; i++) snap.Ships.Add(new ShipState { Id = i });
        snap.Balls.Add(default);
        snap.Balls.Add(default);
        Assert.True(snap.Encode().Length < Protocol.Mtu - 100, $"{snap.Encode().Length} bytes");
    }

    [Fact]
    public void Inputs_RoundTripExactlyAfterQuantizing()
    {
        var inputs = new List<ShipInput>
        {
            InputMessage.Quantize(new ShipInput { Turn = 0.333f, Thrust = true }),
            InputMessage.Quantize(new ShipInput { Turn = -1f, Fire = true, Shield = true, Grab = true }),
        };
        var (first, decoded) = InputMessage.Decode(MessageReader.Open(InputMessage.Encode(41, inputs), out _));
        Assert.Equal(40, first);
        Assert.Equal(inputs, decoded);
    }

    [Fact]
    public void MatchStart_CarriesConfigAndLimits()
    {
        var msg = new MatchStartMessage
        {
            MatchId = 2, YourShipId = 5, ServerName = "Test", MapText = "name: X\n---\n_.",
            Config = new GameConfig { ShipCollisionsKill = false, MaxSpeed = 700 }, ScoreLimit = 15, TimeLimit = null, Laps = 4,
        };
        var copy = MatchStartMessage.Decode(MessageReader.Open(msg.Encode(), out _));
        Assert.Equal(5, copy.YourShipId);
        Assert.Equal(msg.MapText, copy.MapText);
        Assert.False(copy.Config.ShipCollisionsKill);
        Assert.Equal(700, copy.Config.MaxSpeed);
        Assert.Null(copy.TimeLimit);
        Assert.Equal(4, copy.Laps);
    }

    [Theory]
    [InlineData("  Ace  ", "Ace")]
    [InlineData("", "Pilot")]
    [InlineData("A very long pilot name indeed", "A very long pilo")]
    [InlineData("tab\there", "tabhere")]
    public void Names_AreCleaned(string input, string expected) => Assert.Equal(expected, Protocol.CleanName(input));
}

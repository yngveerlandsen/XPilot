using XPilot.Core;
using XPilot.Core.Simulation;

namespace XPilot.Net.Tests;

/// <summary>
/// An in-memory network with one-way latency, jitter and packet loss. Unreliable packets may be lost or
/// reordered; reliable ones are never lost but a "lost" one arrives a round trip late (a resend), and it
/// holds up everything behind it, like an ordered reliable channel.
/// </summary>
internal sealed class SimulatedNetwork(int seed = 1)
{
    private readonly Random _rng = new(seed);
    private readonly PriorityQueue<Action, (double, long)> _queue = new();
    private readonly Dictionary<object, double> _lastReliable = [];
    private long _order;

    public double Time { get; private set; }
    public double Latency { get; set; } = 0.05;
    public double Jitter { get; set; }
    public double Loss { get; set; }

    public void Send(object link, Delivery delivery, Action deliver)
    {
        double at = Time + Latency + _rng.NextDouble() * Jitter;
        bool lost = _rng.NextDouble() < Loss;
        if (delivery == Delivery.Unreliable)
        {
            if (lost) return;
        }
        else
        {
            if (lost) at += 2 * Latency + 0.02;
            at = Math.Max(at, _lastReliable.GetValueOrDefault(link));
            _lastReliable[link] = at;
        }
        _queue.Enqueue(deliver, (at, _order++));
    }

    public void Advance(double dt)
    {
        Time += dt;
        while (_queue.TryPeek(out _, out var key) && key.Item1 <= Time) _queue.Dequeue()();
    }
}

/// <summary>A server and clients joined by a <see cref="SimulatedNetwork"/>, driven at a fixed frame rate.</summary>
internal sealed class SimulatedGame
{
    private sealed class Connection(SimulatedGame game, int id) : IConnection
    {
        public int Id => id;
        public int RoundTripMs => (int)(game.Network.Latency * 2000);
        public GameClient? Client;
        public readonly object Down = new(), Up = new();

        public void Send(byte[] data, Delivery delivery) => game.Network.Send(Down, delivery, () => Client!.Receive(data));
    }

    private readonly List<(Connection Connection, Func<int, ShipInput> Input)> _clients = [];
    private double _serverClock;
    private int _frame;

    public SimulatedGame(GameServer server, SimulatedNetwork network)
    {
        Server = server;
        Network = network;
    }

    public GameServer Server { get; }
    public SimulatedNetwork Network { get; }
    public double FrameTime { get; set; } = 1.0 / 120;
    /// <summary>Called after every server tick, e.g. to record history.</summary>
    public Action? AfterServerTick { get; set; }

    public GameClient Join(string name, Func<int, ShipInput>? input = null)
    {
        var connection = new Connection(this, _clients.Count + 1);
        var client = new GameClient((data, delivery) =>
            Network.Send(connection.Up, delivery, () => Server.Receive(connection, data)));
        connection.Client = client;
        _clients.Add((connection, input ?? (_ => default)));
        // The connection handshake itself takes a round trip.
        Network.Send(connection.Up, Delivery.Reliable, () => Server.Connected(connection, name));
        return client;
    }

    public void Leave(GameClient client)
    {
        var entry = _clients.First(c => c.Connection.Client == client);
        _clients.Remove(entry);
        Network.Send(entry.Connection.Up, Delivery.Reliable, () => Server.Disconnected(entry.Connection));
    }

    public void Run(double seconds, Action? afterFrame = null)
    {
        int frames = (int)Math.Round(seconds / FrameTime);
        for (int i = 0; i < frames; i++)
        {
            Network.Advance(FrameTime);
            _serverClock += FrameTime;
            while (_serverClock >= GameConfig.Dt)
            {
                _serverClock -= GameConfig.Dt;
                Server.Tick();
                AfterServerTick?.Invoke();
            }
            _frame++;
            foreach (var (connection, input) in _clients) connection.Client!.Update((float)FrameTime, input(_frame));
            afterFrame?.Invoke();
        }
    }
}

using XPilot.Core;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Net;

public sealed class ServerOptions
{
    public string Name { get; set; } = "XPilot";
    public int Port { get; set; } = Protocol.DefaultPort;
    public int BotCount { get; set; } = 3;
    public BotDifficulty Difficulty { get; set; } = BotDifficulty.Normal;
    public int ScoreLimit { get; set; } = 10;
    public int CaptureLimit { get; set; } = 3;
    public float? TimeLimit { get; set; }
    public int? Laps { get; set; }
    public float IntermissionSeconds { get; set; } = Protocol.IntermissionSeconds;
    /// <summary>"host:port" of a master server to register with, or null to stay off the internet list.</summary>
    public string? MasterServer { get; set; }
}

/// <summary>
/// The authoritative game. Feed it connections and messages from any transport and call <see cref="Tick"/>
/// 60 times a second. It applies each client's inputs in order, steps the match, and sends events reliably
/// and snapshots unreliably. When a match ends it shows results for a while, then moves to the next map.
/// </summary>
public sealed class GameServer
{
    private sealed class Client(IConnection connection, string name)
    {
        public IConnection Connection { get; } = connection;
        public string Name { get; set; } = name;
        public Ship? Ship;
        public readonly Queue<(int Seq, ShipInput Input)> Pending = new();
        /// <summary>The next sequence number not yet queued; older inputs are duplicates.</summary>
        public int NextSeq;
        public int AckSeq = -1;
        public ShipInput LastInput;
    }

    /// <summary>Queued inputs beyond this mean the client is running ahead; drop down to <see cref="InputQueueTarget"/>.</summary>
    private const int MaxInputQueue = 6;
    private const int InputQueueTarget = 2;

    private readonly ServerOptions _options;
    private readonly List<string> _mapTexts;
    private readonly Dictionary<int, Client> _clients = [];
    private readonly List<NetEvent> _pendingEvents = [];
    private HashSet<int> _knownBullets = [], _currentBullets = [];
    private readonly Random _rng = new();
    private int _mapIndex;
    private int _matchId;
    private float _intermission = -1f;
    private string _rosterSignature = "";

    /// <param name="mapTexts">The map rotation, as map file contents.</param>
    public GameServer(ServerOptions options, IEnumerable<string> mapTexts, int firstMap = 0)
    {
        _options = options;
        _mapTexts = mapTexts.ToList();
        if (_mapTexts.Count == 0) throw new ArgumentException("The server needs at least one map.", nameof(mapTexts));
        _mapIndex = Math.Clamp(firstMap, 0, _mapTexts.Count - 1);
        Match = StartMatch();
    }

    public Match Match { get; private set; }
    public int MatchId => _matchId;
    public int ClientCount => _clients.Count;
    public ServerOptions Options => _options;

    /// <summary>Server log lines: joins, leaves, chat and match changes.</summary>
    public event Action<string>? Log;

    /// <summary>Inputs waiting to be applied for a connection, for tests.</summary>
    internal int PendingInputs(int connectionId) => _clients.TryGetValue(connectionId, out var c) ? c.Pending.Count : 0;

    public ServerInfo Info => new(_options.Name, Match.World.Map.Name, Match.World.Map.Mode.ToString(),
        Match.Humans.Count, Match.Capacity, _options.Port);

    /// <summary>A client finished connecting. They join the match, or spectate if every seat has a human.</summary>
    public void Connected(IConnection connection, string requestedName)
    {
        var name = UniqueName(Protocol.CleanName(requestedName));
        var client = new Client(connection, name);
        _clients[connection.Id] = client;
        client.Ship = Match.AddHuman(name);
        SendMatchStart(client);
        connection.Send(RosterMessage.Encode(_matchId, Match.World.Ships), Delivery.Reliable);
        SendBulletsInFlight(client);
        Announce(client.Ship == null ? $"{name} is spectating (server full)" : $"{name} joined");
    }

    public void Disconnected(IConnection connection)
    {
        if (!_clients.Remove(connection.Id, out var client)) return;
        if (client.Ship != null) Match.RemoveHuman(client.Ship);
        Announce($"{client.Name} left");
    }

    public void Receive(IConnection connection, byte[] data)
    {
        if (!_clients.TryGetValue(connection.Id, out var client)) return;
        try
        {
            var reader = MessageReader.Open(data, out var type);
            switch (type)
            {
                case MessageType.Input when client.Ship != null:
                    QueueInputs(client, reader);
                    break;
                case MessageType.Chat:
                    var text = Protocol.CleanChat(reader.ReadString());
                    if (text.Length == 0) break;
                    Broadcast(ChatMessage.Encode(client.Name, client.Ship?.Team ?? Teams.None, text), Delivery.Reliable);
                    Log?.Invoke($"<{client.Name}> {text}");
                    break;
                case MessageType.SwitchTeam when client.Ship is { } ship && Match.World.Map.Mode == GameModeKind.Ball:
                    Match.SwitchTeam(ship);
                    Announce($"{client.Name} switched to {Teams.Name(ship.Team)}");
                    break;
            }
        }
        catch (Exception ex) when (MessageReader.IsMalformed(ex))
        {
            // A malformed packet: ignore it rather than let one client take the server down.
        }
    }

    private static void QueueInputs(Client client, BinaryReader reader)
    {
        var (firstSeq, inputs) = InputMessage.Decode(reader);
        for (int i = 0; i < inputs.Count; i++)
        {
            int seq = firstSeq + i;
            if (seq < client.NextSeq) continue;
            client.Pending.Enqueue((seq, inputs[i]));
            client.NextSeq = seq + 1;
        }
    }

    /// <summary>Advances the game by one tick of <see cref="GameConfig.Dt"/> and sends what changed.</summary>
    public void Tick()
    {
        if (_intermission >= 0f)
        {
            _intermission -= GameConfig.Dt;
            if (_intermission <= 0f)
            {
                _mapIndex = (_mapIndex + 1) % _mapTexts.Count;
                Match = StartMatch();
            }
        }

        foreach (var client in _clients.Values)
        {
            if (client.Ship == null) continue;
            if (client.Pending.Count > MaxInputQueue)
            {
                while (client.Pending.Count > InputQueueTarget) client.AckSeq = client.Pending.Dequeue().Seq;
            }
            if (client.Pending.TryDequeue(out var next))
            {
                client.AckSeq = next.Seq;
                client.LastInput = next.Input;
            }
            Match.SetInput(client.Ship, client.LastInput);
        }

        Match.Step();
        CollectEvents();
        if (Match.World.Rules.IsOver && _intermission < 0f) _intermission = _options.IntermissionSeconds;
        SendRosterIfChanged();
        if (Match.World.Tick % Protocol.SnapshotInterval == 0) SendUpdates();
    }

    private Match StartMatch()
    {
        var map = MapLoader.Parse(_mapTexts[_mapIndex]);
        _matchId++;
        var match = new Match(new MatchSetup
        {
            Map = map,
            IncludePlayer = false,
            BotCount = _options.BotCount,
            Difficulty = _options.Difficulty,
            ScoreLimit = _options.ScoreLimit,
            CaptureLimit = _options.CaptureLimit,
            TimeLimit = _options.TimeLimit,
            Laps = _options.Laps,
            Seed = _rng.Next(),
        });
        Match = match;
        _intermission = -1f;
        _pendingEvents.Clear();
        _knownBullets.Clear();
        _rosterSignature = "";

        foreach (var client in _clients.Values)
        {
            client.Ship = match.AddHuman(client.Name);
            client.Pending.Clear();
            client.LastInput = default;
            SendMatchStart(client);
        }
        Log?.Invoke($"Match {_matchId}: {map.Name} ({map.Mode})");
        return match;
    }

    private void SendMatchStart(Client client)
    {
        var setup = Match.Setup;
        var msg = new MatchStartMessage
        {
            MatchId = _matchId,
            YourShipId = client.Ship?.Id ?? -1,
            ServerName = _options.Name,
            MapText = _mapTexts[_mapIndex],
            Config = Match.World.Config,
            ScoreLimit = setup.ScoreLimit,
            CaptureLimit = setup.CaptureLimit,
            TimeLimit = setup.TimeLimit,
            Laps = setup.Laps,
        };
        client.Connection.Send(msg.Encode(), Delivery.Reliable);
    }

    /// <summary>A newcomer only hears about bullets fired after they joined, so tell them about the rest.</summary>
    private void SendBulletsInFlight(Client client)
    {
        var world = Match.World;
        if (world.Bullets.Count == 0) return;
        var events = world.Bullets.Select(b => new NetEvent
        {
            Tick = world.Tick,
            Kind = NetEventKind.BulletSpawned,
            BulletId = b.Id,
            BulletOwner = b.OwnerId,
            BulletPosition = b.Position,
            BulletVelocity = b.Velocity,
        }).ToList();
        client.Connection.Send(EventsMessage.Encode(_matchId, events), Delivery.Reliable);
    }

    private void CollectEvents()
    {
        var world = Match.World;
        int tick = world.Tick;
        foreach (var e in world.Events) _pendingEvents.Add(new NetEvent { Tick = tick, Kind = NetEventKind.Game, Game = e });

        _currentBullets.Clear();
        foreach (var b in world.Bullets)
        {
            _currentBullets.Add(b.Id);
            if (_knownBullets.Contains(b.Id)) continue;
            _pendingEvents.Add(new NetEvent
            {
                Tick = tick,
                Kind = NetEventKind.BulletSpawned,
                BulletId = b.Id,
                BulletOwner = b.OwnerId,
                BulletPosition = b.Position,
                BulletVelocity = b.Velocity,
            });
        }
        foreach (int id in _knownBullets)
        {
            if (!_currentBullets.Contains(id)) _pendingEvents.Add(new NetEvent { Tick = tick, Kind = NetEventKind.BulletRemoved, BulletId = id });
        }
        (_knownBullets, _currentBullets) = (_currentBullets, _knownBullets);
    }

    private void SendRosterIfChanged()
    {
        var signature = string.Join(";", Match.World.Ships.Select(s => $"{s.Id}:{s.Name}:{s.IsBot}:{s.Team}"));
        if (signature == _rosterSignature) return;
        _rosterSignature = signature;
        Broadcast(RosterMessage.Encode(_matchId, Match.World.Ships), Delivery.Reliable);
    }

    private void SendUpdates()
    {
        if (_pendingEvents.Count > 0)
        {
            Broadcast(EventsMessage.Encode(_matchId, _pendingEvents), Delivery.Reliable);
            _pendingEvents.Clear();
        }
        if (_clients.Count == 0) return;

        var world = Match.World;
        var rulesStream = new MemoryStream();
        world.Rules.WriteState(new BinaryWriter(rulesStream));
        var snapshot = new Snapshot
        {
            MatchId = _matchId,
            Tick = world.Tick,
            Intermission = _intermission,
            RulesState = rulesStream.ToArray(),
            Ships = world.Ships.Select(ShipState.From).ToList(),
            Balls = world.Balls.Select(BallSnapshot.From).ToList(),
        };
        foreach (var client in _clients.Values)
        {
            snapshot.AckSeq = client.AckSeq;
            client.Connection.Send(snapshot.Encode(), Delivery.Unreliable);
        }
    }

    private void Announce(string text)
    {
        Broadcast(ChatMessage.Encode("", Teams.None, text), Delivery.Reliable);
        Log?.Invoke(text);
    }

    private void Broadcast(byte[] data, Delivery delivery)
    {
        foreach (var client in _clients.Values) client.Connection.Send(data, delivery);
    }

    private string UniqueName(string name)
    {
        var taken = _clients.Values.Select(c => c.Name).Concat(Match.World.Ships.Select(s => s.Name)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
        {
            var candidate = $"{name[..Math.Min(name.Length, Protocol.MaxNameLength - 2)]}{i}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }
}

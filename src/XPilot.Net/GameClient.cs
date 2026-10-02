using System.Numerics;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Net;

/// <summary>
/// The client side of a network game, independent of the transport. It mirrors the server's world for the
/// renderer: other ships are drawn <see cref="InterpolationDelayTicks"/> in the past, smoothly between two
/// snapshots, while the local ship is predicted from the player's own inputs and corrected whenever a
/// snapshot shows where the server really had it.
/// </summary>
public sealed class GameClient : IMatchView
{
    /// <summary>100 ms: enough to always have a snapshot on each side with one or two lost.</summary>
    public const int InterpolationDelayTicks = 6;
    /// <summary>Prediction errors larger than this snap instead of being smoothed out.</summary>
    private const float MaxSmoothedError = 80f;

    private sealed class BulletTrack
    {
        public int SpawnTick;
        public Vector2 Position, Velocity;
        public int Owner;
        public int RemoveTick = int.MaxValue;
    }

    private readonly Action<byte[], Delivery> _send;
    private readonly List<Snapshot> _snapshots = [];
    private readonly Dictionary<int, BulletTrack> _bullets = [];
    private readonly List<Bullet> _bulletPool = [];
    private readonly List<NetEvent> _queuedEvents = [];
    private readonly List<GameEvent> _readyEvents = [];
    private readonly List<ChatLine> _chat = [];
    private readonly List<(int Seq, ShipInput Input)> _unacked = [];

    private World? _world;
    private int _localId = -1;
    private Ship _predicted = new(-1, "", false, 0);
    private Ball? _predictedBall;
    private bool _hasPrediction;
    private Vector2 _correction;
    private int _seq;
    private float _accumulator;
    private double _renderTick = -1;
    private int _latestTick = -1;

    public GameClient(Action<byte[], Delivery> send) => _send = send;

    /// <summary>The mirrored world. Only valid once <see cref="HasMatch"/> is true.</summary>
    public World World => _world ?? throw new InvalidOperationException("No match yet.");
    public bool HasMatch => _world != null;
    /// <summary>True once a snapshot has arrived, so there is something to draw.</summary>
    public bool IsReady => _world != null && _latestTick >= 0;
    public Ship? Player { get; private set; }
    public int MatchId { get; private set; }
    /// <summary>Counts match starts, so a screen can notice a new map.</summary>
    public int MatchCount { get; private set; }
    public string ServerName { get; private set; } = "";
    /// <summary>Seconds until the next match, or negative while one is running.</summary>
    public float Intermission { get; private set; } = -1f;
    public bool IsSpectating => _world != null && _localId < 0;
    public int RoundTripMs { get; set; }
    /// <summary>The server tick being drawn for other ships.</summary>
    public double RenderTick => _renderTick;
    public int LatestTick => _latestTick;
    public int UnackedInputs => _unacked.Count;

    /// <summary>The predicted local ship, before display smoothing. For tests.</summary>
    public Ship PredictedShip => _predicted;
    /// <summary>How far the last snapshot moved the predicted ship, in pixels; 0 when prediction was right.</summary>
    public float LastPredictionError { get; private set; }

    public void Receive(byte[] data)
    {
        try
        {
            var reader = MessageReader.Open(data, out var type);
            switch (type)
            {
                case MessageType.MatchStart:
                    StartMatch(MatchStartMessage.Decode(reader));
                    break;
                case MessageType.Roster:
                    var (rosterMatch, entries) = RosterMessage.Decode(reader);
                    if (rosterMatch == MatchId) ApplyRoster(entries);
                    break;
                case MessageType.Snapshot:
                    OnSnapshot(Snapshot.Decode(reader));
                    break;
                case MessageType.Events:
                    var (eventsMatch, events) = EventsMessage.Decode(reader);
                    if (eventsMatch == MatchId) OnEvents(events);
                    break;
                case MessageType.ChatMessage:
                    _chat.Add(ChatMessage.Decode(reader));
                    break;
            }
        }
        catch (EndOfStreamException)
        {
        }
    }

    public void SendChat(string text)
    {
        text = Protocol.CleanChat(text);
        if (text.Length == 0) return;
        var m = new MessageWriter(MessageType.Chat);
        m.Writer.Write(text);
        _send(m.ToArray(), Delivery.Reliable);
    }

    public void RequestTeamSwitch() => _send(new MessageWriter(MessageType.SwitchTeam).ToArray(), Delivery.Reliable);

    /// <summary>Game events that are due, in the order they should be shown.</summary>
    public List<GameEvent> TakeEvents()
    {
        var list = _readyEvents.ToList();
        _readyEvents.Clear();
        return list;
    }

    public List<ChatLine> TakeChat()
    {
        var list = _chat.ToList();
        _chat.Clear();
        return list;
    }

    /// <summary>Runs input ticks at 60 Hz (sending and predicting each one), then rebuilds the view.</summary>
    public void Update(float dt, ShipInput input)
    {
        if (_world == null) return;
        input = InputMessage.Quantize(input);
        _accumulator += MathF.Min(dt, 0.25f);
        while (_accumulator >= GameConfig.Dt)
        {
            _accumulator -= GameConfig.Dt;
            if (_localId >= 0) InputTick(input);
        }
        AdvanceRenderClock(dt);
        if (_latestTick < 0) return;
        BuildView(dt);
        DispatchEvents();
    }

    private void InputTick(ShipInput input)
    {
        _unacked.Add((++_seq, input));
        // A server that stops acknowledging (e.g. between matches) must not make the list grow forever.
        if (_unacked.Count > 120) _unacked.RemoveAt(0);

        int count = Math.Min(Protocol.InputRedundancy, _unacked.Count);
        var recent = new List<ShipInput>(count);
        for (int i = _unacked.Count - count; i < _unacked.Count; i++) recent.Add(_unacked[i].Input);
        _send(InputMessage.Encode(_seq, recent), Delivery.Unreliable);

        if (!_hasPrediction || !_predicted.Alive) return;
        if (World.PredictShip(_predicted, input, _predictedBall))
        {
            var muzzle = _predicted.Position + MathUtil.FromAngle(_predicted.Heading) * (World.Config.ShipRadius + 3f);
            _readyEvents.Add(new GameEvent(GameEventType.ShipFired, _localId, Position: muzzle));
        }
    }

    private void StartMatch(MatchStartMessage msg)
    {
        var map = MapLoader.Parse(msg.MapText);
        var setup = new MatchSetup
        {
            Map = map,
            IncludePlayer = false,
            BotCount = 0,
            ScoreLimit = msg.ScoreLimit,
            CaptureLimit = msg.CaptureLimit,
            TimeLimit = msg.TimeLimit,
            Laps = msg.Laps,
        };
        var world = new World(map, msg.Config, Match.CreateRules(setup));
        world.Start();

        _world = world;
        MatchId = msg.MatchId;
        MatchCount++;
        ServerName = msg.ServerName;
        _localId = msg.YourShipId;
        Player = null;
        _predicted = new Ship(_localId, "", false, Math.Max(0, _localId));
        _predictedBall = null;
        _hasPrediction = false;
        _correction = Vector2.Zero;
        _snapshots.Clear();
        _bullets.Clear();
        _queuedEvents.Clear();
        _readyEvents.Clear();
        _unacked.Clear();
        _latestTick = -1;
        _renderTick = -1;
        Intermission = -1f;
    }

    private void ApplyRoster(List<RosterEntry> entries)
    {
        var world = World;
        var ids = entries.Select(e => e.Id).ToHashSet();
        foreach (var ship in world.Ships.ToList())
        {
            if (!ids.Contains(ship.Id)) world.RemoveShip(ship);
        }
        foreach (var e in entries)
        {
            var ship = EnsureShip(e.Id);
            ship.Name = e.Name;
            ship.IsBot = e.IsBot;
            ship.Team = e.Team;
        }
        Player = world.GetShip(_localId);
        _predicted.Team = Player?.Team ?? Teams.None;
    }

    private Ship EnsureShip(int id)
    {
        var ship = World.GetShip(id) ?? World.AddShip(id, "?", false);
        if (id == _localId) Player = ship;
        return ship;
    }

    private void OnSnapshot(Snapshot snap)
    {
        if (_world == null || snap.MatchId != MatchId || snap.Tick <= _latestTick) return;
        _snapshots.Add(snap);
        _latestTick = snap.Tick;
        Intermission = snap.Intermission;
        foreach (var s in snap.Ships) EnsureShip(s.Id);

        // Keep a second of history behind the render clock.
        double oldest = (_renderTick >= 0 ? _renderTick : snap.Tick) - GameConfig.TickRate;
        while (_snapshots.Count > 2 && _snapshots[1].Tick < oldest) _snapshots.RemoveAt(0);

        _world.Rules.ReadState(new BinaryReader(new MemoryStream(snap.RulesState)));
        Reconcile(snap);
    }

    /// <summary>
    /// Restarts the local ship's prediction from the server's state and replays the inputs the server hasn't
    /// applied yet. Any jump this causes is blended out over a few frames instead of shown at once.
    /// </summary>
    private void Reconcile(Snapshot snap)
    {
        _unacked.RemoveAll(u => u.Seq <= snap.AckSeq);
        if (_localId < 0 || snap.FindShip(_localId) is not { } state) return;

        var before = _predicted.Position;
        bool wasAlive = _hasPrediction && _predicted.Alive;
        state.ApplyTo(_predicted);

        _predictedBall = null;
        foreach (var b in snap.Balls)
        {
            if (b.State != BallState.Carried || b.CarrierId != _localId) continue;
            var home = World.Balls.FirstOrDefault(x => x.Team == b.Team)?.Home ?? b.Position;
            _predictedBall = new Ball(b.Team, home);
            b.ApplyTo(_predictedBall);
        }

        foreach (var (_, input) in _unacked) World.PredictShip(_predicted, input, _predictedBall);
        _hasPrediction = true;

        LastPredictionError = 0f;
        if (wasAlive && _predicted.Alive)
        {
            var error = World.Map.Delta(_predicted.Position, before);
            LastPredictionError = error.Length();
            _correction = (error + _correction).Length() < MaxSmoothedError ? _correction + error : Vector2.Zero;
        }
        else
        {
            _correction = Vector2.Zero;
        }
    }

    private void OnEvents(List<NetEvent> events)
    {
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case NetEventKind.BulletSpawned:
                    _bullets[e.BulletId] = new BulletTrack
                    {
                        SpawnTick = e.Tick, Position = e.BulletPosition, Velocity = e.BulletVelocity, Owner = e.BulletOwner,
                    };
                    break;
                case NetEventKind.BulletRemoved:
                    if (_bullets.TryGetValue(e.BulletId, out var track)) track.RemoveTick = e.Tick;
                    break;
                default:
                    // Our own shots were already announced by prediction.
                    if (e.Game.Type == GameEventType.ShipFired && e.Game.ShipId == _localId) break;
                    _queuedEvents.Add(e);
                    break;
            }
        }
    }

    /// <summary>
    /// Keeps the render clock <see cref="InterpolationDelayTicks"/> behind the newest snapshot, speeding up or
    /// slowing down a little rather than jumping, so motion stays smooth through network jitter.
    /// </summary>
    private void AdvanceRenderClock(float dt)
    {
        if (_latestTick < 0) return;
        double target = _latestTick - InterpolationDelayTicks;
        if (_renderTick < 0 || Math.Abs(target - _renderTick) > 30)
        {
            _renderTick = target;
            return;
        }
        double rate = 1.0 + Math.Clamp((target - _renderTick) * 0.03, -0.2, 0.2);
        _renderTick = Math.Min(_renderTick + dt * GameConfig.TickRate * rate, _latestTick);
    }

    private void BuildView(float dt)
    {
        var world = World;
        var map = world.Map;
        double rt = _renderTick;

        int bIndex = _snapshots.FindIndex(s => s.Tick > rt);
        var a = bIndex switch
        {
            -1 => _snapshots[^1],
            0 => _snapshots[0],
            _ => _snapshots[bIndex - 1],
        };
        var b = bIndex > 0 ? _snapshots[bIndex] : null;
        float t = b == null ? 0f : (float)Math.Clamp((rt - a.Tick) / (b.Tick - a.Tick), 0.0, 1.0);
        world.SetTick(Math.Max(0, (int)rt));

        foreach (var ship in world.Ships)
        {
            if (ship.Id == _localId) continue;
            if (a.FindShip(ship.Id) is not { } sa)
            {
                ship.Alive = false;
                continue;
            }
            sa.ApplyTo(ship);
            if (b?.FindShip(ship.Id) is { } sb && sa.Alive && sb.Alive)
            {
                ship.Position = ship.PrevPosition = map.WrapPosition(sa.Position + map.Delta(sa.Position, sb.Position) * t);
                ship.Velocity = Vector2.Lerp(sa.Velocity, sb.Velocity, t);
                ship.Heading = ship.PrevHeading = MathUtil.WrapAngle(sa.Heading + MathUtil.WrapAngle(sb.Heading - sa.Heading) * t);
            }
        }

        float alpha = Math.Clamp(_accumulator / GameConfig.Dt, 0f, 1f);
        _correction *= MathF.Exp(-dt * 12f);
        if (Player != null && _snapshots[^1].FindShip(_localId) is { } latest)
        {
            // Scores, fuel and timers from the newest snapshot; motion from prediction.
            latest.ApplyTo(Player);
            if (_hasPrediction)
            {
                var p = _predicted;
                Player.Alive = p.Alive;
                Player.Shield = p.Shield;
                Player.Thrusting = p.Thrusting;
                Player.Refueling = p.Refueling;
                Player.Fuel = p.Fuel;
                Player.Velocity = p.Velocity;
                Player.Position = Player.PrevPosition =
                    map.WrapPosition(p.PrevPosition + map.Delta(p.PrevPosition, p.Position) * alpha + _correction);
                Player.Heading = Player.PrevHeading = MathUtil.WrapAngle(p.PrevHeading + MathUtil.WrapAngle(p.Heading - p.PrevHeading) * alpha);
            }
        }

        foreach (var ball in world.Balls)
        {
            int ia = a.Balls.FindIndex(x => x.Team == ball.Team);
            if (ia < 0) continue;
            var ba = a.Balls[ia];
            ba.ApplyTo(ball);
            if (b != null && b.Balls.FindIndex(x => x.Team == ball.Team) is var ib and >= 0 && b.Balls[ib].State == ba.State)
            {
                ball.Position = ball.PrevPosition = map.WrapPosition(ba.Position + map.Delta(ba.Position, b.Balls[ib].Position) * t);
            }
            if (_predictedBall != null && _predictedBall.Team == ball.Team && Player is { Alive: true })
            {
                var pb = _predictedBall;
                ball.State = BallState.Carried;
                ball.CarrierId = _localId;
                ball.Position = ball.PrevPosition =
                    map.WrapPosition(pb.PrevPosition + map.Delta(pb.PrevPosition, pb.Position) * alpha + _correction);
            }
        }

        UpdateBullets(rt, alpha);
    }

    /// <summary>
    /// Bullets fly in straight lines, so they are placed from their spawn. Other ships' bullets are shown at
    /// the render tick like the ships; our own at the predicted present, so they leave our predicted ship.
    /// </summary>
    private void UpdateBullets(double rt, float alpha)
    {
        var world = World;
        var map = world.Map;
        double present = _latestTick + _unacked.Count + alpha;
        world.Bullets.Clear();
        int used = 0;
        List<int>? expired = null;
        foreach (var (id, track) in _bullets)
        {
            bool own = track.Owner == _localId;
            double at = own ? present : rt;
            if (track.RemoveTick <= rt - 1 && (!own || track.RemoveTick <= present))
            {
                (expired ??= []).Add(id);
                continue;
            }
            if (at < track.SpawnTick || at >= track.RemoveTick) continue;

            var pos = map.WrapPosition(track.Position + track.Velocity * (float)((at - track.SpawnTick) * GameConfig.Dt));
            if (map.PointInWall(pos)) continue;
            if (used == _bulletPool.Count) _bulletPool.Add(new Bullet());
            var bullet = _bulletPool[used++];
            bullet.Id = id;
            bullet.Position = bullet.PrevPosition = pos;
            bullet.Velocity = track.Velocity;
            bullet.OwnerId = track.Owner;
            bullet.Age = (float)((at - track.SpawnTick) * GameConfig.Dt);
            bullet.Dead = false;
            world.Bullets.Add(bullet);
        }
        if (expired != null)
        {
            foreach (int id in expired) _bullets.Remove(id);
        }
    }

    /// <summary>
    /// Releases events once the render clock reaches their tick, so an explosion happens where the ship is
    /// drawn. Events about our own ship are shown at once, since it is drawn in the present.
    /// </summary>
    private void DispatchEvents()
    {
        int dispatched = 0;
        for (; dispatched < _queuedEvents.Count; dispatched++)
        {
            var e = _queuedEvents[dispatched];
            bool own = e.Game.ShipId == _localId && _localId >= 0;
            if (!own && e.Tick > _renderTick) break;
            if (e.Game.Type == GameEventType.LapCompleted && World.GetShip(e.Game.ShipId) is { } lapper) lapper.LapTimes.Add(e.Game.Value);
            _readyEvents.Add(e.Game);
        }
        _queuedEvents.RemoveRange(0, dispatched);

        // Own-ship events behind a not-yet-due event would otherwise wait; release them too.
        for (int i = 0; i < _queuedEvents.Count; i++)
        {
            var e = _queuedEvents[i];
            if (_localId < 0 || e.Game.ShipId != _localId) continue;
            if (e.Game.Type == GameEventType.LapCompleted && World.GetShip(e.Game.ShipId) is { } lapper) lapper.LapTimes.Add(e.Game.Value);
            _readyEvents.Add(e.Game);
            _queuedEvents.RemoveAt(i--);
        }
    }
}

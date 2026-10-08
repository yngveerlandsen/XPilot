using System.Numerics;
using System.Text.Json;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Net;

/// <summary>Sent to each client when a match starts or when they join one in progress.</summary>
public sealed class MatchStartMessage
{
    public int MatchId;
    /// <summary>The ship this client flies, or -1 when spectating.</summary>
    public int YourShipId = -1;
    public string ServerName = "";
    public string MapText = "";
    public GameConfig Config = new();
    public int ScoreLimit;
    public int CaptureLimit;
    public float? TimeLimit;
    public int? Laps;
    /// <summary>The mode played on this map, which needn't be the map's own.</summary>
    public GameModeKind Mode;
    public int TeamScoreLimit;
    public int Lives;
    public int HillScoreLimit;

    public byte[] Encode()
    {
        var m = new MessageWriter(MessageType.MatchStart);
        var w = m.Writer;
        w.Write(MatchId);
        w.Write(YourShipId);
        w.Write(ServerName);
        w.Write(MapText);
        w.Write(JsonSerializer.Serialize(Config));
        w.Write(ScoreLimit);
        w.Write(CaptureLimit);
        w.Write(TimeLimit ?? -1f);
        w.Write(Laps ?? -1);
        w.Write((byte)Mode);
        w.Write(TeamScoreLimit);
        w.Write(Lives);
        w.Write(HillScoreLimit);
        return m.ToArray();
    }

    public static MatchStartMessage Decode(BinaryReader r)
    {
        var msg = new MatchStartMessage
        {
            MatchId = r.ReadInt32(),
            YourShipId = r.ReadInt32(),
            ServerName = r.ReadString(),
            MapText = r.ReadString(),
            Config = JsonSerializer.Deserialize<GameConfig>(r.ReadString()) ?? new GameConfig(),
            ScoreLimit = r.ReadInt32(),
            CaptureLimit = r.ReadInt32(),
        };
        float time = r.ReadSingle();
        msg.TimeLimit = time >= 0f ? time : null;
        int laps = r.ReadInt32();
        msg.Laps = laps >= 0 ? laps : null;
        msg.Mode = (GameModeKind)r.ReadByte();
        if (!Enum.IsDefined(msg.Mode)) throw new InvalidDataException("Unknown game mode");
        msg.TeamScoreLimit = r.ReadInt32();
        msg.Lives = r.ReadInt32();
        msg.HillScoreLimit = r.ReadInt32();
        return msg;
    }
}

public readonly record struct RosterEntry(int Id, string Name, bool IsBot, int Team);

/// <summary>Who is flying which ship. Sent whenever that changes.</summary>
public static class RosterMessage
{
    public static byte[] Encode(int matchId, IEnumerable<Ship> ships)
    {
        var m = new MessageWriter(MessageType.Roster);
        var w = m.Writer;
        w.Write(matchId);
        var list = ships.ToList();
        w.Write((short)list.Count);
        foreach (var s in list)
        {
            w.Write(s.Id);
            w.Write(s.Name);
            w.Write(s.IsBot);
            w.Write((sbyte)s.Team);
        }
        return m.ToArray();
    }

    /// <summary>Id, an empty name, bot flag and team.</summary>
    private const int MinBytes = 7;

    public static (int MatchId, List<RosterEntry> Entries) Decode(BinaryReader r)
    {
        int matchId = r.ReadInt32();
        int count = MessageReader.CheckCount(r, r.ReadInt16(), MinBytes);
        var list = new List<RosterEntry>(count);
        for (int i = 0; i < count; i++) list.Add(new RosterEntry(r.ReadInt32(), r.ReadString(), r.ReadBoolean(), r.ReadSByte()));
        return (matchId, list);
    }
}

/// <summary>Everything about a ship that changes during play.</summary>
public struct ShipState
{
    public const int Size = 60;

    [Flags]
    private enum Flags : byte
    {
        Alive = 1, Shield = 2, Thrusting = 4, Refueling = 8, Finished = 16, GrabHeld = 32,
    }

    public int Id;
    public int Team;
    public bool Alive, Shield, Thrusting, Refueling, Finished, GrabHeld;
    public Vector2 Position, Velocity;
    public float Heading;
    public float Fuel, RespawnTimer, FireCooldown, SpawnProtection;
    public int Kills, Deaths, Score;
    public int NextCheckpoint, LastCheckpoint, Lap, Place;
    public float LapStartTime, FinishTime;

    public static ShipState From(Ship s) => new()
    {
        Id = s.Id, Team = s.Team,
        Alive = s.Alive, Shield = s.Shield, Thrusting = s.Thrusting, Refueling = s.Refueling, Finished = s.Finished, GrabHeld = s.GrabHeld,
        Position = s.Position, Velocity = s.Velocity, Heading = s.Heading,
        Fuel = s.Fuel, RespawnTimer = s.RespawnTimer, FireCooldown = s.FireCooldown, SpawnProtection = s.SpawnProtection,
        Kills = s.Kills, Deaths = s.Deaths, Score = s.Score,
        NextCheckpoint = s.NextCheckpoint, LastCheckpoint = s.LastCheckpoint, Lap = s.Lap, Place = s.Place,
        LapStartTime = s.LapStartTime, FinishTime = s.FinishTime,
    };

    /// <summary>Copies the state onto a ship, with no interpolation.</summary>
    public readonly void ApplyTo(Ship s)
    {
        s.Team = Team;
        s.Alive = Alive; s.Shield = Shield; s.Thrusting = Thrusting; s.Refueling = Refueling; s.Finished = Finished; s.GrabHeld = GrabHeld;
        s.Position = s.PrevPosition = Position;
        s.Velocity = Velocity;
        s.Heading = s.PrevHeading = Heading;
        s.Fuel = Fuel; s.RespawnTimer = RespawnTimer; s.FireCooldown = FireCooldown; s.SpawnProtection = SpawnProtection;
        s.Kills = Kills; s.Deaths = Deaths; s.Score = Score;
        s.NextCheckpoint = NextCheckpoint; s.LastCheckpoint = LastCheckpoint; s.Lap = Lap; s.Place = Place;
        s.LapStartTime = LapStartTime; s.FinishTime = FinishTime;
    }

    public readonly void Write(BinaryWriter w)
    {
        var flags = (Alive ? Flags.Alive : 0) | (Shield ? Flags.Shield : 0) | (Thrusting ? Flags.Thrusting : 0) |
                    (Refueling ? Flags.Refueling : 0) | (Finished ? Flags.Finished : 0) | (GrabHeld ? Flags.GrabHeld : 0);
        w.Write(Id);
        w.Write((sbyte)Team);
        w.Write((byte)flags);
        w.Write(Position);
        w.Write(Velocity);
        w.Write(Heading);
        w.Write(Fuel);
        w.Write(RespawnTimer);
        w.Write(FireCooldown);
        w.Write(SpawnProtection);
        w.Write((short)Kills);
        w.Write((short)Deaths);
        w.Write((short)Score);
        w.Write((byte)NextCheckpoint);
        w.Write((sbyte)LastCheckpoint);
        w.Write((byte)Lap);
        w.Write((byte)Place);
        w.Write(LapStartTime);
        w.Write(FinishTime);
    }

    public static ShipState Read(BinaryReader r)
    {
        var s = new ShipState { Id = r.ReadInt32(), Team = r.ReadSByte() };
        var flags = (Flags)r.ReadByte();
        s.Alive = flags.HasFlag(Flags.Alive);
        s.Shield = flags.HasFlag(Flags.Shield);
        s.Thrusting = flags.HasFlag(Flags.Thrusting);
        s.Refueling = flags.HasFlag(Flags.Refueling);
        s.Finished = flags.HasFlag(Flags.Finished);
        s.GrabHeld = flags.HasFlag(Flags.GrabHeld);
        s.Position = r.ReadVector2();
        s.Velocity = r.ReadVector2();
        s.Heading = r.ReadSingle();
        s.Fuel = r.ReadSingle();
        s.RespawnTimer = r.ReadSingle();
        s.FireCooldown = r.ReadSingle();
        s.SpawnProtection = r.ReadSingle();
        s.Kills = r.ReadInt16();
        s.Deaths = r.ReadInt16();
        s.Score = r.ReadInt16();
        s.NextCheckpoint = r.ReadByte();
        s.LastCheckpoint = r.ReadSByte();
        s.Lap = r.ReadByte();
        s.Place = r.ReadByte();
        s.LapStartTime = r.ReadSingle();
        s.FinishTime = r.ReadSingle();
        return s;
    }
}

public struct BallSnapshot
{
    public const int Size = 26;

    public int Team;
    public BallState State;
    public int CarrierId;
    public Vector2 Position, Velocity;
    public float LooseTime;

    public static BallSnapshot From(Ball b) => new()
    {
        Team = b.Team, State = b.State, CarrierId = b.CarrierId, Position = b.Position, Velocity = b.Velocity, LooseTime = b.LooseTime,
    };

    public readonly void ApplyTo(Ball b)
    {
        b.State = State;
        b.CarrierId = CarrierId;
        b.Position = b.PrevPosition = Position;
        b.Velocity = Velocity;
        b.LooseTime = LooseTime;
    }

    public readonly void Write(BinaryWriter w)
    {
        w.Write((sbyte)Team);
        w.Write((byte)State);
        w.Write(CarrierId);
        w.Write(Position);
        w.Write(Velocity);
        w.Write(LooseTime);
    }

    public static BallSnapshot Read(BinaryReader r) => new()
    {
        Team = r.ReadSByte(),
        State = (BallState)r.ReadByte(),
        CarrierId = r.ReadInt32(),
        Position = r.ReadVector2(),
        Velocity = r.ReadVector2(),
        LooseTime = r.ReadSingle(),
    };
}

/// <summary>The state of the world after a server tick, sent unreliably about 30 times a second.</summary>
public sealed class Snapshot
{
    /// <summary>Where <see cref="AckSeq"/> sits in an encoded snapshot: after the type, match id and tick.</summary>
    public const int AckSeqOffset = 1 + 4 + 4;

    /// <summary>A copy of an encoded snapshot with a different <see cref="AckSeq"/>, without encoding it again.</summary>
    public static byte[] WithAckSeq(byte[] encoded, int ackSeq)
    {
        var copy = (byte[])encoded.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(copy.AsSpan(AckSeqOffset), ackSeq);
        return copy;
    }

    public int MatchId;
    public int Tick;
    /// <summary>The last input sequence number the server applied for the receiving client.</summary>
    public int AckSeq = -1;
    /// <summary>Seconds until the next match starts, or a negative number while a match is running.</summary>
    public float Intermission = -1f;
    public byte[] RulesState = [];
    /// <summary>Random events running or announced (<see cref="ChaosDirector.WriteState"/>).</summary>
    public byte[] ChaosState = [0, 0];
    public List<ShipState> Ships = [];
    public List<BallSnapshot> Balls = [];

    public ShipState? FindShip(int id)
    {
        foreach (var s in Ships)
        {
            if (s.Id == id) return s;
        }
        return null;
    }

    public byte[] Encode()
    {
        var m = new MessageWriter(MessageType.Snapshot);
        var w = m.Writer;
        w.Write(MatchId);
        w.Write(Tick);
        w.Write(AckSeq);
        w.Write(Intermission);
        w.Write((short)RulesState.Length);
        w.Write(RulesState);
        w.Write((byte)ChaosState.Length);
        w.Write(ChaosState);
        w.Write((ushort)Ships.Count);
        foreach (var s in Ships) s.Write(w);
        w.Write((ushort)Balls.Count);
        foreach (var b in Balls) b.Write(w);
        return m.ToArray();
    }

    public static Snapshot Decode(BinaryReader r)
    {
        var snap = new Snapshot
        {
            MatchId = r.ReadInt32(),
            Tick = r.ReadInt32(),
            AckSeq = r.ReadInt32(),
            Intermission = r.ReadSingle(),
        };
        snap.RulesState = r.ReadBytes(MessageReader.CheckCount(r, r.ReadInt16(), 1));
        snap.ChaosState = r.ReadBytes(MessageReader.CheckCount(r, r.ReadByte(), 1));
        int ships = MessageReader.CheckCount(r, r.ReadUInt16(), ShipState.Size);
        for (int i = 0; i < ships; i++) snap.Ships.Add(ShipState.Read(r));
        int balls = MessageReader.CheckCount(r, r.ReadUInt16(), BallSnapshot.Size);
        for (int i = 0; i < balls; i++) snap.Balls.Add(BallSnapshot.Read(r));
        return snap;
    }
}

public enum NetEventKind : byte { Game, BulletSpawned, BulletRemoved }

/// <summary>
/// Something that happened on a given server tick. Sent reliably, because a missed explosion or score change
/// can't be recovered from a later snapshot. Bullets travel in straight lines, so the client simulates them
/// from their spawn instead of receiving their positions in every snapshot.
/// </summary>
public struct NetEvent
{
    /// <summary>The smallest event on the wire: tick, kind and a bullet id.</summary>
    public const int MinSize = 9;

    public int Tick;
    public NetEventKind Kind;
    public GameEvent Game;
    public int BulletId;
    public int BulletOwner;
    public Vector2 BulletPosition, BulletVelocity;

    public readonly void Write(BinaryWriter w)
    {
        w.Write(Tick);
        w.Write((byte)Kind);
        switch (Kind)
        {
            case NetEventKind.Game:
                w.Write((byte)Game.Type);
                w.Write(Game.ShipId);
                w.Write(Game.OtherId);
                w.Write(Game.Position);
                w.Write(Game.Velocity);
                w.Write(Game.Value);
                w.Write((byte)Game.Cause);
                break;
            case NetEventKind.BulletSpawned:
                w.Write(BulletId);
                w.Write(BulletOwner);
                w.Write(BulletPosition);
                w.Write(BulletVelocity);
                break;
            default:
                w.Write(BulletId);
                break;
        }
    }

    public static NetEvent Read(BinaryReader r)
    {
        var e = new NetEvent { Tick = r.ReadInt32(), Kind = (NetEventKind)r.ReadByte() };
        switch (e.Kind)
        {
            case NetEventKind.Game:
                e.Game = new GameEvent(
                    (GameEventType)r.ReadByte(),
                    r.ReadInt32(),
                    r.ReadInt32(),
                    r.ReadVector2(),
                    r.ReadVector2(),
                    r.ReadSingle(),
                    (DeathCause)r.ReadByte());
                break;
            case NetEventKind.BulletSpawned:
                e.BulletId = r.ReadInt32();
                e.BulletOwner = r.ReadInt32();
                e.BulletPosition = r.ReadVector2();
                e.BulletVelocity = r.ReadVector2();
                break;
            default:
                e.BulletId = r.ReadInt32();
                break;
        }
        return e;
    }
}

public static class EventsMessage
{
    public static byte[] Encode(int matchId, IReadOnlyList<NetEvent> events)
    {
        var m = new MessageWriter(MessageType.Events);
        m.Writer.Write(matchId);
        m.Writer.Write(events.Count);
        foreach (var e in events) e.Write(m.Writer);
        return m.ToArray();
    }

    public static (int MatchId, List<NetEvent> Events) Decode(BinaryReader r)
    {
        int matchId = r.ReadInt32();
        int count = MessageReader.CheckCount(r, r.ReadInt32(), NetEvent.MinSize);
        var list = new List<NetEvent>(count);
        for (int i = 0; i < count; i++) list.Add(NetEvent.Read(r));
        return (matchId, list);
    }
}

/// <summary>Client inputs, newest last, each tagged with a sequence number.</summary>
public static class InputMessage
{
    /// <summary>
    /// Rounds an input to what survives the trip to the server, so client prediction uses exactly the
    /// values the server will.
    /// </summary>
    public static ShipInput Quantize(ShipInput input) => input with { Turn = EncodeTurn(input.Turn) / 127f };

    private static sbyte EncodeTurn(float turn) => (sbyte)Math.Round(Math.Clamp(turn, -1f, 1f) * 127f);

    public static byte[] Encode(int newestSeq, IReadOnlyList<ShipInput> inputs)
    {
        var m = new MessageWriter(MessageType.Input);
        var w = m.Writer;
        w.Write(newestSeq);
        w.Write((byte)inputs.Count);
        foreach (var input in inputs)
        {
            w.Write(EncodeTurn(input.Turn));
            w.Write((byte)((input.Thrust ? 1 : 0) | (input.Fire ? 2 : 0) | (input.Shield ? 4 : 0) | (input.Grab ? 8 : 0)));
        }
        return m.ToArray();
    }

    /// <returns>The sequence number of the first input, and the inputs oldest first.</returns>
    public static (int FirstSeq, List<ShipInput> Inputs) Decode(BinaryReader r)
    {
        int newest = r.ReadInt32();
        int count = MessageReader.CheckCount(r, r.ReadByte(), 2);
        var list = new List<ShipInput>(count);
        for (int i = 0; i < count; i++)
        {
            float turn = r.ReadSByte() / 127f;
            int flags = r.ReadByte();
            list.Add(new ShipInput { Turn = turn, Thrust = (flags & 1) != 0, Fire = (flags & 2) != 0, Shield = (flags & 4) != 0, Grab = (flags & 8) != 0 });
        }
        return (newest - count + 1, list);
    }
}

public static class ChatMessage
{
    /// <param name="from">The sender's name, or empty for server announcements.</param>
    public static byte[] Encode(string from, int team, string text)
    {
        var m = new MessageWriter(MessageType.ChatMessage);
        m.Writer.Write(from);
        m.Writer.Write((sbyte)team);
        m.Writer.Write(text);
        return m.ToArray();
    }

    public static ChatLine Decode(BinaryReader r) => new(r.ReadString(), r.ReadSByte(), r.ReadString());
}

/// <param name="From">Empty for server announcements.</param>
public readonly record struct ChatLine(string From, int Team, string Text);

/// <summary>What a server tells LAN browsers and the master server about itself.</summary>
public sealed record ServerInfo(string Name, string MapName, string Mode, int Humans, int Capacity, int Port)
{
    /// <summary>Longest name or map name sent; anyone can register with a master server, so readers enforce it.</summary>
    public const int MaxTextLength = 64;

    public void Write(BinaryWriter w)
    {
        w.Write(Protocol.Version);
        w.Write(Clip(Name));
        w.Write(Clip(MapName));
        w.Write(Clip(Mode));
        w.Write((byte)Humans);
        w.Write((byte)Capacity);
        w.Write(Port);
    }

    /// <returns>Null if the server speaks a different protocol version.</returns>
    public static ServerInfo? Read(BinaryReader r)
    {
        if (r.ReadInt32() != Protocol.Version) return null;
        return new ServerInfo(ReadText(r), ReadText(r), ReadText(r), r.ReadByte(), r.ReadByte(), r.ReadInt32());
    }

    private static string Clip(string text) => text.Length <= MaxTextLength ? text : text[..MaxTextLength];

    private static string ReadText(BinaryReader r)
    {
        var text = r.ReadString();
        return text.Length <= MaxTextLength ? text : throw new InvalidDataException($"Server info text of {text.Length} characters");
    }
}

using System.Numerics;

namespace XPilot.Net;

public static class Protocol
{
    /// <summary>Bumped whenever the wire format changes; clients and servers must match.</summary>
    public const int Version = 2;
    /// <summary>The port the original XPilot used.</summary>
    public const int DefaultPort = 15345;
    public const int DefaultMasterPort = 15346;
    /// <summary>LiteNetLib's connection key, so stray UDP traffic is rejected.</summary>
    public const string ConnectionKey = "xpilot";
    public const int MaxNameLength = 16;
    public const int MaxChatLength = 120;

    /// <summary>Server ticks between snapshots (60 Hz / 2 = 30 snapshots a second).</summary>
    public const int SnapshotInterval = 2;
    /// <summary>Inputs repeated in every input packet, so a lost packet costs nothing.</summary>
    public const int InputRedundancy = 8;
    /// <summary>Seconds of results between the end of one match and the start of the next.</summary>
    public const float IntermissionSeconds = 10f;

    /// <summary>Large enough for a full snapshot, small enough to fit through almost any internet path.</summary>
    public const int Mtu = 1200;

    public static string CleanName(string? name)
    {
        var chars = (name ?? "").Where(c => c >= ' ' && c < 127).ToArray();
        var clean = new string(chars).Trim();
        if (clean.Length > MaxNameLength) clean = clean[..MaxNameLength].Trim();
        return clean.Length == 0 ? "Pilot" : clean;
    }

    public const int MaxServerNameLength = 40;

    public static string CleanServerName(string? name)
    {
        var chars = (name ?? "").Where(c => c >= ' ' && c < 127).ToArray();
        var clean = new string(chars).Trim();
        if (clean.Length > MaxServerNameLength) clean = clean[..MaxServerNameLength].Trim();
        return clean.Length == 0 ? "XPilot" : clean;
    }

    public static string CleanChat(string? text)
    {
        var chars = (text ?? "").Where(c => c >= ' ' && c < 127).ToArray();
        var clean = new string(chars).Trim();
        return clean.Length > MaxChatLength ? clean[..MaxChatLength] : clean;
    }
}

public enum MessageType : byte
{
    // Client to server
    Input = 1,
    Chat = 2,
    SwitchTeam = 3,

    // Server to client
    MatchStart = 16,
    Roster = 17,
    Snapshot = 18,
    Events = 19,
    ChatMessage = 20,

    // Unconnected (LAN discovery and the master server)
    DiscoveryRequest = 32,
    DiscoveryResponse = 33,
    MasterRegister = 34,
    MasterListRequest = 35,
    MasterListResponse = 36,
}

public enum Delivery { Unreliable, Reliable }

/// <summary>A connected peer, from the server's point of view.</summary>
public interface IConnection
{
    int Id { get; }
    /// <summary>Round trip time in milliseconds, if known.</summary>
    int RoundTripMs { get; }
    void Send(byte[] data, Delivery delivery);
}

public static class BinaryExtensions
{
    public static void Write(this BinaryWriter w, Vector2 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
    }

    public static Vector2 ReadVector2(this BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
}

/// <summary>Builds a message: a <see cref="MessageType"/> byte followed by its fields.</summary>
public sealed class MessageWriter
{
    private readonly MemoryStream _stream = new();

    public MessageWriter(MessageType type)
    {
        Writer = new BinaryWriter(_stream);
        Writer.Write((byte)type);
    }

    public BinaryWriter Writer { get; }

    public byte[] ToArray()
    {
        Writer.Flush();
        return _stream.ToArray();
    }
}

public static class MessageReader
{
    /// <summary>
    /// Checks that a count read from a message fits in what is left of it, given the smallest size of one
    /// item, so a corrupt or hostile count can't make the reader allocate a huge list.
    /// </summary>
    public static int CheckCount(BinaryReader r, int count, int minItemBytes)
    {
        long remaining = r.BaseStream.Length - r.BaseStream.Position;
        if (count < 0 || (long)count * minItemBytes > remaining)
        {
            throw new InvalidDataException($"Message claims {count} items with only {remaining} bytes left");
        }
        return count;
    }

    /// <summary>
    /// Exceptions that mean a message was truncated, corrupt or hostile. Receivers drop such messages instead
    /// of letting one bad packet take down a client or server.
    /// </summary>
    public static bool IsMalformed(Exception ex) =>
        ex is IOException or InvalidDataException or ArgumentException or FormatException or OverflowException
            or System.Text.Json.JsonException or XPilot.Core.Maps.MapFormatException;

    /// <summary>Reads the message type and returns a reader positioned at the fields.</summary>
    public static BinaryReader Open(byte[] data, out MessageType type)
    {
        var reader = new BinaryReader(new MemoryStream(data, writable: false));
        type = (MessageType)reader.ReadByte();
        return reader;
    }
}

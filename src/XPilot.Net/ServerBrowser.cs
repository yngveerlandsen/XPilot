using System.Diagnostics;
using System.Net;
using LiteNetLib;

namespace XPilot.Net;

/// <summary>A server found on the LAN or through the master server.</summary>
/// <param name="ServerId">The id the master server knows it by, for NAT introduction; null for LAN servers.</param>
public sealed record ServerEntry(IPEndPoint EndPoint, ServerInfo Info, bool OnLan, string? ServerId)
{
    public double LastSeen { get; set; }
}

/// <summary>
/// Finds servers: broadcasts on the LAN every couple of seconds and, if a master server is configured,
/// asks it for the internet list. Call <see cref="Poll"/> once per frame.
/// </summary>
public sealed class ServerBrowser : IDisposable
{
    private const double LanInterval = 2, MasterInterval = 5, Expiry = 12;

    private readonly NetManager _net;
    private readonly EndPointLookup? _master;
    private readonly Dictionary<string, ServerEntry> _servers = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _nextLan, _nextMaster;

    /// <param name="masterServer">"host:port" of a master server, or null for LAN only.</param>
    public ServerBrowser(string? masterServer)
    {
        var listener = new EventBasedNetListener();
        _net = new NetManager(listener) { AutoRecycle = true, UnconnectedMessagesEnabled = true };
        listener.NetworkReceiveUnconnectedEvent += (remote, reader, _) => OnMessage(remote, reader.GetRemainingBytes());
        _net.Start();
        if (!string.IsNullOrWhiteSpace(masterServer))
        {
            _master = new EndPointLookup(masterServer, Protocol.DefaultMasterPort, retry: TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A master server is configured (it may not have been found yet).</summary>
    public bool HasMaster => _master != null;
    /// <summary>The master server's address, once its name has been looked up.</summary>
    public IPEndPoint? Master => _master?.Result;
    /// <summary>The master server's name couldn't be looked up; it is retried every few seconds.</summary>
    public bool MasterNotFound => _master is { Result: null, Failed: true };

    /// <summary>Servers heard from recently, LAN servers first. Re-sorted only when the list changes.</summary>
    public IReadOnlyList<ServerEntry> Servers => _sorted ??= _servers.Values
        .OrderByDescending(s => s.OnLan)
        .ThenBy(s => s.Info.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private List<ServerEntry>? _sorted;

    public void Refresh()
    {
        _nextLan = _nextMaster = 0;
    }

    public void Poll()
    {
        _net.PollEvents();
        _master?.Poll();
        double now = _clock.Elapsed.TotalSeconds;
        if (now >= _nextLan)
        {
            _nextLan = now + LanInterval;
            _net.SendBroadcast(new MessageWriter(MessageType.DiscoveryRequest).ToArray(), Protocol.DefaultPort);
        }
        if (Master is { } master && now >= _nextMaster)
        {
            _nextMaster = now + MasterInterval;
            _net.SendUnconnectedMessage(new MessageWriter(MessageType.MasterListRequest).ToArray(), master);
        }
        foreach (var key in _servers.Where(kv => now - kv.Value.LastSeen > Expiry).Select(kv => kv.Key).ToList())
        {
            _servers.Remove(key);
            _sorted = null;
        }
    }

    private void OnMessage(IPEndPoint remote, byte[] data)
    {
        try
        {
            var reader = MessageReader.Open(data, out var type);
            double now = _clock.Elapsed.TotalSeconds;
            if (type == MessageType.DiscoveryResponse && ServerInfo.Read(reader) is { } info)
            {
                var endPoint = new IPEndPoint(remote.Address, info.Port);
                _servers[endPoint.ToString()] = new ServerEntry(endPoint, info, true, null) { LastSeen = now };
                _sorted = null;
            }
            else if (type == MessageType.MasterListResponse)
            {
                // Id, address and the smallest server info.
                int count = MessageReader.CheckCount(reader, reader.ReadByte(), 14);
                for (int i = 0; i < count; i++)
                {
                    var id = reader.ReadString();
                    var address = reader.ReadString();
                    var entryInfo = ServerInfo.Read(reader);
                    if (entryInfo == null || !IPEndPoint.TryParse(address, out var endPoint)) continue;
                    // A server already found on the LAN is better reached directly.
                    if (_servers.Values.Any(s => s.OnLan && s.Info.Name == entryInfo.Name && s.EndPoint.Port == endPoint.Port)) continue;
                    _servers[endPoint.ToString()] = new ServerEntry(endPoint, entryInfo, false, id) { LastSeen = now };
                    _sorted = null;
                }
            }
        }
        catch (Exception ex) when (MessageReader.IsMalformed(ex))
        {
        }
    }

    public void Dispose() => _net.Stop();
}

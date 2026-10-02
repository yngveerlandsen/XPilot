using System.Diagnostics;
using System.Net;
using LiteNetLib;

namespace XPilot.Net;

/// <summary>
/// A meta-server, like XPilot's original one: game servers register every few seconds, clients ask for the
/// list, and it introduces a joining client to a server so both can punch through their NATs. It must run
/// somewhere reachable from the internet; it never relays game traffic.
/// </summary>
public sealed class MasterServer : IDisposable
{
    /// <summary>Servers that haven't checked in for this long are dropped from the list.</summary>
    private const double Expiry = 45;
    private const int EntriesPerPacket = 8;
    /// <summary>Registration is unauthenticated, so cap how many servers one address, and everyone, can list.</summary>
    private const int MaxServers = 500;
    private const int MaxPerAddress = 4;
    /// <summary>
    /// The reply to a list request is bigger than the request and goes to an address that could be forged, so
    /// each address gets one reply per interval and replies are capped, to keep the master useless for
    /// flooding someone else.
    /// </summary>
    private const double ListInterval = 2;
    private const int MaxListPackets = 8;

    private sealed class Registration
    {
        public required string Id;
        public required IPEndPoint External;
        public required ServerInfo Info;
        public IPEndPoint? NatInternal, NatExternal;
        public double LastSeen;
    }

    private readonly NetManager _net;
    private readonly Dictionary<string, Registration> _servers = [];
    private readonly Dictionary<IPAddress, double> _lastList = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public MasterServer()
    {
        var listener = new EventBasedNetListener();
        _net = new NetManager(listener) { AutoRecycle = true, UnconnectedMessagesEnabled = true, NatPunchEnabled = true };
        listener.NetworkReceiveUnconnectedEvent += (remote, reader, _) => OnMessage(remote, reader.GetRemainingBytes());

        var nat = new EventBasedNatPunchListener();
        nat.NatIntroductionRequest += OnNatRequest;
        _net.NatPunchModule.Init(nat);
    }

    public int ServerCount => _servers.Count;
    public int Port => _net.LocalPort;
    public event Action<string>? Log;

    public static string HostToken(string serverId) => "host:" + serverId;
    public static string JoinToken(string serverId) => "join:" + serverId;

    public bool Start(int port) => _net.Start(port);

    public void Poll()
    {
        _net.PollEvents();
        _net.NatPunchModule.PollEvents();
        double now = _clock.Elapsed.TotalSeconds;
        foreach (var id in _servers.Values.Where(s => now - s.LastSeen > Expiry).Select(s => s.Id).ToList())
        {
            _servers.Remove(id);
            Log?.Invoke($"Server {id} expired");
        }
        foreach (var address in _lastList.Where(kv => now - kv.Value > ListInterval).Select(kv => kv.Key).ToList()) _lastList.Remove(address);
    }

    private void OnMessage(IPEndPoint remote, byte[] data)
    {
        try
        {
            var reader = MessageReader.Open(data, out var type);
            switch (type)
            {
                case MessageType.MasterRegister:
                    var id = reader.ReadString();
                    if (id.Length is 0 or > 32 || ServerInfo.Read(reader) is not { } info) return;
                    if (!_servers.TryGetValue(id, out var reg))
                    {
                        if (_servers.Count >= MaxServers) return;
                        if (_servers.Values.Count(s => s.External.Address.Equals(remote.Address)) >= MaxPerAddress) return;
                        reg = new Registration { Id = id, External = remote, Info = info };
                        _servers[id] = reg;
                        Log?.Invoke($"Server '{info.Name}' registered from {remote}");
                    }
                    // The game port may differ from the port the registration came from, but the NAT mapping
                    // for the game socket is the same one: the server registers from its game socket.
                    reg.External = remote;
                    reg.Info = info;
                    reg.LastSeen = _clock.Elapsed.TotalSeconds;
                    break;

                case MessageType.MasterListRequest:
                    double now = _clock.Elapsed.TotalSeconds;
                    if (_lastList.TryGetValue(remote.Address, out double last) && now - last < ListInterval) return;
                    _lastList[remote.Address] = now;
                    SendList(remote);
                    break;
            }
        }
        catch (Exception ex) when (MessageReader.IsMalformed(ex))
        {
        }
    }

    private void SendList(IPEndPoint remote)
    {
        var all = _servers.Values.OrderByDescending(s => s.LastSeen).Take(MaxListPackets * EntriesPerPacket).ToList();
        for (int start = 0; start == 0 || start < all.Count; start += EntriesPerPacket)
        {
            var chunk = all.Skip(start).Take(EntriesPerPacket).ToList();
            var m = new MessageWriter(MessageType.MasterListResponse);
            m.Writer.Write((byte)chunk.Count);
            foreach (var s in chunk)
            {
                m.Writer.Write(s.Id);
                m.Writer.Write(s.External.ToString());
                s.Info.Write(m.Writer);
            }
            _net.SendUnconnectedMessage(m.ToArray(), remote);
            if (all.Count == 0) break;
        }
    }

    /// <summary>
    /// Servers keep a fresh "host" request on file; a "join" request for that server gets both sides
    /// introduced to each other.
    /// </summary>
    private void OnNatRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
    {
        if (token.StartsWith("host:", StringComparison.Ordinal))
        {
            if (_servers.TryGetValue(token[5..], out var host))
            {
                host.NatInternal = localEndPoint;
                host.NatExternal = remoteEndPoint;
            }
        }
        else if (token.StartsWith("join:", StringComparison.Ordinal))
        {
            if (!_servers.TryGetValue(token[5..], out var host) || host.NatInternal == null || host.NatExternal == null) return;
            _net.NatPunchModule.NatIntroduce(host.NatInternal, host.NatExternal, localEndPoint, remoteEndPoint, token);
            Log?.Invoke($"Introduced {remoteEndPoint} to '{host.Info.Name}'");
        }
    }

    public void Dispose() => _net.Stop();
}

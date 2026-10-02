using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace XPilot.Net;

public enum ConnectionStatus { Connecting, Connected, Disconnected }

/// <summary>
/// Connects a <see cref="GameClient"/> to a server over UDP. Call <see cref="Poll"/> once per frame from the
/// game loop; everything runs on that thread.
/// </summary>
public sealed class ClientConnection : IDisposable
{
    /// <summary>How long to wait for a NAT introduction before trying the server's address directly.</summary>
    private const double NatTimeoutSeconds = 3;

    private readonly NetManager _net;
    private readonly string _playerName;
    private NetPeer? _peer;
    private IPEndPoint? _pendingDirect;
    private DateTime _natDeadline;

    public ClientConnection(string playerName)
    {
        _playerName = Protocol.CleanName(playerName);
        Client = new GameClient(Send);
        var listener = new EventBasedNetListener();
        _net = new NetManager(listener)
        {
            AutoRecycle = true,
            NatPunchEnabled = true,
            MtuOverride = Protocol.Mtu,
            DisconnectTimeout = 10000,
        };
        var nat = new EventBasedNatPunchListener();
        nat.NatIntroductionSuccess += (target, _, _) =>
        {
            if (_peer != null || _pendingDirect == null) return;
            _pendingDirect = null;
            Connect(target);
        };
        _net.NatPunchModule.Init(nat);

        listener.PeerConnectedEvent += _ => Status = ConnectionStatus.Connected;
        listener.PeerDisconnectedEvent += (_, info) =>
        {
            Status = ConnectionStatus.Disconnected;
            string? reason = info.AdditionalData.AvailableBytes > 0 && info.AdditionalData.TryGetString(out var r) ? r : null;
            Error = reason ?? info.Reason switch
            {
                DisconnectReason.ConnectionFailed => "No answer from the server",
                DisconnectReason.Timeout => "Connection timed out",
                DisconnectReason.RemoteConnectionClose => "The server closed the connection",
                DisconnectReason.ConnectionRejected => "The server refused the connection",
                DisconnectReason.HostUnreachable or DisconnectReason.NetworkUnreachable => "Server unreachable",
                DisconnectReason.DisconnectPeerCalled => null,
                _ => info.Reason.ToString(),
            };
        };
        listener.NetworkReceiveEvent += (_, reader, _, _) => Client.Receive(reader.GetRemainingBytes());
        _net.Start();
    }

    public GameClient Client { get; }
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Connecting;
    /// <summary>Why the connection failed or ended, if known.</summary>
    public string? Error { get; private set; }
    public string Address { get; private set; } = "";

    /// <summary>Connects to "host", "host:port" or "ip:port".</summary>
    public void Connect(string address)
    {
        Address = address;
        var endPoint = ServerHost.ParseEndPoint(address, Protocol.DefaultPort);
        if (endPoint == null)
        {
            Fail($"Unknown address '{address}'");
            return;
        }
        Connect(endPoint);
    }

    public void Connect(IPEndPoint endPoint)
    {
        if (Address.Length == 0) Address = endPoint.ToString();
        var data = new NetDataWriter();
        data.Put(Protocol.ConnectionKey);
        data.Put(Protocol.Version);
        data.Put(_playerName);
        _peer = _net.Connect(endPoint, data);
        if (_peer == null) Fail("Could not start connecting");
    }

    /// <summary>
    /// Joins a server listed by a master server: asks the master to introduce us so both sides punch a hole
    /// through their NATs, and falls back to connecting directly if no introduction arrives.
    /// </summary>
    public void ConnectViaMaster(IPEndPoint master, string serverId, IPEndPoint serverAddress)
    {
        Address = serverAddress.ToString();
        _pendingDirect = serverAddress;
        _natDeadline = DateTime.UtcNow.AddSeconds(NatTimeoutSeconds);
        _net.NatPunchModule.SendNatIntroduceRequest(master, MasterServer.JoinToken(serverId));
    }

    public void Poll()
    {
        _net.PollEvents();
        _net.NatPunchModule.PollEvents();
        if (_pendingDirect != null && DateTime.UtcNow >= _natDeadline)
        {
            var direct = _pendingDirect;
            _pendingDirect = null;
            Connect(direct);
        }
        if (_peer != null) Client.RoundTripMs = _peer.RoundTripTime;
    }

    private void Send(byte[] data, Delivery delivery)
    {
        if (_peer == null || Status != ConnectionStatus.Connected) return;
        _peer.SendMessage(data, delivery);
    }

    private void Fail(string error)
    {
        Error = error;
        Status = ConnectionStatus.Disconnected;
    }

    public void Dispose()
    {
        _peer?.Disconnect();
        _net.PollEvents();
        _net.Stop();
    }
}

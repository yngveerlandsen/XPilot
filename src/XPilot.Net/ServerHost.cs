using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using LiteNetLib;
using LiteNetLib.Utils;
using XPilot.Core;

namespace XPilot.Net;

/// <summary>
/// Runs a <see cref="GameServer"/> over UDP with LiteNetLib on its own thread. It also answers LAN discovery
/// broadcasts and, if configured, registers with a master server so internet players can find it.
/// </summary>
public sealed class ServerHost : IDisposable
{
    private const double MasterHeartbeatSeconds = 15;

    private sealed class PeerConnection(NetPeer peer) : IConnection
    {
        public int Id => peer.Id;
        public int RoundTripMs => peer.RoundTripTime;

        public void Send(byte[] data, Delivery delivery) => peer.SendMessage(data, delivery);
    }

    private readonly GameServer _server;
    private readonly NetManager _net;
    private readonly Dictionary<int, PeerConnection> _connections = [];
    private readonly string _serverId = Guid.NewGuid().ToString("N")[..12];
    private IPEndPoint? _master;
    private Thread? _thread;
    private volatile bool _stopping;

    public ServerHost(GameServer server)
    {
        _server = server;
        var listener = new EventBasedNetListener();
        _net = new NetManager(listener)
        {
            AutoRecycle = true,
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,
            NatPunchEnabled = true,
            MtuOverride = Protocol.Mtu,
            DisconnectTimeout = 10000,
        };
        _net.NatPunchModule.Init(new EventBasedNatPunchListener());

        listener.ConnectionRequestEvent += OnConnectionRequest;
        listener.PeerConnectedEvent += peer =>
        {
            var connection = new PeerConnection(peer);
            _connections[peer.Id] = connection;
            _server.Connected(connection, peer.Tag as string ?? "Pilot");
        };
        listener.PeerDisconnectedEvent += (peer, _) =>
        {
            if (_connections.Remove(peer.Id, out var connection)) _server.Disconnected(connection);
        };
        listener.NetworkReceiveEvent += (peer, reader, _, _) =>
        {
            if (_connections.TryGetValue(peer.Id, out var connection)) _server.Receive(connection, reader.GetRemainingBytes());
        };
        listener.NetworkReceiveUnconnectedEvent += OnUnconnected;
    }

    public GameServer Server => _server;
    public int Port => _net.LocalPort;
    /// <summary>Set if the server thread crashed.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Opens the port and starts ticking on a background thread.</summary>
    /// <returns>False if the port could not be opened (usually because it is in use).</returns>
    public bool Start()
    {
        if (!_net.Start(_server.Options.Port)) return false;
        _master = ParseEndPoint(_server.Options.MasterServer, Protocol.DefaultMasterPort);
        _thread = new Thread(Run) { IsBackground = true, Name = "XPilot server" };
        _thread.Start();
        return true;
    }

    private void Run()
    {
        using var timer = HighResolutionTimer.Begin();
        var clock = Stopwatch.StartNew();
        double nextTick = 0, nextHeartbeat = 0;
        try
        {
            while (!_stopping)
            {
                _net.PollEvents();
                _net.NatPunchModule.PollEvents();

                double now = clock.Elapsed.TotalSeconds;
                for (int i = 0; i < 5 && now >= nextTick; i++)
                {
                    _server.Tick();
                    nextTick += GameConfig.Dt;
                }
                // Hopelessly behind (the machine stalled): skip ahead instead of fast-forwarding.
                if (now - nextTick > 0.5) nextTick = now;

                if (_master != null && now >= nextHeartbeat)
                {
                    RegisterWithMaster(_master);
                    nextHeartbeat = now + MasterHeartbeatSeconds;
                }
                Thread.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            Error = ex;
            Console.Error.WriteLine($"Server crashed: {ex}");
        }
        finally
        {
            _net.Stop();
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        var data = request.Data;
        if (!data.TryGetString(out var key) || key != Protocol.ConnectionKey ||
            !data.TryGetInt(out int version) || !data.TryGetString(out var name))
        {
            request.Reject();
            return;
        }
        if (version != Protocol.Version)
        {
            var reason = new NetDataWriter();
            reason.Put($"Version mismatch: server {Protocol.Version}, client {version}");
            request.Reject(reason);
            return;
        }
        var peer = request.Accept();
        peer.Tag = name;
    }

    private void OnUnconnected(IPEndPoint remote, NetPacketReader reader, UnconnectedMessageType kind)
    {
        if (reader.AvailableBytes < 1 || (MessageType)reader.GetByte() != MessageType.DiscoveryRequest) return;
        var m = new MessageWriter(MessageType.DiscoveryResponse);
        _server.Info.Write(m.Writer);
        _net.SendUnconnectedMessage(m.ToArray(), remote);
    }

    private void RegisterWithMaster(IPEndPoint master)
    {
        var m = new MessageWriter(MessageType.MasterRegister);
        m.Writer.Write(_serverId);
        _server.Info.Write(m.Writer);
        _net.SendUnconnectedMessage(m.ToArray(), master);
        // Keeps a fresh NAT mapping at the master, which it uses to introduce joining clients.
        _net.NatPunchModule.SendNatIntroduceRequest(master, MasterServer.HostToken(_serverId));
    }

    public static IPEndPoint? ParseEndPoint(string? text, int defaultPort)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        string host = text;
        int port = defaultPort;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int p))
        {
            host = text[..colon];
            port = p;
        }
        if (IPAddress.TryParse(host, out var address)) return new IPEndPoint(address, port);
        try
        {
            var addresses = Dns.GetHostAddresses(host);
            var v4 = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            return v4 == null ? null : new IPEndPoint(v4, port);
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_thread != null && Thread.CurrentThread != _thread) _thread.Join(2000);
        if (_thread == null) _net.Stop();
    }
}

internal static class PeerExtensions
{
    /// <summary>
    /// Sends on the single reliable ordered channel, or unreliably. Unreliable packets can't be split, so
    /// anything too big for one packet goes reliably instead.
    /// </summary>
    public static void SendMessage(this NetPeer peer, byte[] data, Delivery delivery)
    {
        var method = delivery == Delivery.Reliable || data.Length > peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable)
            ? DeliveryMethod.ReliableOrdered
            : DeliveryMethod.Unreliable;
        peer.Send(data, method);
    }
}

/// <summary>Asks Windows for 1 ms sleeps, so the server thread wakes up on time for each tick.</summary>
internal sealed class HighResolutionTimer : IDisposable
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);

    private readonly bool _active;

    private HighResolutionTimer(bool active) => _active = active;

    public static HighResolutionTimer Begin()
    {
        if (!OperatingSystem.IsWindows()) return new HighResolutionTimer(false);
        try
        {
            timeBeginPeriod(1);
            return new HighResolutionTimer(true);
        }
        catch (DllNotFoundException)
        {
            return new HighResolutionTimer(false);
        }
    }

    public void Dispose()
    {
        if (_active) timeEndPeriod(1);
    }
}

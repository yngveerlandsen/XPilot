using System.Net;
using System.Net.Sockets;

namespace XPilot.Net;

/// <summary>
/// Turns "host", "host:port" or "ip:port" into an address without blocking the caller. IP addresses are ready
/// at once; names are looked up in the background, retried after a failure, and optionally looked up again
/// now and then so a server follows a DNS change. Call <see cref="Poll"/> regularly from the owning thread.
/// </summary>
public sealed class EndPointLookup
{
    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _retry;
    private readonly TimeSpan? _refresh;
    private Task<IPAddress[]>? _lookup;
    private DateTime _next = DateTime.MinValue;

    /// <param name="retry">How long to wait after a failed lookup before trying again.</param>
    /// <param name="refresh">How often to look a found name up again; null to keep the first answer.</param>
    public EndPointLookup(string text, int defaultPort, TimeSpan retry, TimeSpan? refresh = null)
    {
        Text = text.Trim();
        (_host, _port) = Split(Text, defaultPort);
        _retry = retry;
        _refresh = refresh;
        if (IPAddress.TryParse(_host, out var address)) Result = new IPEndPoint(address, _port);
        else if (_host.Length == 0) Failed = true;
    }

    public string Text { get; }
    /// <summary>The address, once known. A later failed refresh keeps the last good one.</summary>
    public IPEndPoint? Result { get; private set; }
    /// <summary>The last lookup failed (it will be retried).</summary>
    public bool Failed { get; private set; }

    /// <summary>Starts or finishes a lookup as needed.</summary>
    /// <returns>True if <see cref="Result"/> or <see cref="Failed"/> changed.</returns>
    public bool Poll()
    {
        if (_host.Length == 0 || IPAddress.TryParse(_host, out _)) return false;
        var now = DateTime.UtcNow;
        if (_lookup == null)
        {
            if (now < _next || (Result != null && _refresh == null)) return false;
            _lookup = Dns.GetHostAddressesAsync(_host);
            return false;
        }
        if (!_lookup.IsCompleted) return false;

        var found = _lookup.IsCompletedSuccessfully ? Pick(_lookup.Result) : null;
        _lookup = null;
        var before = (Result, Failed);
        if (found != null)
        {
            Result = new IPEndPoint(found, _port);
            Failed = false;
            _next = _refresh is { } refresh ? After(now, refresh) : DateTime.MaxValue;
        }
        else
        {
            Failed = true;
            _next = After(now, _retry);
        }
        return before != (Result, Failed);
    }

    private static DateTime After(DateTime now, TimeSpan delay) => delay >= DateTime.MaxValue - now ? DateTime.MaxValue : now + delay;

    /// <summary>Prefers IPv4, which is what the game has been tested on.</summary>
    private static IPAddress? Pick(IPAddress[] addresses) =>
        addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();

    public static (string Host, int Port) Split(string text, int defaultPort)
    {
        int colon = text.LastIndexOf(':');
        // One colon means host:port; more than one is a bare IPv6 address.
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port)) return (text[..colon], port);
        return (text, defaultPort);
    }
}

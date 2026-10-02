using System.Diagnostics;
using System.Net;

namespace XPilot.Net.Tests;

/// <summary>Name lookups must never block the game loop, and failures must be visible and retried.</summary>
public class LookupTests
{
    // .invalid is reserved and never resolves.
    private const string Nowhere = "xpilot-test.invalid";

    private static bool PollUntil(Func<bool> done, Action poll, double seconds = 10)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            poll();
            if (done()) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    [Theory]
    [InlineData("127.0.0.1", 15345)]
    [InlineData(" 127.0.0.1:2000 ", 2000)]
    public void IpAddresses_AreReadyAtOnce(string text, int port)
    {
        var lookup = new EndPointLookup(text, 15345, TimeSpan.FromSeconds(1));
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, port), lookup.Result);
    }

    [Fact]
    public void Names_AreLookedUpInTheBackground()
    {
        var lookup = new EndPointLookup("localhost:2000", 15345, TimeSpan.FromSeconds(1));
        Assert.True(PollUntil(() => lookup.Result != null, () => lookup.Poll()));
        Assert.Equal(2000, lookup.Result!.Port);
        Assert.False(lookup.Failed);
    }

    [Fact]
    public void UnknownNames_Fail_AndAreRetried()
    {
        var lookup = new EndPointLookup(Nowhere, 15345, TimeSpan.Zero);
        Assert.True(PollUntil(() => lookup.Failed, () => lookup.Poll()));
        Assert.Null(lookup.Result);
        // With no retry delay the next poll starts another lookup instead of giving up.
        lookup.Poll();
        Assert.True(lookup.Failed);
    }

    [Fact]
    public void ServerBrowser_DoesNotBlock_AndReportsAMissingMaster()
    {
        var clock = Stopwatch.StartNew();
        using var browser = new ServerBrowser(Nowhere);
        browser.Poll();
        Assert.True(clock.ElapsedMilliseconds < 500, $"creating the browser took {clock.ElapsedMilliseconds} ms");
        Assert.True(browser.HasMaster);
        Assert.True(PollUntil(() => browser.MasterNotFound, browser.Poll));
        Assert.Null(browser.Master);
    }

    [Fact]
    public void ClientConnection_DoesNotBlock_AndExplainsAnUnknownAddress()
    {
        using var connection = new ClientConnection("Pilot");
        var clock = Stopwatch.StartNew();
        connection.Connect(Nowhere);
        Assert.True(clock.ElapsedMilliseconds < 500, $"Connect took {clock.ElapsedMilliseconds} ms");
        Assert.Equal(ConnectionStatus.Connecting, connection.Status);
        Assert.True(PollUntil(() => connection.Status == ConnectionStatus.Disconnected, connection.Poll));
        Assert.Contains("Could not find", connection.Error);
    }

    [Fact]
    public void DedicatedServer_StartsWithoutItsMaster_AndSaysSo()
    {
        var options = new ServerOptions { Port = 0, MasterServer = Nowhere };
        using var host = new ServerHost(new GameServer(options, [File.ReadAllText(Path.Combine(TestMaps.Directory, "arena.xpm"))]));
        var log = new List<string>();
        host.Log += line => { lock (log) log.Add(line); };
        var clock = Stopwatch.StartNew();
        Assert.True(host.Start());
        Assert.True(clock.ElapsedMilliseconds < 500, $"Start took {clock.ElapsedMilliseconds} ms");
        Assert.True(PollUntil(() => { lock (log) return log.Any(l => l.Contains("Could not find master server")); }, () => { }));
    }
}

using XPilot.Core;
using XPilot.Core.Simulation;
using XPilot.Net;

namespace XPilot.Desktop;

/// <summary>A game being played: a local match against bots, or a connection to a server.</summary>
public interface IGameSession : IMatchView, IDisposable
{
    bool IsNetwork { get; }
    /// <summary>How far between the last two ticks to draw things, 0..1.</summary>
    float Alpha { get; }
    void Update(float dt, ShipInput input);
    /// <summary>Events since the last call, for sound, particles and messages.</summary>
    List<GameEvent> TakeEvents();
}

public sealed class LocalSession : IGameSession
{
    private readonly List<GameEvent> _events = [];
    private float _accumulator;

    public LocalSession(MatchSetup setup)
    {
        Match = new Match(setup);
        _events.AddRange(Match.World.Events);
    }

    public Match Match { get; }
    public World World => Match.World;
    public Ship? Player => Match.Player;
    public bool IsNetwork => false;
    public float Alpha => Math.Clamp(_accumulator / GameConfig.Dt, 0f, 1f);

    public void Update(float dt, ShipInput input)
    {
        _accumulator += MathF.Min(dt, 0.1f);
        while (_accumulator >= GameConfig.Dt)
        {
            Match.Step(input);
            _events.AddRange(Match.World.Events);
            _accumulator -= GameConfig.Dt;
        }
    }

    public List<GameEvent> TakeEvents()
    {
        var list = _events.ToList();
        _events.Clear();
        return list;
    }

    public void Dispose()
    {
    }
}

/// <summary>A connection to a server, and the server itself when this player is hosting.</summary>
public sealed class NetworkSession(ClientConnection connection, ServerHost? host) : IGameSession
{
    public ClientConnection Connection { get; } = connection;
    public GameClient Client => Connection.Client;
    public ServerHost? Host { get; } = host;
    public World World => Client.World;
    public Ship? Player => Client.Player;
    public bool IsNetwork => true;
    /// <summary>The client places everything itself, so the renderer needn't interpolate.</summary>
    public float Alpha => 1f;

    public void Update(float dt, ShipInput input)
    {
        Connection.Poll();
        Client.Update(dt, input);
    }

    public List<GameEvent> TakeEvents() => Client.TakeEvents();

    public void Dispose()
    {
        Connection.Dispose();
        Host?.Dispose();
    }
}

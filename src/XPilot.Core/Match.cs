using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core;

public sealed class MatchSetup
{
    public required Map Map { get; init; }
    public int BotCount { get; init; } = 3;
    public BotDifficulty Difficulty { get; init; } = BotDifficulty.Normal;
    public bool IncludePlayer { get; init; } = true;
    public string PlayerName { get; init; } = "Player";
    public int Seed { get; init; } = Environment.TickCount;
    /// <summary>Dogfight kills needed to win.</summary>
    public int ScoreLimit { get; init; } = 10;
    /// <summary>Ball mode captures needed to win.</summary>
    public int CaptureLimit { get; init; } = 3;
    /// <summary>Seconds (0 = unlimited). Defaults to 5 minutes for dogfight, 10 for ball mode.</summary>
    public float? TimeLimit { get; init; }
    public int? Laps { get; init; }
    public GameConfig? Config { get; init; }
}

/// <summary>A world plus the bots flying in it. The player's input is fed in from outside.</summary>
public sealed class Match
{
    private static readonly string[] BotNames =
        ["Vega", "Orion", "Lyra", "Draco", "Nova", "Rigel", "Sirius", "Altair", "Deneb", "Mira", "Castor", "Pollux"];

    private readonly List<BotController> _bots = [];
    private readonly ShipInput[] _inputs;

    public Match(MatchSetup setup)
    {
        Setup = setup;
        var map = setup.Map;
        var config = (setup.Config ?? new GameConfig()).Clone();
        IGameRules rules;
        switch (map.Mode)
        {
            case GameModeKind.Race:
                rules = new RaceRules(setup.Laps ?? map.Laps);
                config.ShipCollisionsKill = false;
                break;
            case GameModeKind.Ball:
                rules = new BallRules(setup.CaptureLimit, setup.TimeLimit ?? 600f);
                break;
            default:
                rules = new DogfightRules(setup.ScoreLimit, setup.TimeLimit ?? 300f);
                break;
        }

        World = new World(map, config, rules, setup.Seed);
        Nav = new NavGrid(map);
        bool teams = map.Mode == GameModeKind.Ball;

        if (setup.IncludePlayer)
        {
            Player = World.AddShip(setup.PlayerName, false);
            if (teams) Player.Team = Teams.Red;
        }
        int botCount = Math.Clamp(setup.BotCount, 0, map.Bases.Count - (Player != null ? 1 : 0));
        var rng = new Random(setup.Seed);
        for (int i = 0; i < botCount; i++)
        {
            var ship = World.AddShip(BotNames[i % BotNames.Length], true);
            // Fill teams alternately, starting with the side the player is not on.
            if (teams) ship.Team = (i % 2 == 0) == (Player != null) ? Teams.Blue : Teams.Red;
            _bots.Add(new BotController(World, Nav, ship, setup.Difficulty, rng.Next()));
        }

        _inputs = new ShipInput[World.Ships.Count];
        World.Start();
    }

    public MatchSetup Setup { get; }
    public World World { get; }
    public NavGrid Nav { get; }
    public Ship? Player { get; }
    public IReadOnlyList<BotController> Bots => _bots;

    public void Step(ShipInput playerInput = default)
    {
        if (Player != null) _inputs[Player.Id] = playerInput;
        foreach (var bot in _bots) _inputs[bot.Ship.Id] = bot.Update();
        World.Step(_inputs);
    }
}

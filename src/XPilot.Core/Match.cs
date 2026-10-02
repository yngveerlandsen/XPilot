using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core;

public sealed class MatchSetup
{
    public required Map Map { get; init; }
    /// <summary>Bots wanted. Humans who join take over bot seats when the map is full.</summary>
    public int BotCount { get; init; } = 3;
    public BotDifficulty Difficulty { get; init; } = BotDifficulty.Normal;
    /// <summary>Add a local player, <see cref="Match.Player"/>. A server leaves this off and adds humans as they join.</summary>
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

/// <summary>What the renderer and HUD draw: a world, and the ship the local player flies (if any).</summary>
public interface IMatchView
{
    World World { get; }
    Ship? Player { get; }
}

/// <summary>
/// A world plus whoever flies in it: bots, and humans whose inputs are fed in from outside. Bots fill the
/// seats humans don't use, up to <see cref="MatchSetup.BotCount"/>.
/// </summary>
public sealed class Match : IMatchView
{
    private static readonly string[] BotNames =
        ["Vega", "Orion", "Lyra", "Draco", "Nova", "Rigel", "Sirius", "Altair", "Deneb", "Mira", "Castor", "Pollux"];

    private readonly List<BotController> _bots = [];
    private readonly List<Ship> _humans = [];
    private readonly Dictionary<int, ShipInput> _humanInputs = [];
    private readonly Random _rng;
    private ShipInput[] _inputs = [];
    private bool _started;

    public Match(MatchSetup setup)
    {
        Setup = setup;
        World = new World(setup.Map, CreateConfig(setup), CreateRules(setup), setup.Seed);
        Nav = new NavGrid(setup.Map);
        _rng = new Random(setup.Seed);

        if (setup.IncludePlayer)
        {
            Player = World.AddShip(setup.PlayerName, false);
            if (IsTeamMode) Player.Team = Teams.Red;
            _humans.Add(Player);
        }
        RebalanceBots();

        World.Start();
        _started = true;
    }

    public MatchSetup Setup { get; }
    public World World { get; }
    public NavGrid Nav { get; }
    /// <summary>The local player, when <see cref="MatchSetup.IncludePlayer"/> is set.</summary>
    public Ship? Player { get; }
    public IReadOnlyList<BotController> Bots => _bots;
    public IReadOnlyList<Ship> Humans => _humans;
    /// <summary>Most ships the match holds: one per base.</summary>
    public int Capacity => World.Map.Bases.Count;
    private bool IsTeamMode => World.Map.Mode == GameModeKind.Ball;

    /// <summary>The settings a match with this setup runs on.</summary>
    public static GameConfig CreateConfig(MatchSetup setup)
    {
        var config = (setup.Config ?? new GameConfig()).Clone();
        if (setup.Map.Mode == GameModeKind.Race) config.ShipCollisionsKill = false;
        return config;
    }

    public static IGameRules CreateRules(MatchSetup setup) => setup.Map.Mode switch
    {
        GameModeKind.Race => new RaceRules(setup.Laps ?? setup.Map.Laps),
        GameModeKind.Ball => new BallRules(setup.CaptureLimit, setup.TimeLimit ?? 600f),
        _ => new DogfightRules(setup.ScoreLimit, setup.TimeLimit ?? 300f),
    };

    /// <summary>Adds a human, taking over a bot's seat if the map is full. They spawn on the next tick.</summary>
    /// <returns>The new ship, or null if every seat already has a human in it.</returns>
    public Ship? AddHuman(string name)
    {
        if (_humans.Count >= Capacity) return null;
        var ship = World.AddShip(name, false);
        if (IsTeamMode) ship.Team = PickTeam(humansOnly: true);
        _humans.Add(ship);
        Joined(ship);
        RebalanceBots();
        return ship;
    }

    /// <summary>Removes a human who left; a bot takes the seat if fewer than <see cref="MatchSetup.BotCount"/> are flying.</summary>
    public void RemoveHuman(Ship ship)
    {
        if (!_humans.Remove(ship)) return;
        _humanInputs.Remove(ship.Id);
        World.RemoveShip(ship);
        RebalanceBots();
    }

    /// <summary>Moves a human to the other team in ball mode. They leave the field and respawn at their new base.</summary>
    public void SwitchTeam(Ship ship)
    {
        if (!IsTeamMode || !_humans.Contains(ship)) return;
        World.Despawn(ship, 1f);
        ship.Team = Teams.Opponent(ship.Team);
        RebalanceBots();
    }

    public void SetInput(Ship ship, ShipInput input) => _humanInputs[ship.Id] = input;

    /// <summary>Steps with the local player's input.</summary>
    public void Step(ShipInput playerInput)
    {
        if (Player != null) SetInput(Player, playerInput);
        Step();
    }

    /// <summary>Steps with the inputs last given to <see cref="SetInput"/> for each human.</summary>
    public void Step()
    {
        int size = 0;
        foreach (var s in World.Ships) size = Math.Max(size, s.Id + 1);
        if (_inputs.Length < size) _inputs = new ShipInput[Math.Max(size, _inputs.Length * 2)];
        Array.Clear(_inputs);
        foreach (var (id, input) in _humanInputs)
        {
            if (id < _inputs.Length) _inputs[id] = input;
        }
        foreach (var bot in _bots) _inputs[bot.Ship.Id] = bot.Update();
        World.Step(_inputs);
    }

    /// <summary>
    /// Adds or removes bots so there are <see cref="MatchSetup.BotCount"/> of them in the seats humans leave free,
    /// then evens out the teams by moving bots across.
    /// </summary>
    private void RebalanceBots()
    {
        int wanted = Math.Clamp(Setup.BotCount, 0, Math.Max(0, Capacity - _humans.Count));
        while (_bots.Count > wanted)
        {
            var bot = BotToRemove(IsTeamMode ? LargerTeam() : Teams.None);
            _bots.Remove(bot);
            World.RemoveShip(bot.Ship);
        }
        while (_bots.Count < wanted)
        {
            var ship = World.AddShip(NextBotName(), true);
            if (IsTeamMode) ship.Team = PickTeam(humansOnly: false);
            _bots.Add(new BotController(World, Nav, ship, Setup.Difficulty, _rng.Next()));
            Joined(ship);
        }

        if (!IsTeamMode) return;
        while (Math.Abs(TeamSize(Teams.Red, false) - TeamSize(Teams.Blue, false)) > 1)
        {
            int larger = LargerTeam();
            var bot = _bots.LastOrDefault(b => b.Ship.Team == larger);
            if (bot == null) break;
            World.Despawn(bot.Ship, 1f);
            bot.Ship.Team = Teams.Opponent(larger);
        }
    }

    private BotController BotToRemove(int team) => _bots.LastOrDefault(b => b.Ship.Team == team) ?? _bots[^1];

    /// <summary>A ship added after the start is dead until it respawns on the next tick.</summary>
    private void Joined(Ship ship)
    {
        if (!_started) return;
        ship.Alive = false;
        ship.RespawnTimer = 0f;
        World.Rules.OnShipJoined(World, ship);
    }

    private int TeamSize(int team, bool humansOnly) => World.Ships.Count(s => s.Team == team && (!humansOnly || !s.IsBot));

    private int LargerTeam() => TeamSize(Teams.Blue, false) > TeamSize(Teams.Red, false) ? Teams.Blue : Teams.Red;

    /// <summary>The team with fewer humans (or fewer ships in total), Red on a tie.</summary>
    private int PickTeam(bool humansOnly)
    {
        if (humansOnly)
        {
            int red = TeamSize(Teams.Red, true), blue = TeamSize(Teams.Blue, true);
            if (red != blue) return red < blue ? Teams.Red : Teams.Blue;
        }
        return TeamSize(Teams.Blue, false) < TeamSize(Teams.Red, false) ? Teams.Blue : Teams.Red;
    }

    private string NextBotName()
    {
        foreach (var name in BotNames)
        {
            if (!World.Ships.Any(s => s.Name == name)) return name;
        }
        return BotNames[_bots.Count % BotNames.Length] + " " + (_bots.Count / BotNames.Length + 1);
    }
}

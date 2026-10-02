using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XPilot.Core;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;
using XPilot.Net;

namespace XPilot.Desktop.Screens;

public sealed class MainMenuScreen : Screen
{
    private enum Item { Mode, Map, Bots, Difficulty, Name, Start, Host, Join, Settings, Quit }

    private static readonly Item[] Items = Enum.GetValues<Item>();

    private int _selected = Array.IndexOf(Items, Item.Start);
    private GameModeKind _mode;
    private int _mapIndex;
    private int _bots;
    private BotDifficulty _difficulty;
    private float _time;
    /// <summary>The name being typed, or null when not editing it.</summary>
    private string? _editingName;
    private string? _message;

    public MainMenuScreen(XPilotGame game) : base(game)
    {
        var s = game.Settings;
        _mode = Enum.TryParse<GameModeKind>(s.Mode, true, out var mode) ? mode : GameModeKind.Dogfight;
        _difficulty = Enum.TryParse<BotDifficulty>(s.Difficulty, true, out var d) ? d : BotDifficulty.Normal;
        _bots = s.Bots;
        SelectRememberedMap();
    }

    private IReadOnlyList<Map> MapsForMode => Game.Maps.ForMode(_mode);
    private Map? CurrentMap => MapsForMode.Count > 0 ? MapsForMode[Math.Clamp(_mapIndex, 0, MapsForMode.Count - 1)] : null;
    private int MaxBots => CurrentMap is { } m ? Math.Min(7, m.Bases.Count - 1) : 0;
    private int MinBots => _mode == GameModeKind.Race ? 0 : 1;

    private static string ModeName(GameModeKind mode) => mode switch
    {
        GameModeKind.Race => "RACE",
        GameModeKind.Ball => "CAPTURE THE BALL",
        _ => "DOGFIGHT",
    };

    public override void Enter() => Sounds.StopAll();

    public override void Update(float dt)
    {
        _time += dt;
        Game.Background.Update(dt, Game.Settings.ParticleDensity);

        if (_editingName != null)
        {
            EditName();
            return;
        }

        if (Input.MenuUp) Move(-1);
        if (Input.MenuDown) Move(1);
        if (Input.MenuLeft) Change(-1);
        if (Input.MenuRight) Change(1);
        if (Input.MenuSelect) Activate();
        if (Input.MenuBack) Game.Exit();
    }

    private void EditName()
    {
        if (Input.WasPressed(Keys.Escape))
        {
            _editingName = null;
            return;
        }
        if (Input.WasPressed(Keys.Enter))
        {
            Game.Settings.PlayerName = Protocol.CleanName(_editingName);
            Game.Settings.Save();
            _editingName = null;
            Sounds.Play("select", 0.6f, 0.3f);
            return;
        }
        _editingName = Input.EditText(_editingName!, Protocol.MaxNameLength);
    }

    private void Move(int delta)
    {
        _message = null;
        _selected = (_selected + delta + Items.Length) % Items.Length;
        Sounds.Play("select", 0.6f);
    }

    private void Change(int delta)
    {
        switch (Items[_selected])
        {
            case Item.Mode:
                _mode = (GameModeKind)(((int)_mode + delta + 3) % 3);
                SelectRememberedMap();
                break;
            case Item.Map:
                if (MapsForMode.Count > 0) _mapIndex = (_mapIndex + delta + MapsForMode.Count) % MapsForMode.Count;
                break;
            case Item.Bots:
                _bots = Math.Clamp(_bots + delta, MinBots, MaxBots);
                break;
            case Item.Difficulty:
                _difficulty = (BotDifficulty)(((int)_difficulty + delta + 3) % 3);
                break;
            default:
                return;
        }
        _bots = Math.Clamp(_bots, MinBots, Math.Max(MinBots, MaxBots));
        Sounds.Play("select", 0.6f, 0.3f);
    }

    private void Activate()
    {
        switch (Items[_selected])
        {
            case Item.Start:
                StartMatch();
                break;
            case Item.Host:
                HostGame();
                break;
            case Item.Join:
                if (CurrentMap is { } joinMap) RememberChoices(joinMap);
                Sounds.Play("select", 0.6f);
                Game.SetScreen(new JoinScreen(Game));
                break;
            case Item.Settings:
                if (CurrentMap is { } settingsMap) RememberChoices(settingsMap);
                Sounds.Play("select", 0.6f);
                Game.SetScreen(new SettingsScreen(Game));
                break;
            case Item.Name:
                _editingName = Game.Settings.PlayerName;
                break;
            case Item.Quit:
                Game.Exit();
                break;
            default:
                Change(1);
                break;
        }
    }

    private void StartMatch()
    {
        var map = CurrentMap;
        if (map == null) return;
        RememberChoices(map);
        Sounds.Play("go", 0.7f);
        Game.SetScreen(new PlayScreen(Game, new MatchSetup
        {
            Map = map,
            BotCount = _bots,
            Difficulty = _difficulty,
            PlayerName = Game.Settings.PlayerName,
            ScoreLimit = Game.Settings.ScoreLimit,
            CaptureLimit = Game.Settings.CaptureLimit,
            TimeLimit = Game.Settings.TimeLimitSeconds,
            Laps = Game.Settings.LapsOrDefault,
        }));
    }

    /// <summary>
    /// Starts a server in this process with the menu's mode, map and bots, and joins it. The rotation is every
    /// map of the mode, starting with the chosen one.
    /// </summary>
    private void HostGame()
    {
        var map = CurrentMap;
        if (map == null) return;
        RememberChoices(map);
        var s = Game.Settings;
        var maps = MapsForMode;
        int first = Math.Max(0, maps.ToList().IndexOf(map));
        var rotation = maps.Skip(first).Concat(maps.Take(first)).Where(m => m.SourcePath != null).Select(m => File.ReadAllText(m.SourcePath!));

        var options = new ServerOptions
        {
            Name = $"{s.PlayerName}'s game",
            Port = s.HostPort,
            BotCount = _bots,
            Difficulty = _difficulty,
            MasterServer = s.MasterServer,
            ScoreLimit = s.ScoreLimit,
            CaptureLimit = s.CaptureLimit,
            TimeLimit = s.TimeLimitSeconds,
            Laps = s.LapsOrDefault,
        };
        var host = new ServerHost(new GameServer(options, rotation));
        if (!host.Start())
        {
            host.Dispose();
            _message = $"COULD NOT OPEN UDP PORT {s.HostPort} - IS ANOTHER SERVER RUNNING?";
            Sounds.Play("bounce", 0.6f);
            return;
        }

        var connection = new ClientConnection(s.PlayerName);
        connection.Connect(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, host.Port));
        Sounds.Play("go", 0.7f);
        Game.SetScreen(new PlayScreen(Game, new NetworkSession(connection, host)));
    }

    private void RememberChoices(Map map)
    {
        var s = Game.Settings;
        s.Mode = _mode.ToString();
        s.Bots = _bots;
        s.Difficulty = _difficulty.ToString();
        switch (_mode)
        {
            case GameModeKind.Race: s.LastRaceMap = map.Name; break;
            case GameModeKind.Ball: s.LastBallMap = map.Name; break;
            default: s.LastDogfightMap = map.Name; break;
        }
        s.Save();
    }

    private void SelectRememberedMap()
    {
        var name = _mode switch
        {
            GameModeKind.Race => Game.Settings.LastRaceMap,
            GameModeKind.Ball => Game.Settings.LastBallMap,
            _ => Game.Settings.LastDogfightMap,
        };
        var maps = MapsForMode;
        _mapIndex = 0;
        for (int i = 0; i < maps.Count; i++)
        {
            if (maps[i].Name == name) _mapIndex = i;
        }
        _bots = Math.Clamp(_bots, MinBots, Math.Max(MinBots, MaxBots));
    }

    public override void Draw(float dt)
    {
        var vp = Game.GraphicsDevice.Viewport;
        float s = vp.Height / 720f;
        var pb = Primitives;
        var bindings = InputBindings.FromSettings(Game.Settings);

        Game.Background.Draw(Game, pb, vp);
        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);

        float cx = vp.Width / 2f;
        float glow = 0.85f + 0.15f * MathF.Sin(_time * 2f);
        VectorFont.Draw(pb, "XPILOT", new Vector2(cx, 70 * s), 72f * s, Palette.Accent * glow, TextAlign.Center, 5f * s, 1.2f);
        VectorFont.Draw(pb, "A NEW FLIGHT", new Vector2(cx, 160 * s), 14f * s, Palette.TextDim, TextAlign.Center);

        float y = 200 * s;
        for (int i = 0; i < Items.Length; i++)
        {
            var item = Items[i];
            bool selected = i == _selected;
            var color = selected ? Palette.Text : Palette.TextDim * 0.8f;
            float size = (item is Item.Start or Item.Host or Item.Join ? 20f : 16f) * s;
            if (item == Item.Start) y += 14 * s;
            var (label, value) = item switch
            {
                Item.Mode => ("MODE", ModeName(_mode)),
                Item.Map => ("MAP", CurrentMap?.Name.ToUpperInvariant() ?? "NONE"),
                Item.Bots => ("BOTS", _bots.ToString()),
                Item.Difficulty => ("SKILL", _difficulty.ToString().ToUpperInvariant()),
                Item.Name => ("NAME", _editingName != null
                    ? _editingName + (((int)(_time * 3f) & 1) == 0 ? "_" : " ")
                    : Game.Settings.PlayerName),
                Item.Start => ("PLAY " + ModeName(_mode) + " VS BOTS", null),
                Item.Host => ("HOST " + ModeName(_mode) + " GAME", null),
                Item.Join => ("JOIN NETWORK GAME", null),
                Item.Settings => ("SETTINGS", null),
                _ => ("QUIT", (string?)null),
            };
            if (selected) color *= 0.85f + 0.15f * MathF.Sin(_time * 8f);
            if (value != null)
            {
                VectorFont.Draw(pb, label, new Vector2(cx - 24 * s, y), size, color, TextAlign.Right);
                bool arrows = selected && item != Item.Name;
                VectorFont.Draw(pb, arrows ? $"< {value} >" : value, new Vector2(cx + 24 * s, y), size,
                    item == Item.Name && _editingName != null ? Palette.Accent : color);
            }
            else
            {
                VectorFont.Draw(pb, selected ? $">  {label}  <" : label, new Vector2(cx, y), size, color, TextAlign.Center);
            }
            y += size + 14 * s;
        }

        if (_message != null)
        {
            VectorFont.Draw(pb, _message, new Vector2(cx, y + 4 * s), 10f * s, Palette.Warning, TextAlign.Center);
            y += 20 * s;
        }

        if (CurrentMap is { } map)
        {
            y += 10 * s;
            VectorFont.Draw(pb, map.Description, new Vector2(cx, y), 10f * s, Palette.TextDim, TextAlign.Center);
        }

        float hy = vp.Height - 90 * s;
        string controls = $"TURN {bindings.Describe(GameAction.TurnLeft)} {bindings.Describe(GameAction.TurnRight)}   " +
                          $"THRUST {bindings.Describe(GameAction.Thrust)}   FIRE {bindings.Describe(GameAction.Fire)}   " +
                          $"SHIELD {bindings.Describe(GameAction.Shield)}   GRAB {bindings.Describe(GameAction.Grab)}";
        VectorFont.Draw(pb, controls, new Vector2(cx, hy), 10f * s, Palette.TextDim, TextAlign.Center);
        VectorFont.Draw(pb, "TAB SCORES   ESC PAUSE   F11 FULLSCREEN   GAMEPAD SUPPORTED",
            new Vector2(cx, hy + 22 * s), 10f * s, Palette.TextDim * 0.8f, TextAlign.Center);
        VectorFont.Draw(pb, _editingName != null ? "TYPE YOUR NAME   ENTER OK   ESC CANCEL" : "ARROWS CHOOSE   ENTER START   ESC QUIT",
            new Vector2(cx, hy + 50 * s), 10f * s, Palette.TextDim * 0.6f, TextAlign.Center);

        if (Game.Maps.Errors.Count > 0)
        {
            VectorFont.Draw(pb, $"{Game.Maps.Errors.Count} MAP(S) FAILED TO LOAD - SEE CONSOLE",
                new Vector2(cx, 200 * s), 9f * s, Palette.Warning, TextAlign.Center);
        }
        pb.End();
    }
}

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XPilot.Core;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;

namespace XPilot.Desktop.Screens;

public sealed class MainMenuScreen : Screen
{
    private enum Item { Mode, Map, Bots, Difficulty, Controls, Start, Quit }

    private static readonly Item[] Items = Enum.GetValues<Item>();

    private int _selected = Array.IndexOf(Items, Item.Start);
    private GameModeKind _mode;
    private int _mapIndex;
    private int _bots;
    private BotDifficulty _difficulty;
    private float _time;
    private Vector2 _drift;

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
        _drift += new Vector2(18f, 6f) * dt;

        if (Input.MenuUp) Move(-1);
        if (Input.MenuDown) Move(1);
        if (Input.MenuLeft) Change(-1);
        if (Input.MenuRight) Change(1);
        if (Input.MenuSelect) Activate();
        if (Input.MenuBack) Game.Exit();
    }

    private void Move(int delta)
    {
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
            case Item.Controls:
                Game.Settings.ControlPreset = Game.Settings.ControlPreset == InputBindings.ClassicPreset
                    ? InputBindings.ModernPreset
                    : InputBindings.ClassicPreset;
                Game.Settings.Save();
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

        Sounds.Play("go", 0.7f);
        Game.SetScreen(new PlayScreen(Game, new MatchSetup
        {
            Map = map,
            BotCount = _bots,
            Difficulty = _difficulty,
            PlayerName = s.PlayerName,
        }));
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

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        Game.Starfield.Draw(pb, _drift, vp.Width, vp.Height, _time);

        float cx = vp.Width / 2f;
        float glow = 0.85f + 0.15f * MathF.Sin(_time * 2f);
        VectorFont.Draw(pb, "XPILOT", new Vector2(cx, 70 * s), 72f * s, Palette.Accent * glow, TextAlign.Center, 5f * s, 1.2f);
        VectorFont.Draw(pb, "A NEW FLIGHT", new Vector2(cx, 160 * s), 14f * s, Palette.TextDim, TextAlign.Center);

        float y = 220 * s;
        for (int i = 0; i < Items.Length; i++)
        {
            var item = Items[i];
            bool selected = i == _selected;
            var color = selected ? Palette.Text : Palette.TextDim * 0.8f;
            float size = (item is Item.Start ? 22f : 16f) * s;
            if (item == Item.Start) y += 14 * s;
            var (label, value) = item switch
            {
                Item.Mode => ("MODE", ModeName(_mode)),
                Item.Map => ("MAP", CurrentMap?.Name.ToUpperInvariant() ?? "NONE"),
                Item.Bots => ("BOTS", _bots.ToString()),
                Item.Difficulty => ("SKILL", _difficulty.ToString().ToUpperInvariant()),
                Item.Controls => ("CONTROLS", Game.Settings.ControlPreset.ToUpperInvariant()),
                Item.Start => ("START " + ModeName(_mode), null),
                _ => ("QUIT", (string?)null),
            };
            if (selected) color *= 0.85f + 0.15f * MathF.Sin(_time * 8f);
            if (value != null)
            {
                VectorFont.Draw(pb, label, new Vector2(cx - 24 * s, y), size, color, TextAlign.Right);
                VectorFont.Draw(pb, selected ? $"< {value} >" : value, new Vector2(cx + 24 * s, y), size, color);
            }
            else
            {
                VectorFont.Draw(pb, selected ? $">  {label}  <" : label, new Vector2(cx, y), size, color, TextAlign.Center);
            }
            y += size + 18 * s;
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
        VectorFont.Draw(pb, "ARROWS CHOOSE   ENTER START   ESC QUIT",
            new Vector2(cx, hy + 50 * s), 10f * s, Palette.TextDim * 0.6f, TextAlign.Center);

        if (Game.Maps.Errors.Count > 0)
        {
            VectorFont.Draw(pb, $"{Game.Maps.Errors.Count} MAP(S) FAILED TO LOAD - SEE CONSOLE",
                new Vector2(cx, 200 * s), 9f * s, Palette.Warning, TextAlign.Center);
        }
        pb.End();
    }
}

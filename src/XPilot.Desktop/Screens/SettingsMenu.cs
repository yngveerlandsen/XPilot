using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XPilot.Core;
using XPilot.Core.AI;
using XPilot.Core.Simulation;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;
using XPilot.Net;

namespace XPilot.Desktop.Screens;

/// <summary>The pages of the settings menu.</summary>
public enum SettingsPage { Game, Match, Physics, Events, Video, Audio, Controls, Network }

/// <summary>
/// Tabbed settings pages. Every change is saved and applied at once. Runs as its own screen from the main
/// menu, and as an overlay in the pause menu so a game in progress keeps going.
/// </summary>
public sealed class SettingsMenu(XPilotGame game, bool inGame, SettingsPage page = SettingsPage.Game)
{
    /// <param name="Change">Left/right (-1/+1), or Enter (+1) when there is no <paramref name="Activate"/>.</param>
    /// <param name="Note">A line explaining the row, shown while it is selected.</param>
    private sealed record Item(string Label, Func<string> Value, Action<int>? Change = null, Action? Activate = null, string? Note = null);

    private static readonly SettingsPage[] Tabs = Enum.GetValues<SettingsPage>();
    private static readonly int[] ScoreLimits = [5, 10, 15, 20, 25, 30, 0];
    private static readonly int[] TeamScoreLimits = [10, 15, 20, 30, 50, 0];
    private static readonly int[] CaptureLimits = [1, 2, 3, 4, 5, 7, 10, 0];
    private static readonly int[] LivesCounts = [1, 2, 3, 4, 5, 7, 10];
    private static readonly int[] HillScoreLimits = [30, 45, 60, 90, 120, 180, 0];
    private static readonly int[] TimeLimits = [-1, 2, 3, 5, 10, 15, 20, 30, 0];
    private static readonly int[] LapCounts = [0, 1, 2, 3, 4, 5, 7, 10];
    private static readonly int[] GravityLevels = [0, 25, 50, 75, 100, 150, 200, 300];
    private static readonly int[] PowerLevels = [50, 75, 100, 125, 150, 200];
    private static readonly int[] FuelLevels = [0, 25, 50, 100, 150, 200, 300];
    private static readonly int[] FireRates = [50, 75, 100, 150, 200, 300];
    private static readonly int[] EventLengths = [5, 10, 15, 20, 30];
    private static readonly float[] ShakeLevels = [0f, 0.5f, 1f];
    private static readonly string[] ParticleLevels = ["Low", "Normal", "High"];

    private int _tab = Array.IndexOf(Tabs, page);
    /// <summary>-1 is the tab bar, otherwise a row on the current tab.</summary>
    private int _row = -1;
    /// <summary>The first row shown, when a page has more rows than fit.</summary>
    private int _scroll;
    /// <summary>A text field being typed into, with what to do with the result.</summary>
    private (string Text, int Max, Action<string> Commit)? _editing;
    private GameAction? _capturing;
    private string? _message;

    public bool IsOpen { get; private set; } = true;

    private Settings S => game.Settings;
    private RuleOptions R => game.Settings.Rules;
    private InputState Input => game.Input;

    private List<Item> Items() => Tabs[_tab] switch
    {
        SettingsPage.Game =>
        [
            new("NAME", () => S.PlayerName, Activate: () => Edit(S.PlayerName, Protocol.MaxNameLength, t => S.PlayerName = Protocol.CleanName(t))),
            new("SKILL", () => S.Difficulty.ToUpperInvariant(),
                d => S.Difficulty = Cycle(Enum.GetNames<BotDifficulty>(), S.Difficulty, d)),
            new("SHIP NAMES", () => OnOff(S.ShowShipNames), _ => S.ShowShipNames = !S.ShowShipNames),
        ],
        SettingsPage.Match =>
        [
            new("DOGFIGHT KILLS TO WIN", () => Limit(S.ScoreLimit), d => S.ScoreLimit = Cycle(ScoreLimits, S.ScoreLimit, d)),
            new("TEAM KILLS TO WIN", () => Limit(S.TeamScoreLimit), d => S.TeamScoreLimit = Cycle(TeamScoreLimits, S.TeamScoreLimit, d),
                Note: "TEAM DOGFIGHT: KILLS A TEAM NEEDS"),
            new("BALL CAPTURES TO WIN", () => Limit(S.CaptureLimit), d => S.CaptureLimit = Cycle(CaptureLimits, S.CaptureLimit, d)),
            new("LIVES", () => S.Lives.ToString(), d => S.Lives = Cycle(LivesCounts, S.Lives, d),
                Note: "LAST PILOT STANDING: LOSE THEM ALL AND YOU ARE OUT"),
            new("HILL SECONDS TO WIN", () => Limit(S.HillScoreLimit), d => S.HillScoreLimit = Cycle(HillScoreLimits, S.HillScoreLimit, d),
                Note: "KING OF THE HILL: SECONDS ALONE ON THE HILL TO WIN"),
            new("TIME LIMIT", () => S.TimeLimitMinutes switch { < 0 => "MODE DEFAULT", 0 => "NONE", var m => $"{m} MIN" },
                d => S.TimeLimitMinutes = Cycle(TimeLimits, S.TimeLimitMinutes, d)),
            new("RACE LAPS", () => S.Laps == 0 ? "MAP DEFAULT" : S.Laps.ToString(), d => S.Laps = Cycle(LapCounts, S.Laps, d)),
        ],
        SettingsPage.Physics =>
        [
            new("GRAVITY", () => R.GravityPercent == 0 ? "OFF" : Percent(R.GravityPercent), d => R.GravityPercent = Cycle(GravityLevels, R.GravityPercent, d),
                Note: "MAP GRAVITY AND ATTRACTORS"),
            new("ENGINE POWER", () => Percent(R.ThrustPercent), d => R.ThrustPercent = Cycle(PowerLevels, R.ThrustPercent, d)),
            new("TOP SPEED", () => Percent(R.SpeedPercent), d => R.SpeedPercent = Cycle(PowerLevels, R.SpeedPercent, d)),
            new("FUEL USE", () => R.FuelUsagePercent == 0 ? "NONE - UNLIMITED FUEL" : Percent(R.FuelUsagePercent),
                d => R.FuelUsagePercent = Cycle(FuelLevels, R.FuelUsagePercent, d), Note: "FUEL BURNED BY THRUST, SHIELD AND GUNS"),
            new("BULLET SPEED", () => Percent(R.BulletSpeedPercent), d => R.BulletSpeedPercent = Cycle(PowerLevels, R.BulletSpeedPercent, d)),
            new("FIRE RATE", () => Percent(R.FireRatePercent), d => R.FireRatePercent = Cycle(FireRates, R.FireRatePercent, d)),
            new("WALL CRASHES", () => R.Walls.ToString().ToUpperInvariant(), d => R.Walls = Cycle(Enum.GetValues<WallDamage>(), R.Walls, d),
                Note: "FORGIVING TAKES TWICE THE SPEED TO CRASH - OFF NEVER CRASHES"),
            new("RAMMING", () => R.ShipCollisions ? "DEADLY" : "BOUNCE", _ => R.ShipCollisions = !R.ShipCollisions,
                Note: "SHIPS COLLIDING HARD BOTH EXPLODE, OR BOUNCE OFF"),
            new("RESET TO NORMAL", () => R.IsStandardPhysics ? "(NOTHING CHANGED)" : "", Activate: () =>
            {
                R.ResetPhysics();
                _message = "NORMAL PHYSICS RESTORED";
            }),
        ],
        SettingsPage.Events => EventItems(),
        SettingsPage.Video =>
        [
            new("FULLSCREEN", () => OnOff(S.Fullscreen), _ =>
            {
                S.Fullscreen = !S.Fullscreen;
                game.ApplyVideoSettings();
            }),
            new("VSYNC", () => OnOff(S.VSync), _ =>
            {
                S.VSync = !S.VSync;
                game.ApplyVideoSettings();
            }),
            new("ANTIALIASING", () => OnOff(S.Antialiasing), _ =>
            {
                S.Antialiasing = !S.Antialiasing;
                game.ApplyVideoSettings();
            }),
            new("SCREEN SHAKE", () => S.ScreenShake switch { <= 0f => "OFF", < 1f => "LOW", _ => "FULL" },
                d => S.ScreenShake = Cycle(ShakeLevels, S.ScreenShake, d)),
            new("PARTICLES", () => S.Particles.ToUpperInvariant(), d => S.Particles = Cycle(ParticleLevels, S.Particles, d)),
            new("SHOW FPS", () => OnOff(S.ShowFps), _ => S.ShowFps = !S.ShowFps),
        ],
        SettingsPage.Audio =>
        [
            new("SOUND EFFECTS", () => VolumeBar(S.Volume), d =>
            {
                S.Volume = Math.Clamp(MathF.Round(S.Volume * 10f + d) / 10f, 0f, 1f);
                game.Sounds.Volume = S.Volume;
                game.Sounds.Play("fire", 0.8f);
            }),
            new("MUSIC", () => VolumeBar(S.MusicVolume), d => S.MusicVolume = Math.Clamp(MathF.Round(S.MusicVolume * 10f + d) / 10f, 0f, 1f)),
            new("NOW PLAYING", () => game.Music.TrackCount == 0 ? "NO MUSIC FOUND"
                    : S.MusicVolume <= 0f ? "MUSIC OFF"
                    : game.Music.NowPlaying ?? "-",
                Activate: () =>
                {
                    if (S.MusicVolume > 0f) game.Music.Skip();
                }),
            new("MUTE IN BACKGROUND", () => OnOff(S.MuteInBackground), _ => S.MuteInBackground = !S.MuteInBackground),
        ],
        SettingsPage.Controls => ControlItems(),
        _ =>
        [
            new("HOST PORT", () => S.HostPort.ToString(), Activate: () => Edit(S.HostPort.ToString(), 5, t =>
            {
                if (int.TryParse(t, out int port) && port is > 0 and < 65536) S.HostPort = port;
                else _message = "PORTS GO FROM 1 TO 65535";
            })),
            new("MASTER SERVER", () => S.EffectiveMasterServer?.ToUpperInvariant() ?? "NONE (LAN ONLY)",
                Activate: () => Edit(S.EffectiveMasterServer ?? "", 64, t =>
                    S.MasterServer = t.Trim() == Settings.DefaultMasterServer ? null : t.Trim())),
            new("LIST MY HOSTED GAMES", () => OnOff(S.ListHostedGames), _ => S.ListHostedGames = !S.ListHostedGames),
            new("RESET MASTER SERVER", () => "", Activate: () => S.MasterServer = null),
            new("RESET HOST PORT", () => "", Activate: () => S.HostPort = Protocol.DefaultPort),
        ],
    };

    private List<Item> EventItems()
    {
        var items = new List<Item>
        {
            new("HOW OFTEN", () => R.Events.ToString().ToUpperInvariant(), d => R.Events = Cycle(Enum.GetValues<EventFrequency>(), R.Events, d),
                Note: "RANDOM EVENTS SHAKE THINGS UP FOR A FEW SECONDS - CHAOS RUNS TWO AT ONCE"),
            new("HOW LONG", () => $"{R.EventSeconds} SEC", d => R.EventSeconds = Cycle(EventLengths, R.EventSeconds, d)),
        };
        foreach (var kind in ChaosDirector.AllKinds)
        {
            items.Add(new(ChaosDirector.Name(kind).ToUpperInvariant(), () => OnOff(R.IsEventEnabled(kind)),
                _ => R.SetEventEnabled(kind, !R.IsEventEnabled(kind)),
                Note: ChaosDirector.Description(kind).ToUpperInvariant() + (ChaosDirector.NeedsWeapons(kind) ? " - NOT IN RACES" : "")));
        }
        return items;
    }

    private List<Item> ControlItems()
    {
        var bindings = InputBindings.FromSettings(S);
        var items = new List<Item>
        {
            new("PRESET", () => S.ControlPreset.ToUpperInvariant(), _ =>
            {
                S.ControlPreset = S.ControlPreset == InputBindings.ClassicPreset ? InputBindings.ModernPreset : InputBindings.ClassicPreset;
                S.CustomBindings = null;
                _message = "PRESET CHANGED - CUSTOM KEYS CLEARED";
            }),
        };
        foreach (var action in Enum.GetValues<GameAction>())
        {
            items.Add(new(InputBindings.ActionName(action),
                () => _capturing == action ? "PRESS A KEY..." : bindings.Describe(action),
                Activate: () =>
                {
                    _capturing = action;
                    _message = "PRESS THE NEW KEY, OR ESC TO CANCEL";
                }));
        }
        items.Add(new("RESET TO PRESET", () => S.CustomBindings is { Count: > 0 } ? "" : "(NOTHING CHANGED)", Activate: () =>
        {
            S.CustomBindings = null;
            _message = $"{S.ControlPreset.ToUpperInvariant()} KEYS RESTORED";
        }));
        return items;
    }

    private void Edit(string text, int max, Action<string> commit) => _editing = (text, max, commit);

    public void Update(float dt)
    {
        if (_capturing is { } action)
        {
            if (Input.WasPressed(Keys.Escape))
            {
                _capturing = null;
                _message = null;
            }
            else if (Input.FirstPressedKey() is { } key)
            {
                if (InputBindings.IsReserved(key))
                {
                    _message = $"{InputBindings.KeyName(key)} IS USED BY THE GAME - PICK ANOTHER KEY";
                    return;
                }
                InputBindings.Rebind(S, action, key);
                _capturing = null;
                _message = $"{InputBindings.ActionName(action)} IS NOW {InputBindings.KeyName(key)}";
                Save();
            }
            return;
        }

        if (_editing is { } edit)
        {
            if (Input.WasPressed(Keys.Escape))
            {
                _editing = null;
            }
            else if (Input.WasPressed(Keys.Enter))
            {
                _editing = null;
                edit.Commit(edit.Text);
                Save();
            }
            else
            {
                _editing = edit with { Text = Input.EditText(edit.Text, edit.Max) };
            }
            return;
        }

        if (Input.MenuBack)
        {
            Close();
            return;
        }

        int pageDelta = Input.MenuPagePrevious ? -1 : Input.MenuPageNext ? 1 : 0;
        if (pageDelta != 0)
        {
            SwitchTab(pageDelta);
            if (_row >= 0) _row = 0;
            return;
        }

        var items = Items();
        if (Input.MenuUp) Move(-1, items.Count);
        if (Input.MenuDown) Move(1, items.Count);

        if (_row < 0)
        {
            if (Input.MenuLeft) SwitchTab(-1);
            if (Input.MenuRight) SwitchTab(1);
            if (Input.MenuSelect) _row = 0;
            return;
        }

        _row = Math.Min(_row, items.Count - 1);
        var item = items[_row];
        int delta = Input.MenuLeft ? -1 : Input.MenuRight ? 1 : 0;
        if (delta != 0 && item.Change != null) Apply(() => item.Change(delta));
        if (Input.MenuSelect)
        {
            if (item.Activate != null) Apply(item.Activate);
            else if (item.Change != null) Apply(() => item.Change(1));
        }
    }

    private void Apply(Action change)
    {
        _message = null;
        change();
        game.Sounds.Play("select", 0.6f, 0.3f);
        Save();
    }

    private void Save() => S.Save();

    private void Move(int delta, int count)
    {
        _row = Math.Clamp(_row + delta, -1, count - 1);
        _message = null;
        game.Sounds.Play("select", 0.6f);
    }

    private void SwitchTab(int delta)
    {
        _tab = (_tab + delta + Tabs.Length) % Tabs.Length;
        _scroll = 0;
        _message = null;
        game.Sounds.Play("select", 0.6f);
    }

    private void Close()
    {
        IsOpen = false;
        game.Sounds.Play("select", 0.6f);
    }

    public void Draw(PrimitiveBatch pb, Viewport vp, float time)
    {
        float s = vp.Height / 720f, cx = vp.Width / 2f;
        if (inGame)
        {
            pb.Begin(Matrix.Identity, BlendState.AlphaBlend);
            pb.Rect(0, 0, vp.Width, vp.Height, Color.Black * 0.75f);
            pb.End();
        }

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        VectorFont.Draw(pb, "SETTINGS", new Vector2(cx, 50 * s), 40f * s, Palette.Accent, TextAlign.Center, 3f * s);

        // Tab bar, squeezed to fit narrow windows.
        float tabY = 125 * s, spacing = MathF.Min(150 * s, (vp.Width - 200 * s) / (Tabs.Length - 1));
        float tabSize = MathF.Min(15f * s, spacing * 0.9f / (VectorFont.Measure("CONTROLS", 15f) / 15f));
        float tabX = cx - spacing * (Tabs.Length - 1) / 2f;
        for (int i = 0; i < Tabs.Length; i++)
        {
            bool current = i == _tab;
            var name = Tabs[i].ToString().ToUpperInvariant();
            var color = current ? (_row < 0 ? Pulse(Palette.Text, time) : Palette.Accent) : Palette.TextDim * 0.7f;
            var pos = new Vector2(tabX + i * spacing, tabY);
            VectorFont.Draw(pb, name, pos, tabSize, color, TextAlign.Center);
            if (current)
            {
                float w = VectorFont.Measure(name, tabSize);
                pb.Line(new Vector2(pos.X - w / 2f, tabY + 24 * s), new Vector2(pos.X + w / 2f, tabY + 24 * s), 2f * s, color);
            }
        }
        VectorFont.Draw(pb, "LB", new Vector2(tabX - spacing * 0.75f, tabY + 2 * s), 10f * s, Palette.TextDim * 0.7f, TextAlign.Center);
        VectorFont.Draw(pb, "RB", new Vector2(tabX + (Tabs.Length - 1) * spacing + spacing * 0.75f, tabY + 2 * s), 10f * s, Palette.TextDim * 0.7f, TextAlign.Center);

        // Rows, scrolled to keep the selected one in view.
        var items = Items();
        const float rowHeight = 36f;
        int visible = Math.Max(3, (int)((vp.Height / s - 190f - 150f) / rowHeight));
        if (_row >= 0)
        {
            if (_row < _scroll) _scroll = _row;
            if (_row >= _scroll + visible) _scroll = _row - visible + 1;
        }
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, items.Count - visible));
        if (_scroll > 0) VectorFont.Draw(pb, "- MORE ABOVE -", new Vector2(cx, 166 * s), 9f * s, Palette.TextDim * 0.6f, TextAlign.Center);

        float y = 190 * s;
        for (int i = _scroll; i < Math.Min(items.Count, _scroll + visible); i++)
        {
            var item = items[i];
            bool selected = i == _row;
            var color = selected ? Pulse(Palette.Text, time) : Palette.TextDim * 0.85f;
            string value = item.Value();
            if (selected && _editing is { } edit) value = edit.Text + (((int)(time * 3f) & 1) == 0 ? "_" : " ");
            else if (selected && item.Change != null && item.Activate == null) value = $"< {value} >";

            if (value.Length == 0)
            {
                VectorFont.Draw(pb, selected ? $">  {item.Label}  <" : item.Label, new Vector2(cx, y), 15f * s, color, TextAlign.Center);
            }
            else
            {
                VectorFont.Draw(pb, item.Label, new Vector2(cx - 24 * s, y), 15f * s, color, TextAlign.Right);
                var valueColor = selected && (_editing != null || _capturing != null) ? Palette.Accent : color;
                VectorFont.Draw(pb, value, new Vector2(cx + 24 * s, y), 15f * s, valueColor);
            }
            y += rowHeight * s;
        }
        if (_scroll + visible < items.Count)
        {
            VectorFont.Draw(pb, "- MORE BELOW -", new Vector2(cx, y - 8 * s), 9f * s, Palette.TextDim * 0.6f, TextAlign.Center);
            y += 12 * s;
        }

        string later = inGame ? " - FROM THE NEXT MATCH" : "";
        string? note = _row >= 0 && _row < items.Count ? items[_row].Note : null;
        note ??= Tabs[_tab] switch
        {
            SettingsPage.Game => "SKILL SETS BOTS AND RACE CHECKPOINT SIZE",
            SettingsPage.Match => "MATCH RULES APPLY TO GAMES YOU PLAY OR HOST" + later,
            SettingsPage.Physics => "PHYSICS APPLY TO GAMES YOU PLAY OR HOST" + later,
            SettingsPage.Events => "EVENTS APPLY TO GAMES YOU PLAY OR HOST" + later,
            SettingsPage.Audio => "ENTER ON NOW PLAYING SKIPS TO ANOTHER TRACK - ADD .OGG FILES TO THE MUSIC FOLDER",
            SettingsPage.Controls => "GAMEPAD: STICK TURNS, A/RT THRUST, X/RB FIRE, B/LT SHIELD, Y GRAB",
            SettingsPage.Network => "CLEAR THE MASTER SERVER FOR LAN ONLY - LISTING HOSTED GAMES SHOWS YOUR ADDRESS",
            _ => null,
        };
        if (note != null) Note(pb, cx, y + 10 * s, s, note);

        if (_message != null)
        {
            VectorFont.Draw(pb, _message, new Vector2(cx, vp.Height - 90 * s), 11f * s, Palette.Warning, TextAlign.Center);
        }
        string hint = _capturing != null ? "PRESS A KEY   ESC CANCEL"
            : _editing != null ? "TYPE   ENTER OK   ESC CANCEL"
            : _row < 0 ? "LEFT/RIGHT OR LB/RB CHOOSE A PAGE   DOWN OR ENTER TO EDIT   ESC BACK"
            : "UP/DOWN CHOOSE   LEFT/RIGHT CHANGE   LB/RB OR PGUP/PGDN PAGE   ESC BACK";
        VectorFont.Draw(pb, hint, new Vector2(cx, vp.Height - 50 * s), 10f * s, Palette.TextDim * 0.7f, TextAlign.Center);
        pb.End();
    }

    private static void Note(PrimitiveBatch pb, float cx, float y, float s, string text) =>
        VectorFont.Draw(pb, text, new Vector2(cx, y), 9f * s, Palette.TextDim * 0.6f, TextAlign.Center);

    private static Color Pulse(Color color, float time) => color * (0.85f + 0.15f * MathF.Sin(time * 8f));

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static string Limit(int value) => value == 0 ? "NO LIMIT" : value.ToString();

    private static string Percent(int value) => value == 100 ? "NORMAL" : $"{value}%";

    private static string VolumeBar(float volume)
    {
        int steps = (int)MathF.Round(volume * 10f);
        return $"{new string('#', steps)}{new string('-', 10 - steps)} {steps * 10}%";
    }

    /// <summary>The next option in <paramref name="options"/>, wrapping around; an unknown value starts from the first.</summary>
    private static T Cycle<T>(T[] options, T current, int delta)
    {
        int index = Array.IndexOf(options, current);
        if (index < 0) return options[0];
        return options[(index + delta + options.Length) % options.Length];
    }

    private static string Cycle(string[] options, string current, int delta)
    {
        int index = Array.FindIndex(options, o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return options[0];
        return options[(index + delta + options.Length) % options.Length];
    }
}

/// <summary>The settings menu on its own, from the main menu.</summary>
public sealed class SettingsScreen(XPilotGame game, SettingsPage page = SettingsPage.Game) : Screen(game)
{
    private readonly SettingsMenu _menu = new(game, inGame: false, page);
    private float _time;

    public override void Update(float dt)
    {
        _time += dt;
        Game.Background.Update(dt, Game.Settings.ParticleDensity);
        _menu.Update(dt);
        if (!_menu.IsOpen) Game.SetScreen(new MainMenuScreen(Game));
    }

    public override void Draw(float dt)
    {
        var vp = Game.GraphicsDevice.Viewport;
        Game.Background.Draw(Game, Primitives, vp);
        _menu.Draw(Primitives, vp, _time);
    }
}

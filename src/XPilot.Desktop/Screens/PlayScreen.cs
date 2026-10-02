using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;
using XPilot.Net;

namespace XPilot.Desktop.Screens;

/// <summary>Plays a local match or a network game; most of the screen doesn't care which.</summary>
public sealed class PlayScreen : Screen
{
    private enum PauseItem { Resume, Restart, SwitchTeam, Settings, MainMenu, Leave }

    private readonly MatchSetup? _setup;
    private readonly IGameSession _session;
    private readonly NetworkSession? _net;
    private readonly Camera _camera = new();
    private readonly ParticleSystem _particles = new();
    private InputBindings _bindings;
    private readonly Random _rng = new();

    private WorldRenderer? _renderer;
    private Hud? _hud;
    private int _matchCount = -1;
    private float _time;
    private float _overTimer;
    private float _shake;
    private float _thrustSpawn;
    private Vector2 _starCamera;
    private bool _paused;
    private int _pauseIndex;
    private string? _chatInput;
    private bool _leaving;
    private SettingsMenu? _settingsMenu;

    /// <summary>A local match against bots.</summary>
    public PlayScreen(XPilotGame game, MatchSetup setup) : this(game, new LocalSession(setup))
    {
        _setup = setup;
    }

    public PlayScreen(XPilotGame game, IGameSession session) : base(game)
    {
        _session = session;
        _net = session as NetworkSession;
        _bindings = InputBindings.FromSettings(game.Settings);
        _particles.Density = game.Settings.ParticleDensity;
        if (!session.IsNetwork) BeginMatch();
    }

    private World World => _session.World;
    private Map Map => World.Map;

    private PauseItem[] PauseItems => _net == null
        ? [PauseItem.Resume, PauseItem.Restart, PauseItem.Settings, PauseItem.MainMenu]
        : World.Map.Mode == GameModeKind.Ball && _session.Player != null
            ? [PauseItem.Resume, PauseItem.SwitchTeam, PauseItem.Settings, PauseItem.Leave]
            : [PauseItem.Resume, PauseItem.Settings, PauseItem.Leave];

    private static string Label(PauseItem item) => item switch
    {
        PauseItem.Resume => "RESUME",
        PauseItem.Restart => "RESTART",
        PauseItem.SwitchTeam => "SWITCH TEAM",
        PauseItem.Settings => "SETTINGS",
        PauseItem.MainMenu => "MAIN MENU",
        _ => "LEAVE GAME",
    };

    public override void Enter() => Sounds.StopAll();

    public override void Leave()
    {
        _session.Dispose();
        Sounds.StopAll();
    }

    /// <summary>Sets up the renderer and HUD for the current map. Network games call this for every new map.</summary>
    private void BeginMatch()
    {
        _renderer = new WorldRenderer(Map) { ShowNames = Game.Settings.ShowShipNames };
        _hud = new Hud(Map);
        _particles.Clear();
        _overTimer = 0f;
        var focus = _session.Player?.Position ?? Map.Bases[0];
        _camera.Position = focus.ToXna();
        _starCamera = _camera.Position;

        if (_net != null)
        {
            _hud.ShowCenter(Map.Name.ToUpperInvariant(), Palette.Accent, 3f,
                _session.Player == null ? "SPECTATING - SERVER FULL" : $"ON {_net.Client.ServerName.ToUpperInvariant()}");
        }
        if (World.Rules is DogfightRules dogfight && _net == null)
        {
            _hud.ShowCenter("DOGFIGHT", Palette.Accent, 2.5f, dogfight.ScoreLimit > 0 ? $"FIRST TO {dogfight.ScoreLimit} KILLS WINS" : "NO KILL LIMIT");
        }
        else if (World.Rules is BallRules ball && _session.Player is { } player)
        {
            _hud.ShowCenter($"{Teams.Name(player.Team).ToUpperInvariant()} TEAM", Palette.Team(player.Team), 3.5f,
                $"{_bindings.Describe(GameAction.Grab)} GRABS THE ENEMY BALL" + (ball.CaptureLimit > 0 ? $" - FIRST TO {ball.CaptureLimit}" : ""));
        }
        HandleEvents(_session.TakeEvents());
    }

    public override void Update(float dt)
    {
        _time += dt;
        if (_leaving) return;

        if (_net is { Connection.Status: ConnectionStatus.Disconnected })
        {
            if (Input.MenuSelect || Input.MenuBack) ReturnToMenu();
            return;
        }

        if (_net is { Client.IsReady: false })
        {
            if (Input.MenuBack) ReturnToMenu();
            else _session.Update(dt, default);
            return;
        }

        if (_settingsMenu != null)
        {
            _settingsMenu.Update(dt);
            if (!_settingsMenu.IsOpen)
            {
                _settingsMenu = null;
                ApplySettings();
            }
        }
        else if (!UpdateChat() && Input.Pause)
        {
            _paused = !_paused;
            _pauseIndex = 0;
            Sounds.Play("select", 0.6f);
        }
        if (_paused && _net == null)
        {
            Sounds.SetThrust(0f, 1f);
            if (_settingsMenu == null) UpdatePauseMenu();
            return;
        }

        var input = _paused || _chatInput != null ? default : _bindings.Read(Input);
        _session.Update(dt, input);
        if (_paused && _settingsMenu == null) UpdatePauseMenu();
        if (_leaving) return;

        if (_net != null)
        {
            if (!_net.Client.IsReady) return;
            if (_net.Client.MatchCount != _matchCount)
            {
                _matchCount = _net.Client.MatchCount;
                BeginMatch();
            }
            foreach (var line in _net.Client.TakeChat())
            {
                if (line.From.Length == 0) _hud!.AddChat(line.Text.ToUpperInvariant(), Palette.TextDim);
                else _hud!.AddChat($"{line.From}: {line.Text}", line.Team == Teams.None ? Palette.Text : Palette.Team(line.Team));
            }
            var c = _net.Client;
            _hud!.StatusText = $"PING {c.RoundTripMs} MS" + (c.IsSpectating ? "   SPECTATING" : "") +
                               (_net.Host != null ? $"   HOSTING ON PORT {_net.Host.Port}" : "");
        }

        HandleEvents(_session.TakeEvents());
        var hud = _hud!;
        hud.ChatInput = _chatInput;

        EmitThrustParticles(dt);
        _particles.Update(dt, p => Map.WrapPosition(p.ToNum()).ToXna());
        UpdateCamera(dt);
        hud.Update(dt);

        var player = _session.Player;
        Sounds.SetThrust(player is { Alive: true, Thrusting: true } && !_paused ? 1f : 0f, dt);

        if (World.Rules.IsOver)
        {
            _overTimer += dt;
            if (_net == null && (_overTimer > 4f || (_overTimer > 0.75f && Input.MenuSelect))) ShowResults();
        }
        else if (_net == null && player is { Finished: true } && Input.WasPressed(Keys.Enter))
        {
            ShowResults();
        }
    }

    /// <summary>T opens the chat line in network games. Returns true if this frame's keys went to the chat.</summary>
    private bool UpdateChat()
    {
        if (_net == null) return false;
        if (_chatInput == null)
        {
            if (_paused || !Input.WasPressed(Keys.T)) return false;
            // The "t" that opened the chat is in this frame's typing; start empty.
            _chatInput = "";
            return true;
        }

        if (Input.WasPressed(Keys.Escape))
        {
            _chatInput = null;
            return true;
        }
        if (Input.WasPressed(Keys.Enter))
        {
            _net.Client.SendChat(_chatInput);
            _chatInput = null;
            return true;
        }
        _chatInput = Input.EditText(_chatInput, Protocol.MaxChatLength);
        return true;
    }

    private void ShowResults()
    {
        if (_session is not LocalSession local || _setup == null) return;
        Sounds.StopAll();
        Game.SetScreen(new ResultsScreen(Game, local.Match, _setup));
    }

    private void ReturnToMenu()
    {
        _leaving = true;
        Game.SetScreen(new MainMenuScreen(Game));
    }

    private void UpdatePauseMenu()
    {
        var items = PauseItems;
        if (Input.MenuUp) _pauseIndex = (_pauseIndex + items.Length - 1) % items.Length;
        if (Input.MenuDown) _pauseIndex = (_pauseIndex + 1) % items.Length;
        _pauseIndex = Math.Clamp(_pauseIndex, 0, items.Length - 1);
        if (!Input.MenuSelect) return;
        switch (items[_pauseIndex])
        {
            case PauseItem.Resume:
                _paused = false;
                break;
            case PauseItem.Restart when _setup != null:
                _leaving = true;
                Game.SetScreen(new PlayScreen(Game, _setup));
                break;
            case PauseItem.Settings:
                _settingsMenu = new SettingsMenu(Game, inGame: true);
                break;
            case PauseItem.SwitchTeam:
                _net?.Client.RequestTeamSwitch();
                _paused = false;
                break;
            default:
                ReturnToMenu();
                break;
        }
    }

    /// <summary>Picks up changes made in the settings overlay.</summary>
    private void ApplySettings()
    {
        var s = Game.Settings;
        _bindings = InputBindings.FromSettings(s);
        _particles.Density = s.ParticleDensity;
        if (_renderer != null) _renderer.ShowNames = s.ShowShipNames;
    }

    private void UpdateCamera(float dt)
    {
        // Follow the player, or the first ship flying when spectating.
        var player = _session.Player ?? World.Ships.FirstOrDefault(s => s.Alive) ?? World.Ships.FirstOrDefault();
        if (player is { Alive: true })
        {
            var pos = player.PrevPosition + Map.Delta(player.PrevPosition, player.Position) * _session.Alpha;
            var lead = MathUtil.ClampLength(player.Velocity * 0.25f, 160f);
            var target = pos + lead;
            var delta = Map.Delta(_camera.Position.ToNum(), target).ToXna();
            var move = delta * MathF.Min(1f, dt * 6f);
            _camera.Position = Map.WrapPosition((_camera.Position + move).ToNum()).ToXna();
            _starCamera += move;
        }

        _shake = MathF.Max(0f, _shake - dt * 25f);
        _camera.Shake = _shake > 0f
            ? new Vector2((float)_rng.NextDouble() * 2f - 1f, (float)_rng.NextDouble() * 2f - 1f) * (_shake * Game.Settings.ScreenShake)
            : Vector2.Zero;
    }

    private void EmitThrustParticles(float dt)
    {
        _thrustSpawn += dt * 70f * _particles.Density;
        int count = (int)_thrustSpawn;
        _thrustSpawn -= count;
        if (count == 0) return;
        foreach (var s in World.Ships)
        {
            if (!s.Alive || !s.Thrusting) continue;
            var dir = MathUtil.FromAngle(s.Heading).ToXna();
            var tail = s.Position.ToXna() - dir * 10f;
            for (int i = 0; i < count; i++)
            {
                float spread = _particles.Random(-0.35f, 0.35f);
                var back = new Vector2(-dir.X * MathF.Cos(spread) + dir.Y * MathF.Sin(spread), -dir.Y * MathF.Cos(spread) - dir.X * MathF.Sin(spread));
                var color = Color.Lerp(new Color(255, 200, 80), new Color(255, 70, 30), _particles.Random(0f, 1f));
                _particles.Spawn(tail, s.Velocity.ToXna() + back * _particles.Random(140f, 260f), _particles.Random(0.15f, 0.4f), color, 1.5f, 3f);
            }
        }
    }

    private void HandleEvents(List<GameEvent> events)
    {
        var hud = _hud!;
        var world = World;
        var player = _session.Player;
        foreach (var e in events)
        {
            var pos = e.Position.ToXna();
            var ship = world.GetShip(e.ShipId);
            var other = world.GetShip(e.OtherId);
            bool isPlayer = ship != null && ship == player;
            var (volume, pan) = Spatial(e.Position);

            switch (e.Type)
            {
                case GameEventType.ShipFired:
                    Sounds.Play("fire", (isPlayer ? 0.6f : 0.45f) * volume, (float)_rng.NextDouble() * 0.1f, pan);
                    break;

                case GameEventType.ShipDestroyed when ship != null:
                    Explode(pos, e.Velocity.ToXna(), Palette.Ship(ship));
                    Sounds.Play("explosion", MathF.Max(volume, isPlayer ? 1f : 0f), (float)_rng.NextDouble() * 0.2f - 0.1f, pan);
                    hud.AddFeed(DeathMessage(ship, other, e.Cause), other != null && other != ship ? Palette.Ship(other) : Palette.TextDim);
                    if (isPlayer)
                    {
                        _shake = 12f;
                        if (world.Rules.Mode != GameModeKind.Race)
                        {
                            hud.ShowCenter("DESTROYED", Palette.Warning, 1.6f, other != null && other != ship ? $"BY {other.Name.ToUpperInvariant()}" : null);
                        }
                    }
                    else if (other != null && other == player)
                    {
                        hud.ShowCenter("KILL", Palette.Ship(ship), 0.8f, ship.Name.ToUpperInvariant());
                    }
                    break;

                case GameEventType.WallBounce:
                    _particles.Burst(pos, Vector2.Zero, 8, 40f, 160f, new Color(180, 200, 255), 0.15f, 0.4f);
                    Sounds.Play("bounce", Math.Clamp(e.Value / 200f, 0.2f, 1f) * volume, 0f, pan);
                    break;

                case GameEventType.ShieldHit:
                    _particles.Burst(pos, Vector2.Zero, 12, 60f, 220f, Palette.Accent, 0.2f, 0.45f);
                    Sounds.Play("shield", volume, 0f, pan);
                    break;

                case GameEventType.BulletHitWall:
                    _particles.Burst(pos, Vector2.Zero, 4, 30f, 120f, new Color(200, 210, 255), 0.1f, 0.3f, 1.2f);
                    break;

                case GameEventType.ShipSpawned when isPlayer:
                    Sounds.Play("spawn", 0.6f);
                    break;

                case GameEventType.CheckpointPassed when isPlayer:
                    _particles.Burst(pos, Vector2.Zero, 40, 100f, 260f, Palette.Checkpoint, 0.3f, 0.7f);
                    Sounds.Play("checkpoint", 0.7f);
                    break;

                case GameEventType.LapCompleted when isPlayer && ship != null && world.Rules is RaceRules race && ship.Lap < race.Laps:
                    Sounds.Play("lap", 0.8f);
                    hud.ShowCenter(ship.Lap + 1 == race.Laps ? "FINAL LAP" : $"LAP {ship.Lap + 1}", Palette.Checkpoint, 2f,
                        $"LAP TIME {Hud.FormatTime(e.Value)}");
                    break;

                case GameEventType.ShipFinished when ship != null:
                    hud.AddFeed($"{ship.Name} FINISHED #{(int)e.Value}", Palette.Ship(ship));
                    if (isPlayer)
                    {
                        Sounds.Play("lap", 1f);
                        hud.ShowCenter((int)e.Value == 1 ? "YOU WIN!" : $"PLACE {(int)e.Value}", Palette.Checkpoint, 5f,
                            $"TIME {Hud.FormatTime(ship.FinishTime)}" + (_net == null ? "   ENTER FOR RESULTS" : ""));
                    }
                    break;

                case GameEventType.CountdownTick:
                    Sounds.Play("beep", 0.7f);
                    hud.ShowCenter(((int)e.Value).ToString(), Palette.Checkpoint, 0.9f);
                    break;

                case GameEventType.RaceStarted:
                    Sounds.Play("go", 0.8f);
                    hud.ShowCenter("GO!", Palette.Fuel, 1f);
                    break;

                case GameEventType.BallGrabbed when ship != null:
                {
                    int ballTeam = (int)e.Value;
                    hud.AddFeed($"{ship.Name} GRABBED THE {Teams.Name(ballTeam)} BALL", Palette.Ship(ship));
                    if (isPlayer)
                    {
                        Sounds.Play("spawn", 0.7f);
                        hud.ShowCenter("GOT IT!", Palette.Team(ballTeam), 1.5f, "TOW IT TO YOUR TREASURE");
                    }
                    else if (player != null && ballTeam == player.Team)
                    {
                        Sounds.Play("beep", 0.8f, 0.5f);
                        hud.ShowCenter("BALL TAKEN!", Palette.Warning, 1.5f, $"{ship.Name.ToUpperInvariant()} HAS YOUR BALL");
                    }
                    break;
                }

                case GameEventType.BallDropped:
                    _particles.Burst(pos, Vector2.Zero, 16, 40f, 160f, Palette.Team((int)e.Value), 0.3f, 0.7f);
                    break;

                case GameEventType.BallCaptured when ship != null:
                {
                    var color = Palette.Team(ship.Team);
                    _particles.Burst(pos, Vector2.Zero, 120, 80f, 420f, color, 0.6f, 1.6f, 2f);
                    _particles.Burst(pos, Vector2.Zero, 40, 40f, 200f, Color.White, 0.4f, 1.2f);
                    Sounds.Play("lap", 1f);
                    _shake = MathF.Max(_shake, 6f);
                    hud.AddFeed($"{ship.Name} CAPTURED THE {Teams.Name((int)e.Value)} BALL!", color);
                    hud.ShowCenter($"{Teams.Name(ship.Team).ToUpperInvariant()} SCORES!", color, 2.5f, $"CAPTURED BY {ship.Name.ToUpperInvariant()}");
                    break;
                }

                case GameEventType.BallReturned:
                {
                    int ballTeam = (int)e.Value;
                    hud.AddFeed(ship != null ? $"{ship.Name} RETURNED THE {Teams.Name(ballTeam)} BALL" : $"{Teams.Name(ballTeam)} BALL RETURNED",
                        Palette.Team(ballTeam));
                    if (player != null && ballTeam == player.Team) Sounds.Play("checkpoint", 0.7f);
                    break;
                }

                case GameEventType.MatchOver when world.Rules is BallRules:
                {
                    int team = (int)e.Value;
                    string text = team == Teams.None ? "DRAW" : player?.Team == team ? "YOUR TEAM WINS!" : $"{Teams.Name(team).ToUpperInvariant()} TEAM WINS";
                    hud.ShowCenter(text, Palette.Team(team), 5f, "MATCH OVER");
                    break;
                }

                case GameEventType.MatchOver:
                    var winner = world.Rules.GetStandings(world).FirstOrDefault();
                    if (world.Rules.Mode == GameModeKind.Dogfight && winner != null)
                    {
                        hud.ShowCenter(winner == player ? "YOU WIN!" : $"{winner.Name.ToUpperInvariant()} WINS", Palette.Ship(winner), 5f, "MATCH OVER");
                    }
                    else if (player is not { Finished: true })
                    {
                        hud.ShowCenter("RACE OVER", Palette.Warning, 5f);
                    }
                    break;
            }
        }
    }

    private static string DeathMessage(Ship victim, Ship? killer, DeathCause cause) => cause switch
    {
        DeathCause.Bullet when killer != null && killer != victim => $"{killer.Name} SHOT {victim.Name}",
        DeathCause.Bullet => $"{victim.Name} SHOT THEMSELF",
        DeathCause.Collision when killer != null => $"{killer.Name} RAMMED {victim.Name}",
        DeathCause.Collision => $"{victim.Name} COLLIDED",
        _ => $"{victim.Name} CRASHED",
    };

    private void Explode(Vector2 pos, Vector2 velocity, Color color)
    {
        var drift = velocity * 0.4f;
        _particles.Burst(pos, drift, 50, 40f, 360f, color, 0.4f, 1.3f, 1.8f);
        _particles.Burst(pos, drift, 30, 20f, 200f, new Color(255, 200, 120), 0.3f, 0.9f, 1.5f);
        _particles.Burst(pos, drift, 10, 10f, 90f, Color.White, 0.8f, 1.8f, 2.2f);
    }

    /// <summary>Volume and stereo pan for a sound at a world position, relative to the camera.</summary>
    private (float Volume, float Pan) Spatial(System.Numerics.Vector2 position)
    {
        var d = Map.Delta(_camera.Position.ToNum(), position);
        float dist = d.Length();
        float volume = Math.Clamp(1f - dist / 1100f, 0f, 1f);
        return (volume * volume, Math.Clamp(d.X / 700f, -1f, 1f));
    }

    public override void Draw(float dt)
    {
        var vp = Game.GraphicsDevice.Viewport;
        var pb = Primitives;
        _camera.Viewport = vp;
        _camera.Zoom = vp.Height / 720f;

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        Game.Starfield.Draw(pb, _starCamera, vp.Width, vp.Height, _time);
        pb.End();

        if (_net is { Connection.Status: ConnectionStatus.Disconnected })
        {
            DrawMessage(pb, vp, _matchCount < 0 ? "COULD NOT CONNECT" : "DISCONNECTED",
                (_net.Connection.Error ?? "Connection closed").ToUpperInvariant(), "ENTER  BACK TO MENU");
            return;
        }
        if (_renderer == null || _hud == null || _net is { Client.IsReady: false })
        {
            DrawMessage(pb, vp, "CONNECTING", _net?.Connection.Address.ToUpperInvariant() ?? "", "ESC  CANCEL");
            return;
        }

        _renderer.Draw(pb, _camera, _session, _particles, _session.Alpha, _time);

        bool over = World.Rules.IsOver && _overTimer > 1.5f;
        bool scoreboard = Input.IsDown(Keys.Tab) || Input.IsDown(Buttons.Back) || over;
        _hud.Draw(pb, vp, _session, scoreboard, _time);

        if (_net != null && over && _net.Client.Intermission > 0f)
        {
            float s = vp.Height / 720f;
            pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
            VectorFont.Draw(pb, $"NEXT MATCH IN {MathF.Ceiling(_net.Client.Intermission):0}", new Vector2(vp.Width / 2f, vp.Height - 50 * s),
                14f * s, Palette.Accent, TextAlign.Center);
            pb.End();
        }

        if (_settingsMenu != null) _settingsMenu.Draw(pb, vp, _time);
        else if (_paused) DrawPauseMenu(pb, vp);
    }

    private static void DrawMessage(PrimitiveBatch pb, Viewport vp, string title, string detail, string hint)
    {
        float s = vp.Height / 720f, cx = vp.Width / 2f;
        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        VectorFont.Draw(pb, title, new Vector2(cx, vp.Height * 0.35f), 32f * s, Palette.Accent, TextAlign.Center, 3f * s);
        VectorFont.Draw(pb, detail, new Vector2(cx, vp.Height * 0.35f + 60 * s), 12f * s, Palette.Text, TextAlign.Center);
        VectorFont.Draw(pb, hint, new Vector2(cx, vp.Height - 60 * s), 11f * s, Palette.TextDim, TextAlign.Center);
        pb.End();
    }

    private void DrawPauseMenu(PrimitiveBatch pb, Viewport vp)
    {
        float s = vp.Height / 720f;
        pb.Begin(Matrix.Identity, BlendState.AlphaBlend);
        pb.Rect(0, 0, vp.Width, vp.Height, Color.Black * 0.6f);
        pb.End();

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        float cx = vp.Width / 2f, y = vp.Height * 0.32f;
        VectorFont.Draw(pb, _net == null ? "PAUSED" : "MENU", new Vector2(cx, y), 36f * s, Palette.Accent, TextAlign.Center, 3f * s);
        y += 80 * s;
        var items = PauseItems;
        for (int i = 0; i < items.Length; i++)
        {
            bool selected = i == _pauseIndex;
            var label = selected ? $">  {Label(items[i])}  <" : Label(items[i]);
            VectorFont.Draw(pb, label, new Vector2(cx, y), 18f * s, selected ? Palette.Text : Palette.TextDim, TextAlign.Center);
            y += 40 * s;
        }
        if (_net != null)
        {
            VectorFont.Draw(pb, "THE GAME KEEPS RUNNING", new Vector2(cx, y + 20 * s), 10f * s, Palette.TextDim, TextAlign.Center);
        }
        pb.End();
    }
}

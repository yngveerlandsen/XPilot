using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;
using XPilot.Desktop.Graphics;
using XPilot.Desktop.Input;

namespace XPilot.Desktop.Screens;

public sealed class PlayScreen : Screen
{
    private static readonly string[] PauseItems = ["RESUME", "RESTART", "MAIN MENU"];

    private readonly MatchSetup _setup;
    private readonly Match _match;
    private readonly WorldRenderer _renderer;
    private readonly Hud _hud;
    private readonly Camera _camera = new();
    private readonly ParticleSystem _particles = new();
    private readonly InputBindings _bindings;
    private readonly Random _rng = new();

    private float _accumulator;
    private float _time;
    private float _overTimer;
    private float _shake;
    private float _thrustSpawn;
    private Vector2 _starCamera;
    private bool _paused;
    private int _pauseIndex;

    public PlayScreen(XPilotGame game, MatchSetup setup) : base(game)
    {
        _setup = setup;
        _match = new Match(setup);
        _renderer = new WorldRenderer(setup.Map);
        _hud = new Hud(setup.Map);
        _bindings = InputBindings.FromSettings(game.Settings);

        var focus = _match.Player?.Position ?? setup.Map.Bases[0];
        _camera.Position = focus.ToXna();
        _starCamera = _camera.Position;

        if (_match.World.Rules is DogfightRules dogfight)
        {
            _hud.ShowCenter("DOGFIGHT", Palette.Accent, 2.5f, $"FIRST TO {dogfight.ScoreLimit} KILLS WINS");
        }
        else if (_match.World.Rules is BallRules ball && _match.Player is { } player)
        {
            _hud.ShowCenter($"{Teams.Name(player.Team).ToUpperInvariant()} TEAM", Palette.Team(player.Team), 3.5f,
                $"{_bindings.Describe(GameAction.Grab)} GRABS THE ENEMY BALL - FIRST TO {ball.CaptureLimit}");
        }
        HandleEvents();
    }

    private Map Map => _match.World.Map;
    private float Alpha => Math.Clamp(_accumulator / GameConfig.Dt, 0f, 1f);

    public override void Enter() => Sounds.StopAll();

    public override void Update(float dt)
    {
        _time += dt;
        if (Input.Pause)
        {
            _paused = !_paused;
            _pauseIndex = 0;
            Sounds.Play("select", 0.6f);
        }
        if (_paused)
        {
            Sounds.SetThrust(0f, 1f);
            UpdatePauseMenu();
            return;
        }

        var input = _bindings.Read(Input);
        _accumulator += MathF.Min(dt, 0.1f);
        while (_accumulator >= GameConfig.Dt)
        {
            _match.Step(input);
            HandleEvents();
            _accumulator -= GameConfig.Dt;
        }

        EmitThrustParticles(dt);
        _particles.Update(dt, p => Map.WrapPosition(p.ToNum()).ToXna());
        UpdateCamera(dt);
        _hud.Update(dt);

        var player = _match.Player;
        Sounds.SetThrust(player is { Alive: true, Thrusting: true } ? 1f : 0f, dt);

        var rules = _match.World.Rules;
        if (rules.IsOver)
        {
            _overTimer += dt;
            if (_overTimer > 4f || (_overTimer > 0.75f && Input.MenuSelect)) ShowResults();
        }
        else if (player is { Finished: true } && Input.WasPressed(Keys.Enter))
        {
            ShowResults();
        }
    }

    private void ShowResults()
    {
        Sounds.StopAll();
        Game.SetScreen(new ResultsScreen(Game, _match, _setup));
    }

    private void UpdatePauseMenu()
    {
        if (Input.MenuUp) _pauseIndex = (_pauseIndex + PauseItems.Length - 1) % PauseItems.Length;
        if (Input.MenuDown) _pauseIndex = (_pauseIndex + 1) % PauseItems.Length;
        if (!Input.MenuSelect) return;
        switch (_pauseIndex)
        {
            case 0:
                _paused = false;
                break;
            case 1:
                Game.SetScreen(new PlayScreen(Game, _setup));
                break;
            default:
                Game.SetScreen(new MainMenuScreen(Game));
                break;
        }
    }

    private void UpdateCamera(float dt)
    {
        // Follow the player, or the first bot when spectating.
        var player = _match.Player ?? _match.World.Ships.FirstOrDefault();
        if (player is { Alive: true })
        {
            var pos = player.PrevPosition + Map.Delta(player.PrevPosition, player.Position) * Alpha;
            var lead = MathUtil.ClampLength(player.Velocity * 0.25f, 160f);
            var target = pos + lead;
            var delta = Map.Delta(_camera.Position.ToNum(), target).ToXna();
            var move = delta * MathF.Min(1f, dt * 6f);
            _camera.Position = Map.WrapPosition((_camera.Position + move).ToNum()).ToXna();
            _starCamera += move;
        }

        _shake = MathF.Max(0f, _shake - dt * 25f);
        _camera.Shake = _shake > 0f
            ? new Vector2((float)_rng.NextDouble() * 2f - 1f, (float)_rng.NextDouble() * 2f - 1f) * _shake
            : Vector2.Zero;
    }

    private void EmitThrustParticles(float dt)
    {
        _thrustSpawn += dt * 70f;
        int count = (int)_thrustSpawn;
        _thrustSpawn -= count;
        if (count == 0) return;
        foreach (var s in _match.World.Ships)
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

    private void HandleEvents()
    {
        var world = _match.World;
        var player = _match.Player;
        foreach (var e in world.Events)
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
                    _hud.AddFeed(DeathMessage(ship, other, e.Cause), other != null && other != ship ? Palette.Ship(other) : Palette.TextDim);
                    if (isPlayer)
                    {
                        _shake = 12f;
                        if (world.Rules.Mode != GameModeKind.Race)
                        {
                            _hud.ShowCenter("DESTROYED", Palette.Warning, 1.6f, other != null && other != ship ? $"BY {other.Name.ToUpperInvariant()}" : null);
                        }
                    }
                    else if (other != null && other == player)
                    {
                        _hud.ShowCenter("KILL", Palette.Ship(ship), 0.8f, ship.Name.ToUpperInvariant());
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
                    _hud.ShowCenter(ship.Lap + 1 == race.Laps ? "FINAL LAP" : $"LAP {ship.Lap + 1}", Palette.Checkpoint, 2f,
                        $"LAP TIME {Hud.FormatTime(e.Value)}");
                    break;

                case GameEventType.ShipFinished when ship != null:
                    _hud.AddFeed($"{ship.Name} FINISHED #{ship.Place}", Palette.Ship(ship));
                    if (isPlayer)
                    {
                        Sounds.Play("lap", 1f);
                        _hud.ShowCenter(ship.Place == 1 ? "YOU WIN!" : $"PLACE {ship.Place}", Palette.Checkpoint, 5f,
                            $"TIME {Hud.FormatTime(ship.FinishTime)}   ENTER FOR RESULTS");
                    }
                    break;

                case GameEventType.CountdownTick:
                    Sounds.Play("beep", 0.7f);
                    _hud.ShowCenter(((int)e.Value).ToString(), Palette.Checkpoint, 0.9f);
                    break;

                case GameEventType.RaceStarted:
                    Sounds.Play("go", 0.8f);
                    _hud.ShowCenter("GO!", Palette.Fuel, 1f);
                    break;

                case GameEventType.BallGrabbed when ship != null:
                {
                    int ballTeam = (int)e.Value;
                    _hud.AddFeed($"{ship.Name} GRABBED THE {Teams.Name(ballTeam)} BALL", Palette.Ship(ship));
                    if (isPlayer)
                    {
                        Sounds.Play("spawn", 0.7f);
                        _hud.ShowCenter("GOT IT!", Palette.Team(ballTeam), 1.5f, "TOW IT TO YOUR TREASURE");
                    }
                    else if (player != null && ballTeam == player.Team)
                    {
                        Sounds.Play("beep", 0.8f, 0.5f);
                        _hud.ShowCenter("BALL TAKEN!", Palette.Warning, 1.5f, $"{ship.Name.ToUpperInvariant()} HAS YOUR BALL");
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
                    _hud.AddFeed($"{ship.Name} CAPTURED THE {Teams.Name((int)e.Value)} BALL!", color);
                    _hud.ShowCenter($"{Teams.Name(ship.Team).ToUpperInvariant()} SCORES!", color, 2.5f, $"CAPTURED BY {ship.Name.ToUpperInvariant()}");
                    break;
                }

                case GameEventType.BallReturned:
                {
                    int ballTeam = (int)e.Value;
                    _hud.AddFeed(ship != null ? $"{ship.Name} RETURNED THE {Teams.Name(ballTeam)} BALL" : $"{Teams.Name(ballTeam)} BALL RETURNED",
                        Palette.Team(ballTeam));
                    if (player != null && ballTeam == player.Team) Sounds.Play("checkpoint", 0.7f);
                    break;
                }

                case GameEventType.MatchOver when world.Rules is BallRules ballRules:
                {
                    int team = ballRules.WinningTeam;
                    string text = team == Teams.None ? "DRAW" : player?.Team == team ? "YOUR TEAM WINS!" : $"{Teams.Name(team).ToUpperInvariant()} TEAM WINS";
                    _hud.ShowCenter(text, Palette.Team(team), 5f, "MATCH OVER");
                    break;
                }

                case GameEventType.MatchOver:
                    var winner = world.Rules.GetStandings(world).FirstOrDefault();
                    if (world.Rules.Mode == GameModeKind.Dogfight && winner != null)
                    {
                        _hud.ShowCenter(winner == player ? "YOU WIN!" : $"{winner.Name.ToUpperInvariant()} WINS", Palette.Ship(winner), 5f, "MATCH OVER");
                    }
                    else if (player is not { Finished: true })
                    {
                        _hud.ShowCenter("RACE OVER", Palette.Warning, 5f);
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

        _renderer.Draw(pb, _camera, _match, _particles, Alpha, _time);

        bool scoreboard = Input.IsDown(Keys.Tab) || Input.IsDown(Buttons.Back) || (_match.World.Rules.IsOver && _overTimer > 1.5f);
        _hud.Draw(pb, vp, _match, scoreboard, _time);

        if (_paused) DrawPauseMenu(pb, vp);
    }

    private void DrawPauseMenu(PrimitiveBatch pb, Viewport vp)
    {
        float s = vp.Height / 720f;
        pb.Begin(Matrix.Identity, BlendState.AlphaBlend);
        pb.Rect(0, 0, vp.Width, vp.Height, Color.Black * 0.6f);
        pb.End();

        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        float cx = vp.Width / 2f, y = vp.Height * 0.32f;
        VectorFont.Draw(pb, "PAUSED", new Vector2(cx, y), 36f * s, Palette.Accent, TextAlign.Center, 3f * s);
        y += 80 * s;
        for (int i = 0; i < PauseItems.Length; i++)
        {
            bool selected = i == _pauseIndex;
            var label = selected ? $">  {PauseItems[i]}  <" : PauseItems[i];
            VectorFont.Draw(pb, label, new Vector2(cx, y), 18f * s, selected ? Palette.Text : Palette.TextDim, TextAlign.Center);
            y += 40 * s;
        }
        pb.End();
    }
}

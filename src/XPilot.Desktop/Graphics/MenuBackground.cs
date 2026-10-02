using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XPilot.Core;
using XPilot.Core.AI;
using XPilot.Core.Maps;
using XPilot.Core.Simulation;

namespace XPilot.Desktop.Graphics;

/// <summary>
/// A bots-only free-for-all running behind the menus, dimmed so the text stays readable. The camera drifts
/// from fight to fight. It keeps going as you move between menu screens and is silent, so music plays on.
/// </summary>
public sealed class MenuBackground(Map? map)
{
    private const int BotCount = 20;
    /// <summary>Seconds on one ship before looking for another fight.</summary>
    private const float ShotLength = 7f;

    private readonly Match? _match = map == null ? null : new Match(new MatchSetup
    {
        Map = map,
        IncludePlayer = false,
        BotCount = BotCount,
        Capacity = BotCount,
        Difficulty = BotDifficulty.Hard,
        ScoreLimit = 0,
        TimeLimit = 0,
    });
    private readonly WorldRenderer? _renderer = map == null ? null : new WorldRenderer(map) { ShowNames = false };
    private readonly ParticleSystem _particles = new();
    private readonly Camera _camera = new();
    private readonly Random _rng = new();
    private Ship? _subject;
    private float _shotTimer;
    private float _accumulator;
    private float _thrustSpawn;
    private float _time;
    private Vector2 _starCamera;
    private bool _cameraPlaced;

    /// <summary>Uses the Arena if it's there, otherwise any dogfight map; with none, just stars.</summary>
    public static MenuBackground Create(MapCatalog maps)
    {
        var dogfight = maps.ForMode(GameModeKind.Dogfight);
        return new MenuBackground(dogfight.FirstOrDefault(m => m.Name == "Arena") ?? dogfight.FirstOrDefault());
    }

    public void Update(float dt, float particleDensity)
    {
        _time += dt;
        _starCamera += new Vector2(18f, 6f) * dt;
        if (_match == null) return;
        var world = _match.World;

        _accumulator += MathF.Min(dt, 0.1f);
        while (_accumulator >= GameConfig.Dt)
        {
            _match.Step();
            _accumulator -= GameConfig.Dt;
            foreach (var e in world.Events)
            {
                var pos = e.Position.ToXna();
                switch (e.Type)
                {
                    case GameEventType.ShipDestroyed when world.GetShip(e.ShipId) is { } ship:
                        Effects.Explode(_particles, pos, e.Velocity.ToXna(), Palette.Ship(ship));
                        break;
                    case GameEventType.BulletHitWall:
                        Effects.BulletSpark(_particles, pos);
                        break;
                    case GameEventType.ShieldHit:
                        Effects.ShieldSpark(_particles, pos);
                        break;
                }
            }
        }

        _particles.Density = particleDensity;
        Effects.ThrustTrails(_particles, world, dt, ref _thrustSpawn);
        _particles.Update(dt, p => world.Map.WrapPosition(p.ToNum()).ToXna());
        UpdateCamera(dt);
    }

    /// <summary>Follows one ship for a while, then cuts to whichever has the most company.</summary>
    private void UpdateCamera(float dt)
    {
        var world = _match!.World;
        var map = world.Map;
        _shotTimer -= dt;
        if (_subject is not { Alive: true } || _shotTimer <= 0f)
        {
            _subject = world.Ships
                .Where(s => s.Alive)
                .OrderByDescending(s => world.Ships.Count(o => o != s && o.Alive && map.Distance(o.Position, s.Position) < 450f) + _rng.NextDouble())
                .FirstOrDefault();
            _shotTimer = ShotLength;
        }
        if (_subject == null) return;

        var target = _subject.Position + MathUtil.ClampLength(_subject.Velocity * 0.4f, 200f);
        if (!_cameraPlaced)
        {
            _camera.Position = target.ToXna();
            _cameraPlaced = true;
            return;
        }
        var delta = map.Delta(_camera.Position.ToNum(), target).ToXna();
        var move = delta * MathF.Min(1f, dt * 1.5f);
        _camera.Position = map.WrapPosition((_camera.Position + move).ToNum()).ToXna();
        _starCamera += move * 0.5f;
    }

    /// <summary>Stars, the fight, and a dark veil over both for the menu to sit on.</summary>
    public void Draw(XPilotGame game, PrimitiveBatch pb, Viewport vp)
    {
        pb.Begin(Matrix.Identity, PrimitiveBatch.Additive);
        game.Starfield.Draw(pb, _starCamera, vp.Width, vp.Height, _time);
        pb.End();

        if (_match != null && _renderer != null)
        {
            _camera.Viewport = vp;
            _camera.Zoom = vp.Height / 720f * 0.75f;
            _renderer.Draw(pb, _camera, _match, _particles, Math.Clamp(_accumulator / GameConfig.Dt, 0f, 1f), _time);
        }

        // An even veil, plus a softly darker band down the middle where the menu text sits.
        pb.Begin(Matrix.Identity, BlendState.AlphaBlend);
        pb.Rect(0, 0, vp.Width, vp.Height, Palette.Background * 0.5f);
        float s = vp.Height / 720f, cx = vp.Width / 2f;
        for (int i = 0; i < 6; i++)
        {
            float half = (520f - i * 60f) * s;
            pb.Rect(cx - half, 0, half * 2f, vp.Height, Palette.Background * 0.09f);
        }
        pb.End();
    }
}

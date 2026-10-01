using Microsoft.Xna.Framework;

namespace XPilot.Desktop.Graphics;

/// <summary>Short-lived sparks drawn as streaks. Purely cosmetic; not part of the simulation.</summary>
public sealed class ParticleSystem
{
    private struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;
        public float MaxLife;
        public Color Color;
        public float Width;
        public float Drag;
    }

    private readonly Particle[] _particles = new Particle[6000];
    private readonly Random _rng = new();
    private int _count;

    public void Spawn(Vector2 position, Vector2 velocity, float life, Color color, float width = 1.5f, float drag = 1.5f)
    {
        if (_count >= _particles.Length) return;
        _particles[_count++] = new Particle
        {
            Position = position,
            Velocity = velocity,
            Life = life,
            MaxLife = life,
            Color = color,
            Width = width,
            Drag = drag,
        };
    }

    public void Burst(Vector2 position, Vector2 baseVelocity, int count, float minSpeed, float maxSpeed,
        Color color, float minLife, float maxLife, float width = 1.5f)
    {
        for (int i = 0; i < count; i++)
        {
            float angle = (float)_rng.NextDouble() * MathF.Tau;
            float speed = minSpeed + (float)_rng.NextDouble() * (maxSpeed - minSpeed);
            float life = minLife + (float)_rng.NextDouble() * (maxLife - minLife);
            Spawn(position, baseVelocity + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed, life, color, width);
        }
    }

    public float Random(float min, float max) => min + (float)_rng.NextDouble() * (max - min);

    public void Update(float dt, Func<Vector2, Vector2> wrap)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _particles[i] = _particles[--_count];
                i--;
                continue;
            }
            p.Velocity *= MathF.Max(0f, 1f - p.Drag * dt);
            p.Position = wrap(p.Position + p.Velocity * dt);
        }
    }

    /// <param name="toView">Maps a world position to the copy nearest the camera (for wrapping maps).</param>
    public void Draw(PrimitiveBatch pb, Func<Vector2, Vector2> toView, RectangleF visible)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            var pos = toView(p.Position);
            if (!visible.Contains(pos, 40f)) continue;
            float t = p.Life / p.MaxLife;
            var tail = pos - p.Velocity * 0.035f;
            if (Vector2.DistanceSquared(tail, pos) < 2f) tail = pos - new Vector2(1.5f, 0);
            pb.Line(tail, pos, p.Width, p.Color * t);
        }
    }

    public void Clear() => _count = 0;
}

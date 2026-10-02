using Microsoft.Xna.Framework;
using XPilot.Core;
using XPilot.Core.Simulation;

namespace XPilot.Desktop.Graphics;

/// <summary>Particle effects shared by the game and the menu background.</summary>
public static class Effects
{
    public static void Explode(ParticleSystem particles, Vector2 pos, Vector2 velocity, Color color)
    {
        var drift = velocity * 0.4f;
        particles.Burst(pos, drift, 50, 40f, 360f, color, 0.4f, 1.3f, 1.8f);
        particles.Burst(pos, drift, 30, 20f, 200f, new Color(255, 200, 120), 0.3f, 0.9f, 1.5f);
        particles.Burst(pos, drift, 10, 10f, 90f, Color.White, 0.8f, 1.8f, 2.2f);
    }

    /// <summary>Exhaust from every thrusting ship. <paramref name="spawn"/> carries fractional particles between frames.</summary>
    public static void ThrustTrails(ParticleSystem particles, World world, float dt, ref float spawn)
    {
        spawn += dt * 70f * particles.Density;
        int count = (int)spawn;
        spawn -= count;
        if (count == 0) return;
        foreach (var s in world.Ships)
        {
            if (!s.Alive || !s.Thrusting) continue;
            var dir = MathUtil.FromAngle(s.Heading).ToXna();
            var tail = s.Position.ToXna() - dir * 10f;
            for (int i = 0; i < count; i++)
            {
                float spread = particles.Random(-0.35f, 0.35f);
                var back = new Vector2(-dir.X * MathF.Cos(spread) + dir.Y * MathF.Sin(spread), -dir.Y * MathF.Cos(spread) - dir.X * MathF.Sin(spread));
                var color = Color.Lerp(new Color(255, 200, 80), new Color(255, 70, 30), particles.Random(0f, 1f));
                particles.Spawn(tail, s.Velocity.ToXna() + back * particles.Random(140f, 260f), particles.Random(0.15f, 0.4f), color, 1.5f, 3f);
            }
        }
    }

    public static void BulletSpark(ParticleSystem particles, Vector2 pos) =>
        particles.Burst(pos, Vector2.Zero, 4, 30f, 120f, new Color(200, 210, 255), 0.1f, 0.3f, 1.2f);

    public static void ShieldSpark(ParticleSystem particles, Vector2 pos) =>
        particles.Burst(pos, Vector2.Zero, 12, 60f, 220f, Palette.Accent, 0.2f, 0.45f);
}

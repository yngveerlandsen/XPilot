using System.Numerics;

namespace XPilot.Core;

public static class MathUtil
{
    public const float TwoPi = MathF.PI * 2f;

    /// <summary>Wraps an angle into [-PI, PI].</summary>
    public static float WrapAngle(float angle) => MathF.IEEERemainder(angle, TwoPi);

    public static Vector2 FromAngle(float angle) => new(MathF.Cos(angle), MathF.Sin(angle));

    public static float ToAngle(Vector2 v) => MathF.Atan2(v.Y, v.X);

    public static float Mod(float a, float m)
    {
        float r = a % m;
        if (r < 0f) r += m;
        return r >= m ? 0f : r;
    }

    public static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

    public static Vector2 ClampLength(Vector2 v, float max)
    {
        float l2 = v.LengthSquared();
        return l2 > max * max ? v * (max / MathF.Sqrt(l2)) : v;
    }

    public static Vector2 SafeNormalize(Vector2 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v / l : Vector2.Zero;
    }

    public static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    /// <summary>Distance from point p to segment ab.</summary>
    public static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-6f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }
}

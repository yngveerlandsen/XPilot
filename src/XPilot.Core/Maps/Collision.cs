using System.Numerics;

namespace XPilot.Core.Maps;

public static class Collision
{
    /// <summary>
    /// Circle vs convex polygon. On hit, <paramref name="normal"/> points from the polygon towards the circle
    /// and <paramref name="depth"/> is how far the circle must move along it to separate.
    /// </summary>
    public static bool CircleVsPolygon(Vector2 c, float r, ReadOnlySpan<Vector2> v, out Vector2 normal, out float depth)
    {
        normal = Vector2.Zero;
        depth = 0f;
        int n = v.Length;
        if (n < 3) return false;

        var centroid = Vector2.Zero;
        foreach (var p in v) centroid += p;
        centroid /= n;

        bool inside = true;
        float maxSep = float.MinValue;
        var maxNormal = Vector2.Zero;
        float bestDist2 = float.MaxValue;
        var bestPoint = Vector2.Zero;

        for (int i = 0; i < n; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % n];
            var e = b - a;
            var en = Vector2.Normalize(new Vector2(e.Y, -e.X));
            if (Vector2.Dot(en, (a + b) * 0.5f - centroid) < 0f) en = -en;

            float sep = Vector2.Dot(c - a, en);
            if (sep > 0f) inside = false;
            if (sep > maxSep)
            {
                maxSep = sep;
                maxNormal = en;
            }

            float t = Math.Clamp(Vector2.Dot(c - a, e) / e.LengthSquared(), 0f, 1f);
            var q = a + e * t;
            float d2 = Vector2.DistanceSquared(c, q);
            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                bestPoint = q;
            }
        }

        if (inside)
        {
            normal = maxNormal;
            depth = r - maxSep;
            return true;
        }

        if (bestDist2 >= r * r) return false;
        float dist = MathF.Sqrt(bestDist2);
        normal = dist > 1e-5f ? (c - bestPoint) / dist : maxNormal;
        depth = r - dist;
        return true;
    }
}

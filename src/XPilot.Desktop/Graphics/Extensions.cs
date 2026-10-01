using Microsoft.Xna.Framework;

namespace XPilot.Desktop.Graphics;

public static class VectorExtensions
{
    public static Vector2 ToXna(this System.Numerics.Vector2 v) => new(v.X, v.Y);

    public static System.Numerics.Vector2 ToNum(this Vector2 v) => new(v.X, v.Y);
}

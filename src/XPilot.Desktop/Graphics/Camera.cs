using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace XPilot.Desktop.Graphics;

public sealed class Camera
{
    public Vector2 Position;
    public float Zoom = 1f;
    public Vector2 Shake;
    public Viewport Viewport;

    public Matrix View =>
        Matrix.CreateTranslation(-(Position.X + Shake.X), -(Position.Y + Shake.Y), 0f)
        * Matrix.CreateScale(Zoom, Zoom, 1f)
        * Matrix.CreateTranslation(Viewport.Width / 2f, Viewport.Height / 2f, 0f);

    /// <summary>Visible world rectangle around the (unwrapped) camera position.</summary>
    public RectangleF VisibleBounds
    {
        get
        {
            float w = Viewport.Width / Zoom, h = Viewport.Height / Zoom;
            return new RectangleF(Position.X + Shake.X - w / 2f, Position.Y + Shake.Y - h / 2f, w, h);
        }
    }

    public Vector2 WorldToScreen(Vector2 world) => Vector2.Transform(world, View);
}

public readonly record struct RectangleF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Intersects(RectangleF o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public bool Contains(Vector2 p, float margin = 0f) =>
        p.X >= X - margin && p.X <= Right + margin && p.Y >= Y - margin && p.Y <= Bottom + margin;
}

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace XPilot.Desktop.Graphics;

/// <summary>
/// Batches colored triangles (lines, polygons, circles) and draws them with a BasicEffect.
/// All colors are premultiplied alpha: use <c>color * alpha</c> to fade.
/// </summary>
public sealed class PrimitiveBatch : IDisposable
{
    /// <summary>Glow-friendly additive blending for premultiplied colors.</summary>
    public static readonly BlendState Additive = new()
    {
        Name = "PremultipliedAdditive",
        ColorSourceBlend = Blend.One,
        AlphaSourceBlend = Blend.One,
        ColorDestinationBlend = Blend.One,
        AlphaDestinationBlend = Blend.One,
    };

    private readonly GraphicsDevice _device;
    private readonly BasicEffect _effect;
    private VertexPositionColor[] _vertices = new VertexPositionColor[16384];
    private int _count;
    private BlendState _blend = BlendState.AlphaBlend;

    public PrimitiveBatch(GraphicsDevice device)
    {
        _device = device;
        _effect = new BasicEffect(device)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            TextureEnabled = false,
        };
    }

    public void Begin(Matrix view, BlendState blend)
    {
        var vp = _device.Viewport;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, -1, 1);
        _effect.View = view;
        _effect.World = Matrix.Identity;
        _blend = blend;
        _count = 0;
    }

    public void End() => Flush();

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color color)
    {
        Ensure(3);
        _vertices[_count++] = new VertexPositionColor(new Vector3(a, 0), color);
        _vertices[_count++] = new VertexPositionColor(new Vector3(b, 0), color);
        _vertices[_count++] = new VertexPositionColor(new Vector3(c, 0), color);
    }

    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color color)
    {
        Triangle(a, b, c, color);
        Triangle(a, c, d, color);
    }

    /// <summary>A filled ring, shading from <paramref name="innerColor"/> to <paramref name="outerColor"/>.</summary>
    public void Ring(Vector2 center, float inner, float outer, Color innerColor, Color outerColor, int segments = 64)
    {
        Ensure(segments * 6);
        var prev = new Vector2(1f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var a0 = new VertexPositionColor(new Vector3(center + prev * inner, 0), innerColor);
            var a1 = new VertexPositionColor(new Vector3(center + dir * inner, 0), innerColor);
            var b0 = new VertexPositionColor(new Vector3(center + prev * outer, 0), outerColor);
            var b1 = new VertexPositionColor(new Vector3(center + dir * outer, 0), outerColor);
            _vertices[_count++] = a0;
            _vertices[_count++] = b0;
            _vertices[_count++] = b1;
            _vertices[_count++] = a0;
            _vertices[_count++] = b1;
            _vertices[_count++] = a1;
            prev = dir;
        }
    }

    public void Rect(float x, float y, float w, float h, Color color) =>
        Quad(new Vector2(x, y), new Vector2(x + w, y), new Vector2(x + w, y + h), new Vector2(x, y + h), color);

    public void RectOutline(float x, float y, float w, float h, float width, Color color)
    {
        Line(new Vector2(x, y), new Vector2(x + w, y), width, color);
        Line(new Vector2(x + w, y), new Vector2(x + w, y + h), width, color);
        Line(new Vector2(x + w, y + h), new Vector2(x, y + h), width, color);
        Line(new Vector2(x, y + h), new Vector2(x, y), width, color);
    }

    public void Line(Vector2 a, Vector2 b, float width, Color color)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var n = new Vector2(-d.Y, d.X) * (width * 0.5f / len);
        Quad(a + n, b + n, b - n, a - n, color);
    }

    /// <summary>A line with a soft halo, for the vector-display look. Best with <see cref="Additive"/>.</summary>
    public void GlowLine(Vector2 a, Vector2 b, float width, Color color, float glow = 1f)
    {
        if (glow > 0f)
        {
            Line(a, b, width * 5f, color * (0.07f * glow));
            Line(a, b, width * 2.5f, color * (0.18f * glow));
        }
        Line(a, b, width, color);
    }

    public void Polyline(ReadOnlySpan<Vector2> points, bool closed, float width, Color color, float glow = 0f)
    {
        for (int i = 0; i + 1 < points.Length; i++) GlowLine(points[i], points[i + 1], width, color, glow);
        if (closed && points.Length > 2) GlowLine(points[^1], points[0], width, color, glow);
    }

    public void Circle(Vector2 center, float radius, float width, Color color, int segments = 32, float glow = 0f, bool dashed = false, float rotation = 0f)
    {
        var prev = center + new Vector2(MathF.Cos(rotation), MathF.Sin(rotation)) * radius;
        for (int i = 1; i <= segments; i++)
        {
            float a = rotation + i * MathF.Tau / segments;
            var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            if (!dashed || i % 2 == 0) GlowLine(prev, p, width, color, glow);
            prev = p;
        }
    }

    public void FilledCircle(Vector2 center, float radius, Color color, int segments = 20)
    {
        var prev = center + new Vector2(radius, 0);
        for (int i = 1; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius;
            Triangle(center, prev, p, color);
            prev = p;
        }
    }

    private void Ensure(int n)
    {
        if (_count + n <= _vertices.Length) return;
        if (_vertices.Length < 1 << 20) Array.Resize(ref _vertices, _vertices.Length * 2);
        else Flush();
    }

    private void Flush()
    {
        if (_count == 0) return;
        _device.BlendState = _blend;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.DepthStencilState = DepthStencilState.None;
        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _device.DrawUserPrimitives(PrimitiveType.TriangleList, _vertices, 0, _count / 3);
        }
        _count = 0;
    }

    public void Dispose() => _effect.Dispose();
}

using Microsoft.Xna.Framework;

namespace XPilot.Desktop.Graphics;

/// <summary>Three parallax layers of stars, tiled infinitely in screen space.</summary>
public sealed class Starfield
{
    private const float TileSize = 1024f;
    private readonly (Vector2 Pos, float Brightness)[][] _layers;
    private static readonly float[] Parallax = [0.15f, 0.3f, 0.5f];

    public Starfield(int seed = 1234)
    {
        var rng = new Random(seed);
        _layers = new (Vector2, float)[Parallax.Length][];
        for (int l = 0; l < Parallax.Length; l++)
        {
            _layers[l] = new (Vector2, float)[90 - l * 25];
            for (int i = 0; i < _layers[l].Length; i++)
            {
                _layers[l][i] = (new Vector2((float)rng.NextDouble() * TileSize, (float)rng.NextDouble() * TileSize),
                    0.25f + (float)rng.NextDouble() * 0.5f);
            }
        }
    }

    /// <summary>Draws in screen space (call between Begin(Matrix.Identity, Additive) and End).</summary>
    public void Draw(PrimitiveBatch pb, Vector2 cameraPosition, int screenWidth, int screenHeight, float twinkleTime)
    {
        for (int l = 0; l < _layers.Length; l++)
        {
            var offset = new Vector2(Mod(-cameraPosition.X * Parallax[l]), Mod(-cameraPosition.Y * Parallax[l]));
            float size = 1f + l * 0.6f;
            for (float ty = -TileSize; ty < screenHeight; ty += TileSize)
            for (float tx = -TileSize; tx < screenWidth; tx += TileSize)
            {
                foreach (var (pos, brightness) in _layers[l])
                {
                    var p = pos + offset + new Vector2(tx, ty);
                    if (p.X < 0 || p.Y < 0 || p.X > screenWidth || p.Y > screenHeight) continue;
                    float twinkle = 0.8f + 0.2f * MathF.Sin(twinkleTime * 2f + pos.X);
                    pb.Rect(p.X, p.Y, size, size, new Color(180, 200, 255) * (brightness * twinkle * (0.5f + l * 0.25f)));
                }
            }
        }
    }

    private static float Mod(float v)
    {
        float r = v % TileSize;
        return r < 0 ? r + TileSize : r;
    }
}

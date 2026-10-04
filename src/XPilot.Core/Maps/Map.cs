using System.Numerics;

namespace XPilot.Core.Maps;

public enum TileShape : byte
{
    Empty,
    Full,
    SolidTopLeft,
    SolidTopRight,
    SolidBottomLeft,
    SolidBottomRight,
}

public enum TileSide { Top, Right, Bottom, Left }

public enum GameModeKind { Dogfight, Race, Ball }

public static class Teams
{
    public const int None = -1;
    public const int Red = 0;
    public const int Blue = 1;

    public static string Name(int team) => team switch
    {
        Red => "Red",
        Blue => "Blue",
        _ => "None",
    };

    public static int Opponent(int team) => team == Red ? Blue : Red;
}

/// <param name="Sign">+1 attracts, -1 repels.</param>
public readonly record struct GravitySource(Vector2 Position, float Sign);

/// <summary>Where a team's ball lives, and where enemy balls must be brought to score.</summary>
public readonly record struct Treasure(Vector2 Position, int Team);

public sealed class Map
{
    public const float TileSize = 32f;

    private readonly TileShape[] _tiles;

    public Map(int width, int height, TileShape[] tiles)
    {
        if (tiles.Length != width * height) throw new ArgumentException("Tile array size mismatch.", nameof(tiles));
        Width = width;
        Height = height;
        _tiles = tiles;
    }

    public string Name { get; init; } = "Unnamed";
    public string Description { get; init; } = "";
    public GameModeKind Mode { get; init; }
    public bool Wrap { get; init; }
    /// <summary>Constant acceleration applied everywhere (px/s^2).</summary>
    public Vector2 Gravity { get; init; }
    public float AttractorStrength { get; init; } = 3_000_000f;
    public int Laps { get; init; } = 3;
    public IReadOnlyList<Vector2> Bases { get; init; } = [];
    /// <summary>Team of each entry in <see cref="Bases"/> (<see cref="Teams.None"/> for neutral bases).</summary>
    public IReadOnlyList<int> BaseTeams { get; init; } = [];
    public IReadOnlyList<Treasure> Treasures { get; init; } = [];
    public IReadOnlyList<Vector2> FuelStations { get; init; } = [];
    /// <summary>Race checkpoints in the order they must be passed.</summary>
    public IReadOnlyList<Vector2> Checkpoints { get; init; } = [];
    public IReadOnlyList<GravitySource> GravitySources { get; init; } = [];
    public string? SourcePath { get; set; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The team's own bases, or every base when the map has none for that team.</summary>
    public IReadOnlyList<Vector2> TeamBases(int team)
    {
        var own = Bases.Where((_, i) => i < BaseTeams.Count && BaseTeams[i] == team).ToList();
        return own.Count > 0 ? own : Bases;
    }

    public Vector2? TreasureOf(int team)
    {
        foreach (var t in Treasures)
        {
            if (t.Team == team) return t.Position;
        }
        return null;
    }
    public float PixelWidth => Width * TileSize;
    public float PixelHeight => Height * TileSize;

    public TileShape GetTile(int x, int y)
    {
        if (Wrap)
        {
            x = MathUtil.Mod(x, Width);
            y = MathUtil.Mod(y, Height);
        }
        else if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return TileShape.Full;
        }
        return _tiles[y * Width + x];
    }

    public static Vector2 TileCenter(int x, int y) => new((x + 0.5f) * TileSize, (y + 0.5f) * TileSize);

    public static (int X, int Y) TileOf(Vector2 p) =>
        ((int)MathF.Floor(p.X / TileSize), (int)MathF.Floor(p.Y / TileSize));

    public Vector2 WrapPosition(Vector2 p) =>
        Wrap ? new Vector2(MathUtil.Mod(p.X, PixelWidth), MathUtil.Mod(p.Y, PixelHeight)) : p;

    /// <summary>Shortest vector from <paramref name="from"/> to <paramref name="to"/>, honoring edge wrap.</summary>
    public Vector2 Delta(Vector2 from, Vector2 to)
    {
        var d = to - from;
        if (Wrap)
        {
            float w = PixelWidth, h = PixelHeight;
            if (d.X > w / 2) d.X -= w; else if (d.X < -w / 2) d.X += w;
            if (d.Y > h / 2) d.Y -= h; else if (d.Y < -h / 2) d.Y += h;
        }
        return d;
    }

    public float Distance(Vector2 a, Vector2 b) => Delta(a, b).Length();

    /// <summary>Writes the solid polygon of a tile shape with its top-left corner at (ox, oy).</summary>
    public static int GetShapePolygon(TileShape shape, float ox, float oy, Span<Vector2> v)
    {
        const float T = TileSize;
        var tl = new Vector2(ox, oy);
        var tr = new Vector2(ox + T, oy);
        var bl = new Vector2(ox, oy + T);
        var br = new Vector2(ox + T, oy + T);
        switch (shape)
        {
            case TileShape.Full: v[0] = tl; v[1] = tr; v[2] = br; v[3] = bl; return 4;
            case TileShape.SolidTopLeft: v[0] = tl; v[1] = tr; v[2] = bl; return 3;
            case TileShape.SolidTopRight: v[0] = tl; v[1] = tr; v[2] = br; return 3;
            case TileShape.SolidBottomLeft: v[0] = tl; v[1] = br; v[2] = bl; return 3;
            case TileShape.SolidBottomRight: v[0] = tr; v[1] = br; v[2] = bl; return 3;
            default: return 0;
        }
    }

    /// <summary>True if the shape covers the whole of the given tile edge.</summary>
    public static bool IsSideSolid(TileShape shape, TileSide side) => shape switch
    {
        TileShape.Full => true,
        TileShape.SolidTopLeft => side is TileSide.Top or TileSide.Left,
        TileShape.SolidTopRight => side is TileSide.Top or TileSide.Right,
        TileShape.SolidBottomLeft => side is TileSide.Bottom or TileSide.Left,
        TileShape.SolidBottomRight => side is TileSide.Bottom or TileSide.Right,
        _ => false,
    };

    public bool PointInWall(Vector2 p)
    {
        var (tx, ty) = TileOf(p);
        var shape = GetTile(tx, ty);
        if (shape == TileShape.Empty) return false;
        if (shape == TileShape.Full) return true;
        float u = p.X / TileSize - tx, v = p.Y / TileSize - ty;
        return shape switch
        {
            TileShape.SolidTopLeft => u + v < 1f,
            TileShape.SolidTopRight => v < u,
            TileShape.SolidBottomLeft => v > u,
            TileShape.SolidBottomRight => u + v > 1f,
            _ => false,
        };
    }

    /// <summary>Finds the deepest wall penetration of a circle. Normal points out of the wall.</summary>
    public bool FindDeepestContact(Vector2 c, float r, out Vector2 normal, out float depth)
    {
        normal = Vector2.Zero;
        depth = 0f;
        bool found = false;
        Span<Vector2> poly = stackalloc Vector2[4];
        int x0 = (int)MathF.Floor((c.X - r) / TileSize), x1 = (int)MathF.Floor((c.X + r) / TileSize);
        int y0 = (int)MathF.Floor((c.Y - r) / TileSize), y1 = (int)MathF.Floor((c.Y + r) / TileSize);
        for (int ty = y0; ty <= y1; ty++)
        for (int tx = x0; tx <= x1; tx++)
        {
            var shape = GetTile(tx, ty);
            if (shape == TileShape.Empty) continue;
            int n = GetShapePolygon(shape, tx * TileSize, ty * TileSize, poly);
            if (Collision.CircleVsPolygon(c, r, poly[..n], out var nrm, out float d) && d > depth)
            {
                normal = nrm;
                depth = d;
                found = true;
            }
        }
        return found;
    }

    public bool CircleOverlapsWall(Vector2 c, float r)
    {
        if (r <= 0.5f) return PointInWall(c);
        return FindDeepestContact(c, r, out _, out _);
    }

    /// <summary>True if a circle of the given radius can sweep from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public bool SegmentClear(Vector2 from, Vector2 to, float radius)
    {
        var d = Delta(from, to);
        float len = d.Length();
        float step = Math.Clamp(radius, 4f, 8f);
        int steps = Math.Max(1, (int)MathF.Ceiling(len / step));
        for (int i = 1; i <= steps; i++)
        {
            var p = WrapPosition(from + d * (i / (float)steps));
            if (CircleOverlapsWall(p, radius)) return false;
        }
        return true;
    }

    /// <summary>How far a circle can travel along <paramref name="dir"/> before touching a wall (capped at max).</summary>
    public float ClearDistance(Vector2 from, Vector2 dir, float max, float radius)
    {
        const float step = 8f;
        for (float d = step; d <= max; d += step)
        {
            if (CircleOverlapsWall(WrapPosition(from + dir * d), radius)) return d - step;
        }
        return max;
    }
}

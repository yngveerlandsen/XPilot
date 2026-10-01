using System.Numerics;
using XPilot.Core.Maps;

namespace XPilot.Core.AI;

/// <summary>
/// Tile-level navigation data for bots: which tiles are open, how far each is from a wall,
/// and Dijkstra distance fields ("flow fields") towards goals.
/// </summary>
public sealed class NavGrid
{
    private static readonly (int Dx, int Dy)[] Directions =
        [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    private readonly Map _map;
    private readonly bool[] _passable;
    private readonly byte[] _clearance;
    private readonly float[] _cost;
    private readonly Dictionary<int, float[]> _checkpointFields = [];

    public NavGrid(Map map)
    {
        _map = map;
        Width = map.Width;
        Height = map.Height;
        int n = Width * Height;
        _passable = new bool[n];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            _passable[y * Width + x] = map.GetTile(x, y) == TileShape.Empty;
        }

        _clearance = ComputeClearance();
        _cost = new float[n];
        for (int i = 0; i < n; i++)
        {
            int c = _clearance[i];
            _cost[i] = 1f + (c <= 1 ? 3f : c == 2 ? 0.8f : 0f);
        }
    }

    public int Width { get; }
    public int Height { get; }

    public bool TryIndex(int x, int y, out int index)
    {
        if (_map.Wrap)
        {
            x = MathUtil.Mod(x, Width);
            y = MathUtil.Mod(y, Height);
        }
        else if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            index = -1;
            return false;
        }
        index = y * Width + x;
        return true;
    }

    public bool IsPassable(int x, int y) => TryIndex(x, y, out int i) && _passable[i];

    /// <summary>Chebyshev distance in tiles to the nearest blocked tile (0 for blocked tiles).</summary>
    public int Clearance(int x, int y) => TryIndex(x, y, out int i) ? _clearance[i] : 0;

    public int IndexOf(Vector2 p)
    {
        var (x, y) = Map.TileOf(_map.WrapPosition(p));
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        return y * Width + x;
    }

    public float[] ComputeField(int goalIndex) => ComputeField([goalIndex]);

    /// <summary>Dijkstra distance (in weighted tiles) from every tile to the nearest goal.</summary>
    public float[] ComputeField(IReadOnlyList<int> goals)
    {
        var dist = new float[Width * Height];
        Array.Fill(dist, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        foreach (int g in goals)
        {
            int goal = NearestPassable(g);
            if (goal < 0) continue;
            dist[goal] = 0f;
            queue.Enqueue(goal, 0f);
        }

        while (queue.TryDequeue(out int idx, out float d))
        {
            if (d > dist[idx]) continue;
            int x = idx % Width, y = idx / Width;
            foreach (var (dx, dy) in Directions)
            {
                if (!CanStep(x, y, dx, dy, out int n)) continue;
                float nd = d + (dx != 0 && dy != 0 ? 1.4142f : 1f) * _cost[n];
                if (nd < dist[n])
                {
                    dist[n] = nd;
                    queue.Enqueue(n, nd);
                }
            }
        }
        return dist;
    }

    public float[] CheckpointField(int checkpoint)
    {
        if (!_checkpointFields.TryGetValue(checkpoint, out var field))
        {
            field = ComputeField(IndexOf(_map.Checkpoints[checkpoint]));
            _checkpointFields[checkpoint] = field;
        }
        return field;
    }

    /// <summary>
    /// Follows a field downhill from tile (ux, uy), appending tile centers to <paramref name="path"/>.
    /// Coordinates are kept unwrapped so the path is continuous across map edges.
    /// Stops when the field value drops to <paramref name="stopValue"/>. Returns the steps taken.
    /// </summary>
    public int Trace(float[] field, ref int ux, ref int uy, int maxSteps, List<Vector2> path, float stopValue = 0f)
    {
        if (!TryIndex(ux, uy, out int cur) || float.IsInfinity(field[cur]))
        {
            if (!SnapToField(field, ref ux, ref uy)) return 0;
            TryIndex(ux, uy, out cur);
            path.Add(Map.TileCenter(ux, uy));
        }

        int steps = 0;
        while (steps < maxSteps && field[cur] > stopValue)
        {
            float best = field[cur];
            int bdx = 0, bdy = 0;
            foreach (var (dx, dy) in Directions)
            {
                if (!CanStep(ux, uy, dx, dy, out int n)) continue;
                if (field[n] < best)
                {
                    best = field[n];
                    bdx = dx;
                    bdy = dy;
                }
            }
            if (bdx == 0 && bdy == 0) break;
            ux += bdx;
            uy += bdy;
            TryIndex(ux, uy, out cur);
            path.Add(Map.TileCenter(ux, uy));
            steps++;
        }
        return steps;
    }

    public float FieldValue(float[] field, int x, int y) =>
        TryIndex(x, y, out int i) ? field[i] : float.PositiveInfinity;

    /// <summary>A random open position with some room around it.</summary>
    public Vector2 RandomOpenPosition(Random rng)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int x = rng.Next(Width), y = rng.Next(Height);
            int i = y * Width + x;
            if (_passable[i] && _clearance[i] >= 2) return Map.TileCenter(x, y);
        }
        return _map.Bases[rng.Next(_map.Bases.Count)];
    }

    private bool CanStep(int x, int y, int dx, int dy, out int n)
    {
        if (!TryIndex(x + dx, y + dy, out n) || !_passable[n]) return false;
        if (dx != 0 && dy != 0 && (!IsPassable(x + dx, y) || !IsPassable(x, y + dy))) return false;
        return true;
    }

    private bool SnapToField(float[] field, ref int ux, ref int uy)
    {
        for (int r = 1; r <= 3; r++)
        {
            float best = float.PositiveInfinity;
            int bx = 0, by = 0;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                if (!TryIndex(ux + dx, uy + dy, out int n) || !_passable[n]) continue;
                if (field[n] < best)
                {
                    best = field[n];
                    bx = dx;
                    by = dy;
                }
            }
            if (!float.IsInfinity(best))
            {
                ux += bx;
                uy += by;
                return true;
            }
        }
        return false;
    }

    private int NearestPassable(int index)
    {
        if (index < 0) return -1;
        if (_passable[index]) return index;
        int x0 = index % Width, y0 = index / Width;
        for (int r = 1; r <= 4; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                if (TryIndex(x0 + dx, y0 + dy, out int n) && _passable[n]) return n;
            }
        }
        return -1;
    }

    private byte[] ComputeClearance()
    {
        const byte cap = 6;
        var clearance = new byte[Width * Height];
        Array.Fill(clearance, byte.MaxValue);
        var queue = new Queue<int>();
        for (int i = 0; i < clearance.Length; i++)
        {
            if (!_passable[i])
            {
                clearance[i] = 0;
                queue.Enqueue(i);
            }
        }
        if (!_map.Wrap)
        {
            // Outside a non-wrapping map counts as wall.
            for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int i = y * Width + x;
                if ((x == 0 || y == 0 || x == Width - 1 || y == Height - 1) && clearance[i] > 1)
                {
                    clearance[i] = 1;
                    queue.Enqueue(i);
                }
            }
        }

        while (queue.Count > 0)
        {
            int idx = queue.Dequeue();
            int next = clearance[idx] + 1;
            if (next > cap) continue;
            int x = idx % Width, y = idx / Width;
            foreach (var (dx, dy) in Directions)
            {
                if (!TryIndex(x + dx, y + dy, out int n)) continue;
                if (clearance[n] > next)
                {
                    clearance[n] = (byte)next;
                    queue.Enqueue(n);
                }
            }
        }
        for (int i = 0; i < clearance.Length; i++)
        {
            if (clearance[i] > cap) clearance[i] = cap;
        }
        return clearance;
    }
}

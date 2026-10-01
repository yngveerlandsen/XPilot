using System.Globalization;
using System.Numerics;

namespace XPilot.Core.Maps;

public sealed class MapFormatException(string message) : Exception(message);

/// <summary>
/// Loads text maps (*.xpm). A map is an optional "key: value" header, a line containing only "---",
/// then the tile grid:
/// <code>
///   x or #   solid wall            q w a s   diagonal wall, solid in the top-left/top-right/bottom-left/bottom-right half
///   _        ship base (spawn)     F         fuel station
///   +        gravity attractor     -         gravity repeller
///   1..9     race checkpoints      . or ' '  empty space
/// </code>
/// Header keys: name, description, mode (dogfight|race), size (W H), border (true adds a wall frame),
/// wrap, gravity (X Y), attractor (strength), laps, checkpoint_radius.
/// </summary>
public static class MapLoader
{
    public static Map Load(string path)
    {
        var map = Parse(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path));
        map.SourcePath = path;
        return map;
    }

    public static Map Parse(string text, string fallbackName = "Unnamed")
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int separator = Array.FindIndex(lines, l => l.Trim() == "---");
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (separator >= 0)
        {
            for (int i = 0; i < separator; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) throw new MapFormatException($"Invalid header line {i + 1}: '{line}'");
                header[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var rows = lines.Skip(separator + 1).ToList();
        int width, height;
        if (header.TryGetValue("size", out var sizeText))
        {
            var parts = sizeText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
                throw new MapFormatException($"Invalid size '{sizeText}', expected 'W H'.");
            for (int y = 0; y < Math.Min(height, rows.Count); y++)
            {
                if (rows[y].TrimEnd().Length > width)
                    throw new MapFormatException($"Row {y + 1} is longer than the declared width {width}.");
            }
        }
        else
        {
            while (rows.Count > 0 && rows[^1].Trim().Length == 0) rows.RemoveAt(rows.Count - 1);
            width = rows.Count == 0 ? 0 : rows.Max(r => r.TrimEnd().Length);
            height = rows.Count;
        }
        if (width <= 0 || height <= 0) throw new MapFormatException("Map grid is empty.");

        bool border = GetBool(header, "border", false);
        int off = border ? 1 : 0;
        int w = width + 2 * off, h = height + 2 * off;
        var tiles = new TileShape[w * h];
        if (border)
        {
            for (int x = 0; x < w; x++) { tiles[x] = TileShape.Full; tiles[(h - 1) * w + x] = TileShape.Full; }
            for (int y = 0; y < h; y++) { tiles[y * w] = TileShape.Full; tiles[y * w + w - 1] = TileShape.Full; }
        }

        var bases = new List<Vector2>();
        var fuel = new List<Vector2>();
        var gravity = new List<GravitySource>();
        var checkpoints = new SortedDictionary<int, Vector2>();

        for (int y = 0; y < height; y++)
        {
            string row = y < rows.Count ? rows[y] : "";
            for (int x = 0; x < width; x++)
            {
                char c = x < row.Length ? row[x] : ' ';
                int tx = x + off, ty = y + off;
                var center = Map.TileCenter(tx, ty);
                TileShape shape = TileShape.Empty;
                switch (c)
                {
                    case ' ': case '.': break;
                    case 'x': case 'X': case '#': shape = TileShape.Full; break;
                    case 'q': shape = TileShape.SolidTopLeft; break;
                    case 'w': shape = TileShape.SolidTopRight; break;
                    case 'a': shape = TileShape.SolidBottomLeft; break;
                    case 's': shape = TileShape.SolidBottomRight; break;
                    case '_': bases.Add(center); break;
                    case 'F': fuel.Add(center); break;
                    case '+': gravity.Add(new GravitySource(center, 1f)); break;
                    case '-': gravity.Add(new GravitySource(center, -1f)); break;
                    case >= '1' and <= '9':
                        int number = c - '0';
                        if (!checkpoints.TryAdd(number, center))
                            throw new MapFormatException($"Checkpoint {number} appears more than once.");
                        break;
                    default:
                        throw new MapFormatException($"Unknown map character '{c}' at row {y + 1}, column {x + 1}.");
                }
                tiles[ty * w + tx] = shape;
            }
        }

        var orderedCheckpoints = checkpoints.Values.ToList();
        int expected = 1;
        foreach (var number in checkpoints.Keys)
        {
            if (number != expected++) throw new MapFormatException("Checkpoints must be numbered 1..N without gaps.");
        }

        var mode = header.TryGetValue("mode", out var modeText)
            ? modeText.ToLowerInvariant() switch
            {
                "race" => GameModeKind.Race,
                "dogfight" => GameModeKind.Dogfight,
                _ => throw new MapFormatException($"Unknown mode '{modeText}'."),
            }
            : orderedCheckpoints.Count > 0 ? GameModeKind.Race : GameModeKind.Dogfight;

        if (bases.Count == 0) throw new MapFormatException("Map has no bases ('_').");
        if (mode == GameModeKind.Race && orderedCheckpoints.Count < 2)
            throw new MapFormatException("Race maps need at least two checkpoints.");

        return new Map(w, h, tiles)
        {
            Name = header.GetValueOrDefault("name", fallbackName),
            Description = header.GetValueOrDefault("description", ""),
            Mode = mode,
            Wrap = GetBool(header, "wrap", false),
            Gravity = GetVector(header, "gravity", Vector2.Zero),
            AttractorStrength = GetFloat(header, "attractor", 3_000_000f),
            Laps = (int)GetFloat(header, "laps", 3),
            CheckpointRadius = GetFloat(header, "checkpoint_radius", 96f),
            Bases = bases,
            FuelStations = fuel,
            Checkpoints = orderedCheckpoints,
            GravitySources = gravity,
        };
    }

    private static bool GetBool(Dictionary<string, string> header, string key, bool fallback)
    {
        if (!header.TryGetValue(key, out var v)) return fallback;
        return v.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" => true,
            "false" or "no" or "0" => false,
            _ => throw new MapFormatException($"Invalid boolean for '{key}': '{v}'."),
        };
    }

    private static float GetFloat(Dictionary<string, string> header, string key, float fallback)
    {
        if (!header.TryGetValue(key, out var v)) return fallback;
        if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            throw new MapFormatException($"Invalid number for '{key}': '{v}'.");
        return f;
    }

    private static Vector2 GetVector(Dictionary<string, string> header, string key, Vector2 fallback)
    {
        if (!header.TryGetValue(key, out var v)) return fallback;
        var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            throw new MapFormatException($"Invalid vector for '{key}': '{v}', expected 'X Y'.");
        return new Vector2(x, y);
    }
}

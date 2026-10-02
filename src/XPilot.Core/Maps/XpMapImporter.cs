using System.Globalization;
using System.Numerics;

namespace XPilot.Core.Maps;

/// <summary>
/// Reads maps in the original XPilot ".xp" format: "key: value" options, with the grid in a
/// <c>mapData: \multiline: EndOfMapdata</c> block. Walls, bases, fuel, gravity, treasures, teams, race
/// checkpoints, edge wrapping and constant gravity carry over. Things this game doesn't have (cannons,
/// wormholes, targets, items, currents, rotating gravity, decorations) become open space.
/// </summary>
public static class XpMapImporter
{
    /// <summary>XPilot's default gravity (pointing down) when a map doesn't set one.</summary>
    private const float DefaultGravity = -0.14f;
    /// <summary>
    /// Converts XPilot gravity (pixels per frame² at its 14 fps, for ships that thrust about 2.75 of those) to
    /// px/s² here, keeping gravity's strength relative to a ship's thrust the same.
    /// </summary>
    private const float GravityScale = 320f / 2.75f;

    public static bool IsXpFormat(string text) => text.Contains("mapData", StringComparison.OrdinalIgnoreCase);

    public static Map Parse(string text, string fallbackName = "Unnamed")
    {
        var (options, grid) = ReadOptions(text);
        int width = GetInt(options, "mapwidth") ?? grid.Max(r => r.Length);
        int height = GetInt(options, "mapheight") ?? grid.Count;
        if (width <= 0 || height <= 0 || grid.Count == 0) throw new MapFormatException("XPilot map has no map data.");

        var tiles = new TileShape[width * height];
        var bases = new List<(Vector2 Position, int TeamDigit)>();
        var treasures = new List<Vector2>();
        var fuel = new List<Vector2>();
        var gravityTiles = new Dictionary<(int X, int Y), float>();
        var checkpoints = new SortedDictionary<char, Vector2>();

        for (int y = 0; y < height; y++)
        {
            string row = y < grid.Count ? grid[y] : "";
            for (int x = 0; x < width; x++)
            {
                char c = x < row.Length ? row[x] : ' ';
                var center = Map.TileCenter(x, y);
                // XPilot names its triangles with y pointing up, so they come out flipped vertically here.
                tiles[y * width + x] = c switch
                {
                    'x' => TileShape.Full,
                    's' => TileShape.SolidTopLeft,
                    'a' => TileShape.SolidTopRight,
                    'w' => TileShape.SolidBottomLeft,
                    'q' => TileShape.SolidBottomRight,
                    _ => TileShape.Empty,
                };
                switch (c)
                {
                    case '_': bases.Add((center, -1)); break;
                    case >= '0' and <= '9': bases.Add((center, c - '0')); break;
                    case '#': fuel.Add(center); break;
                    case '*' or '^': treasures.Add(center); break;
                    case '+': gravityTiles[(x, y)] = 1f; break;
                    case '-': gravityTiles[(x, y)] = -1f; break;
                    case >= 'A' and <= 'Z': checkpoints.TryAdd(c, center); break;
                }
            }
        }
        if (bases.Count == 0) throw new MapFormatException("XPilot map has no bases.");

        bool wrap = GetBool(options, "edgewrap");
        var size = new Vector2(width, height) * Map.TileSize;
        var teams = PickTeams(bases, treasures, wrap, size);
        bool timing = GetBool(options, "timing") || GetBool(options, "race");
        var mode = timing && checkpoints.Count >= 2 ? GameModeKind.Race
            : GetBool(options, "teamplay") && teams != null ? GameModeKind.Ball
            : GameModeKind.Dogfight;

        int TeamOf(int digit) => mode == GameModeKind.Ball && teams != null
            ? digit == teams.Value.Red ? Teams.Red : digit == teams.Value.Blue ? Teams.Blue : Teams.None
            : Teams.None;

        var treasureList = new List<Treasure>();
        if (mode == GameModeKind.Ball)
        {
            // XPilot gives each treasure to the team whose base is closest.
            foreach (var t in treasures)
            {
                var nearest = bases.Where(b => TeamOf(b.TeamDigit) != Teams.None)
                    .MinBy(b => Distance(b.Position, t, wrap, size));
                treasureList.Add(new Treasure(t, TeamOf(nearest.TeamDigit)));
            }
        }

        // XPilot races start and finish at checkpoint A: pass B, C, ... and then A again for a lap.
        var order = checkpoints.Values.Skip(1).Append(checkpoints.Values.FirstOrDefault()).ToList();

        string? author = Get(options, "mapauthor")?.Trim('"', ' ');
        return new Map(width, height, tiles)
        {
            Name = Get(options, "mapname") ?? fallbackName,
            Description = author is { Length: > 0 } ? $"Classic XPilot map by {author}." : "Classic XPilot map.",
            Mode = mode,
            Wrap = wrap,
            Gravity = ConvertGravity(options),
            Laps = GetInt(options, "racelaps") ?? 3,
            // Not an original XPilot option; maps made for this game can widen the gates.
            CheckpointRadius = GetFloat(options, "checkpointradius") ?? 96f,
            Bases = bases.Select(b => b.Position).ToList(),
            BaseTeams = bases.Select(b => TeamOf(b.TeamDigit)).ToList(),
            Treasures = treasureList,
            FuelStations = fuel,
            Checkpoints = mode == GameModeKind.Race ? order : [],
            GravitySources = GravityWells(gravityTiles),
        };
    }

    /// <summary>
    /// The two team digits to play as Red and Blue: those with the most bases among teams that are closest to
    /// at least one treasure. Null if fewer than two teams own a treasure.
    /// </summary>
    private static (int Red, int Blue)? PickTeams(List<(Vector2 Position, int TeamDigit)> bases, List<Vector2> treasures, bool wrap, Vector2 size)
    {
        var teamBases = bases.Where(b => b.TeamDigit >= 0).ToList();
        if (teamBases.Count == 0 || treasures.Count < 2) return null;
        var owners = treasures
            .Select(t => teamBases.MinBy(b => Distance(b.Position, t, wrap, size)).TeamDigit)
            .ToHashSet();
        var ranked = owners.OrderByDescending(d => teamBases.Count(b => b.TeamDigit == d)).ThenBy(d => d).ToList();
        return ranked.Count >= 2 ? (ranked[0], ranked[1]) : null;
    }

    /// <summary>
    /// XPilot maps build a gravity well from a clump of + or - tiles. Here every tile is a full-strength well,
    /// so the tiles of a clump share one well's strength between them.
    /// </summary>
    private static List<GravitySource> GravityWells(Dictionary<(int X, int Y), float> tiles)
    {
        var wells = new List<GravitySource>();
        var seen = new HashSet<(int X, int Y)>();
        foreach (var (start, sign) in tiles)
        {
            if (!seen.Add(start)) continue;
            var clump = new List<(int X, int Y)> { start };
            for (int i = 0; i < clump.Count; i++)
            {
                var (cx, cy) = clump[i];
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    var next = (cx + dx, cy + dy);
                    if (tiles.TryGetValue(next, out float s) && s == sign && seen.Add(next)) clump.Add(next);
                }
            }
            foreach (var (x, y) in clump) wells.Add(new GravitySource(Map.TileCenter(x, y), sign / clump.Count));
        }
        return wells;
    }

    private static float Distance(Vector2 a, Vector2 b, bool wrap, Vector2 size)
    {
        var d = Vector2.Abs(a - b);
        if (wrap) d = Vector2.Min(d, size - d);
        return d.Length();
    }

    private static Vector2 ConvertGravity(Dictionary<string, string> options)
    {
        float g = GetFloat(options, "gravity") ?? DefaultGravity;
        float angle = (GetFloat(options, "gravityangle") ?? 90f) * MathF.PI / 180f;
        // XPilot's y axis points up; here it points down.
        return new Vector2(MathF.Cos(angle), -MathF.Sin(angle)) * (g * GravityScale);
    }

    /// <summary>Splits the file into lower-cased options and the map grid rows.</summary>
    private static (Dictionary<string, string> Options, List<string> Grid) ReadOptions(string text)
    {
        var options = new Dictionary<string, string>();
        var grid = new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith('#')) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim().ToLowerInvariant();
            var value = StripOverride(line[(colon + 1)..].Trim());

            if (value.StartsWith(@"\multiline:", StringComparison.OrdinalIgnoreCase))
            {
                var end = value[@"\multiline:".Length..].Trim();
                var block = new List<string>();
                for (i++; i < lines.Length && lines[i].Trim() != end; i++) block.Add(lines[i]);
                if (key == "mapdata") grid = block;
                else options[key] = string.Join(" ", block).Trim();
                continue;
            }
            int comment = value.IndexOf('#');
            if (comment >= 0) value = value[..comment].Trim();
            options[key] = value;
        }
        return (options, grid);
    }

    private static string StripOverride(string value) =>
        value.StartsWith(@"\override:", StringComparison.OrdinalIgnoreCase) ? value[@"\override:".Length..].Trim() : value;

    private static string? Get(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    private static bool GetBool(Dictionary<string, string> options, string key) =>
        Get(options, key)?.ToLowerInvariant() is "yes" or "true" or "on" or "1";

    private static float? GetFloat(Dictionary<string, string> options, string key) =>
        float.TryParse(Get(options, key), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : null;

    private static int? GetInt(Dictionary<string, string> options, string key) =>
        int.TryParse(Get(options, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
}

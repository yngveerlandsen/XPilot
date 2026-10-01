using System.Numerics;
using XPilot.Core;
using XPilot.Core.Maps;
using XPilot.Core.Rules;
using XPilot.Core.Simulation;

namespace XPilot.Core.Tests;

internal static class TestUtil
{
    /// <summary>A 20x12 bordered open box with two bases.</summary>
    public const string OpenBox = """
        name: Box
        border: true
        ---
        ....................
        ..._............_...
        ....................
        ....................
        ....................
        ....................
        ....................
        ....................
        ....................
        ....................
        ....................
        ....................
        """;

    public static string MapsDirectory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "maps"))) dir = dir.Parent;
            return dir == null ? throw new DirectoryNotFoundException("maps folder not found") : Path.Combine(dir.FullName, "maps");
        }
    }

    public static IEnumerable<string> MapFiles => Directory.GetFiles(MapsDirectory, "*.xpm");

    public static Map LoadMap(string name) => MapLoader.Load(Path.Combine(MapsDirectory, name + ".xpm"));

    /// <summary>A started dogfight world on the given map with <paramref name="ships"/> ships.</summary>
    public static World DogfightWorld(string mapText = OpenBox, int ships = 1, GameConfig? config = null)
    {
        var world = new World(MapLoader.Parse(mapText), config ?? new GameConfig(), new DogfightRules(), seed: 1);
        for (int i = 0; i < ships; i++) world.AddShip($"S{i}", false);
        world.Start();
        return world;
    }

    public static void Place(Ship s, Vector2 position, Vector2 velocity, float heading = 0f)
    {
        s.Position = s.PrevPosition = position;
        s.Velocity = velocity;
        s.Heading = heading;
        s.SpawnProtection = 0f;
        s.FireCooldown = 0f;
    }

    public static void Run(World world, int ticks, params ShipInput[] inputs)
    {
        for (int i = 0; i < ticks; i++) world.Step(inputs);
    }
}

using System.Numerics;
using XPilot.Core.Maps;

namespace XPilot.Core.Tests;

public class MapTests
{
    [Fact]
    public void Parse_ReadsHeaderTilesAndObjects()
    {
        var map = MapLoader.Parse("""
            name: Test
            mode: race
            border: true
            gravity: 0 25
            laps: 2
            ---
            _.1.
            xqF2
            """);

        Assert.Equal("Test", map.Name);
        Assert.Equal(GameModeKind.Race, map.Mode);
        Assert.Equal(new Vector2(0, 25), map.Gravity);
        Assert.Equal(2, map.Laps);
        Assert.Equal(6, map.Width);
        Assert.Equal(4, map.Height);
        Assert.Equal(TileShape.Full, map.GetTile(0, 0));
        Assert.Equal(TileShape.Full, map.GetTile(1, 2));
        Assert.Equal(TileShape.SolidTopLeft, map.GetTile(2, 2));
        Assert.Equal(TileShape.Empty, map.GetTile(1, 1));
        Assert.Equal(Map.TileCenter(1, 1), Assert.Single(map.Bases));
        Assert.Equal(Map.TileCenter(3, 2), Assert.Single(map.FuelStations));
        Assert.Equal([Map.TileCenter(3, 1), Map.TileCenter(4, 2)], map.Checkpoints);
    }

    [Fact]
    public void Parse_OutsideNonWrappingMapIsWall()
    {
        var map = MapLoader.Parse(TestUtil.OpenBox);
        Assert.Equal(TileShape.Full, map.GetTile(-1, 3));
        Assert.Equal(TileShape.Full, map.GetTile(3, 999));
    }

    [Theory]
    [InlineData("---\n...\n", "no bases")]
    [InlineData("---\n_?.\n", "Unknown map character")]
    [InlineData("mode: race\n---\n_1.\n", "at least two checkpoints")]
    [InlineData("---\n_13\n", "without gaps")]
    [InlineData("size: 2 1\n---\n_..\n", "longer than")]
    public void Parse_RejectsInvalidMaps(string text, string expectedMessage)
    {
        var ex = Assert.Throws<MapFormatException>(() => MapLoader.Parse(text));
        Assert.Contains(expectedMessage, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Delta_WrapsAroundEdges()
    {
        var map = MapLoader.Parse("wrap: true\n---\n_.........\n..........\n");
        var a = new Vector2(10, 10);
        var b = new Vector2(map.PixelWidth - 10, 10);
        Assert.Equal(new Vector2(-20, 0), map.Delta(a, b));
        Assert.Equal(new Vector2(5, 10), map.WrapPosition(new Vector2(map.PixelWidth + 5, 10)));
    }

    [Fact]
    public void PointInWall_HandlesDiagonalHalves()
    {
        var map = MapLoader.Parse("---\nq_\n");
        Assert.True(map.PointInWall(new Vector2(4, 4)));
        Assert.False(map.PointInWall(new Vector2(28, 28)));
    }

    [Fact]
    public void Collision_CircleAgainstSquareGivesOutwardNormal()
    {
        Span<Vector2> square = [new(0, 0), new(32, 0), new(32, 32), new(0, 32)];
        Assert.True(Collision.CircleVsPolygon(new Vector2(40, 16), 10, square, out var n, out float depth));
        Assert.Equal(1f, n.X, 3);
        Assert.Equal(2f, depth, 3);
        Assert.False(Collision.CircleVsPolygon(new Vector2(50, 16), 10, square, out _, out _));
    }

    public static IEnumerable<object[]> ShippedMaps => TestUtil.MapFiles.Select(f => new object[] { Path.GetRelativePath(TestUtil.MapsDirectory, f) });

    [Theory]
    [MemberData(nameof(ShippedMaps))]
    public void ShippedMaps_LoadAndHaveRoomAtSpawnPoints(string file)
    {
        var map = MapLoader.Load(Path.Combine(TestUtil.MapsDirectory, file));
        Assert.True(map.Bases.Count >= 4, "maps should support at least 4 ships");
        foreach (var p in map.Bases.Concat(map.Checkpoints))
        {
            Assert.False(map.CircleOverlapsWall(p, 14f), $"{file}: spawn/checkpoint at {p} touches a wall");
        }
    }
}

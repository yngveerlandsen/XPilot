using Microsoft.Xna.Framework;

namespace XPilot.Desktop.Graphics;

public static class Palette
{
    public static readonly Color Background = new(3, 4, 12);
    public static readonly Color WallFill = new(10, 18, 42);
    public static readonly Color WallEdge = new(70, 130, 255);
    public static readonly Color Fuel = new(80, 255, 150);
    public static readonly Color Attractor = new(190, 110, 255);
    public static readonly Color Repeller = new(255, 150, 70);
    public static readonly Color Checkpoint = new(255, 220, 60);
    public static readonly Color CheckpointDim = new(80, 80, 100);
    public static readonly Color Base = new(70, 100, 150);
    public static readonly Color Text = new(200, 220, 255);
    public static readonly Color TextDim = new(110, 130, 170);
    public static readonly Color Accent = new(90, 220, 255);
    public static readonly Color Warning = new(255, 90, 80);
    public static readonly Color Panel = new(4, 8, 20);

    private static readonly Color[] ShipColors =
    [
        new(90, 230, 255),   // player: cyan
        new(255, 95, 95),
        new(130, 255, 120),
        new(255, 205, 70),
        new(215, 125, 255),
        new(255, 140, 200),
        new(255, 155, 60),
        new(160, 170, 255),
        new(200, 255, 200),
    ];

    public static Color Ship(int index) => ShipColors[index % ShipColors.Length];
}

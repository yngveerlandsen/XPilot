using System.Globalization;
using Microsoft.Xna.Framework;

namespace XPilot.Desktop.Graphics;

public enum TextAlign { Left, Center, Right }

/// <summary>
/// A stroke font drawn with lines, in keeping with XPilot's vector look. Glyphs live on a 4x6 grid
/// (y down); each glyph is one or more polylines separated by '|'.
/// </summary>
public static class VectorFont
{
    private const float GlyphHeight = 6f;
    private const float Advance = 6f;

    private static readonly Dictionary<char, Vector2[][]> Glyphs = Build(new Dictionary<char, string>
    {
        ['A'] = "0,6 0,2 2,0 4,2 4,6|0,3.5 4,3.5",
        ['\''] = "2,0 1.5,1.5",
        ['B'] = "0,3 0,0 3,0 4,1 4,2 3,3 0,3 0,6 3,6 4,5 4,4 3,3",
        ['C'] = "4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5",
        ['D'] = "0,0 0,6 3,6 4,5 4,1 3,0 0,0",
        ['E'] = "4,0 0,0 0,6 4,6|0,3 3,3",
        ['F'] = "4,0 0,0 0,6|0,3 3,3",
        ['G'] = "4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5 4,3 2,3",
        ['H'] = "0,0 0,6|4,0 4,6|0,3 4,3",
        ['I'] = "1,0 3,0|2,0 2,6|1,6 3,6",
        ['J'] = "4,0 4,5 3,6 1,6 0,5",
        ['K'] = "0,0 0,6|4,0 0,3.5|1.4,2.6 4,6",
        ['L'] = "0,0 0,6 4,6",
        ['M'] = "0,6 0,0 2,3 4,0 4,6",
        ['N'] = "0,6 0,0 4,6 4,0",
        ['O'] = "1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,1 1,0",
        ['P'] = "0,6 0,0 3,0 4,1 4,2 3,3 0,3",
        ['Q'] = "1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,1 1,0|2.5,4.5 4,6",
        ['R'] = "0,6 0,0 3,0 4,1 4,2 3,3 0,3|2,3 4,6",
        ['S'] = "4,1 3,0 1,0 0,1 0,2 1,3 3,3 4,4 4,5 3,6 1,6 0,5",
        ['T'] = "0,0 4,0|2,0 2,6",
        ['U'] = "0,0 0,5 1,6 3,6 4,5 4,0",
        ['V'] = "0,0 2,6 4,0",
        ['W'] = "0,0 1,6 2,3 3,6 4,0",
        ['X'] = "0,0 4,6|4,0 0,6",
        ['Y'] = "0,0 2,3 4,0|2,3 2,6",
        ['Z'] = "0,0 4,0 0,6 4,6",
        ['0'] = "1,0 3,0 4,1 4,5 3,6 1,6 0,5 0,1 1,0|0.5,5 3.5,1",
        ['1'] = "1,1 2,0 2,6|1,6 3,6",
        ['2'] = "0,1 1,0 3,0 4,1 4,2 0,6 4,6",
        ['3'] = "0,1 1,0 3,0 4,1 4,2 3,3 4,4 4,5 3,6 1,6 0,5|1.5,3 3,3",
        ['4'] = "3,6 3,0 0,4 4,4",
        ['5'] = "4,0 0,0 0,3 3,3 4,4 4,5 3,6 0,6",
        ['6'] = "4,1 3,0 1,0 0,1 0,5 1,6 3,6 4,5 4,4 3,3 0,3",
        ['7'] = "0,0 4,0 1,6",
        ['8'] = "1,0 3,0 4,1 4,2 3,3 1,3 0,4 0,5 1,6 3,6 4,5 4,4 3,3|1,3 0,2 0,1 1,0",
        ['9'] = "4,3 1,3 0,2 0,1 1,0 3,0 4,1 4,5 3,6 1,6 0,5",
        [':'] = "2,1.5 2,2.3|2,4.2 2,5",
        ['.'] = "2,5.3 2,6",
        [','] = "2,5 1.4,7",
        ['-'] = "1,3 3,3",
        ['+'] = "0.5,3 3.5,3|2,1.5 2,4.5",
        ['/'] = "4,0 0,6",
        ['('] = "3,0 1.5,1.5 1.5,4.5 3,6",
        [')'] = "1,0 2.5,1.5 2.5,4.5 1,6",
        ['!'] = "2,0 2,4|2,5.3 2,6",
        ['?'] = "0,1 1,0 3,0 4,1 4,2 2,3.5 2,4.3|2,5.3 2,6",
        ['%'] = "0,6 4,0|0,0 1,0 1,1 0,1 0,0|3,5 4,5 4,6 3,6 3,5",
        ['\''] = "2,0 2,1.5",
        ['>'] = "0.5,0.5 3.5,3 0.5,5.5",
        ['<'] = "3.5,0.5 0.5,3 3.5,5.5",
        ['='] = "0.5,2 3.5,2|0.5,4 3.5,4",
        ['_'] = "0,6 4,6",
        ['#'] = "1,0 1,6|3,0 3,6|0,2 4,2|0,4 4,4",
        ['*'] = "2,1 2,5|0.5,2 3.5,4|3.5,2 0.5,4",
        ['['] = "3,0 1.5,0 1.5,6 3,6",
        [']'] = "1,0 2.5,0 2.5,6 1,6",
    });

    private static Dictionary<char, Vector2[][]> Build(Dictionary<char, string> source)
    {
        var result = new Dictionary<char, Vector2[][]>();
        foreach (var (ch, def) in source)
        {
            result[ch] = def.Split('|')
                .Select(stroke => stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pt =>
                    {
                        var xy = pt.Split(',');
                        return new Vector2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
                    })
                    .ToArray())
                .ToArray();
        }
        return result;
    }

    /// <summary>Width of the text when drawn with glyphs <paramref name="size"/> pixels tall.</summary>
    public static float Measure(string text, float size) =>
        text.Length == 0 ? 0f : (text.Length * Advance - 2f) * (size / GlyphHeight);

    public static void Draw(PrimitiveBatch pb, string text, Vector2 position, float size, Color color,
        TextAlign align = TextAlign.Left, float? thickness = null, float glow = 0.8f)
    {
        float unit = size / GlyphHeight;
        float width = thickness ?? MathF.Max(1.2f, size / 9f);
        float x = align switch
        {
            TextAlign.Center => position.X - Measure(text, size) / 2f,
            TextAlign.Right => position.X - Measure(text, size),
            _ => position.X,
        };
        foreach (char raw in text)
        {
            char c = char.ToUpperInvariant(raw);
            if (Glyphs.TryGetValue(c, out var strokes))
            {
                foreach (var stroke in strokes)
                {
                    for (int i = 0; i + 1 < stroke.Length; i++)
                    {
                        var a = new Vector2(x, position.Y) + stroke[i] * unit;
                        var b = new Vector2(x, position.Y) + stroke[i + 1] * unit;
                        pb.GlowLine(a, b, width, color, glow);
                    }
                }
            }
            x += Advance * unit;
        }
    }
}

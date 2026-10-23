using System.Numerics;
using Folio.Painting;
using Folio.Typography;
using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Folio's glyph outlines (TrueType and CFF) against SkiaSharp's, over the fonts in tests/fonts.</summary>
public class GlyphOutlineTests
{
    private static readonly string Folder = Path.Combine(RepoPaths.Tests, "fonts");

    public static TheoryData<string> Fonts =>
        new(Directory.GetFiles(Folder).Where(f => f.EndsWith(".ttf") || f.EndsWith(".otf")).Select(Path.GetFileName).Where(n => !n!.Contains("Emoji")).Order()!);

    // The glyphs checked: the first few hundred, every so often after that, and the ones the ASCII range maps to.
    private static IEnumerable<ushort> Glyphs(FontFace face)
    {
        var chosen = new SortedSet<ushort>();
        for (var g = 0; g < Math.Min(face.GlyphCount, 300); g++)
            chosen.Add((ushort)g);
        for (var g = 300; g < face.GlyphCount; g += 29)
            chosen.Add((ushort)g);
        for (var c = 0x21; c < 0x7F; c++)
            chosen.Add(face.GlyphFor(c));
        for (var c = 0x590; c < 0x700; c += 3)
            chosen.Add(face.GlyphFor(c));
        for (var c = 0x4E00; c < 0xA000; c += 97)
            chosen.Add(face.GlyphFor(c));
        return chosen;
    }

    // Winding number of a point against flattened path (nonzero fill).
    private static bool Contains(List<Vector2[]> polygons, float x, float y)
    {
        var winding = 0;
        foreach (var polygon in polygons)
        {
            for (var i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                if (a.Y <= y)
                {
                    if (b.Y > y && (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y) > 0)
                        winding++;
                }
                else if (b.Y <= y && (b.X - a.X) * (y - a.Y) - (x - a.X) * (b.Y - a.Y) < 0)
                    winding--;
            }
        }
        return winding != 0;
    }

    private static List<Vector2[]> Flatten(PathData path, float yScale)
    {
        var polygons = new List<Vector2[]>();
        var current = new List<Vector2>();
        var last = Vector2.Zero;
        Vector2 Flip(Vector2 p) => new(p.X, p.Y * yScale);
        foreach (var command in path.Commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    if (current.Count > 0)
                        polygons.Add([.. current]);
                    current.Clear();
                    current.Add(Flip(command.P1));
                    last = command.P1;
                    break;
                case PathVerb.LineTo:
                    current.Add(Flip(command.P1));
                    last = command.P1;
                    break;
                case PathVerb.CubicTo:
                    for (var i = 1; i <= 16; i++)
                    {
                        var t = i / 16f;
                        var u = 1 - t;
                        var p = u * u * u * last + 3 * u * u * t * command.P1 + 3 * u * t * t * command.P2 + t * t * t * command.P3;
                        current.Add(Flip(p));
                    }
                    last = command.P3;
                    break;
                case PathVerb.Close:
                    if (current.Count > 0)
                        polygons.Add([.. current]);
                    current.Clear();
                    break;
            }
        }
        if (current.Count > 0)
            polygons.Add([.. current]);
        return polygons;
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void OutlinesMatchSkiaSharp(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Folder, name));
        var face = FontFace.Parse(bytes)!;
        using var typeface = SKTypeface.FromData(SKData.CreateCopy(bytes));
        using var font = new SKFont(typeface, face.UnitsPerEm) { Hinting = SKFontHinting.None, Subpixel = true, LinearMetrics = true, Edging = SKFontEdging.Alias };

        var checkedGlyphs = 0;
        var worst = 0.0;
        string? worstGlyph = null;
        foreach (var glyph in Glyphs(face))
        {
            using var expected = font.GetGlyphPath(glyph);
            var actual = GlyphOutlines.Get(face, glyph);
            var skiaEmpty = expected is null || expected.IsEmpty;
            var folioEmpty = actual is null || actual.Commands.Count == 0;
            if (skiaEmpty || folioEmpty)
            {
                Assert.True(skiaEmpty == folioEmpty, $"{name} glyph {glyph}: Skia empty={skiaEmpty}, Folio empty={folioEmpty}");
                continue;
            }

            // Skia's coordinates have y down.
            var polygons = Flatten(actual!, -1);
            var bounds = expected!.TightBounds;
            var width = bounds.Width;
            var height = bounds.Height;
            var mismatches = 0;
            const int Steps = 40;
            for (var j = 0; j < Steps; j++)
            {
                for (var i = 0; i < Steps; i++)
                {
                    var x = bounds.Left - width * 0.05f + (i + 0.3183f) / Steps * width * 1.1f;
                    var y = bounds.Top - height * 0.05f + (j + 0.4142f) / Steps * height * 1.1f;
                    if (expected.Contains(x, y) != Contains(polygons, x, y))
                        mismatches++;
                }
            }
            var ratio = mismatches / (double)(Steps * Steps);
            if (ratio > worst)
            {
                worst = ratio;
                worstGlyph = glyph.ToString();
            }
            checkedGlyphs++;
        }

        Assert.True(checkedGlyphs > 20, $"{name}: only {checkedGlyphs} glyphs with outlines were compared");
        // Windows: at most 0.4% of the sampled points. SkiaSharp's own glyph paths differ a little between operating systems
        // (on Linux CI every font's worst glyph differed at 1.6 to 2.8% of the points), and Folio's reader is the same code on both,
        // so elsewhere the limit is 4%: a real error, such as an origin shift, still differs at several times that.
        var limit = OperatingSystem.IsWindows() ? 0.004 : 0.04;
        Assert.True(worst <= limit, $"{name}: glyph {worstGlyph} differs at {worst:P2} of the sampled points (limit {limit:P1})");
    }

    [Theory]
    [InlineData("SourceSans3-Regular.ttf")]
    [InlineData("NotoSansJP-Regular.otf")]
    public void OutlinesOfACorruptedFontNeverThrowOrRunLong(string name)
    {
        var original = File.ReadAllBytes(Path.Combine(Folder, name));
        var random = new Random(23);
        var parsed = 0;
        for (var run = 0; run < 150; run++)
        {
            var bytes = (byte[])original.Clone();
            // Flip a few bits in the first 600 KB, which holds the outline tables of these fonts.
            for (var flips = 0; flips < 1 + run % 6; flips++)
                bytes[random.Next(Math.Min(bytes.Length, 600_000))] ^= (byte)(1 << random.Next(8));
            if (FontFace.Parse(bytes) is not { } face)
                continue;
            parsed++;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (var g = 0; g < Math.Min(face.GlyphCount, 60); g++)
                GlyphOutlines.Get(face, (ushort)(g * 7 % Math.Max(1, face.GlyphCount)));
            Assert.True(clock.ElapsedMilliseconds < 3000, $"{name}: outlines took {clock.ElapsedMilliseconds} ms");
        }
        Assert.True(parsed > 10);
    }
}

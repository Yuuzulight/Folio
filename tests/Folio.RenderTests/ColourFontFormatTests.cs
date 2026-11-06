using System.Buffers.Binary;
using System.Numerics;
using Folio.Painting;
using Folio.Painting.Software;
using Folio.Typography;

namespace Folio.RenderTests;

// Layouts are taken from the OpenType COLR/CPAL specification, not the decoder's offsets.
public class ColourFontFormatTests
{
    private static byte[] Source => File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
    private static ushort Glyph => FontFace.Parse(Source)!.GlyphFor('A');
    private static byte[] Fill(ushort palette = 0, short alpha = 0x4000)
    {
        byte[] paint = [10, 0, 0, 6, 0, 0, 2, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(paint.AsSpan(4), Glyph);
        BinaryPrimitives.WriteUInt16BigEndian(paint.AsSpan(7), palette);
        BinaryPrimitives.WriteInt16BigEndian(paint.AsSpan(9), alpha);
        return paint;
    }
    private static FontFace Face(byte[] paint, byte[]? palette = null)
    {
        var colr = new byte[44 + paint.Length];
        BinaryPrimitives.WriteUInt16BigEndian(colr, 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(14), 34);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(38), Glyph);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(40), 10);
        paint.CopyTo(colr, 44);
        return FontFace.Parse(SoftwareRasterTests.AddTables(Source,
            ("COLR", colr), ("CPAL", palette ?? SoftwareRasterTests.SyntheticCpal())))!;
    }

    [Fact]
    public void AffineMatrixIsReadThroughItsOffsetWithCorrectBasisOrder()
    {
        byte[] transform = [12, 0, 0, 31, 0, 0, 7];
        var matrix = new byte[24];
        int[] coefficients = [1, 2, 3, 4, 100, -200]; // xx, yx, xy, yy, dx, dy
        for (var i = 0; i < 6; i++) BinaryPrimitives.WriteInt32BigEndian(matrix.AsSpan(i * 4), coefficients[i] << 16);
        var face = Face([.. transform, .. matrix, .. Fill()]);
        var layer = Assert.Single(Assert.IsType<ColorGlyphs.Layer[]>(ColorGlyphs.Get(face, Glyph)));
        Assert.Equal(new Vector2(111, -184), Vector2.Transform(new Vector2(2, 3), layer.TransformOrIdentity));
    }

    [Fact]
    public void ChildScaleIsAppliedBeforeParentTranslation()
    {
        byte[] translate = [14, 0, 0, 8, 0, 100, 0, 200];
        byte[] scale = [16, 0, 0, 8, 0x60, 0, 0x40, 0];
        var face = Face([.. translate, .. scale, .. Fill()]);
        var layer = Assert.Single(Assert.IsType<ColorGlyphs.Layer[]>(ColorGlyphs.Get(face, Glyph)));
        Assert.Equal(new Vector2(130, 230), Vector2.Transform(new Vector2(20, 30), layer.TransformOrIdentity));
    }

    [Theory]
    [InlineData(18)] [InlineData(19)] [InlineData(22)] [InlineData(23)]
    public void ScaleAroundCenterKeepsItsSpecifiedCenterFixed(byte format)
    {
        var paint = new byte[16]; paint[0] = format; paint[3] = 16;
        BinaryPrimitives.WriteInt16BigEndian(paint.AsSpan(4), 0x6000);
        var centerOffset = format is 18 or 19 ? 8 : 6;
        if (centerOffset == 8) BinaryPrimitives.WriteInt16BigEndian(paint.AsSpan(6), 0x2000);
        BinaryPrimitives.WriteInt16BigEndian(paint.AsSpan(centerOffset), 123);
        BinaryPrimitives.WriteInt16BigEndian(paint.AsSpan(centerOffset + 2), -45);
        var face = Face([.. paint, .. Fill()]);
        var layer = Assert.Single(Assert.IsType<ColorGlyphs.Layer[]>(ColorGlyphs.Get(face, Glyph)));
        Assert.Equal(new Vector2(123, -45), Vector2.Transform(new Vector2(123, -45), layer.TransformOrIdentity));
    }

    [Fact]
    public void FirstPaletteMayStartAtANonzeroColourRecord()
    {
        // One palette entry, two physical records; palette 0 starts at record 1 (blue).
        byte[] cpal = [0,0, 0,1, 0,1, 0,2, 0,0,0,14, 0,1, 0,0,255,255, 255,0,0,255];
        var face = Face(Fill(), cpal);
        Assert.Equal(new Rgba(0, 0, 1, 1), Assert.Single(Assert.IsType<ColorGlyphs.Layer[]>(ColorGlyphs.Get(face, Glyph))).Color);
    }

    [Fact]
    public void ForegroundAlphaIsMultipliedByTheTextAlpha()
    {
        var face = Face(Fill(0xffff, 0x2000));
        using var surface = new SoftwareRasterSurface(96, 96);
        surface.Canvas.DrawGlyphs(face, 64, [Glyph], [new Vector2(12, 76)], new Paint(new Rgba(0, 0, 1, .5f)));
        var maximum = surface.PremultipliedPixels.ToArray().Where((_, index) => index % 4 == 3).Max();
        Assert.InRange(maximum, (byte)63, (byte)65);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void GlyphsWithoutColourRecordsStillRenderTheirOutline(bool bitmap)
    {
        var face = bitmap ? FontFace.Parse(SoftwareRasterTests.AddTables(Source, ("sbix",
            SoftwareRasterTests.SyntheticSbix(FontFace.Parse(Source)!.GlyphCount, Glyph,
                File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "images", "gray-1.png"))))))! : Face(Fill());
        using var surface = new SoftwareRasterSurface(128, 96);
        surface.Canvas.DrawGlyphs(face, 48, [Glyph, face.GlyphFor('B')],
            [new Vector2(8, 70), new Vector2(70, 70)], new Paint(new Rgba(0, 0, 1, 1)));
        Assert.Contains(surface.PremultipliedPixels.ToArray().Chunk(4).Select((pixel, index) => (pixel, index)),
            sample => sample.index % 128 >= 70 && sample.pixel[3] > 0);
    }
}

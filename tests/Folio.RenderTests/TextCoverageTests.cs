using Folio.Painting;
using Folio.Painting.Software;
using Folio.Typography;
using System.Numerics;

namespace Folio.RenderTests;

public class TextCoverageTests
{
    [Theory]
    [InlineData(64, 44)]
    [InlineData(128, 91)]
    [InlineData(191, 145)]
    public void BlackTextMatchesObservedOracleFractionalStemCoverage(byte raw, byte oracle)
    {
        Assert.InRange((int)TextCoverage.Correct(raw, 0), oracle - 2, oracle + 2);
    }

    [Fact]
    public void EveryCoverageTableIsMonotonicAndPreservesEmptyAndSolidInk()
    {
        for (var luminance = 0; luminance < 256; luminance++)
        {
            Assert.Equal((byte)0, TextCoverage.Correct(0, luminance));
            Assert.Equal((byte)255, TextCoverage.Correct(255, luminance));
            for (var value = 1; value < 256; value++)
                Assert.True(TextCoverage.Correct((byte)value, luminance) >= TextCoverage.Correct((byte)(value - 1), luminance));
        }
    }

    [Fact]
    public void ChangingPaintOpacityDoesNotAlterTheReusableMask()
    {
        var opaque = new Paint(new Rgba(.2f, .5f, .8f, 1));
        var transparent = new Paint(new Rgba(.2f, .5f, .8f, .1f));
        Assert.Equal(TextCoverage.Luminance(opaque), TextCoverage.Luminance(transparent));
    }

    [Fact]
    public void ReusingAGlyphWithAnotherColourDoesNotMutateOrReuseTheWrongMask()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        var reused = FontFace.Parse(bytes)!;
        var fresh = FontFace.Parse(bytes)!;
        using (var prime = new SoftwareRasterSurface(64, 64))
            prime.Canvas.DrawGlyphs(reused, 32, [reused.GlyphFor('A')], [new Vector2(8, 40)], new Paint(new Rgba(0, 0, 0, 1)));
        using var actual = new SoftwareRasterSurface(64, 64);
        using var expected = new SoftwareRasterSurface(64, 64);
        var paint = new Paint(new Rgba(.8f, .7f, .6f, .4f));
        actual.Canvas.DrawGlyphs(reused, 32, [reused.GlyphFor('A')], [new Vector2(8, 40)], paint);
        expected.Canvas.DrawGlyphs(fresh, 32, [fresh.GlyphFor('A')], [new Vector2(8, 40)], paint);
        Assert.Equal(expected.PremultipliedPixels, actual.PremultipliedPixels);
    }
}

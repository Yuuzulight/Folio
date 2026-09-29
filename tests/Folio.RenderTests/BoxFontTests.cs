using SkiaSharp;

namespace Folio.RenderTests;

/// <summary>Checks the generated test font with an independent font reader (the raster backend's).</summary>
public class BoxFontTests
{
    private static readonly string FontPath = Path.Combine(RepoPaths.Tests, "fonts", "FolioBox.ttf");

    [Fact]
    public void TheRasterBackendReadsTheBoxFont()
    {
        using var typeface = SKTypeface.FromFile(FontPath);

        Assert.NotNull(typeface);
        Assert.Equal("Folio Box", typeface.FamilyName);
        Assert.Equal(220, typeface.GlyphCount);
        Assert.Equal(1000, typeface.UnitsPerEm);
    }

    [Fact]
    public void EveryInkedGlyphIsAFullEmSquare()
    {
        using var typeface = SKTypeface.FromFile(FontPath);
        using var font = new SKFont(typeface, 100);

        var width = font.MeasureText("AB", out var bounds);

        Assert.Equal(200, width, 0.01);
        Assert.Equal(new SKRect(0, -80, 200, 20), bounds);
    }
}

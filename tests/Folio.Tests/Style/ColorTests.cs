using Folio.Css;

namespace Folio.Tests.Style;

public class ColorTests
{
    [Theory]
    [InlineData("lab(54.29 80.8 69.89)", 255, 0, 0)]
    [InlineData("lch(54.29 106.84 40.85)", 255, 0, 0)]
    [InlineData("oklab(0.628 0.2249 0.1258)", 255, 0, 0)]
    [InlineData("oklch(0.628 0.2577 29.23)", 255, 0, 0)]
    [InlineData("oklab(0.452 -0.0325 -0.3115)", 0, 0, 255)]
    [InlineData("lch(87.82 113.33 134.38)", 0, 255, 0)]
    [InlineData("lab(50% 0 0)", 119, 119, 119)]
    [InlineData("oklch(62.8% 0.2577 29.23deg)", 255, 0, 0)]
    public void ConvertsToSrgb(string css, int r, int g, int b)
    {
        var color = Parse(css);

        Assert.InRange(color.R * 255, r - 1.5, r + 1.5);
        Assert.InRange(color.G * 255, g - 1.5, g + 1.5);
        Assert.InRange(color.B * 255, b - 1.5, b + 1.5);
    }

    [Theory]
    [InlineData("oklch(0.9 0.4 150)")]
    [InlineData("lab(90 -120 100)")]
    [InlineData("oklch(0.5 0.5 300)")]
    public void OutOfGamutColorsAreMappedIntoSrgb(string css)
    {
        var color = Parse(css);

        Assert.All([color.R, color.G, color.B], c => Assert.InRange(c, 0f, 1f));
    }

    [Fact]
    public void GamutMappingKeepsTheHue()
    {
        // A very saturated green stays green.
        var color = Parse("oklch(0.9 0.4 145)");

        Assert.True(color.G > color.R && color.G > color.B);
    }

    [Theory]
    [InlineData("color-mix(in oklch, red, blue)")]
    [InlineData("color-mix(in lch longer hue, red, blue)")]
    [InlineData("color-mix(in hsl, red 10%, blue)")]
    [InlineData("color-mix(in xyz, white, black)")]
    public void MixesInEveryInterpolationSpace(string css)
    {
        var color = Parse(css);

        Assert.All([color.R, color.G, color.B, color.A], c => Assert.InRange(c, 0f, 1f));
    }

    [Fact]
    public void ShorterAndLongerHuesDiffer()
    {
        var shorter = Parse("color-mix(in hsl, hsl(10 100% 50%), hsl(350 100% 50%))");
        var longer = Parse("color-mix(in hsl longer hue, hsl(10 100% 50%), hsl(350 100% 50%))");

        Assert.True(shorter.R > 0.99 && shorter.G < 0.01); // hue 0: red
        Assert.True(longer.B > 0.99 || longer.G > 0.99);   // hue 180: cyan
    }

    private static CssColor Parse(string css)
    {
        var (source, values) = CssParser.ParseComponentValues(css);
        return new ValueReader(source, values).Color() ?? throw new InvalidOperationException($"Not a colour: {css}");
    }
}

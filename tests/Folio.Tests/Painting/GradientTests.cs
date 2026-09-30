using System.Numerics;
using Folio.Css;
using Folio.Painting;
using Folio.Style;

namespace Folio.Tests.Painting;

public class GradientTests
{
    [Fact]
    public void FixesUpStopPositions()
    {
        // Missing first and last positions are 0 and 100%; runs without positions are spread evenly.
        Assert.Equal([0f, 0.25f, 0.5f, 1f], Offsets("linear-gradient(to right, red, lime, blue 50%, white)"));
        // A position before an earlier one moves up to it; the gradient then ends at the last position.
        Assert.Equal([0f, 1f, 1f, 1f], Offsets("linear-gradient(to right, red, blue 20%, green, yellow 10%)"));
    }

    [Fact]
    public void TwoPositionsMakeTwoStops()
    {
        Assert.Equal([0f, 0.5f, 0.5f, 1f], Offsets("linear-gradient(to right, red 0 50%, blue 50% 100%)"));
    }

    [Fact]
    public void HintsMoveTheMidpointColour()
    {
        var stops = Gradient("linear-gradient(to right, red, 25%, blue)")!.Stops;

        // The colour halfway between red and blue falls at the hint.
        var atHint = stops.Single(s => Math.Abs(s.Offset - 0.25f) < 0.001f);
        Assert.Equal(0.5f, atHint.Color.R, 0.02f);
        Assert.Equal(0.5f, atHint.Color.B, 0.02f);
    }

    [Fact]
    public void InterpolatesInTheColourSpaceItNames()
    {
        // Legacy colours without "in" interpolate in sRGB: only the two stops.
        Assert.Equal(2, Gradient("linear-gradient(red, blue)")!.Stops.Count);
        // Other spaces, and modern colours without "in" (Oklab), become extra stops.
        Assert.True(Gradient("linear-gradient(in oklch longer hue, red, blue)")!.Stops.Count > 2);
        Assert.True(Gradient("linear-gradient(oklch(70% 0.2 30), oklch(70% 0.2 200))")!.Stops.Count > 2);
        // Longer hue goes the other way round: halfway from red to blue passes through green, not magenta.
        var middle = Gradient("linear-gradient(in oklch longer hue, red, blue)")!.Stops.MinBy(s => Math.Abs(s.Offset - 0.5f));
        Assert.True(middle.Color.G > middle.Color.R && middle.Color.G > middle.Color.B, middle.ToString());
    }

    [Fact]
    public void PlacesTheGradientLine()
    {
        var right = Gradient("linear-gradient(to right, red, blue)", 100, 50)!;
        Assert.Equal((new Vector2(0, 25), new Vector2(100, 25)), (right.Start, right.End));

        // A corner: the line is perpendicular to the other diagonal, and as long as the box's projection on it.
        var corner = Gradient("linear-gradient(to top right, red, blue)", 200, 100)!;
        var direction = Vector2.Normalize(corner.End - corner.Start);
        Assert.Equal(0, Vector2.Dot(direction, new Vector2(200, 100)), 0.001f);
        Assert.Equal(Vector2.Dot(new Vector2(200, -100), direction), (corner.End - corner.Start).Length(), 0.01f);
    }

    [Fact]
    public void SizesRadialAndConicGradients()
    {
        var circle = Gradient("radial-gradient(circle closest-side at 20px 30px, red, blue)", 100, 50)!;
        Assert.Equal(new Vector2(20, 30), circle.Center);
        Assert.Equal(new Vector2(20, 20), circle.Radii);

        var ellipse = Gradient("radial-gradient(red, blue)", 100, 50)!;
        Assert.Equal(50 * MathF.Sqrt(2), ellipse.Radii.X, 0.01f);
        Assert.Equal(25 * MathF.Sqrt(2), ellipse.Radii.Y, 0.01f);

        var conic = Gradient("conic-gradient(from 90deg, red 25%, blue 0.5turn)", 100, 50)!;
        Assert.Equal((180f, 270f), (conic.StartAngle, conic.EndAngle));
    }

    [Fact]
    public void RejectsMalformedGradients()
    {
        foreach (var bad in (string[])["linear-gradient(red)", "linear-gradient(to left right, red, blue)", "linear-gradient(red, 10%, 20%, blue)",
                     "radial-gradient(square, red, blue)", "conic-gradient(red 10px, blue)"])
            Assert.Null(Specified(bad));
    }

    private static float[] Offsets(string image) => [.. Gradient(image)!.Stops.Select(s => MathF.Round(s.Offset, 3))];

    private static Folio.Painting.Gradient? Gradient(string image, float width = 100, float height = 100)
    {
        var computed = Specified(image) is { } s ? GradientParsing.Compute(s, new ComputeContext(ComputedStyle.Initial, 16, 800, 600)) : null;
        return computed is null ? null : GradientGeometry.Build(computed, new RectF(0, 0, width, height), CssColor.Black);
    }

    private static GradientSpecified? Specified(string image)
    {
        var (source, values) = CssParser.ParseComponentValues(image);
        var reader = new ValueReader(source, values);
        return BackgroundParsing.Image(reader) is GradientImage g && reader.AtEnd ? g.Specified : null;
    }
}

using System.Globalization;
using System.Numerics;
using Folio.Svg;

namespace Folio.Tests.Svg;

/// <summary>SVG attribute grammars: lengths, transform lists and viewBox mapping (SVG 2 §4, §8; css-transforms-1 §SVG syntax).</summary>
public class SvgGeometryTests
{
    private static readonly Vector2 Viewport = new(200, 100);

    [Theory]
    [InlineData("12", 12)]
    [InlineData(" 12px ", 12)]
    [InlineData("1.5e1", 15)]
    [InlineData("2em", 20)]
    [InlineData("2ex", 10)]
    [InlineData("1in", 96)]
    [InlineData("2.54cm", 96)]
    [InlineData("72pt", 96)]
    [InlineData("1pc", 16)]
    [InlineData("50%", 100)]
    [InlineData("-3", -3)]
    [InlineData("12 px", 7)]
    [InlineData("px", 7)]
    [InlineData("12furlongs", 7)]
    [InlineData(null, 7)]
    public void LengthsTakeUnitsAndPercentagesOfTheViewport(string? text, float expected) =>
        Assert.Equal(expected, SvgGeometry.Length(text, SvgAxis.Horizontal, Viewport, fontSize: 10, fallback: 7), 3);

    [Fact]
    public void PercentagesReferToTheirAxisOrTheNormalisedDiagonal()
    {
        Assert.Equal(50, SvgGeometry.Length("50%", SvgAxis.Vertical, Viewport, 10));
        Assert.Equal(MathF.Sqrt((200 * 200 + 100 * 100) / 2f) / 10, SvgGeometry.Length("10%", SvgAxis.Other, Viewport, 10), 3);
    }

    [Theory]
    [InlineData("translate(10)", "1,0,0,1,10,0")]
    [InlineData("translate(10, 20)", "1,0,0,1,10,20")]
    [InlineData("scale(2)", "2,0,0,2,0,0")]
    [InlineData("scale(2 3)", "2,0,0,3,0,0")]
    [InlineData("rotate(90)", "0,1,-1,0,0,0")]
    [InlineData("rotate(90 10 10)", "0,1,-1,0,20,0")]
    [InlineData("skewX(45)", "1,0,1,1,0,0")]
    [InlineData("skewY(45)", "1,1,0,1,0,0")]
    [InlineData("matrix(1 2 3 4 5 6)", "1,2,3,4,5,6")]
    [InlineData("translate(10,20) scale(2)", "2,0,0,2,10,20")]
    [InlineData("scale(2),translate(10 20)", "2,0,0,2,20,40")]
    [InlineData("  translate ( 1 )  ", "1,0,0,1,1,0")]
    [InlineData("", "1,0,0,1,0,0")]
    public void TransformListsApplyRightToLeft(string text, string expected) => Assert.Equal(expected, Matrix(SvgGeometry.ParseTransform(text)));

    [Theory]
    [InlineData("translate(1) foo(2)")]
    [InlineData("translate(1 2 3)")]
    [InlineData("rotate(1 2)")]
    [InlineData("scale(1")]
    [InlineData("translate(1),,scale(2)")]
    [InlineData("Translate(1)")]
    public void InvalidTransformListsAreIgnored(string text) => Assert.Null(SvgGeometry.ParseTransform(text));

    [Theory]
    [InlineData("0 0 10 20", "0,0,10,20")]
    [InlineData("-5,5, 10 ,20", "-5,5,10,20")]
    [InlineData("0 0 0 20", "0,0,0,20")]
    [InlineData("0 0 -1 20", null)]
    [InlineData("0 0 10", null)]
    [InlineData("0 0 10 20 30", null)]
    public void ViewBoxes(string text, string? expected)
    {
        var box = SvgGeometry.ParseViewBox(text);

        Assert.Equal(expected, box is { } b ? string.Create(CultureInfo.InvariantCulture, $"{b.X},{b.Y},{b.Width},{b.Height}") : null);
    }

    [Theory]
    [InlineData(null, "5,0,0,5,25,0")]
    [InlineData("xMinYMin", "5,0,0,5,0,0")]
    [InlineData("xMaxYMax meet", "5,0,0,5,50,0")]
    [InlineData("xMidYMin slice", "10,0,0,10,0,0")]
    [InlineData("xMidYMax slice", "10,0,0,10,0,-50")]
    [InlineData("none", "10,0,0,5,0,0")]
    [InlineData("xMidYMid bogus", "5,0,0,5,25,0")]
    public void ViewBoxesMapIntoTheViewport(string? preserveAspectRatio, string expected) =>
        Assert.Equal(expected, Matrix(SvgGeometry.ViewBoxTransform(new SvgRect(0, 0, 10, 10), preserveAspectRatio, new SvgRect(0, 0, 100, 50))));

    [Fact]
    public void ViewBoxOriginsAndViewportOffsetsMove()
    {
        Assert.Equal("2,0,0,2,-10,30", Matrix(SvgGeometry.ViewBoxTransform(new SvgRect(10, -10, 50, 50), "xMinYMin", new SvgRect(10, 10, 100, 100))));
    }

    [Theory]
    [InlineData("1,2 3,4", false, "M1,2 L3,4")]
    [InlineData("1 2 3 4 5", true, "M1,2 L3,4 Z")]
    [InlineData("1-2-3-4", false, "M1,-2 L-3,-4")]
    [InlineData("1", false, null)]
    public void Points(string text, bool closed, string? expected)
    {
        var segments = SvgGeometry.Polyline(text, closed);

        Assert.Equal(expected, segments is null ? null : string.Join(" ", segments.Select(s => s.Verb == 'Z' ? "Z" : string.Create(CultureInfo.InvariantCulture, $"{s.Verb}{s.P1.X},{s.P1.Y}"))));
    }

    [Fact]
    public void BoundsFollowCurvesToWhereTheyTurn()
    {
        var box = SvgGeometry.Bounds(Folio.Css.PathDataParser.Parse("M0 0 C 10 20 20 -20 30 0 Z M40 5 L40 5")!)!.Value;

        Assert.Equal((0f, -5.774f, 40f, 11.547f), (box.X, MathF.Round(box.Y, 3), box.Width, MathF.Round(box.Height, 3)));
        Assert.Null(SvgGeometry.Bounds([]));
    }

    [Theory]
    [InlineData("M0 0 L10 0 L10 10", "0,0 0 | 10,0 45 | 10,10 90")]
    [InlineData("M0 0 L10 0 L10 10 Z", "0,0 -67.5 | 10,0 45 | 10,10 157.5 | 0,0 -67.5")]
    [InlineData("M0 0 C0 10 10 10 10 0", "0,0 90 | 10,0 -90")]
    [InlineData("M0 0 C0 0 10 10 10 0 L10 0 L20 0", "0,0 45 | 10,0 -90 | 10,0 0 | 20,0 0")]
    [InlineData("M0 0 L10 0 M20 0 L20 10", "0,0 0 | 10,0 0 | 20,0 90 | 20,10 90")]
    public void MarkerVerticesAndAngles(string data, string expected)
    {
        var vertices = SvgGeometry.Vertices(Folio.Css.PathDataParser.Parse(data)!);

        Assert.Equal(expected, string.Join(" | ", vertices.Select(v =>
            string.Create(CultureInfo.InvariantCulture, $"{v.Point.X},{v.Point.Y} {Math.Round(SvgGeometry.MarkerAngle(v.In, v.Out), 2) + 0}"))));
    }

    [Theory]
    [InlineData("45", 45f)]
    [InlineData(" -30deg ", -30f)]
    [InlineData("0.5turn", 180f)]
    [InlineData("100grad", 90f)]
    [InlineData("3.1415927rad", 180f)]
    [InlineData("auto", null)]
    [InlineData("10px", null)]
    public void Angles(string text, float? expected)
    {
        var angle = SvgGeometry.ParseAngle(text);

        Assert.Equal(expected, angle is { } a ? MathF.Round(a, 3) : null);
    }

    private static string? Matrix(Matrix3x2? matrix) => matrix is { } m
        ? string.Join(",", new[] { m.M11, m.M12, m.M21, m.M22, m.M31, m.M32 }.Select(v => (Math.Round(v, 3) + 0).ToString(CultureInfo.InvariantCulture)))
        : null;
}

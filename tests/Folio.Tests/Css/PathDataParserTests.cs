using System.Globalization;
using System.Numerics;
using Folio.Css;

namespace Folio.Tests.Css;

/// <summary>SVG path data (https://www.w3.org/TR/SVG2/paths.html#PathData) normalised to absolute M, L, C and Z.</summary>
public class PathDataParserTests
{
    private static string Parse(string data) => PathDataParser.Parse(data) is { } segments
        ? string.Join(" ", segments.Select(s => s.Verb switch
        {
            'C' => $"C{P(s.P1)} {P(s.P2)} {P(s.P3)}",
            'Z' => "Z",
            _ => $"{s.Verb}{P(s.P1)}",
        }))
        : "invalid";

    private static string P(Vector2 p) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(p.X, 2) + 0},{Math.Round(p.Y, 2) + 0}");

    [Theory]
    [InlineData("M 10 20 L 30 40 Z", "M10,20 L30,40 Z")]
    [InlineData("m10 20 l5 5 h-5 v10 z m1 1 l1 1", "M10,20 L15,25 L10,25 L10,35 Z M11,21 L12,22")]
    [InlineData("M0 0 H10 V20", "M0,0 L10,0 L10,20")]
    [InlineData("M0,0 10,0 10,10", "M0,0 L10,0 L10,10")]
    [InlineData("m1 1 2 2", "M1,1 L3,3")]
    [InlineData("M-1.5.5L1e1-2", "M-1.5,0.5 L10,-2")]
    [InlineData("M0 0 C 1 2 3 4 5 6 S 9 10 11 12", "M0,0 C1,2 3,4 5,6 C7,8 9,10 11,12")]
    [InlineData("M0 0 S 3 4 5 6", "M0,0 C0,0 3,4 5,6")]
    [InlineData("M0 0 Q 3 0 3 3 T 3 9", "M0,0 C2,0 3,1 3,3 C3,5 3,7 3,9")]
    [InlineData("M0 0 c1 2 3 4 5 6", "M0,0 C1,2 3,4 5,6")]
    public void NormalisesCommands(string data, string expected) => Assert.Equal(expected, Parse(data));

    [Fact]
    public void ArcsBecomeQuarterTurnCubics()
    {
        // A half circle of radius 10 from (0, 0) to (20, 0), swept clockwise on screen: two quarter turns over (10, -10).
        Assert.Equal("M0,0 C0,-5.52 4.48,-10 10,-10 C15.52,-10 20,-5.52 20,0", Parse("M0 0 A10 10 0 0 1 20 0"));
        // The other way round, and with the flags written without separators.
        Assert.Equal("M0,0 C0,5.52 4.48,10 10,10 C15.52,10 20,5.52 20,0", Parse("M0 0a10,10 0 0020,0"));
    }

    [Fact]
    public void ArcsWithRadiiTooSmallAreScaledUpAndZeroRadiiAreLines()
    {
        Assert.Equal(Parse("M0 0 A10 10 0 0 1 20 0"), Parse("M0 0 A1 1 0 0 1 20 0"));
        Assert.Equal("M0,0 L20,0", Parse("M0 0 A0 5 0 0 1 20 0"));
        Assert.Equal("M0,0", Parse("M0 0 A5 5 0 0 1 0 0"));
    }

    [Fact]
    public void LargeArcsTakeTheLongWayRound()
    {
        // Three quarter turns of a circle of radius 10 centred at (10, 0), from (0, 0) to (10, 10).
        var segments = PathDataParser.Parse("M0 0 A10 10 0 1 1 10 10")!;
        Assert.Equal(3, segments.Count(s => s.Verb == 'C'));
        Assert.Equal(new Vector2(10, 10), segments[^1].P3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("L 0 0")]
    [InlineData("M 0")]
    [InlineData("M 0 0 L 1")]
    [InlineData("M 0 0,")]
    [InlineData("M 0 0 X 1 1")]
    [InlineData("M 0 0 A 1 1 0 2 0 5 5")]
    [InlineData("M 0 0 Z 1 1")]
    public void RejectsErrors(string data) => Assert.Equal("invalid", Parse(data));
}

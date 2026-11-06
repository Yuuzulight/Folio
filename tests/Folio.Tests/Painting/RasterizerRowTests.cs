using System.Numerics;
using Folio.Painting;
using Folio.Painting.Software;

namespace Folio.Tests.Painting;

public class RasterizerRowTests
{
    [Theory]
    [InlineData(FillRule.NonZero, false)]
    [InlineData(FillRule.NonZero, true)]
    [InlineData(FillRule.EvenOdd, false)]
    [InlineData(FillRule.EvenOdd, true)]
    public void WholeShapeMatchesIndependentClippedRows(FillRule rule, bool snap)
    {
        var random = new Random(98231);
        for (var iteration = 0; iteration < 80; iteration++)
        {
            var x = (float)random.NextDouble() * 6 - 3;
            var y = (float)random.NextDouble() * 8 - 4;
            // Straight sides, sloping shoulders, an inner contour that starts/ends midway, and a disjoint island.
            // Fractional coordinates exercise the first/last antialiased rows and clip boundaries.
            Vector2[][] polygons = [
                [new(x, y + 5), new(x + 4, y), new(x + 28, y), new(x + 32, y + 5),
                 new(x + 32, y + 54), new(x + 25, y + 60), new(x + 5, y + 60), new(x, y + 54)],
                [new(x + 8, y + 12.3f), new(x + 8, y + 37.7f), new(x + 23, y + 37.7f), new(x + 23, y + 12.3f)],
                [new(40, y + 21.2f), new(47, y + 21.2f), new(47, y + 45.4f), new(40, y + 45.4f)],
            ];
            var clip = new IntRect(0, 0, 46, 58);
            var whole = Assert.IsType<Coverage>(Rasterizer.Fill(polygons, rule, clip, snap));
            for (var row = whole.Bounds.Top; row < whole.Bounds.Bottom; row++)
            {
                var independent = Rasterizer.Fill(polygons, rule, new IntRect(clip.Left, row, clip.Right, row + 1), snap);
                for (var column = whole.Bounds.Left; column < whole.Bounds.Right; column++)
                {
                    var expected = independent is not null && column >= independent.Bounds.Left && column < independent.Bounds.Right
                        ? independent.At(column, row) : (byte)0;
                    Assert.Equal(expected, whole.At(column, row));
                }
            }
        }
    }
}

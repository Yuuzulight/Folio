using System.Globalization;
using System.Numerics;
using Folio.Painting;
using Folio.Style;
using Folio.Tests.Layout;

namespace Folio.Tests.Painting;

public class DisplayListTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Painting", "DisplayList"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void Builds(string id)
    {
        var test = AllCases.Value[id];

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(test.Input));

        Assert.Equal(test.Expected, list.Items.Select(Dump).ToList());
    }

    [Fact]
    public void PushesAndPopsBalance()
    {
        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(
            "<div style='overflow: hidden; opacity: .5; height: 10px'><div style='overflow: hidden; height: 5px'>" +
            "<div style='background: red; height: 20px'></div></div></div><div style='position: fixed; background: blue; height: 1px'></div>"));

        var depth = 0;
        foreach (var item in list.Items)
        {
            depth += item.Kind is DisplayItemKind.PushClip or DisplayItemKind.PushOpacity ? 1 : item.Kind == DisplayItemKind.Pop ? -1 : 0;
            Assert.True(depth >= 0);
        }
        Assert.Equal(0, depth);
    }

    private static string Dump(DisplayItem item) => item.Kind switch
    {
        DisplayItemKind.Fill => $"fill {Shape(item.Shape)} {item.Color}",
        DisplayItemKind.Border => $"border {Shape(item.Shape)} {Sides(item.Border!)}",
        DisplayItemKind.PushClip => $"clip {Shape(item.Shape)}",
        DisplayItemKind.PushOpacity => $"opacity {N(item.Opacity)}",
        _ => "pop",
    };

    private static string Shape(RoundedRect shape)
    {
        var r = shape.Rect;
        var text = $"{N(r.X)},{N(r.Y)} {N(r.Width)}x{N(r.Height)}";
        var radii = shape.Radii;
        if (radii.IsZero)
            return text;
        Vector2[] corners = [radii.TopLeft, radii.TopRight, radii.BottomRight, radii.BottomLeft];
        static string Corner(Vector2 c) => c.X == c.Y ? N(c.X) : $"{N(c.X)},{N(c.Y)}";
        return corners.Distinct().Count() == 1 ? $"{text} radius {Corner(corners[0])}" : $"{text} radius {string.Join(" ", corners.Select(Corner))}";
    }

    private static string Sides(BorderGroup b)
    {
        string[] sides =
        [
            $"{N(b.TopWidth)} {Name(b.TopStyle)} {b.TopColor}", $"{N(b.RightWidth)} {Name(b.RightStyle)} {b.RightColor}",
            $"{N(b.BottomWidth)} {Name(b.BottomStyle)} {b.BottomColor}", $"{N(b.LeftWidth)} {Name(b.LeftStyle)} {b.LeftColor}",
        ];
        return sides.Distinct().Count() == 1 ? sides[0] : string.Join(" / ", sides);
    }

    private static string Name(BorderStyle style) => style.ToString().ToLowerInvariant();

    private static string N(float value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}

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
            "<div style='overflow: hidden; opacity: .5; rotate: 5deg; height: 10px'><div style='overflow: hidden; height: 5px'>" +
            "<div style='background: red; height: 20px'></div></div></div><div style='position: fixed; background: blue; height: 1px'></div>"));

        var depth = 0;
        foreach (var item in list.Items)
        {
            depth += item.Kind is DisplayItemKind.PushClip or DisplayItemKind.PushOpacity or DisplayItemKind.PushTransform ? 1 : item.Kind == DisplayItemKind.Pop ? -1 : 0;
            Assert.True(depth >= 0);
        }
        Assert.Equal(0, depth);
    }

    private static string Dump(DisplayItem item) => item.Kind switch
    {
        DisplayItemKind.Fill when item.Gradient is { } g => $"fill {Shape(item.Shape)} {g.Kind.ToString().ToLowerInvariant()} gradient, {g.Stops.Count} stops{(g.Repeat ? ", repeating" : "")}",
        DisplayItemKind.Fill => $"fill {Shape(item.Shape)} {item.Color}",
        DisplayItemKind.Border => $"border {Shape(item.Shape)} {Sides(item.Border!)}",
        DisplayItemKind.Glyphs => $"glyphs {string.Join(" ", item.Glyphs!.Origins.Select(o => $"{N(o.X)},{N(o.Y)}"))} {N(item.Glyphs.Size)}px {item.Color}{(item.Blur > 0 ? $" blur {N(item.Blur)}" : "")}",
        DisplayItemKind.BoxShadow => $"shadow {Shape(item.Shape)} {item.Color} blur {N(item.Blur)} {(item.Inset ? "inside" : "outside")} {Shape(item.Box)}",
        DisplayItemKind.Decoration => $"decoration {Shape(item.Shape)} {item.LineStyle.ToString().ToLowerInvariant()} {item.Color}{(item.Glyphs is null ? "" : " skip-ink")}",
        DisplayItemKind.Image => $"image {Shape(item.Shape)} {item.Image!.Width}x{item.Image.Height} {item.Sampling.ToString().ToLowerInvariant()}",
        DisplayItemKind.PushClip => $"clip {Shape(item.Shape)}",
        DisplayItemKind.PushOpacity => $"opacity {N(item.Opacity)}",
        DisplayItemKind.PushTransform when item.Transform is var m => $"transform {N(m.M11)},{N(m.M12)},{N(m.M21)},{N(m.M22)},{N(m.M31)},{N(m.M32)}",
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

    // Adding zero turns a rounded -0 into 0.
    private static string N(float value) => (Math.Round(value, 2) + 0).ToString(CultureInfo.InvariantCulture);
}

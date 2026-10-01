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

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(test.Input), new Folio.Imaging.ImageLoader(Folio.Resources.ResourceLoader.DataUrlsOnly, null));

        Assert.Equal(test.Expected, list.Items.Select(Dump).ToList());
    }

    [Fact]
    public void ItemsStaySmallAndKeepWhatOnlySomeUse()
    {
        // The common fields only: a 5,000-row table paints tens of thousands of items (#196).
        Assert.True(System.Runtime.CompilerServices.Unsafe.SizeOf<DisplayItem>() <= 128);

        var fill = new DisplayItem(DisplayItemKind.Fill, Color: Folio.Css.CssColor.Black);
        var moved = new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(3, 4));
        Assert.Equal(new Vector2(3, 4), moved.Transform.Translation);
        Assert.Equal(default, fill.Transform);
        Assert.Equal(1, fill.Opacity);
        Assert.Equal(moved, fill with { Kind = DisplayItemKind.PushTransform, Color = default, Transform = Matrix3x2.CreateTranslation(3, 4) });
        Assert.NotEqual(moved, moved with { Transform = Matrix3x2.Identity });
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
            depth += item.Kind is DisplayItemKind.PushClip or DisplayItemKind.PushLayer or DisplayItemKind.PushTransform ? 1 : item.Kind == DisplayItemKind.Pop ? -1 : 0;
            Assert.True(depth >= 0);
        }
        Assert.Equal(0, depth);
    }

    private static string Dump(DisplayItem item) => item.Kind switch
    {
        DisplayItemKind.Fill when item.Gradient is { } g => $"fill {Shape(item.Shape)} {g.Kind.ToString().ToLowerInvariant()} gradient, {g.Stops.Count} stops{(g.Spread == GradientSpread.Repeat ? ", repeating" : "")}{Blend(item.Blend)}",
        DisplayItemKind.Fill => $"fill {Shape(item.Shape)} {item.Color}{Blend(item.Blend)}",
        DisplayItemKind.Border => $"border {Shape(item.Shape)} {Sides(item.Border!)}",
        DisplayItemKind.Glyphs when item.Gradient is { } g => $"glyphs {string.Join(" ", item.Glyphs!.Origins.Select(o => $"{N(o.X)},{N(o.Y)}"))} {N(item.Glyphs.Size)}px {Gradient(g)}",
        DisplayItemKind.Glyphs => $"glyphs {string.Join(" ", item.Glyphs!.Origins.Select(o => $"{N(o.X)},{N(o.Y)}"))} {N(item.Glyphs.Size)}px {item.Color}{(item.Blur > 0 ? $" blur {N(item.Blur)}" : "")}",
        DisplayItemKind.BoxShadow => $"shadow {Shape(item.Shape)} {item.Color} blur {N(item.Blur)} {(item.Inset ? "inside" : "outside")} {Shape(item.Box)}",
        DisplayItemKind.Decoration => $"decoration {Shape(item.Shape)} {item.LineStyle.ToString().ToLowerInvariant()} {item.Color}{(item.Glyphs is null ? "" : " skip-ink")}",
        DisplayItemKind.Image => $"image {Shape(item.Shape)} {item.Image!.Width}x{item.Image.Height} {item.Sampling.ToString().ToLowerInvariant()}",
        DisplayItemKind.FillPath => $"fill path{(item.Rule == FillRule.EvenOdd ? " evenodd" : "")} {Path(item.Path!)} {(item.Gradient is { } g ? Gradient(g) : item.Color)}",
        DisplayItemKind.StrokePath when item.Stroke is { } s =>
            $"stroke path {Path(item.Path!)} {N(s.Width)}{(s.Cap == LineCap.Butt ? "" : $" {s.Cap.ToString().ToLowerInvariant()} cap")}"
            + $"{(s.Join == LineJoin.Miter ? s.MiterLimit == 4 ? "" : $" miter {N(s.MiterLimit)}" : $" {s.Join.ToString().ToLowerInvariant()} join")}"
            + $"{(s.Dashes is { } d ? $" dashes {string.Join(",", d.Select(N))}{(s.DashOffset == 0 ? "" : $" offset {N(s.DashOffset)}")}" : "")} {(item.Gradient is { } g ? Gradient(g) : item.Color)}",
        DisplayItemKind.PushClip when item.Path is { } path => $"clip path{(item.Rule == FillRule.EvenOdd ? " evenodd" : "")} {Path(path)}",
        DisplayItemKind.PushClip => $"clip {Shape(item.Shape)}",
        DisplayItemKind.PushLayer => Layer(item),
        DisplayItemKind.PushTransform when item.Projection is { } p =>
            $"project {N(p.M11)},{N(p.M12)},{N(p.M14)},{N(p.M21)},{N(p.M22)},{N(p.M24)},{N(p.M41)},{N(p.M42)},{N(p.M44)}",
        DisplayItemKind.PushTransform when item.Transform is var m => $"transform {N(m.M11)},{N(m.M12)},{N(m.M21)},{N(m.M22)},{N(m.M31)},{N(m.M32)}",
        _ => "pop",
    };

    // "linear <start> <end>" or "radial <centre> <radius>[ focus <point> <radius>]", the stops as "<offset> <colour>",
    // then the spread when not pad and the transform when there is one.
    private static string Gradient(Gradient g)
    {
        var geometry = g.Kind == GradientKind.Linear
            ? $"linear {N(g.Start.X)},{N(g.Start.Y)} {N(g.End.X)},{N(g.End.Y)}"
            : $"radial {N(g.Center.X)},{N(g.Center.Y)} {N(g.Radii.X)}{(g.Focus is { } f ? $" focus {N(f.X)},{N(f.Y)} {N(g.FocusRadius)}" : "")}";
        var stops = string.Join(", ", g.Stops.Select(s => $"{N(s.Offset)} {new Folio.Css.CssColor(s.Color.R, s.Color.G, s.Color.B, s.Color.A)}"));
        var spread = g.Spread == GradientSpread.Pad ? "" : $" {g.Spread.ToString().ToLowerInvariant()}";
        var transform = g.Transform is { } m ? $" transform {N(m.M11)},{N(m.M12)},{N(m.M21)},{N(m.M22)},{N(m.M31)},{N(m.M32)}" : "";
        return $"{geometry} [{stops}]{spread}{transform}";
    }

    private static string Layer(DisplayItem item)
    {
        var parts = new List<string>();
        if (item.Opacity < 1)
            parts.Add($"opacity {N(item.Opacity)}");
        if (item.Filters is { } filters)
            parts.Add($"filter {string.Join(" ", filters.Select(Filter))}");
        if (item.Backdrop is { } backdrop)
            parts.Add($"backdrop {Shape(item.Shape)} {string.Join(" ", backdrop.Select(Filter))}");
        if (item.Blend != Folio.Painting.BlendMode.Normal)
            parts.Add(Blend(item.Blend).Trim());
        return parts.Count > 0 ? string.Join(" ", parts) : "layer";
    }

    // " blend <mode>" in the CSS keyword's spelling, or nothing for normal.
    private static string Blend(Folio.Painting.BlendMode mode) => mode == Folio.Painting.BlendMode.Normal ? ""
        : " blend " + string.Concat(mode.ToString().Select((c, i) => char.IsUpper(c) && i > 0 ? $"-{char.ToLowerInvariant(c)}" : $"{char.ToLowerInvariant(c)}"));

    // A primitive, then its inputs when not the previous result, its subregion and linear light when set.
    private static string Filter(Filter f)
    {
        var (sx, sy) = f.Deviations is { } d ? (d.X, d.Y) : (f.StdDeviation, f.StdDeviation);
        var deviation = sx == sy ? N(sx) : $"{N(sx)},{N(sy)}";
        var color = $"{N(f.Color.R)},{N(f.Color.G)},{N(f.Color.B)},{N(f.Color.A)}";
        var text = f.Kind switch
        {
            FilterKind.Blur => $"blur({deviation})",
            FilterKind.DropShadow => $"shadow({N(f.Offset.X)},{N(f.Offset.Y)},{deviation},{color})",
            FilterKind.ColorMatrix => $"matrix({string.Join(",", f.Matrix!.Select(N))})",
            FilterKind.Offset => $"offset({N(f.Offset.X)},{N(f.Offset.Y)})",
            FilterKind.Flood => $"flood({color})",
            FilterKind.Composite => $"composite({f.Operator.ToString().ToLowerInvariant()}{(f.Coefficients is { } k ? "," + string.Join(",", k.Select(N)) : "")})",
            FilterKind.Merge => $"merge({string.Join(",", f.Inputs!.Select(Input))})",
            FilterKind.Blend => $"blend({Blend(f.Blend).Replace(" blend ", "")})",
            FilterKind.Morphology => $"{(f.Dilate ? "dilate" : "erode")}({N(f.Radius.X)},{N(f.Radius.Y)})",
            FilterKind.ComponentTransfer => $"transfer({string.Join(";", f.Transfer!.Select(t => $"{t.Kind.ToString().ToLowerInvariant()}{(t.Values is { Count: > 0 } v ? " " + string.Join(",", v.Select(N)) : "")}"))})",
            FilterKind.Turbulence => $"turbulence({N(f.Noise!.BaseFrequency.X)},{N(f.Noise.BaseFrequency.Y)},{f.Noise.Octaves},{N(f.Noise.Seed)}{(f.Noise.Fractal ? ",fractal" : "")}{(f.Noise.Stitch ? ",stitch" : "")})",
            _ => $"displace({N(f.Scale)},{f.XChannel},{f.YChannel})",
        };
        if (f.In.Source != FilterSource.Previous)
            text += $" in={Input(f.In)}";
        if (f.Kind is FilterKind.Composite or FilterKind.Blend or FilterKind.DisplacementMap)
            text += $" in2={Input(f.In2)}";
        if (f.Subregion is { } r)
            text += $" [{Shape(new RoundedRect(r, default))}]";
        return f.LinearRgb ? text + " linear" : text;

        static string Input(FilterInput input) => input.Source switch
        {
            FilterSource.SourceGraphic => "source",
            FilterSource.SourceAlpha => "alpha",
            FilterSource.Result => $"#{input.Index}",
            _ => "previous",
        };
    }

    private static string Path(PathData path) => string.Join(" ", path.Commands.Select(c => c.Verb switch
    {
        PathVerb.MoveTo => $"M{N(c.P1.X)},{N(c.P1.Y)}",
        PathVerb.LineTo => $"L{N(c.P1.X)},{N(c.P1.Y)}",
        PathVerb.CubicTo => $"C{N(c.P1.X)},{N(c.P1.Y)} {N(c.P2.X)},{N(c.P2.Y)} {N(c.P3.X)},{N(c.P3.Y)}",
        _ => "Z",
    }));

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

using Folio.Style;

namespace Folio.Css;

/// <summary>
/// A specified basic shape: its function name and arguments, the radii after <c>round</c>, the centre after
/// <c>at</c>, the fill rule, and for path() its data.
/// </summary>
internal sealed record ShapeFunctionValue(string Name, IReadOnlyList<CssValue> Arguments, RadiusValue[]? Round = null, PositionSpecified? At = null,
                                          bool EvenOdd = false, string? Data = null, IReadOnlyList<PathSegment>? Segments = null) : CssValue;

/// <summary>A specified <c>clip-path</c>: a shape, a reference box, both, or neither for none.</summary>
internal sealed record ClipPathValue(ShapeFunctionValue? Shape, GeometryBox? Box, string? Url = null) : CssValue;

/// <summary>
/// <c>clip-path</c> (https://drafts.csswg.org/css-masking-1/#the-clip-path) with the basic shapes of
/// https://drafts.csswg.org/css-shapes-1/#basic-shape-functions, or a url() reference to an SVG clipPath element.
/// </summary>
internal static class ShapeProperties
{
    public static Property Row { get; } = new Property<ClipPath>(PropertyId.ClipPath, "clip-path", false, "none", ParseClipPath,
        (v, ctx) => v is ClipPathValue { Shape: var shape, Box: var box, Url: var url }
            ? new ClipPath(shape is null ? null : Compute(shape, ctx), box, url)
            : Style.ClipPath.None,
        s => s.Effects.ClipPath, (b, v) => b.Effects = b.Effects with { ClipPath = v });

    private static readonly KeywordMap<GeometryBox> Boxes = new()
    {
        ["border-box"] = GeometryBox.BorderBox, ["padding-box"] = GeometryBox.PaddingBox, ["content-box"] = GeometryBox.ContentBox,
        ["margin-box"] = GeometryBox.MarginBox, ["fill-box"] = GeometryBox.FillBox, ["stroke-box"] = GeometryBox.StrokeBox,
        ["view-box"] = GeometryBox.ViewBox,
    };

    // <url> | none | <basic-shape> || <geometry-box>
    private static CssValue? ParseClipPath(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new ClipPathValue(null, null);
        if (r.Url() is { } url)
            return new ClipPathValue(null, null, url);
        ShapeFunctionValue? shape = null;
        GeometryBox? box = null;
        while (!r.AtEnd)
        {
            if (box is null && r.Keyword([.. Boxes.Keys]) is { } keyword)
                box = Boxes[keyword];
            else if (shape is null && Shape(r) is { } s)
                shape = s;
            else
                return null;
        }
        return shape is null && box is null ? null : new ClipPathValue(shape, box);
    }

    private static readonly string[] Names = ["inset", "rect", "xywh", "circle", "ellipse", "polygon", "path"];

    private static ShapeFunctionValue? Shape(ValueReader r)
    {
        var mark = r.Mark;
        foreach (var name in Names)
        {
            if (r.Function(name) is not { } args)
                continue;
            var shape = name switch
            {
                "inset" => Inset(args),
                "rect" => Rect(args),
                "xywh" => Xywh(args),
                "circle" or "ellipse" => Ellipse(name, args),
                "polygon" => Polygon(args),
                _ => Path(args),
            };
            if (shape is not null && args.AtEnd)
                return shape;
            r.Reset(mark);
            return null;
        }
        return null;
    }

    // inset( <length-percentage>{1,4} [ round <'border-radius'> ]? )
    private static ShapeFunctionValue? Inset(ValueReader r)
    {
        var offsets = new List<CssValue>();
        while (offsets.Count < 4 && r.LengthPercentage() is { } offset)
            offsets.Add(offset);
        if (offsets.Count == 0)
            return null;
        // Missing sides copy the opposite one, as for margins.
        CssValue Side(int i) => i < offsets.Count ? offsets[i] : i == 3 ? offsets[offsets.Count > 1 ? 1 : 0] : offsets[0];
        return Round(r, out var radii) ? new ShapeFunctionValue("inset", [Side(0), Side(1), Side(2), Side(3)], radii) : null;
    }

    // rect( [ <length-percentage> | auto ]{4} [ round <'border-radius'> ]? )
    private static ShapeFunctionValue? Rect(ValueReader r)
    {
        var edges = new List<CssValue>();
        while (edges.Count < 4 && (r.Keyword("auto") is not null ? new KeywordValue("auto") : r.LengthPercentage()) is { } edge)
            edges.Add(edge);
        return edges.Count == 4 && Round(r, out var radii) ? new ShapeFunctionValue("rect", edges, radii) : null;
    }

    // xywh( <length-percentage>{2} <length-percentage [0,∞]>{2} [ round <'border-radius'> ]? )
    private static ShapeFunctionValue? Xywh(ValueReader r)
    {
        var values = new List<CssValue>();
        while (values.Count < 4 && r.LengthPercentage(nonNegative: values.Count >= 2) is { } value)
            values.Add(value);
        return values.Count == 4 && Round(r, out var radii) ? new ShapeFunctionValue("xywh", values, radii) : null;
    }

    private static bool Round(ValueReader r, out RadiusValue[]? radii)
    {
        radii = null;
        if (r.Keyword("round") is null)
            return true;
        radii = Properties.BorderRadii(r);
        return radii is not null;
    }

    // circle( <shape-radius>? [ at <position> ]? ) and ellipse( [ <shape-radius>{2} ]? [ at <position> ]? )
    private static ShapeFunctionValue? Ellipse(string name, ValueReader r)
    {
        var radii = new List<CssValue>();
        while (radii.Count < (name == "circle" ? 1 : 2)
               && (r.Keyword("closest-side", "farthest-side") is { } side ? new KeywordValue(side) : r.LengthPercentage(nonNegative: true)) is { } radius)
            radii.Add(radius);
        if (name == "ellipse" && radii.Count == 1)
            return null;
        PositionSpecified? at = null;
        if (r.Keyword("at") is not null && (at = BackgroundParsing.Position(r)) is null)
            return null;
        return new ShapeFunctionValue(name, radii, At: at);
    }

    // polygon( <'fill-rule'>? , [ <length-percentage> <length-percentage> ]# )
    private static ShapeFunctionValue? Polygon(ValueReader r)
    {
        var evenOdd = FillRule(r);
        if (evenOdd is null)
            return null;
        var points = new List<CssValue>();
        do
        {
            if (r.LengthPercentage() is not { } x || r.LengthPercentage() is not { } y)
                return null;
            points.Add(x);
            points.Add(y);
        }
        while (r.Comma());
        return new ShapeFunctionValue("polygon", points, EvenOdd: evenOdd.Value);
    }

    // path( <'fill-rule'>? , <string> )
    private static ShapeFunctionValue? Path(ValueReader r)
    {
        var evenOdd = FillRule(r);
        return evenOdd is not null && r.String() is { } data && PathDataParser.Parse(data) is { } segments
            ? new ShapeFunctionValue("path", [], EvenOdd: evenOdd.Value, Data: data, Segments: segments)
            : null;
    }

    // An optional fill rule and its comma: whether it is evenodd, or null when the comma is missing.
    private static bool? FillRule(ValueReader r)
    {
        if (r.Keyword("nonzero", "evenodd") is not { } rule)
            return false;
        return r.Comma() ? rule == "evenodd" : null;
    }

    private static BasicShape Compute(ShapeFunctionValue shape, ComputeContext ctx)
    {
        var args = shape.Arguments;
        LengthPercentage L(int i) => ctx.LengthPercentage(args[i]);
        var round = shape.Round?.Select(r => new CornerRadius(ctx.LengthPercentage(r.X, nonNegative: true), ctx.LengthPercentage(r.Y, nonNegative: true))).ToArray()
                    ?? [default, default, default, default];
        InsetShape Inset(LengthPercentage top, LengthPercentage right, LengthPercentage bottom, LengthPercentage left) =>
            new(top, right, bottom, left, round[0], round[1], round[2], round[3]);
        switch (shape.Name)
        {
            case "inset":
                return Inset(L(0), L(1), L(2), L(3));
            // rect() and xywh() compute to the equivalent inset() (https://drafts.csswg.org/css-shapes-1/#basic-shape-computed-values):
            // rect()'s edges are offsets from the top and left edges, auto being the box's own edge.
            case "rect":
                LengthPercentage Edge(int i, bool fromEnd) =>
                    args[i] is KeywordValue ? default : fromEnd ? Properties.FromEdge(L(i), true) : L(i);
                return Inset(Edge(0, false), Edge(1, true), Edge(2, true), Edge(3, false));
            case "xywh":
                return Inset(L(1), Properties.FromEdge(Sum(L(0), L(2)), true), Properties.FromEdge(Sum(L(1), L(3)), true), L(0));
            case "circle" or "ellipse":
                ShapeRadius Radius(int i) => i >= args.Count ? default
                    : args[i] is KeywordValue k ? new ShapeRadius(null, k.Keyword == "farthest-side") : new ShapeRadius(ctx.LengthPercentage(args[i], nonNegative: true));
                var center = shape.At is { } at ? Properties.ComputePosition(at, ctx) : new BackgroundPosition(new LengthPercentage(0, 50), new LengthPercentage(0, 50));
                return new EllipseShape(Radius(0), shape.Name == "circle" ? null : Radius(1), center);
            case "polygon":
                return new PolygonShape(shape.EvenOdd, [.. Enumerable.Range(0, args.Count / 2).Select(i => new BackgroundPosition(L(2 * i), L(2 * i + 1)))]);
            default:
                return new PathShape(shape.EvenOdd, shape.Data!, shape.Segments!);
        }
    }

    private static LengthPercentage Sum(LengthPercentage a, LengthPercentage b)
    {
        if (a.Calc is null && b.Calc is null)
            return new LengthPercentage(a.Px + b.Px, a.Percent + b.Percent);
        static CalcNode Node(LengthPercentage l) => l.Calc ?? new CalcSum(new CalcLength(new Length(l.Px, LengthUnit.Px)), new CalcPercent(l.Percent));
        return new LengthPercentage(0, 0, new CalcSum(Node(a), Node(b)));
    }
}

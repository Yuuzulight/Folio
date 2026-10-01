using System.Numerics;
using Folio.Style;

namespace Folio.Css;

/// <summary>An angle in degrees.</summary>
internal sealed record AngleValue(float Degrees) : CssValue;

/// <summary>A specified transform function: its lower-case name and arguments (lengths, percentages, numbers, angles).</summary>
internal sealed record TransformFunctionValue(string Name, IReadOnlyList<CssValue> Arguments) : CssValue;

/// <summary>A specified transform list, or an individual transform property's value; empty is <c>none</c>.</summary>
internal sealed record TransformListValue(IReadOnlyList<TransformFunctionValue> Functions) : CssValue;

/// <summary>A specified <c>transform-origin</c>: horizontal and vertical length-percentages and a z length.</summary>
internal sealed record TransformOriginValue(CssValue X, CssValue Y, CssValue Z) : CssValue;

/// <summary>
/// The transform properties (https://www.w3.org/TR/css-transforms-1/, https://www.w3.org/TR/css-transforms-2/):
/// grammars and computation for the property table.
/// </summary>
internal static class TransformProperties
{
    public static IEnumerable<Property> Rows =>
    [
        List(PropertyId.Transform, "transform", ParseTransform, s => s.Transform.Transform, (b, v) => b.Transform = b.Transform with { Transform = v }),
        List(PropertyId.Translate, "translate", ParseTranslate, s => s.Transform.Translate, (b, v) => b.Transform = b.Transform with { Translate = v }),
        List(PropertyId.Rotate, "rotate", ParseRotate, s => s.Transform.Rotate, (b, v) => b.Transform = b.Transform with { Rotate = v }),
        List(PropertyId.Scale, "scale", ParseScale, s => s.Transform.Scale, (b, v) => b.Transform = b.Transform with { Scale = v }),
        new Property<TransformOrigin>(PropertyId.TransformOrigin, "transform-origin", false, "50% 50%",
            ParseOrigin,
            (v, ctx) => v is TransformOriginValue o
                ? new TransformOrigin(ctx.LengthPercentage(o.X), ctx.LengthPercentage(o.Y), ctx.LengthPercentage(o.Z).Px)
                : TransformOrigin.Center,
            s => s.Transform.Origin, (b, v) => b.Transform = b.Transform with { Origin = v }),
        new Property<float?>(PropertyId.Perspective, "perspective", false, "none",
            r => r.Keyword("none") is not null ? new KeywordValue("none") : r.LengthPercentage(allowPercent: false, nonNegative: true),
            (v, ctx) => v is KeywordValue ? null : ctx.LengthPercentage(v).Px,
            s => s.Transform.Perspective, (b, v) => b.Transform = b.Transform with { Perspective = v }),
        new Property<TransformOrigin>(PropertyId.PerspectiveOrigin, "perspective-origin", false, "50% 50%",
            r => ParseOrigin(r) is TransformOriginValue { Z: LengthValue { Length.Value: 0 } } origin ? origin : null,
            (v, ctx) => v is TransformOriginValue o ? new TransformOrigin(ctx.LengthPercentage(o.X), ctx.LengthPercentage(o.Y), 0) : TransformOrigin.Center,
            s => s.Transform.PerspectiveOrigin, (b, v) => b.Transform = b.Transform with { PerspectiveOrigin = v }),
    ];

    private static Property<TransformList> List(PropertyId id, string name, Func<ValueReader, CssValue?> parse,
                                                Func<ComputedStyle, TransformList> get, Action<StyleBuilder, TransformList> set) =>
        new(id, name, false, "none", parse, Compute, get, set);

    private static TransformList Compute(CssValue value, ComputeContext context) =>
        value is TransformListValue { Functions.Count: > 0 } list ? new TransformList([.. list.Functions.Select(f => Compute(f, context))]) : TransformList.None;

    // ---------------------------------------------------------------- transform

    // none | <transform-function>+
    private static CssValue? ParseTransform(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new TransformListValue([]);
        var functions = new List<TransformFunctionValue>();
        while (!r.AtEnd && Function(r) is { } function)
            functions.Add(function);
        return functions.Count > 0 ? new TransformListValue(functions) : null;
    }

    private enum Arg { LengthPercentage, Length, Number, NumberPercentage, Angle, PerspectiveLength }

    // Each function's argument types, and how many may be given (the rest take defaults when computed).
    private static readonly Dictionary<string, (Arg[] Types, int Required)> Functions = new(StringComparer.Ordinal)
    {
        ["matrix"] = ([.. Enumerable.Repeat(Arg.Number, 6)], 6),
        ["matrix3d"] = ([.. Enumerable.Repeat(Arg.Number, 16)], 16),
        ["translate"] = ([Arg.LengthPercentage, Arg.LengthPercentage], 1),
        ["translatex"] = ([Arg.LengthPercentage], 1),
        ["translatey"] = ([Arg.LengthPercentage], 1),
        ["translatez"] = ([Arg.Length], 1),
        ["translate3d"] = ([Arg.LengthPercentage, Arg.LengthPercentage, Arg.Length], 3),
        ["scale"] = ([Arg.NumberPercentage, Arg.NumberPercentage], 1),
        ["scalex"] = ([Arg.NumberPercentage], 1),
        ["scaley"] = ([Arg.NumberPercentage], 1),
        ["scalez"] = ([Arg.NumberPercentage], 1),
        ["scale3d"] = ([Arg.NumberPercentage, Arg.NumberPercentage, Arg.NumberPercentage], 3),
        ["rotate"] = ([Arg.Angle], 1),
        ["rotatex"] = ([Arg.Angle], 1),
        ["rotatey"] = ([Arg.Angle], 1),
        ["rotatez"] = ([Arg.Angle], 1),
        ["rotate3d"] = ([Arg.Number, Arg.Number, Arg.Number, Arg.Angle], 4),
        ["skew"] = ([Arg.Angle, Arg.Angle], 1),
        ["skewx"] = ([Arg.Angle], 1),
        ["skewy"] = ([Arg.Angle], 1),
        ["perspective"] = ([Arg.PerspectiveLength], 1),
    };

    /// <summary>Reads one transform function; false (nothing read) when the next value is not one.</summary>
    public static bool OneFunction(ValueReader r) => Function(r) is not null;

    private static TransformFunctionValue? Function(ValueReader r)
    {
        var mark = r.Mark;
        foreach (var (name, (types, required)) in Functions)
        {
            if (r.Function(name) is not { } args)
                continue;
            var values = new List<CssValue>();
            foreach (var type in types)
            {
                if (values.Count > 0 && !args.Comma())
                    break;
                if (Argument(args, type) is not { } value)
                    return Fail(r, mark);
                values.Add(value);
            }
            return values.Count >= required && args.AtEnd ? new TransformFunctionValue(name, values) : Fail(r, mark);
        }
        return null;
    }

    private static TransformFunctionValue? Fail(ValueReader r, int mark)
    {
        r.Reset(mark);
        return null;
    }

    private static CssValue? Argument(ValueReader r, Arg type) => type switch
    {
        Arg.LengthPercentage => r.LengthPercentage(),
        Arg.Length => r.LengthPercentage(allowPercent: false),
        Arg.Number => r.Number() is { } n ? new NumberValue(n) : null,
        Arg.NumberPercentage => NumberPercentage(r),
        Arg.Angle => Angle(r, allowZero: true),
        _ => r.Keyword("none") is not null ? new KeywordValue("none") : r.LengthPercentage(allowPercent: false, nonNegative: true),
    };

    private static CssValue? NumberPercentage(ValueReader r) =>
        r.Number() is { } n ? new NumberValue(n) : r.LengthPercentage() is PercentageValue p ? new NumberValue(p.Percent / 100) : null;

    /// <summary>https://www.w3.org/TR/css-values-4/#angles; a unitless zero where transform functions allow it.</summary>
    public static AngleValue? Angle(ValueReader r, bool allowZero)
    {
        var mark = r.Mark;
        if (r.Dimension() is { } d)
        {
            float? degrees = d.Unit.ToLowerInvariant() switch
            {
                "deg" => d.Value,
                "grad" => d.Value * 0.9f,
                "rad" => d.Value * 180 / MathF.PI,
                "turn" => d.Value * 360,
                _ => null,
            };
            if (degrees is { } value)
                return new AngleValue(value);
        }
        else if (allowZero && r.Number() is 0)
        {
            return new AngleValue(0);
        }
        r.Reset(mark);
        return null;
    }

    private static TransformOp Compute(TransformFunctionValue function, ComputeContext context)
    {
        var args = function.Arguments;
        LengthPercentage L(int i) => i < args.Count ? context.LengthPercentage(args[i]) : default;
        float Px(int i) => L(i).Px;
        float Num(int i, float fallback) => i < args.Count && args[i] is NumberValue n ? n.Number : fallback;
        float Deg(int i) => i < args.Count && args[i] is AngleValue a ? a.Degrees : 0;
        return function.Name switch
        {
            "matrix" => new MatrixOp(new Matrix4x4(Num(0, 1), Num(1, 0), 0, 0, Num(2, 0), Num(3, 1), 0, 0, 0, 0, 1, 0, Num(4, 0), Num(5, 0), 0, 1)),
            "matrix3d" => new MatrixOp(new Matrix4x4(Num(0, 1), Num(1, 0), Num(2, 0), Num(3, 0), Num(4, 0), Num(5, 1), Num(6, 0), Num(7, 0),
                Num(8, 0), Num(9, 0), Num(10, 1), Num(11, 0), Num(12, 0), Num(13, 0), Num(14, 0), Num(15, 1))),
            "translate" => new TranslateOp(L(0), L(1), 0),
            "translatex" => new TranslateOp(L(0), default, 0),
            "translatey" => new TranslateOp(default, L(0), 0),
            "translatez" => new TranslateOp(default, default, Px(0)),
            "translate3d" => new TranslateOp(L(0), L(1), Px(2)),
            "scale" => new ScaleOp(Num(0, 1), Num(1, Num(0, 1)), 1),
            "scalex" => new ScaleOp(Num(0, 1), 1, 1),
            "scaley" => new ScaleOp(1, Num(0, 1), 1),
            "scalez" => new ScaleOp(1, 1, Num(0, 1)),
            "scale3d" => new ScaleOp(Num(0, 1), Num(1, 1), Num(2, 1)),
            "rotate" or "rotatez" => new RotateOp(0, 0, 1, Deg(0)),
            "rotatex" => new RotateOp(1, 0, 0, Deg(0)),
            "rotatey" => new RotateOp(0, 1, 0, Deg(0)),
            "rotate3d" => new RotateOp(Num(0, 0), Num(1, 0), Num(2, 0), Deg(3)),
            "skew" => new SkewOp(Deg(0), Deg(1)),
            "skewx" => new SkewOp(Deg(0), 0),
            "skewy" => new SkewOp(0, Deg(0)),
            _ => new PerspectiveOp(args[0] is KeywordValue ? null : Px(0)),
        };
    }

    // ---------------------------------------------------------------- individual transforms

    // none | <length-percentage> [<length-percentage> <length>?]?
    private static CssValue? ParseTranslate(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new TransformListValue([]);
        if (r.LengthPercentage() is not { } x)
            return null;
        List<CssValue> args = [x];
        if (r.LengthPercentage() is { } y)
        {
            args.Add(y);
            if (r.LengthPercentage(allowPercent: false) is { } z)
                args.Add(z);
        }
        return new TransformListValue([new TransformFunctionValue(args.Count == 3 ? "translate3d" : "translate", args)]);
    }

    // none | <angle> | [x | y | z | <number>{3}] && <angle>
    private static CssValue? ParseRotate(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new TransformListValue([]);
        var angle = Angle(r, allowZero: false);
        CssValue[]? axis = r.Keyword("x", "y", "z") switch
        {
            "x" => [new NumberValue(1), new NumberValue(0), new NumberValue(0)],
            "y" => [new NumberValue(0), new NumberValue(1), new NumberValue(0)],
            "z" => [new NumberValue(0), new NumberValue(0), new NumberValue(1)],
            _ => Axis(r),
        };
        angle ??= Angle(r, allowZero: false);
        if (angle is null)
            return null;
        return new TransformListValue([new TransformFunctionValue("rotate3d", [.. axis ?? [new NumberValue(0), new NumberValue(0), new NumberValue(1)], angle])]);
    }

    private static CssValue[]? Axis(ValueReader r)
    {
        var mark = r.Mark;
        if (r.Number() is { } a && r.Number() is { } b && r.Number() is { } c)
            return [new NumberValue(a), new NumberValue(b), new NumberValue(c)];
        r.Reset(mark);
        return null;
    }

    // none | [<number> | <percentage>]{1,3}
    private static CssValue? ParseScale(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new TransformListValue([]);
        var values = new List<CssValue>();
        while (values.Count < 3 && NumberPercentage(r) is { } value)
            values.Add(value);
        return values.Count switch
        {
            0 => null,
            1 => new TransformListValue([new TransformFunctionValue("scale", values)]),
            2 => new TransformListValue([new TransformFunctionValue("scale", values)]),
            _ => new TransformListValue([new TransformFunctionValue("scale3d", values)]),
        };
    }

    // ---------------------------------------------------------------- transform-origin

    // https://www.w3.org/TR/css-transforms-1/#transform-origin-property: one or two positions (keywords in either
    // order when both are keywords), then an optional z length.
    private static CssValue? ParseOrigin(ValueReader r)
    {
        var first = OriginPart(r);
        if (first is null)
            return null;
        var second = OriginPart(r);
        CssValue x, y;
        if (second is null)
        {
            (x, y) = first.Value.Keyword is "top" or "bottom" ? (Percent("center"), Percent(first.Value.Keyword)) : (first.Value.Value, Percent("center"));
        }
        else
        {
            var (a, b) = (first.Value, second.Value);
            // Two keywords may come in either order; a length-percentage fixes its own position.
            if (a.Keyword is "top" or "bottom" || b.Keyword is "left" or "right")
            {
                if (a.Keyword is null || b.Keyword is null || a.Keyword is "left" or "right" || b.Keyword is "top" or "bottom")
                    return null;
                (a, b) = (b, a);
            }
            (x, y) = (a.Value, b.Value);
        }
        var z = r.LengthPercentage(allowPercent: false) ?? new LengthValue(Length.Zero);
        return new TransformOriginValue(x, y, z);
    }

    private static (CssValue Value, string? Keyword)? OriginPart(ValueReader r) =>
        r.Keyword("left", "center", "right", "top", "bottom") is { } keyword ? (Percent(keyword), keyword)
        : r.LengthPercentage() is { } value ? (value, null)
        : null;

    private static PercentageValue Percent(string keyword) => new(keyword switch
    {
        "left" or "top" => 0,
        "right" or "bottom" => 100,
        _ => 50,
    });
}

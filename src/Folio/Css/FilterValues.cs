using Folio.Style;

namespace Folio.Css;

/// <summary>
/// A specified filter function: its lower-case name, its arguments and, for drop-shadow, its colour; for a url()
/// reference, "url" and the URL.
/// </summary>
internal sealed record FilterFunctionValue(string Name, IReadOnlyList<CssValue> Arguments, CssValue? Color = null, string? Url = null) : CssValue;

/// <summary>A specified filter list; empty is <c>none</c>.</summary>
internal sealed record FilterListValue(IReadOnlyList<FilterFunctionValue> Functions) : CssValue;

/// <summary>
/// <c>filter</c> (https://drafts.csswg.org/filter-effects-1/#FilterProperty) and <c>backdrop-filter</c>
/// (https://drafts.csswg.org/filter-effects-2/#BackdropFilterProperty): grammars and computation for the property table.
/// </summary>
internal static class FilterProperties
{
    public static IEnumerable<Property> Rows =>
    [
        Row(PropertyId.Filter, "filter", s => s.Effects.Filter, (b, v) => b.Effects = b.Effects with { Filter = v }),
        Row(PropertyId.BackdropFilter, "backdrop-filter", s => s.Effects.BackdropFilter, (b, v) => b.Effects = b.Effects with { BackdropFilter = v }),
    ];

    private static Property<FilterList> Row(PropertyId id, string name, Func<ComputedStyle, FilterList> get, Action<StyleBuilder, FilterList> set) =>
        new(id, name, false, "none", Parse, Compute, get, set);

    // none | [ <filter-function> | <url> ]+
    private static CssValue? Parse(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new FilterListValue([]);
        var functions = new List<FilterFunctionValue>();
        while (!r.AtEnd && Function(r) is { } function)
            functions.Add(function);
        return functions.Count > 0 ? new FilterListValue(functions) : null;
    }

    private static readonly string[] Names =
        ["blur", "brightness", "contrast", "drop-shadow", "grayscale", "hue-rotate", "invert", "opacity", "saturate", "sepia"];

    // Each argument is optional except drop-shadow's lengths; none may be negative, except a hue-rotate angle.
    private static FilterFunctionValue? Function(ValueReader r)
    {
        if (r.Url() is { } url)
            return new FilterFunctionValue("url", [], Url: url);
        var mark = r.Mark;
        foreach (var name in Names)
        {
            if (r.Function(name) is not { } args)
                continue;
            var function = name switch
            {
                "blur" => Optional(name, args, a => a.LengthPercentage(allowPercent: false, nonNegative: true)),
                "hue-rotate" => Optional(name, args, a => TransformProperties.Angle(a, allowZero: true)),
                "drop-shadow" => DropShadow(args),
                _ => Optional(name, args, Amount),
            };
            if (function is not null && args.AtEnd)
                return function;
            r.Reset(mark);
            return null;
        }
        return null;
    }

    private static FilterFunctionValue? Optional(string name, ValueReader args, Func<ValueReader, CssValue?> read) =>
        args.AtEnd ? new FilterFunctionValue(name, []) : read(args) is { } value ? new FilterFunctionValue(name, [value]) : null;

    // <number [0,∞]> | <percentage [0,∞]>, as a number.
    private static CssValue? Amount(ValueReader r) =>
        r.Number(nonNegative: true) is { } n ? new NumberValue(n)
        : r.LengthPercentage(nonNegative: true) is PercentageValue p ? new NumberValue(p.Percent / 100)
        : null;

    // [ <color>? && <length>{2,3} ], the third length a standard deviation.
    private static FilterFunctionValue? DropShadow(ValueReader r)
    {
        var color = r.ColorSpecified();
        var lengths = new List<CssValue>();
        while (lengths.Count < 3 && r.LengthPercentage(allowPercent: false, nonNegative: lengths.Count == 2) is { } length)
            lengths.Add(length);
        color ??= r.ColorSpecified();
        return lengths.Count >= 2 ? new FilterFunctionValue("drop-shadow", lengths, color) : null;
    }

    private static FilterList Compute(CssValue value, ComputeContext context) =>
        value is FilterListValue { Functions.Count: > 0 } list ? new FilterList([.. list.Functions.Select(f => Compute(f, context))]) : FilterList.None;

    // Omitted values take their defaults: 0 for lengths and angles, 1 for amounts, currentcolor for the shadow.
    private static FilterFunction Compute(FilterFunctionValue function, ComputeContext context)
    {
        var args = function.Arguments;
        float Px(int i) => i < args.Count ? context.LengthPercentage(args[i]).Px : 0;
        return function.Name switch
        {
            "url" => new FilterFunction("url", 0, Url: function.Url),
            "blur" => new FilterFunction("blur", Px(0)),
            "hue-rotate" => new FilterFunction("hue-rotate", args.Count > 0 ? ((AngleValue)args[0]).Degrees : 0),
            "drop-shadow" => new FilterFunction("drop-shadow", Px(2), Px(0), Px(1),
                function.Color is null ? CssColor.CurrentColor : context.Color(function.Color, context.CurrentColor)),
            _ => new FilterFunction(function.Name, args.Count > 0 ? ((NumberValue)args[0]).Number : 1),
        };
    }
}

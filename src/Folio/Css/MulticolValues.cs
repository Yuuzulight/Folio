using Folio.Style;

namespace Folio.Css;

/// <summary>
/// The multi-column properties (https://www.w3.org/TR/css-multicol-1/): <c>column-count</c>, <c>column-width</c>
/// and <c>columns</c>, the column rule longhands and <c>column-rule</c>, <c>column-span</c>, <c>column-fill</c>,
/// and <c>break-inside</c> (https://www.w3.org/TR/css-break-3/#break-within). <c>column-gap</c> is shared with flex
/// and grid.
/// </summary>
internal static class MulticolProperties
{
    public static Properties.LazyRow[] Rows =>
    [
        new(PropertyId.ColumnCount, "column-count", static () => new Property<int?>(PropertyId.ColumnCount, "column-count", false, "auto", Count,
            (v, _) => v is NumberValue n ? (int)n.Number : null,
            s => s.Multicol.Count, (b, v) => b.Multicol = b.Multicol with { Count = v })),
        new(PropertyId.ColumnWidth, "column-width", static () => new Property<float?>(PropertyId.ColumnWidth, "column-width", false, "auto", Width,
            (v, ctx) => v is KeywordValue ? null : ctx.LengthPercentage(v, nonNegative: true).Px,
            s => s.Multicol.Width, (b, v) => b.Multicol = b.Multicol with { Width = v })),
        new(PropertyId.ColumnRuleWidth, "column-rule-width", static () => Properties.BorderWidth(PropertyId.ColumnRuleWidth, "column-rule-width",
            s => s.Multicol.RuleWidthPx, (b, v) => b.Multicol = b.Multicol with { RuleWidthPx = v })),
        new(PropertyId.ColumnRuleStyle, "column-rule-style", static () => new Property<BorderStyle>(PropertyId.ColumnRuleStyle, "column-rule-style", false, "none",
            r => Properties.Get(PropertyId.BorderTopStyle).Parse(r),
            (v, _) => Enum.Parse<BorderStyle>(((KeywordValue)v).Keyword, ignoreCase: true),
            s => s.Multicol.RuleStyle, (b, v) => b.Multicol = b.Multicol with { RuleStyle = v })),
        new(PropertyId.ColumnRuleColor, "column-rule-color", static () => Properties.Color(PropertyId.ColumnRuleColor, "column-rule-color", "currentcolor",
            s => s.Multicol.RuleColor, (b, v) => b.Multicol = b.Multicol with { RuleColor = v })),
        new(PropertyId.ColumnSpan, "column-span", static () => new Property<bool>(PropertyId.ColumnSpan, "column-span", false, "none",
            r => r.Keyword("none", "all") is { } k ? new KeywordValue(k) : null,
            (v, _) => ((KeywordValue)v).Keyword == "all",
            s => s.Multicol.SpanAll, (b, v) => b.Multicol = b.Multicol with { SpanAll = v })),
        new(PropertyId.ColumnFill, "column-fill", static () => new Property<bool>(PropertyId.ColumnFill, "column-fill", false, "balance",
            r => r.Keyword("auto", "balance", "balance-all") is { } k ? new KeywordValue(k) : null,
            (v, _) => ((KeywordValue)v).Keyword == "auto",
            s => s.Multicol.FillAuto, (b, v) => b.Multicol = b.Multicol with { FillAuto = v })),
        new(PropertyId.BreakInside, "break-inside", static () => new Property<bool>(PropertyId.BreakInside, "break-inside", false, "auto",
            r => r.Keyword("auto", "avoid", "avoid-page", "avoid-column", "avoid-region") is { } k ? new KeywordValue(k) : null,
            (v, _) => ((KeywordValue)v).Keyword is "avoid" or "avoid-column",
            s => s.Multicol.AvoidBreakInside, (b, v) => b.Multicol = b.Multicol with { AvoidBreakInside = v })),
    ];

    public static IEnumerable<(string Name, Properties.Shorthand Shorthand)> Shorthands =>
    [
        ("columns", new Properties.Shorthand([PropertyId.ColumnWidth, PropertyId.ColumnCount], Columns)),
        ("column-rule", new Properties.Shorthand([PropertyId.ColumnRuleWidth, PropertyId.ColumnRuleStyle, PropertyId.ColumnRuleColor], Rule)),
        // The legacy names (https://www.w3.org/TR/css-break-3/#page-break-properties): avoid maps to avoid.
        ("page-break-inside", new Properties.Shorthand([PropertyId.BreakInside], BreakInsideAlias)),
        ("-webkit-column-break-inside", new Properties.Shorthand([PropertyId.BreakInside], BreakInsideAlias)),
    ];

    private static List<(PropertyId, CssValue)>? BreakInsideAlias(ValueReader r) =>
        r.Keyword("auto", "avoid") is { } k && r.AtEnd ? [(PropertyId.BreakInside, new KeywordValue(k))] : null;

    // <'column-rule-width'> || <'column-rule-style'> || <'column-rule-color'>
    private static List<(PropertyId, CssValue)>? Rule(ValueReader r)
    {
        CssValue? width = null, style = null, color = null;
        while (!r.AtEnd)
        {
            var one = r.OneValue();
            if (width is null && Properties.Get(PropertyId.ColumnRuleWidth).Parse(one.Copy()) is { } w)
                width = w;
            else if (style is null && Properties.Get(PropertyId.ColumnRuleStyle).Parse(one.Copy()) is { } s)
                style = s;
            else if (color is null && Properties.Get(PropertyId.ColumnRuleColor).Parse(one.Copy()) is { } c)
                color = c;
            else
                return null;
        }
        return width is null && style is null && color is null ? null
            :
            [
                (PropertyId.ColumnRuleWidth, width ?? Properties.Get(PropertyId.ColumnRuleWidth).Initial),
                (PropertyId.ColumnRuleStyle, style ?? Properties.Get(PropertyId.ColumnRuleStyle).Initial),
                (PropertyId.ColumnRuleColor, color ?? Properties.Get(PropertyId.ColumnRuleColor).Initial),
            ];
    }

    // auto | <integer [1,∞]>
    private static CssValue? Count(ValueReader r) =>
        r.Keyword("auto") is not null ? new KeywordValue("auto") : r.Integer() is { } n and >= 1 ? new NumberValue(n) : null;

    // auto | <length [0,∞]>
    private static CssValue? Width(ValueReader r) =>
        r.Keyword("auto") is not null ? new KeywordValue("auto") : r.LengthPercentage(allowPercent: false, nonNegative: true);

    // <'column-width'> || <'column-count'>, in either order; a lone auto sets both to auto.
    private static List<(PropertyId, CssValue)>? Columns(ValueReader r)
    {
        CssValue? width = null, count = null;
        var autos = 0;
        while (!r.AtEnd)
        {
            if (r.Keyword("auto") is not null)
                autos++;
            else if (count is null && r.Integer() is { } n and >= 1)
                count = new NumberValue(n);
            else if (width is null && r.LengthPercentage(allowPercent: false, nonNegative: true) is { } w)
                width = w;
            else
                return null;
        }
        if (autos + (width is null ? 0 : 1) + (count is null ? 0 : 1) is 0 or > 2)
            return null;
        return [(PropertyId.ColumnWidth, width ?? new KeywordValue("auto")), (PropertyId.ColumnCount, count ?? new KeywordValue("auto"))];
    }
}

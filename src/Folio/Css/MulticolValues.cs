using Folio.Style;

namespace Folio.Css;

/// <summary>
/// The multi-column properties (https://www.w3.org/TR/css-multicol-1/#the-number-and-width-of-columns):
/// <c>column-count</c>, <c>column-width</c> and the <c>columns</c> shorthand. <c>column-gap</c> is shared with flex
/// and grid.
/// </summary>
// ponytail: column-rule, column-span and column-fill parse as unsupported; they come with the rest of #102.
internal static class MulticolProperties
{
    public static IEnumerable<Property> Rows =>
    [
        new Property<int?>(PropertyId.ColumnCount, "column-count", false, "auto", Count,
            (v, _) => v is NumberValue n ? (int)n.Number : null,
            s => s.Multicol.Count, (b, v) => b.Multicol = b.Multicol with { Count = v }),
        new Property<float?>(PropertyId.ColumnWidth, "column-width", false, "auto", Width,
            (v, ctx) => v is KeywordValue ? null : ctx.LengthPercentage(v, nonNegative: true).Px,
            s => s.Multicol.Width, (b, v) => b.Multicol = b.Multicol with { Width = v }),
    ];

    public static IEnumerable<(string Name, Properties.Shorthand Shorthand)> Shorthands =>
    [
        ("columns", new Properties.Shorthand([PropertyId.ColumnWidth, PropertyId.ColumnCount], Columns)),
    ];

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

using Folio.Style;

namespace Folio.Css;

/// <summary>
/// The flow-relative properties (https://www.w3.org/TR/css-logical-1/): margin, padding, inset and border sides named
/// by block-start, block-end, inline-start and inline-end, the sizes named inline and block, and their shorthands.
/// They parse as their physical counterparts do and never compute themselves: the cascade maps each to the physical
/// property it stands for under the element's writing-mode and direction (<see cref="Physical"/>), where the later of a
/// logical and a physical declaration of one side wins (css-logical-1 §3, logical property groups).
/// </summary>
internal static class LogicalProperties
{
    private enum Side { BlockStart, BlockEnd, InlineStart, InlineEnd }

    // Each group of four in Side order, and the physical group it maps into (top, right, bottom, left).
    private static readonly (PropertyId First, PropertyId PhysicalTop, string Name, string Initial)[] Groups =
    [
        (PropertyId.MarginBlockStart, PropertyId.MarginTop, "margin-{0}", "0"),
        (PropertyId.PaddingBlockStart, PropertyId.PaddingTop, "padding-{0}", "0"),
        (PropertyId.InsetBlockStart, PropertyId.Top, "inset-{0}", "auto"),
        (PropertyId.BorderBlockStartWidth, PropertyId.BorderTopWidth, "border-{0}-width", "medium"),
        (PropertyId.BorderBlockStartStyle, PropertyId.BorderTopStyle, "border-{0}-style", "none"),
        (PropertyId.BorderBlockStartColor, PropertyId.BorderTopColor, "border-{0}-color", "currentcolor"),
    ];

    private static readonly string[] SideNames = ["block-start", "block-end", "inline-start", "inline-end"];

    // inline-size, block-size and their min and max: the physical width or height they stand for when horizontal.
    private static readonly (PropertyId Id, string Name, PropertyId Horizontal, PropertyId Vertical, string Initial)[] Sizes =
    [
        (PropertyId.InlineSize, "inline-size", PropertyId.Width, PropertyId.Height, "auto"),
        (PropertyId.BlockSize, "block-size", PropertyId.Height, PropertyId.Width, "auto"),
        (PropertyId.MinInlineSize, "min-inline-size", PropertyId.MinWidth, PropertyId.MinHeight, "auto"),
        (PropertyId.MinBlockSize, "min-block-size", PropertyId.MinHeight, PropertyId.MinWidth, "auto"),
        (PropertyId.MaxInlineSize, "max-inline-size", PropertyId.MaxWidth, PropertyId.MaxHeight, "none"),
        (PropertyId.MaxBlockSize, "max-block-size", PropertyId.MaxHeight, PropertyId.MaxWidth, "none"),
    ];

    public static bool IsLogical(PropertyId id) => id is >= PropertyId.MarginBlockStart and <= PropertyId.MaxBlockSize;

    public static IEnumerable<Property> Rows =>
        Groups.SelectMany(g => Enumerable.Range(0, 4).Select(s =>
                (Property)new Logical(g.First + s, string.Format(g.Name, SideNames[s]), g.Initial, g.PhysicalTop)))
            .Concat(Sizes.Select(s => (Property)new Logical(s.Id, s.Name, s.Initial, s.Horizontal)));

    public static IEnumerable<(string Name, Properties.Shorthand Shorthand)> Shorthands =>
    [
        .. Pairs("margin-block", PropertyId.MarginBlockStart), .. Pairs("margin-inline", PropertyId.MarginInlineStart),
        .. Pairs("padding-block", PropertyId.PaddingBlockStart), .. Pairs("padding-inline", PropertyId.PaddingInlineStart),
        .. Pairs("inset-block", PropertyId.InsetBlockStart), .. Pairs("inset-inline", PropertyId.InsetInlineStart),
        .. Pairs("border-block-width", PropertyId.BorderBlockStartWidth), .. Pairs("border-inline-width", PropertyId.BorderInlineStartWidth),
        .. Pairs("border-block-style", PropertyId.BorderBlockStartStyle), .. Pairs("border-inline-style", PropertyId.BorderInlineStartStyle),
        .. Pairs("border-block-color", PropertyId.BorderBlockStartColor), .. Pairs("border-inline-color", PropertyId.BorderInlineStartColor),
        .. new[] { Side.BlockStart, Side.BlockEnd, Side.InlineStart, Side.InlineEnd }.Select(s => ("border-" + SideNames[(int)s], Border(s))),
        ("border-block", Border(Side.BlockStart, Side.BlockEnd)),
        ("border-inline", Border(Side.InlineStart, Side.InlineEnd)),
    ];

    /// <summary>The physical property a flow-relative one stands for in a box with this writing mode and direction.</summary>
    public static PropertyId Physical(PropertyId id, WritingMode mode, Direction direction)
    {
        var vertical = mode != WritingMode.HorizontalTb;
        foreach (var s in Sizes)
        {
            if (s.Id == id)
                return vertical ? s.Vertical : s.Horizontal;
        }
        foreach (var g in Groups)
        {
            if (id < g.First || id > g.First + 3)
                continue;
            // Physical sides: 0 top, 1 right, 2 bottom, 3 left.
            var ltr = direction == Direction.Ltr;
            var side = (Side)(id - g.First) switch
            {
                Side.BlockStart => mode switch { WritingMode.VerticalRl => 1, WritingMode.VerticalLr => 3, _ => 0 },
                Side.BlockEnd => mode switch { WritingMode.VerticalRl => 3, WritingMode.VerticalLr => 1, _ => 2 },
                Side.InlineStart => vertical ? (ltr ? 0 : 2) : (ltr ? 3 : 1),
                _ => vertical ? (ltr ? 2 : 0) : (ltr ? 1 : 3),
            };
            return g.PhysicalTop + side;
        }
        return id;
    }

    // <x>-block and <x>-inline: the start value, then the end one (the start again when left out).
    private static IEnumerable<(string, Properties.Shorthand)> Pairs(string name, PropertyId start) =>
    [
        (name, new Properties.Shorthand([start, start + 1], r =>
        {
            var first = Properties.Get(start).Parse(r.OneValue());
            if (first is null)
                return null;
            var second = r.AtEnd ? first : Properties.Get(start).Parse(r.OneValue());
            return second is null ? null : [(start, first), (start + 1, second)];
        })),
    ];

    // border-<side> and border-block/-inline: <line-width> || <line-style> || <color>; omitted parts reset to initial.
    private static Properties.Shorthand Border(params Side[] sides)
    {
        var longhands = sides.SelectMany(s => new[] { PropertyId.BorderBlockStartWidth + (int)s, PropertyId.BorderBlockStartStyle + (int)s, PropertyId.BorderBlockStartColor + (int)s }).ToArray();
        return new(longhands, r =>
        {
            CssValue? width = null, style = null, color = null;
            while (!r.AtEnd)
            {
                var one = r.OneValue();
                if (width is null && Properties.Get(PropertyId.BorderTopWidth).Parse(one.Copy()) is { } w)
                    width = w;
                else if (style is null && Properties.Get(PropertyId.BorderTopStyle).Parse(one.Copy()) is { } s)
                    style = s;
                else if (color is null && Properties.Get(PropertyId.BorderTopColor).Parse(one.Copy()) is { } c)
                    color = c;
                else
                    return null;
            }
            if (width is null && style is null && color is null)
                return null;
            return longhands.Select((id, i) => (id, (i % 3) switch
            {
                0 => width ?? Properties.Get(id).Initial,
                1 => style ?? Properties.Get(id).Initial,
                _ => color ?? Properties.Get(id).Initial,
            })).ToList();
        });
    }

    // A flow-relative longhand: parsed with its physical counterpart's grammar, never computed itself.
    private sealed class Logical(PropertyId id, string name, string initial, PropertyId template) : Property(id, name, false, initial)
    {
        public override CssValue? Parse(ValueReader reader) => Properties.Get(template).Parse(reader);

        public override void Apply(StyleBuilder builder, CssValue value, ComputeContext context) { }

        public override void Inherit(StyleBuilder builder, ComputedStyle parent) { }

        public override string Describe(ComputedStyle style) => "";
    }
}

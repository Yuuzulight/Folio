using System.Runtime.CompilerServices;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Min-content and max-content inline sizes (https://www.w3.org/TR/css-sizing-3/#intrinsic-sizes), cached per box for
/// one layout (study 06: repeated measuring is what makes nested shrink-to-fit exponential).
/// </summary>
// ponytail: floats side by side contribute one at a time, not summed; percentages of the containing block count as 0
// (the cyclic-percentage rule); replaced boxes without natural dimensions contribute only their specified widths.
internal static class IntrinsicSizes
{
    /// <summary>A box's own min-content and max-content content-box widths.</summary>
    public static (float Min, float Max) Content(Box box, LayoutContext context)
    {
        if (context.Intrinsic.TryGetValue(box, out var cached))
            return cached;
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return (0, 0);

        (float Min, float Max) sizes = (0, 0);
        if (box is BlockContainerBox { Inline: { } inline } block)
        {
            sizes = InlineLayout.Measure(block, inline, context);
        }
        else if (box is TableWrapperBox table)
        {
            sizes = TableLayout.IntrinsicWidths(table, context);
        }
        else if (box is GridContainerBox grid)
        {
            sizes = GridLayout.IntrinsicWidths(grid, context);
        }
        else if (box is FlexContainerBox flex)
        {
            // css-flexbox-1 §9.9 (the simple rule, study 07): a single-line row adds its items up, gaps included;
            // multi-line rows and columns take their largest item.
            var style = flex.Style.Flex;
            var row = style.Direction is FlexDirection.Row or FlexDirection.RowReverse;
            var items = flex.Children.Where(c => !c.IsAbsolutelyPositioned).Select(c => Contribution(c, context)).ToList();
            var gaps = row && items.Count > 1 ? (style.ColumnGap.HasPercent ? 0 : style.ColumnGap.Px) * (items.Count - 1) : 0;
            if (items.Count > 0)
            {
                sizes = !row ? (items.Max(i => i.Min), items.Max(i => i.Max))
                    : style.Wrap == FlexWrap.Nowrap ? (items.Sum(i => i.Min) + gaps, items.Sum(i => i.Max) + gaps)
                    : (items.Max(i => i.Min), items.Sum(i => i.Max) + gaps);
            }
        }
        else if (box is ReplacedBox replaced && BlockLayout.ReplacedSize(replaced, 0, null, 0, 0) is { } natural)
        {
            // Percentages count as auto here, so the natural size (or the one the other size and the ratio give) counts.
            sizes = (natural.Width, natural.Width);
        }
        else if (box is BlockContainerBox)
        {
            foreach (var child in box.Children)
            {
                if (child.IsAbsolutelyPositioned)
                    continue;
                var c = Contribution(child, context);
                sizes = (Math.Max(sizes.Min, c.Min), Math.Max(sizes.Max, c.Max));
            }
        }
        context.Intrinsic[box] = sizes;
        return sizes;
    }

    /// <summary>A box's contribution to its parent's intrinsic sizes: its margin box at its min and max widths.</summary>
    public static (float Min, float Max) Contribution(Box box, LayoutContext context)
    {
        var style = box.Style;
        var spacing = style.Spacing;
        // A table wrapper's own sizes already include the table's border and padding.
        var frame = box is TableWrapperBox ? 0 : style.Border.LeftWidth + style.Border.RightWidth + Fixed(spacing.PaddingLeft) + Fixed(spacing.PaddingRight);
        var margins = FixedMargin(spacing.MarginLeft) + FixedMargin(spacing.MarginRight);
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;

        var (min, max) = Width(style.Size.Width) is { } w ? (w, w) : Content(box, context);
        if (style.Size.Width.Kind is SizeKind.MinContent)
            max = min;
        else if (style.Size.Width.Kind is SizeKind.MaxContent)
            min = max;
        if (Width(style.Size.MaxWidth) is { } upper)
            (min, max) = (Math.Min(min, upper), Math.Min(max, upper));
        if (Width(style.Size.MinWidth) is { } lower)
            (min, max) = (Math.Max(min, lower), Math.Max(max, lower));
        return (min + frame + margins, max + frame + margins);

        float? Width(SizeValue value) => value.Kind == SizeKind.Length && !value.Length.HasPercent
            ? Math.Max(0, borderBox ? value.Length.Px - frame : value.Length.Px)
            : null;
    }

    /// <summary>
    /// Shrink-to-fit (CSS 2.2 §10.3.5, css-sizing-3 fit-content): the content-box width that fits the available width
    /// without going below min-content or above max-content.
    /// </summary>
    public static float FitContent(Box box, float available, LayoutContext context)
    {
        var (min, max) = Content(box, context);
        return Math.Min(Math.Max(min, available), max);
    }

    /// <summary>The content-box width a sizing keyword asks for, or null for other values.</summary>
    public static float? Keyword(SizeValue value, Box box, float available, LayoutContext? context) => context is null ? null : value.Kind switch
    {
        SizeKind.MinContent => Content(box, context).Min,
        SizeKind.MaxContent => Content(box, context).Max,
        SizeKind.FitContent => FitContent(box, available, context),
        _ => null,
    };

    private static float Fixed(LengthPercentage value) => value.HasPercent ? 0 : Math.Max(0, value.Px);

    private static float FixedMargin(SizeValue value) => value.Kind == SizeKind.Length && !value.Length.HasPercent ? value.Length.Px : 0;
}

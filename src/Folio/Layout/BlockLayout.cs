using System.Runtime.CompilerServices;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Block-level boxes in normal flow (docs/study/06-layout-block-and-inline.md): widths and horizontal margins
/// (CSS 2.2 §10.3.3), heights (§10.6.3), min/max sizes (§10.4, §10.7), box-sizing, and margin collapsing (§8.3.1).
/// </summary>
internal static class BlockLayout
{
    // ponytail: until their own layout lands, inline formatting contexts have no height, and flex, grid, table and
    // replaced boxes are sized from their width and height properties only, with no content laid out.
    public static Fragment Layout(Box box, ConstraintSpace space)
    {
        var style = box.Style;
        var cbWidth = space.ContainingWidth;

        // Content that nests deeper than the stack allows is left out rather than overflowing it (study 16: limits
        // stop work gracefully).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return new Fragment(box, 0, 0, []);

        var border = style.Border;
        var padding = (
            Top: Resolve(style.Spacing.PaddingTop, cbWidth), Right: Resolve(style.Spacing.PaddingRight, cbWidth),
            Bottom: Resolve(style.Spacing.PaddingBottom, cbWidth), Left: Resolve(style.Spacing.PaddingLeft, cbWidth));
        var frameX = border.LeftWidth + padding.Left + padding.Right + border.RightWidth;
        var frameY = border.TopWidth + padding.Top + padding.Bottom + border.BottomWidth;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;

        var (width, marginLeft, marginRight) = SolveWidth(box, cbWidth, frameX, borderBox);
        var marginTop = Margin(style.Spacing.MarginTop, cbWidth);
        var marginBottom = Margin(style.Spacing.MarginBottom, cbWidth);

        var height = ContentSize(style.Size.Height, space.ContainingHeight, frameY, borderBox);
        var minHeight = ContentSize(style.Size.MinHeight, space.ContainingHeight, frameY, borderBox) ?? 0;
        var maxHeight = ContentSize(style.Size.MaxHeight, space.ContainingHeight, frameY, borderBox) ?? float.PositiveInfinity;

        var independent = EstablishesIndependentFormattingContext(box);
        var collapseTop = !independent && border.TopWidth == 0 && padding.Top == 0;
        var bottomEdgeOpen = !independent && border.BottomWidth == 0 && padding.Bottom == 0;
        var collapseBottom = bottomEdgeOpen && height is null;

        // Normal flow: place block-level children top to bottom, collapsing adjoining margins (§8.3.1).
        var children = new List<ChildFragment>();
        var childSpace = new ConstraintSpace(width, height is { } h ? Clamp(h, minHeight, maxHeight) : null);
        var pending = default(MarginStrut); // margins after the last placed content, not yet resolved
        var leading = default(MarginStrut); // margins adjoining this box's top edge
        var atTop = collapseTop;            // no content placed yet, so margins still adjoin the top edge
        var cursor = 0f;                    // bottom of the last placed content, from the content box top
        var hasContent = false;
        foreach (var child in FlowChildren(box))
        {
            var fragment = Layout(child, childSpace);
            var x = border.LeftWidth + padding.Left + fragment.MarginLeft;
            if (fragment.CollapsesThrough)
            {
                // An empty block sits where its top border edge would be; its margins join the pending ones.
                var y = atTop ? 0 : cursor + pending.Append(fragment.TopMargins).Resolve();
                children.Add(new(x, border.TopWidth + padding.Top + y, fragment));
                pending = pending.Append(fragment.TopMargins);
                continue;
            }
            var margins = pending.Append(fragment.TopMargins);
            float top;
            if (atTop)
            {
                (leading, top, atTop) = (margins, 0, false);
            }
            else
            {
                top = cursor + margins.Resolve();
            }
            children.Add(new(x, border.TopWidth + padding.Top + top, fragment));
            cursor = top + fragment.Height;
            pending = fragment.BottomMargins;
            hasContent = true;
        }
        if (box is BlockContainerBox { Inline: { } inline } && inline.Items.Any(i => i.Kind is not (InlineItemKind.Float or InlineItemKind.OutOfFlow)))
        {
            // Line boxes are content: margins before them no longer adjoin this box's edges.
            if (atTop)
                (leading, atTop) = (pending, false);
            else
                cursor += pending.Resolve();
            pending = default;
            hasContent = true;
        }

        // Height: auto is the flow's extent; min and max apply either way (§10.6.3, §10.7).
        var flowHeight = cursor;
        var contentHeight = Clamp(height ?? flowHeight, minHeight, maxHeight);
        if (collapseBottom && contentHeight != flowHeight)
            collapseBottom = false; // min-height or max-height moved the bottom edge away from the last child
        if (!collapseBottom && !atTop)
        {
            contentHeight = Clamp(height ?? flowHeight + pending.Resolve(), minHeight, maxHeight);
            pending = default;
        }

        var own = (Top: MarginStrut.Of(marginTop), Bottom: MarginStrut.Of(marginBottom));
        // §8.3.1: no content, no top or bottom border or padding, zero or auto height and zero min-height.
        var collapsesThrough = collapseTop && bottomEdgeOpen && (height ?? 0) == 0 && !hasContent && contentHeight == 0 && minHeight == 0;
        if (atTop && !collapsesThrough)
        {
            // Only empty children: their margins adjoin this box's top edge.
            (leading, pending) = (pending, default);
        }

        return new Fragment(box, width + frameX, contentHeight + frameY, children)
        {
            MarginLeft = marginLeft,
            MarginRight = marginRight,
            TopMargins = collapsesThrough ? own.Top.Append(pending).Append(own.Bottom) : own.Top.Append(leading),
            BottomMargins = collapsesThrough ? default : own.Bottom.Append(pending),
            CollapsesThrough = collapsesThrough,
        };
    }

    // In-flow block-level children; floats and positioned boxes are laid out by their own algorithms.
    private static IEnumerable<Box> FlowChildren(Box box) =>
        box is BlockContainerBox { Inline: null } ? box.Children.Where(c => !c.IsFloat && !c.IsAbsolutelyPositioned) : [];

    /// <summary>
    /// The width and horizontal margins of a block-level box in normal flow (§10.3.3), with max-width then min-width
    /// applied by solving again with the limit as the width (§10.4).
    /// </summary>
    private static (float Width, float MarginLeft, float MarginRight) SolveWidth(Box box, float cbWidth, float frameX, bool borderBox)
    {
        var style = box.Style;
        var width = ContentSize(style.Size.Width, cbWidth, frameX, borderBox);
        var result = Solve(width);
        if (ContentSize(style.Size.MaxWidth, cbWidth, frameX, borderBox) is { } max && result.Width > max)
            result = Solve(max);
        if (ContentSize(style.Size.MinWidth, cbWidth, frameX, borderBox) is { } min && result.Width < min)
            result = Solve(min);
        return result;

        (float Width, float MarginLeft, float MarginRight) Solve(float? specified)
        {
            var left = style.Spacing.MarginLeft;
            var right = style.Spacing.MarginRight;
            var ml = Margin(left, cbWidth);
            var mr = Margin(right, cbWidth);
            if (specified is not { } w)
            {
                // Auto margins are zero; the width fills the rest, and if that is negative the right margin gives way.
                var fill = Math.Max(0, cbWidth - ml - mr - frameX);
                return (fill, ml, cbWidth - ml - frameX - fill);
            }

            var remaining = cbWidth - w - frameX;
            return (left.Kind == SizeKind.Auto, right.Kind == SizeKind.Auto) switch
            {
                // Centred, unless the box is wider than its containing block (then the left margin is zero).
                (true, true) => (w, Math.Max(0, remaining / 2), remaining - Math.Max(0, remaining / 2)),
                (true, false) => (w, remaining - mr, mr),
                // Over-constrained or only the right margin auto: the right margin takes what is left (ltr).
                _ => (w, ml, remaining - ml),
            };
        }
    }

    /// <summary>
    /// A width or height as a content-box size: null when auto, none, a sizing keyword, or a percentage of an
    /// indefinite size.
    /// </summary>
    // ponytail: min-content, max-content and fit-content act as auto until intrinsic sizes arrive with inline layout.
    private static float? ContentSize(SizeValue value, float? basis, float frame, bool borderBox)
    {
        if (value.Kind != SizeKind.Length || value.Length.HasPercent && basis is null)
            return null;
        var size = Math.Max(0, value.Length.Resolve(basis ?? 0));
        return borderBox ? Math.Max(0, size - frame) : size;
    }

    // Margins and paddings resolve percentages against the containing block's width, vertical ones included.
    private static float Margin(SizeValue value, float cbWidth) => value.Kind == SizeKind.Length ? value.Length.Resolve(cbWidth) : 0;

    private static float Resolve(LengthPercentage value, float cbWidth) => Math.Max(0, value.Resolve(cbWidth));

    // max wins over the size, min wins over max (§10.4, §10.7).
    private static float Clamp(float size, float min, float max) => Math.Max(min, Math.Min(size, max));

    /// <summary>
    /// Whether the box's content is laid out independently of its surroundings, so its margins do not collapse with
    /// its children's (https://www.w3.org/TR/CSS22/visuren.html#block-formatting and css-display-3 §2.3).
    /// </summary>
    private static bool EstablishesIndependentFormattingContext(Box box)
    {
        var style = box.Style.Box;
        return box.Parent is null || box.IsFloat || box.IsAbsolutelyPositioned
            || box is not BlockContainerBox
            || box is BlockContainerBox { IsAtomicInline: true }
            || box is TablePartBox { Part: TablePart.Cell or TablePart.Caption }
            || box.Parent is FlexContainerBox or GridContainerBox
            || style.Display == Display.FlowRoot
            || style.OverflowX is Overflow.Hidden or Overflow.Scroll or Overflow.Auto
            || style.OverflowY is Overflow.Hidden or Overflow.Scroll or Overflow.Auto;
    }
}

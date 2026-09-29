using System.Runtime.CompilerServices;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Block-level boxes in normal flow (docs/study/06-layout-block-and-inline.md): widths and horizontal margins
/// (CSS 2.2 §10.3.3), heights (§10.6.3, §10.6.7), min/max sizes (§10.4, §10.7), box-sizing, margin collapsing
/// (§8.3.1), floats and clearance (§9.5).
/// </summary>
internal static class BlockLayout
{
    // ponytail: until their own layout lands, table and replaced boxes are sized from their width and height
    // properties only.
    public static Fragment Layout(Box box, ConstraintSpace space, LayoutContext context)
    {
        var style = box.Style;
        var cbWidth = space.ContainingWidth;

        // Content that nests deeper than the stack allows is left out rather than overflowing it (study 16: limits
        // stop work gracefully).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return new Fragment(box, 0, 0, []) { Exclusions = space.Exclusions };

        var border = style.Border;
        var padding = (
            Top: Resolve(style.Spacing.PaddingTop, cbWidth), Right: Resolve(style.Spacing.PaddingRight, cbWidth),
            Bottom: Resolve(style.Spacing.PaddingBottom, cbWidth), Left: Resolve(style.Spacing.PaddingLeft, cbWidth));
        var frameX = border.LeftWidth + padding.Left + padding.Right + border.RightWidth;
        var frameY = border.TopWidth + padding.Top + padding.Bottom + border.BottomWidth;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;

        var (width, marginLeft, marginRight) = space.FixedWidth is { } fixedWidth
            ? (Math.Max(0, fixedWidth - frameX), 0f, 0f)
            : SolveWidth(box, cbWidth, frameX, borderBox, context);
        var marginTop = Margin(style.Spacing.MarginTop, cbWidth);
        var marginBottom = Margin(style.Spacing.MarginBottom, cbWidth);

        var height = space.FixedHeight is { } fixedHeight
            ? Math.Max(0, fixedHeight - frameY)
            : ContentSize(style.Size.Height, space.ContainingHeight, frameY, borderBox);
        var minHeight = space.FixedHeight is null ? ContentSize(style.Size.MinHeight, space.ContainingHeight, frameY, borderBox) ?? 0 : 0;
        var maxHeight = space.FixedHeight is null ? ContentSize(style.Size.MaxHeight, space.ContainingHeight, frameY, borderBox) ?? float.PositiveInfinity : float.PositiveInfinity;
        var definiteHeight = height is { } h ? Clamp(h, minHeight, maxHeight) : (float?)null;

        var independent = EstablishesIndependentFormattingContext(box);
        var collapseTop = !independent && border.TopWidth == 0 && padding.Top == 0;
        var bottomEdgeOpen = !independent && border.BottomWidth == 0 && padding.Bottom == 0;
        var collapseBottom = bottomEdgeOpen && height is null;

        // Block formatting context coordinates: an independent box starts its own at its border box.
        var (boxX, boxY) = independent ? (0f, 0f) : (space.BfcLeft + marginLeft, space.BfcTop);
        var contentX = boxX + border.LeftWidth + padding.Left;
        var contentY = boxY + border.TopWidth + padding.Top;
        var exclusions = independent ? ExclusionSpace.Empty : space.Exclusions ?? ExclusionSpace.Empty;

        // Normal flow: place block-level children top to bottom, collapsing adjoining margins (§8.3.1).
        var children = new List<ChildFragment>();
        var pending = default(MarginStrut); // margins after the last placed content, not yet resolved
        var leading = default(MarginStrut); // margins adjoining this box's top edge
        var atTop = collapseTop;            // no content placed yet, so margins still adjoin the top edge
        var cursor = 0f;                    // bottom of the last placed content, from the content box top
        var hasContent = false;
        var outOfFlow = new List<OutOfFlowBox>();

        // Adds a child fragment, shifted by its relative offset, and carries up its positioned descendants.
        void Place(Box child, Fragment fragment, float x, float y)
        {
            var (dx, dy) = PositionedLayout.RelativeOffset(child, width, definiteHeight);
            children.Add(new(x + dx, y + dy, fragment));
            foreach (var o in fragment.OutOfFlow)
                outOfFlow.Add(o with { StaticX = o.StaticX + x + dx, StaticY = o.StaticY + y + dy });
        }

        void PlaceFloat(Box child, float top)
        {
            var spacing = child.Style.Spacing;
            var fragment = Layout(child, new ConstraintSpace(width, definiteHeight), context);
            var (ml, mr) = (Margin(spacing.MarginLeft, width), Margin(spacing.MarginRight, width));
            var (mt, mb) = (Margin(spacing.MarginTop, width), Margin(spacing.MarginBottom, width));
            var (outerWidth, outerHeight) = (ml + fragment.Width + mr, mt + fragment.Height + mb);
            var side = child.Style.Box.Float is FloatSide.Right or FloatSide.InlineEnd ? FloatSide.Right : FloatSide.Left;
            var minTop = Math.Max(contentY + top, exclusions.ClearEdge(child.Style.Box.Clear) ?? float.NegativeInfinity);
            var (fx, fy) = exclusions.PlaceFloat(side, outerWidth, outerHeight, minTop, contentX, contentX + width);
            exclusions = exclusions.Add(new FloatArea(side, fx, fy, fx + outerWidth, fy + outerHeight));
            Place(child, fragment, fx + ml - boxX, fy + mt - boxY);
        }

        if (box is FlexContainerBox flexBox)
        {
            var (items, flexHeight, flexOutOfFlow) = FlexLayout.Layout(flexBox, width, definiteHeight, minHeight, maxHeight, context);
            foreach (var item in items)
                Place(item.Fragment.Box!, item.Fragment, border.LeftWidth + padding.Left + item.X, border.TopWidth + padding.Top + item.Y);
            foreach (var child in flexOutOfFlow)
                outOfFlow.Add(new(child, border.LeftWidth + padding.Left, border.TopWidth + padding.Top));
            cursor = flexHeight;
            hasContent = items.Count > 0;
        }
        else if (box is GridContainerBox gridBox)
        {
            var (items, gridHeight, gridOutOfFlow) = GridLayout.Layout(gridBox, width, definiteHeight, minHeight, maxHeight, context);
            foreach (var item in items)
                Place(item.Fragment.Box!, item.Fragment, border.LeftWidth + padding.Left + item.X, border.TopWidth + padding.Top + item.Y);
            foreach (var child in gridOutOfFlow)
                outOfFlow.Add(new(child, border.LeftWidth + padding.Left, border.TopWidth + padding.Top));
            cursor = gridHeight;
            hasContent = items.Count > 0;
        }

        foreach (var child in box is BlockContainerBox { Inline: null } ? box.Children : [])
        {
            if (child.IsAbsolutelyPositioned)
            {
                outOfFlow.Add(new(child, border.LeftWidth + padding.Left, border.TopWidth + padding.Top + (atTop ? 0 : cursor + pending.Resolve())));
                continue;
            }
            if (child.IsFloat)
            {
                PlaceFloat(child, atTop ? 0 : cursor + pending.Resolve());
                continue;
            }

            var childIndependent = EstablishesIndependentFormattingContext(child);
            // Where the child's border box goes if its top margins collapse as expected. With floats around, that
            // must be right before layout, so the margins collapsing through its top are worked out first; otherwise
            // its own margin will do, and any floats it places are moved once its position is known.
            var expected = exclusions.IsEmpty || childIndependent
                ? MarginStrut.Of(Margin(child.Style.Spacing.MarginTop, width))
                : LeadingMargins(child, width);
            var y = atTop ? 0 : cursor + pending.Append(expected).Resolve();

            // Clearance (§9.5.2): the border box goes below the floats it clears, and margins stop collapsing across.
            var clearance = false;
            if (exclusions.ClearEdge(child.Style.Box.Clear) is { } edge && contentY + y < edge)
                (y, clearance) = (edge - contentY, true);

            Fragment fragment;
            var x = border.LeftWidth + padding.Left;
            if (childIndependent && !exclusions.IsEmpty)
            {
                var wanted = y;
                (fragment, var left, var top) = AvoidFloats(child, exclusions, contentX, contentX + width, contentY + y, definiteHeight, context);
                (x, y) = (left - boxX, top - contentY);
                clearance |= y > wanted;
            }
            else
            {
                fragment = Layout(child, new ConstraintSpace(width, definiteHeight, exclusions, contentX, contentY + y), context);
            }
            x += fragment.MarginLeft;

            if (clearance)
            {
                Place(child, fragment, x, border.TopWidth + padding.Top + y);
                if (!childIndependent)
                    exclusions = fragment.Exclusions ?? exclusions;
                (cursor, atTop, hasContent) = (y + fragment.Height, false, true);
                pending = fragment.CollapsesThrough ? MarginStrut.Of(Margin(child.Style.Spacing.MarginBottom, width)) : fragment.BottomMargins;
                continue;
            }

            if (fragment.CollapsesThrough)
            {
                // An empty block sits where its top border edge would be; its margins join the pending ones.
                var throughY = atTop ? 0 : cursor + pending.Append(fragment.TopMargins).Resolve();
                Place(child, fragment, x, border.TopWidth + padding.Top + throughY);
                exclusions = MoveFloats(fragment, exclusions, throughY - y);
                pending = pending.Append(fragment.TopMargins);
                continue;
            }
            var margins = pending.Append(fragment.TopMargins);
            var placedY = atTop ? 0 : cursor + margins.Resolve();
            if (atTop)
                (leading, atTop) = (margins, false);
            Place(child, fragment, x, border.TopWidth + padding.Top + placedY);
            if (!childIndependent)
                exclusions = MoveFloats(fragment, exclusions, placedY - y);
            cursor = placedY + fragment.Height;
            pending = fragment.BottomMargins;
            hasContent = true;
        }
        if (box is BlockContainerBox { Inline: { } inline } container)
        {
            var top = atTop ? 0 : cursor + pending.Resolve();
            var environment = new InlineLayout.Environment(
                (from, to) =>
                {
                    var (l, r) = exclusions.Available(contentY + from, contentY + to, contentX, contentX + width);
                    return (l - contentX, r - contentX);
                },
                (from, to) => exclusions.NextBottom(contentY + from, contentY + to) is { } b ? b - contentY : null,
                PlaceFloat,
                (child, x, y) => outOfFlow.Add(new(child, border.LeftWidth + padding.Left + x, border.TopWidth + padding.Top + y)));
            var (lines, bottom, hasLineBoxes) = InlineLayout.Layout(container, inline, width, top, environment, context);
            foreach (var line in lines)
                children.Add(line with { X = line.X + border.LeftWidth + padding.Left, Y = line.Y + border.TopWidth + padding.Top });
            if (hasLineBoxes)
            {
                // Line boxes are content: margins before them no longer adjoin this box's edges.
                if (atTop)
                    (leading, atTop) = (pending, false);
                pending = default;
                cursor = bottom;
                hasContent = true;
            }
        }

        // Height: auto is the flow's extent; min and max apply either way (§10.6.3, §10.7). An independent formatting
        // context's auto height also contains its floats (§10.6.7).
        var flowHeight = cursor;
        var contentHeight = Clamp(height ?? flowHeight, minHeight, maxHeight);
        if (collapseBottom && contentHeight != flowHeight)
            collapseBottom = false; // min-height or max-height moved the bottom edge away from the last child
        if (!collapseBottom && !atTop)
        {
            contentHeight = Clamp(height ?? flowHeight + pending.Resolve(), minHeight, maxHeight);
            pending = default;
        }
        if (independent && height is null && !exclusions.IsEmpty)
            contentHeight = Clamp(Math.Max(contentHeight, exclusions.Bottom - contentY), minHeight, maxHeight);

        var own = (Top: MarginStrut.Of(marginTop), Bottom: MarginStrut.Of(marginBottom));
        // §8.3.1: no content, no top or bottom border or padding, zero or auto height and zero min-height.
        var collapsesThrough = collapseTop && bottomEdgeOpen && (height ?? 0) == 0 && !hasContent && contentHeight == 0 && minHeight == 0;
        if (atTop && !collapsesThrough)
        {
            // Only empty children: their margins adjoin this box's top edge.
            (leading, pending) = (pending, default);
        }

        // A positioned box is the containing block of its absolutely positioned descendants (CSS 2.2 §10.1): they are
        // laid out against its padding box now that its size is known. Fixed ones go on up to the viewport.
        if (style.Box.Position != Position.Static && outOfFlow.Count > 0)
        {
            var carried = outOfFlow.ToList();
            outOfFlow.Clear();
            var (paddingWidth, paddingHeight) = (width + padding.Left + padding.Right, contentHeight + padding.Top + padding.Bottom);
            foreach (var o in carried)
            {
                if (o.Box.Style.Box.Position == Position.Fixed)
                {
                    outOfFlow.Add(o);
                    continue;
                }
                var placed = PositionedLayout.LayoutAbsolute(o.Box, paddingWidth, paddingHeight, o.StaticX - border.LeftWidth, o.StaticY - border.TopWidth, context);
                Place(o.Box, placed.Fragment, placed.X + border.LeftWidth, placed.Y + border.TopWidth);
            }
        }

        return new Fragment(box, width + frameX, contentHeight + frameY, children)
        {
            MarginLeft = marginLeft,
            MarginRight = marginRight,
            TopMargins = collapsesThrough ? own.Top.Append(pending).Append(own.Bottom) : own.Top.Append(leading),
            BottomMargins = collapsesThrough ? default : own.Bottom.Append(pending),
            CollapsesThrough = collapsesThrough,
            Exclusions = independent ? space.Exclusions : exclusions,
            OutOfFlow = outOfFlow,
        };
    }

    // The floats after a child that joined this formatting context, with the ones it placed moved by dy: the child
    // was laid out at an expected position that its collapsed margins then changed.
    private static ExclusionSpace MoveFloats(Fragment child, ExclusionSpace before, float dy) =>
        (child.Exclusions ?? before).Translate(before.Count, dy);

    /// <summary>
    /// Places a box with an independent formatting context next to the floats (§9.5): at the first position from
    /// <paramref name="top"/> down where its border box fits beside them, its auto width shrinking to the space left.
    /// Returns its fragment and the left edge and top of the space it was given.
    /// </summary>
    // ponytail: percentages inside resolve against the narrowed space rather than the containing block.
    private static (Fragment Fragment, float Left, float Top) AvoidFloats(
        Box child, ExclusionSpace exclusions, float left, float right, float top, float? containingHeight, LayoutContext context)
    {
        var height = 0f;
        for (var attempt = 0; ; attempt++)
        {
            var (l, r) = exclusions.Available(top, top + height, left, right);
            var available = Math.Max(0, r - l);
            var fragment = Layout(child, new ConstraintSpace(available, containingHeight), context);
            var outer = fragment.MarginLeft + fragment.Width + Margin(child.Style.Spacing.MarginRight, available);
            var band = exclusions.Available(top, top + fragment.Height, left, right);
            if (band == (l, r) && outer <= available + 0.01f)
                return (fragment, l, top);
            if (attempt > exclusions.Count * 2 || exclusions.NextBottom(top, top + fragment.Height) is not { } next)
                return (fragment, band.Left, top);
            if (band == (l, r))
                top = next; // too wide beside these floats: try below the first of them to end
            height = fragment.Height;
        }
    }

    /// <summary>
    /// The margins that collapse through a box's top edge, worked out from the box tree before layout (§8.3.1): its
    /// own top margin, joined by its first in-flow child's when nothing separates them, and past children that
    /// collapse through.
    /// </summary>
    private static MarginStrut LeadingMargins(Box box, float cbWidth)
    {
        var style = box.Style;
        var strut = MarginStrut.Of(Margin(style.Spacing.MarginTop, cbWidth));
        if (EstablishesIndependentFormattingContext(box) || style.Border.TopWidth != 0 || Resolve(style.Spacing.PaddingTop, cbWidth) != 0)
            return strut;
        var width = ChildWidth(box, cbWidth);
        foreach (var child in FlowChildren(box))
        {
            if (child.IsFloat)
                continue;
            strut = strut.Append(LeadingMargins(child, width));
            if (!CollapsesThrough(child, width))
                return strut;
            strut = strut.Append(MarginStrut.Of(Margin(child.Style.Spacing.MarginBottom, width)));
        }
        return strut;
    }

    // Whether a box collapses through (§8.3.1), judged from the box tree; Layout decides it exactly.
    private static bool CollapsesThrough(Box box, float cbWidth)
    {
        var style = box.Style;
        if (EstablishesIndependentFormattingContext(box) || style.Box.Clear != Clear.None
            || style.Border.TopWidth != 0 || style.Border.BottomWidth != 0
            || Resolve(style.Spacing.PaddingTop, cbWidth) != 0 || Resolve(style.Spacing.PaddingBottom, cbWidth) != 0
            || style.Size.Height is { Kind: SizeKind.Length } h && (h.Length.HasPercent || h.Length.Px != 0)
            || style.Size.MinHeight is { Kind: SizeKind.Length } min && (min.Length.HasPercent || min.Length.Px != 0)
            || box is BlockContainerBox { Inline: { } inline } && inline.Items.Any(i => i.Kind is not (InlineItemKind.Float or InlineItemKind.OutOfFlow)))
            return false;
        var width = ChildWidth(box, cbWidth);
        return FlowChildren(box).All(c => c.IsFloat || CollapsesThrough(c, width));
    }

    // The content-box width a box gives its children.
    private static float ChildWidth(Box box, float cbWidth)
    {
        var style = box.Style;
        var frameX = style.Border.LeftWidth + Resolve(style.Spacing.PaddingLeft, cbWidth)
            + Resolve(style.Spacing.PaddingRight, cbWidth) + style.Border.RightWidth;
        return SolveWidth(box, cbWidth, frameX, style.Box.BoxSizing == BoxSizing.BorderBox).Width;
    }

    // In-flow block-level children and floats; positioned boxes are laid out by their own algorithm.
    private static IEnumerable<Box> FlowChildren(Box box) =>
        box is BlockContainerBox { Inline: null } ? box.Children.Where(c => !c.IsAbsolutelyPositioned) : [];

    /// <summary>
    /// The width and horizontal margins of a block-level box in normal flow (§10.3.3), with max-width then min-width
    /// applied by solving again with the limit as the width (§10.4).
    /// </summary>
    /// <remarks>
    /// With a layout context, auto widths of floats and inline-blocks shrink to fit (§10.3.5) and the sizing keywords
    /// resolve; without one (structural estimates), they act as auto.
    /// </remarks>
    private static (float Width, float MarginLeft, float MarginRight) SolveWidth(Box box, float cbWidth, float frameX, bool borderBox,
                                                                                 LayoutContext? context = null)
    {
        var style = box.Style;
        var available = cbWidth - Margin(style.Spacing.MarginLeft, cbWidth) - Margin(style.Spacing.MarginRight, cbWidth) - frameX;
        float? Size(SizeValue value) =>
            ContentSize(value, cbWidth, frameX, borderBox) ?? IntrinsicSizes.Keyword(value, box, available, context);
        var width = Size(style.Size.Width)
            ?? (context is not null && (box.IsFloat || box is BlockContainerBox { IsAtomicInline: true })
                ? IntrinsicSizes.FitContent(box, available, context)
                : null);
        var result = Solve(width);
        if (Size(style.Size.MaxWidth) is { } max && result.Width > max)
            result = Solve(max);
        if (Size(style.Size.MinWidth) is { } min && result.Width < min)
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
    internal static float? ContentSize(SizeValue value, float? basis, float frame, bool borderBox)
    {
        if (value.Kind != SizeKind.Length || value.Length.HasPercent && basis is null)
            return null;
        var size = Math.Max(0, value.Length.Resolve(basis ?? 0));
        return borderBox ? Math.Max(0, size - frame) : size;
    }

    // Margins and paddings resolve percentages against the containing block's width, vertical ones included.
    internal static float Margin(SizeValue value, float cbWidth) => value.Kind == SizeKind.Length ? value.Length.Resolve(cbWidth) : 0;

    internal static float Resolve(LengthPercentage value, float cbWidth) => Math.Max(0, value.Resolve(cbWidth));

    // max wins over the size, min wins over max (§10.4, §10.7).
    internal static float Clamp(float size, float min, float max) => Math.Max(min, Math.Min(size, max));

    /// <summary>
    /// Whether the box's content is laid out independently of its surroundings, so its margins do not collapse with
    /// its children's and floats outside do not reach in (https://www.w3.org/TR/CSS22/visuren.html#block-formatting
    /// and css-display-3 §2.3).
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

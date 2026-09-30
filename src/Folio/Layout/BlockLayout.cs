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
    // ponytail: replaced boxes without natural dimensions (canvas, video, iframe, images that did not load) are still
    // sized from their width and height properties only.
    public static Fragment Layout(Box box, ConstraintSpace space, LayoutContext context)
    {
        var style = box.Style;
        var cbWidth = space.ContainingWidth;

        // Content that nests deeper than the stack allows is left out rather than overflowing it (study 16: limits
        // stop work gracefully).
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return new Fragment(box, 0, 0, []) { Exclusions = space.Exclusions };

        // A table wrapper has the table's style, but its border and padding belong to the table grid box inside it.
        var wrapper = box is TableWrapperBox;
        var border = wrapper ? ComputedStyle.Initial.Border : space.Border ?? style.Border;
        var padding = wrapper ? (Top: 0f, Right: 0f, Bottom: 0f, Left: 0f) : (
            Top: Resolve(style.Spacing.PaddingTop, cbWidth), Right: Resolve(style.Spacing.PaddingRight, cbWidth),
            Bottom: Resolve(style.Spacing.PaddingBottom, cbWidth), Left: Resolve(style.Spacing.PaddingLeft, cbWidth));
        var frameX = border.LeftWidth + padding.Left + padding.Right + border.RightWidth;
        var frameY = border.TopWidth + padding.Top + padding.Bottom + border.BottomWidth;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;

        var replaced = box is ReplacedBox replacedBox
            ? ReplacedSize(replacedBox, cbWidth, space.ContainingHeight, frameX, frameY, space.FixedWidth - frameX,
                Math.Max(0, cbWidth - Margin(style.Spacing.MarginLeft, cbWidth) - Margin(style.Spacing.MarginRight, cbWidth) - frameX))
            : null;
        var (width, marginLeft, marginRight) = space.FixedWidth is { } fixedWidth
            ? (Math.Max(0, fixedWidth - frameX), 0f, 0f)
            : SolveWidth(box, cbWidth, frameX, borderBox, context, replaced?.Width);
        var marginTop = Margin(style.Spacing.MarginTop, cbWidth);
        var marginBottom = Margin(style.Spacing.MarginBottom, cbWidth);

        var height = space.FixedHeight is { } fixedHeight
            ? Math.Max(0, fixedHeight - frameY)
            : replaced?.Height ?? ContentSize(style.Size.Height, space.ContainingHeight, frameY, borderBox);
        var minHeight = space.FixedHeight is null ? ContentSize(style.Size.MinHeight, space.ContainingHeight, frameY, borderBox) ?? 0 : 0;
        var maxHeight = space.FixedHeight is null ? ContentSize(style.Size.MaxHeight, space.ContainingHeight, frameY, borderBox) ?? float.PositiveInfinity : float.PositiveInfinity;
        // aspect-ratio (css-sizing-4 §5.1): an auto height follows the width through the ratio, in the box-sizing box. It
        // is still at least the content's height unless the box scrolls or clips (the automatic minimum size).
        var fromRatio = false;
        if (height is null && style.Size.AspectRatio is { } ratio && box is not ReplacedBox)
        {
            height = borderBox ? Math.Max(0, (width + frameX) / ratio - frameY) : width / ratio;
            fromRatio = style.Box.OverflowX == Overflow.Visible && style.Box.OverflowY == Overflow.Visible;
        }
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
            var side = PhysicalFloat(child.Style.Box.Float, style.Text.Direction);
            var minTop = Math.Max(contentY + top, exclusions.ClearEdge(PhysicalClear(child.Style.Box.Clear, style.Text.Direction)) ?? float.NegativeInfinity);
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
            var (items, gridHeight, gridPositioned) = GridLayout.Layout(gridBox, width, definiteHeight, minHeight, maxHeight, context);
            foreach (var item in items)
                Place(item.Fragment.Box!, item.Fragment, border.LeftWidth + padding.Left + item.X, border.TopWidth + padding.Top + item.Y);
            foreach (var (child, left, top, right, bottom) in gridPositioned)
            {
                if (!IsContainingBlock(box, child))
                {
                    outOfFlow.Add(new(child, border.LeftWidth + padding.Left, border.TopWidth + padding.Top));
                    continue;
                }
                // A grid that is the containing block of its positioned children gives each its grid area, auto
                // lines being its padding edges (css-grid-1 §9.1).
                var (l, t) = (left ?? -padding.Left, top ?? -padding.Top);
                var (r, b) = (right ?? width + padding.Right, bottom ?? Clamp(height ?? gridHeight, minHeight, maxHeight) + padding.Bottom);
                var placed = PositionedLayout.LayoutAbsolute(child, Math.Max(0, r - l), Math.Max(0, b - t), 0, 0, context);
                Place(child, placed.Fragment, border.LeftWidth + padding.Left + l + placed.X, border.TopWidth + padding.Top + t + placed.Y);
            }
            cursor = gridHeight;
            hasContent = items.Count > 0;
        }
        else if (box is TableWrapperBox tableWrapper)
        {
            var (parts, tableHeight) = TableLayout.LayoutWrapper(tableWrapper, width, context);
            foreach (var part in parts)
            {
                children.Add(part);
                foreach (var o in part.Fragment.OutOfFlow)
                    outOfFlow.Add(o with { StaticX = o.StaticX + part.X, StaticY = o.StaticY + part.Y });
            }
            cursor = tableHeight;
            hasContent = parts.Count > 0;
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
            if (exclusions.ClearEdge(PhysicalClear(child.Style.Box.Clear, style.Text.Direction)) is { } edge && contentY + y < edge)
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
        var contentHeight = Clamp(fromRatio ? Math.Max(height!.Value, flowHeight) : height ?? flowHeight, minHeight, maxHeight);
        if (collapseBottom && contentHeight != flowHeight)
            collapseBottom = false; // min-height or max-height moved the bottom edge away from the last child
        if (!collapseBottom && !atTop)
        {
            contentHeight = Clamp(fromRatio ? Math.Max(height!.Value, flowHeight + pending.Resolve()) : height ?? flowHeight + pending.Resolve(), minHeight, maxHeight);
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

        // Positioned descendants this box is the containing block of are laid out against its padding box now that its
        // size is known; the others go on up.
        if ((style.Box.Position != Position.Static || box.ContainsFixed) && outOfFlow.Count > 0)
        {
            var carried = outOfFlow.ToList();
            outOfFlow.Clear();
            var (paddingWidth, paddingHeight) = (width + padding.Left + padding.Right, contentHeight + padding.Top + padding.Bottom);
            foreach (var o in carried)
            {
                if (!IsContainingBlock(box, o.Box))
                {
                    outOfFlow.Add(o);
                    continue;
                }
                var placed = PositionedLayout.LayoutAbsolute(o.Box, paddingWidth, paddingHeight, o.StaticX - border.LeftWidth, o.StaticY - border.TopWidth, context);
                Place(o.Box, placed.Fragment, placed.X + border.LeftWidth, placed.Y + border.TopWidth);
            }
        }

        if (box is BlockContainerBox { Marker: { } marker })
            PlaceMarker(marker, children, border.LeftWidth + padding.Left, border.TopWidth + padding.Top, width, context);

        return new Fragment(box, width + frameX, contentHeight + frameY, children)
        {
            MarginLeft = marginLeft,
            MarginRight = marginRight,
            TopMargins = collapsesThrough ? own.Top.Append(pending).Append(own.Bottom) : own.Top.Append(leading),
            BottomMargins = collapsesThrough ? default : own.Bottom.Append(pending),
            CollapsesThrough = collapsesThrough,
            Exclusions = independent ? space.Exclusions : exclusions,
            OutOfFlow = outOfFlow,
            // ponytail: rebuilt on every layout of the box (flex and grid may lay an item out more than once); cache it per
            // box and size if large SVG shows up in profiles.
            Svg = box is ReplacedBox { Kind: ReplacedKind.Svg, Node: Dom.ElementNode svg } ? Svg.SvgRenderTree.Build(svg, width, contentHeight, context) : null,
        };
    }

    // A positioned box is the containing block of its absolutely positioned descendants (CSS 2.2 §10.1); a transformed
    // or filtered box is the containing block of its fixed ones too (Box.ContainsFixed). Fixed boxes otherwise go on up
    // to the viewport.
    private static bool IsContainingBlock(Box box, Box positioned) =>
        box.ContainsFixed || box.Style.Box.Position != Position.Static && positioned.Style.Box.Position == Position.Absolute;

    // float and clear: inline-start and inline-end are left and right in a left-to-right containing block, and the other
    // way round in a right-to-left one (css-logical-1 §3.1).
    internal static FloatSide PhysicalFloat(FloatSide side, Direction direction) =>
        side is FloatSide.Right || side == (direction == Direction.Rtl ? FloatSide.InlineStart : FloatSide.InlineEnd) ? FloatSide.Right : FloatSide.Left;

    private static Clear PhysicalClear(Clear clear, Direction direction) => clear switch
    {
        Clear.InlineStart => direction == Direction.Rtl ? Clear.Right : Clear.Left,
        Clear.InlineEnd => direction == Direction.Rtl ? Clear.Left : Clear.Right,
        _ => clear,
    };

    /// <summary>
    /// Places an outside list marker (https://www.w3.org/TR/css-lists-3/#list-style-position-property): its text ends
    /// where the first line box starts (or begins where it ends, right to left), on that line's baseline. The first
    /// line may be in a descendant block; with no line at all, the marker sits at the top of the content box. An image
    /// marker comes before its text, its bottom edge on the baseline like an inline image's.
    /// </summary>
    private static void PlaceMarker(MarkerBox marker, List<ChildFragment> children, float contentX, float contentY, float contentWidth, LayoutContext context)
    {
        var (runs, ascent) = InlineLayout.MarkerText(marker, context);
        var image = marker.Image is { } imageBox ? Layout(imageBox, new ConstraintSpace(contentWidth, null), context) : null;
        if (runs.Count == 0 && image is null)
            return;
        var markerWidth = runs.Sum(r => r.Width) + (image?.Width ?? 0);
        var (lineX, lineY, lineWidth, baseline) = FirstLine(children, 0, 0) ?? (contentX, contentY, contentWidth, Math.Max(ascent, image?.Height ?? 0));
        var rtl = marker.Style.Text.Direction == Style.Direction.Rtl;
        var x = rtl ? lineX + lineWidth : lineX - markerWidth;
        // The image is on the far side of the text from the line: first left to right, last right to left.
        if (image is not null && !rtl)
        {
            children.Add(new ChildFragment(x, lineY + baseline - image.Height, image));
            x += image.Width;
        }
        foreach (var run in runs)
        {
            children.Add(new ChildFragment(x, lineY + baseline - run.Text!.Ascent, run));
            x += run.Width;
        }
        if (image is not null && rtl)
            children.Add(new ChildFragment(x, lineY + baseline - image.Height, image));

        // The first line box in flow order, through in-flow block children, as its box-relative position and baseline.
        static (float X, float Y, float Width, float Baseline)? FirstLine(IReadOnlyList<ChildFragment> fragments, float dx, float dy)
        {
            foreach (var child in fragments)
            {
                if (child.Fragment.Kind == FragmentKind.Line && child.Fragment.Height > 0)
                    return (dx + child.X, dy + child.Y, child.Fragment.Width, child.Fragment.Baseline);
                if (child.Fragment is { Kind: FragmentKind.Box, Box: BlockContainerBox { IsFloat: false, IsAbsolutelyPositioned: false, IsAtomicInline: false } }
                    && FirstLine(child.Fragment.Children, dx + child.X, dy + child.Y) is { } inner)
                    return inner;
            }
            return null;
        }
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
    /// <param name="replacedWidth">A replaced box's used width (<see cref="ReplacedSize"/>), min and max already applied.</param>
    private static (float Width, float MarginLeft, float MarginRight) SolveWidth(Box box, float cbWidth, float frameX, bool borderBox,
                                                                                 LayoutContext? context = null, float? replacedWidth = null)
    {
        var style = box.Style;
        // Which margin gives way when the widths do not add up: the end one of the containing block (§10.3.3).
        var rtl = (box.Parent?.Style ?? style).Text.Direction == Direction.Rtl;
        var available = cbWidth - Margin(style.Spacing.MarginLeft, cbWidth) - Margin(style.Spacing.MarginRight, cbWidth) - frameX;
        float? Size(SizeValue value) =>
            ContentSize(value, cbWidth, frameX, borderBox) ?? IntrinsicSizes.Keyword(value, box, available, context);
        var width = Size(style.Size.Width)
            ?? (context is not null && (box.IsFloat || box is BlockContainerBox { IsAtomicInline: true } || box is TableWrapperBox)
                ? IntrinsicSizes.FitContent(box, available, context)
                : null);
        if (replacedWidth is { } used)
            return Solve(used);
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
                // Auto margins are zero; the width fills the rest, and if that is negative the end margin gives way.
                var fill = Math.Max(0, cbWidth - ml - mr - frameX);
                return rtl ? (fill, cbWidth - mr - frameX - fill, mr) : (fill, ml, cbWidth - ml - frameX - fill);
            }

            var remaining = cbWidth - w - frameX;
            var half = Math.Max(0, remaining / 2);
            return (left.Kind == SizeKind.Auto, right.Kind == SizeKind.Auto) switch
            {
                // Centred, unless the box is wider than its containing block (then the start margin is zero).
                (true, true) => rtl ? (w, remaining - half, half) : (w, half, remaining - half),
                (true, false) => (w, remaining - mr, mr),
                (false, true) => (w, ml, remaining - ml),
                // Over-constrained: the end margin takes what is left.
                _ => rtl ? (w, remaining - mr, mr) : (w, ml, remaining - ml),
            };
        }
    }

    /// <summary>
    /// The used content-box size of a replaced box (CSS 2.2 §10.3.2 and §10.6.2): an auto size follows the other through
    /// the natural aspect ratio, or is the natural size, and min and max sizes apply as §10.4's table says, keeping the
    /// ratio when both sizes are auto. Without a natural width, a box with a ratio fills <paramref name="fillWidth"/>
    /// and one without is 300px wide; without a natural height, one without a ratio is 150px high. Null for boxes with
    /// no natural dimensions at all (not an image or SVG).
    /// </summary>
    /// <param name="usedWidth">A width already decided by the parent's algorithm (content box), which the height follows.</param>
    /// <param name="fillWidth">The width that fills the containing block; null in intrinsic sizing, where 300px is used.</param>
    internal static (float Width, float Height)? ReplacedSize(ReplacedBox box, float cbWidth, float? cbHeight, float frameX, float frameY,
                                                            float? usedWidth = null, float? fillWidth = null)
    {
        float? nw, nh, ratio;
        if (box.NaturalSize is { } natural)
            (nw, nh, ratio) = (natural.Width, natural.Height, natural.Width > 0 && natural.Height > 0 ? natural.Width / natural.Height : null);
        else if (box.SvgNatural is { } svg)
            (nw, nh, ratio) = svg;
        else
            return null;
        var naturalWidth = nw ?? (nh is { } h0 && ratio is { } r0 ? h0 * r0 : ratio is not null && fillWidth is { } fill ? fill : 300);
        var naturalHeight = nh ?? (ratio is { } r1 ? naturalWidth / r1 : 150);
        var size = box.Style.Size;
        var borderBox = box.Style.Box.BoxSizing == BoxSizing.BorderBox;
        float? W(SizeValue v) => ContentSize(v, cbWidth, frameX, borderBox);
        float? H(SizeValue v) => ContentSize(v, cbHeight, frameY, borderBox);
        var (minW, maxW) = (W(size.MinWidth) ?? 0, W(size.MaxWidth) ?? float.PositiveInfinity);
        var (minH, maxH) = (H(size.MinHeight) ?? 0, H(size.MaxHeight) ?? float.PositiveInfinity);

        var (width, height) = (usedWidth ?? W(size.Width), H(size.Height));
        if (width is null && height is null && ratio is { } r)
            return Constrain(naturalWidth, naturalHeight, r);
        var w = width ?? (height is { } hh && ratio is { } r1b ? Clamp(hh, minH, maxH) * r1b : naturalWidth);
        if (usedWidth is null)
            w = Clamp(w, minW, maxW);
        var h = height ?? (ratio is { } r2 ? w / r2 : naturalHeight);
        return (w, Clamp(h, minH, maxH));

        // §10.4, the table for both sizes auto: each violation is resolved keeping the ratio where the other limits allow.
        (float, float) Constrain(float w, float h, float r)
        {
            maxW = Math.Max(minW, maxW);
            maxH = Math.Max(minH, maxH);
            if (w > maxW && h > maxH)
                return maxW / w <= maxH / h ? (maxW, Math.Max(minH, maxW / r)) : (Math.Max(minW, maxH * r), maxH);
            if (w < minW && h < minH)
                return minW / w <= minH / h ? (Math.Min(maxW, minH * r), minH) : (minW, Math.Min(maxH, minW / r));
            if (w < minW && h > maxH)
                return (minW, maxH);
            if (w > maxW && h < minH)
                return (maxW, minH);
            if (w > maxW)
                return (maxW, Math.Max(maxW / r, minH));
            if (w < minW)
                return (minW, Math.Min(minW / r, maxH));
            if (h > maxH)
                return (Math.Max(maxH * r, minW), maxH);
            if (h < minH)
                return (Math.Min(minH * r, maxW), minH);
            return (w, h);
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

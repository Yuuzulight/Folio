using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Vertical writing modes (https://www.w3.org/TR/css-writing-modes-4/) for block containers: inline content is laid
/// out in lines along the box's height as horizontal lines would be along its width, then turned a quarter clockwise,
/// so lines run top to bottom; lines and block children stack from the right (vertical-rl) or from the left
/// (vertical-lr), each block child as tall as the box. In a horizontal parent it is an orthogonal flow (§7.3): its
/// height is the definite one, else its content's max-content length, and its auto width is its content's extent.
/// </summary>
// ponytail: floats and positioned boxes in vertical flow are left out, block margins do not collapse, and atomic
// inlines other than ruby keep their own orientation.
internal static class VerticalLayout
{
    public static Fragment Layout(BlockContainerBox box, ConstraintSpace space, LayoutContext context)
    {
        var style = box.Style;
        var cbWidth = space.ContainingWidth;
        var border = space.Border ?? style.Border;
        var padding = (Top: BlockLayout.Resolve(style.Spacing.PaddingTop, cbWidth), Right: BlockLayout.Resolve(style.Spacing.PaddingRight, cbWidth),
            Bottom: BlockLayout.Resolve(style.Spacing.PaddingBottom, cbWidth), Left: BlockLayout.Resolve(style.Spacing.PaddingLeft, cbWidth));
        var frameX = border.LeftWidth + padding.Left + padding.Right + border.RightWidth;
        var frameY = border.TopWidth + padding.Top + padding.Bottom + border.BottomWidth;

        var inlineSize = InlineSize(box, space.FixedHeight, space.ContainingHeight, context);
        var (items, blockSize) = Content(box, inlineSize, context);
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;
        var width = space.FixedWidth is { } fixedWidth ? Math.Max(0, fixedWidth - frameX)
            : BlockLayout.ContentSize(style.Size.Width, cbWidth, frameX, borderBox) ?? blockSize;
        width = BlockLayout.Clamp(width, BlockLayout.ContentSize(style.Size.MinWidth, cbWidth, frameX, borderBox) ?? 0,
            BlockLayout.ContentSize(style.Size.MaxWidth, cbWidth, frameX, borderBox) ?? float.PositiveInfinity);

        var (left, top) = (border.LeftWidth + padding.Left, border.TopWidth + padding.Top);
        var lr = style.Text.WritingMode == WritingMode.VerticalLr;
        var children = items.Select(item => new ChildFragment(
            left + (lr ? item.BlockOffset : width - item.BlockOffset - item.Fragment.Width), top + item.InlineOffset, item.Fragment)).ToList();
        return new Fragment(box, width + frameX, inlineSize + frameY, children)
        {
            MarginLeft = BlockLayout.Margin(style.Spacing.MarginLeft, cbWidth),
            MarginRight = BlockLayout.Margin(style.Spacing.MarginRight, cbWidth),
            TopMargins = MarginStrut.Of(BlockLayout.Margin(style.Spacing.MarginTop, cbWidth)),
            BottomMargins = MarginStrut.Of(BlockLayout.Margin(style.Spacing.MarginBottom, cbWidth)),
            Exclusions = space.Exclusions,
        };
    }

    /// <summary>
    /// The box when it is laid out here: a block container in a vertical writing mode, other than a ruby column and its
    /// base and annotation, which are laid out horizontally and turned with the line they sit in.
    /// </summary>
    public static BlockContainerBox? Applies(Box box) =>
        box is BlockContainerBox block && block is not RubyColumnBox && box.Style.Text.IsVertical && box.Parent is not RubyColumnBox ? block : null;

    /// <summary>The extent a vertical box's content takes across its lines and blocks, for its intrinsic widths.</summary>
    public static float BlockSize(BlockContainerBox box, LayoutContext context) => Content(box, InlineSize(box, null, null, context), context).BlockSize;

    // The box's content as physical fragments, each with where it starts along the block axis (from the block-start
    // edge) and along the inline axis (from the top): turned lines, or block children as tall as the box.
    private static (List<(Fragment Fragment, float BlockOffset, float InlineOffset)> Items, float BlockSize) Content(
        BlockContainerBox box, float inlineSize, LayoutContext context)
    {
        var items = new List<(Fragment, float, float)>();
        if (box.Inline is { } inline)
        {
            var (lines, bottom, _) = InlineLayout.Layout(box, inline, inlineSize, 0,
                new InlineLayout.Environment((_, _) => (0, inlineSize), (_, _) => null, (_, _) => { }, (_, _, _) => { }), context);
            items.AddRange(lines.Select(line => (Turn(line.Fragment), line.Y, line.X)));
            return (items, bottom);
        }

        var cursor = 0f;
        var lr = box.Style.Text.WritingMode == WritingMode.VerticalLr;
        foreach (var child in box.Children)
        {
            if (child.IsFloat || child.IsAbsolutelyPositioned)
                continue;
            var spacing = child.Style.Spacing;
            var (marginTop, marginBottom) = (BlockLayout.Margin(spacing.MarginTop, inlineSize), BlockLayout.Margin(spacing.MarginBottom, inlineSize));
            var (blockStart, blockEnd) = lr
                ? (BlockLayout.Margin(spacing.MarginLeft, inlineSize), BlockLayout.Margin(spacing.MarginRight, inlineSize))
                : (BlockLayout.Margin(spacing.MarginRight, inlineSize), BlockLayout.Margin(spacing.MarginLeft, inlineSize));
            // A block child fills the box's inline size (its height) unless it sets its own.
            var stretch = child.Style.Size.Height.Kind == SizeKind.Auto ? Math.Max(0, inlineSize - marginTop - marginBottom) : (float?)null;
            var fragment = child is BlockContainerBox block && child.Style.Text.IsVertical
                ? Layout(block, new ConstraintSpace(inlineSize, inlineSize, FixedHeight: stretch), context)
                : BlockLayout.Layout(child, new ConstraintSpace(inlineSize, inlineSize), context);
            cursor += blockStart;
            items.Add((fragment, cursor, marginTop));
            cursor += fragment.Width + blockEnd;
        }
        return (items, cursor);
    }

    // The length of its lines: the box's content height when definite, else its content's max-content length, within its
    // min and max heights.
    private static float InlineSize(BlockContainerBox box, float? fixedHeight, float? containingHeight, LayoutContext context)
    {
        var style = box.Style;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;
        var frameY = FrameY(style);
        var size = fixedHeight is { } h ? Math.Max(0, h - frameY)
            : BlockLayout.ContentSize(style.Size.Height, containingHeight, frameY, borderBox) ?? MaxContent(box, context);
        return BlockLayout.Clamp(size, BlockLayout.ContentSize(style.Size.MinHeight, containingHeight, frameY, borderBox) ?? 0,
            BlockLayout.ContentSize(style.Size.MaxHeight, containingHeight, frameY, borderBox) ?? float.PositiveInfinity);
    }

    private static float FrameY(ComputedStyle style) =>
        style.Border.TopWidth + style.Border.BottomWidth + BlockLayout.Resolve(style.Spacing.PaddingTop, 0) + BlockLayout.Resolve(style.Spacing.PaddingBottom, 0);

    // The max-content inline size: the longest line, or the longest block child with its own frame and margins.
    private static float MaxContent(BlockContainerBox box, LayoutContext context)
    {
        if (box.Inline is { } inline)
            return InlineLayout.Measure(box, inline, context).Max;
        var max = 0f;
        foreach (var child in box.Children)
        {
            if (child is not BlockContainerBox block || child.IsFloat || child.IsAbsolutelyPositioned)
                continue;
            var spacing = child.Style.Spacing;
            max = Math.Max(max, MaxContent(block, context) + FrameY(child.Style) + BlockLayout.Margin(spacing.MarginTop, 0) + BlockLayout.Margin(spacing.MarginBottom, 0));
        }
        return max;
    }

    // A fragment laid out horizontally, turned a quarter clockwise in place: what was its top edge is now its right one.
    // Text is marked turned for painting. Inline boxes and ruby columns turn with their content; other atomic inlines
    // are placed but not turned.
    private static Fragment Turn(Fragment fragment, bool everything = false) =>
        new(fragment.Box, fragment.Height, fragment.Width, fragment.Children.Select(c => Turned(c, fragment.Height, everything)).ToList())
        {
            Kind = fragment.Kind,
            Baseline = fragment.Baseline,
            Text = fragment.Text is { } text ? text with { Turned = true } : null,
        };

    // A child of a fragment being turned, placed in its turned parent of logical height height. An atomic inline that keeps
    // its orientation was placed in the line with its width across it.
    private static ChildFragment Turned(ChildFragment c, float height, bool everything)
    {
        var turns = everything || c.Fragment.Kind != FragmentKind.Box || c.Fragment.Box is InlineBox or RubyColumnBox;
        var fragment = turns ? Turn(c.Fragment, everything || c.Fragment.Box is RubyColumnBox) : c.Fragment;
        return new ChildFragment(height - c.Y - fragment.Width, c.X, fragment);
    }
}

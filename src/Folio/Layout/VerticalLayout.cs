using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Vertical writing modes (https://www.w3.org/TR/css-writing-modes-4/) for a block container holding inline content:
/// its lines are laid out along the box's height as horizontal lines would be along its width, then turned a quarter
/// clockwise, so they run top to bottom and stack from the right (vertical-rl) or from the left (vertical-lr). In a
/// horizontal parent it is an orthogonal flow (§7.3): its height is the definite one, else its content's max-content
/// length, and its auto width is its lines' total height.
/// </summary>
// ponytail: only inline content is turned; vertical boxes with block children, floats or positioned boxes inside lay
// those out horizontally, and atomic inlines keep their own orientation.
internal static class VerticalLayout
{
    public static Fragment Layout(BlockContainerBox box, InlineFormattingContext inline, ConstraintSpace space, LayoutContext context)
    {
        var style = box.Style;
        var cbWidth = space.ContainingWidth;
        var border = space.Border ?? style.Border;
        var padding = (Top: BlockLayout.Resolve(style.Spacing.PaddingTop, cbWidth), Right: BlockLayout.Resolve(style.Spacing.PaddingRight, cbWidth),
            Bottom: BlockLayout.Resolve(style.Spacing.PaddingBottom, cbWidth), Left: BlockLayout.Resolve(style.Spacing.PaddingLeft, cbWidth));
        var frameX = border.LeftWidth + padding.Left + padding.Right + border.RightWidth;
        var frameY = border.TopWidth + padding.Top + padding.Bottom + border.BottomWidth;

        var inlineSize = InlineSize(box, inline, space.FixedHeight, space.ContainingHeight, context);
        var (lines, blockSize, _) = LayOutLines(box, inline, inlineSize, context);
        var width = space.FixedWidth is { } fixedWidth ? Math.Max(0, fixedWidth - frameX)
            : BlockLayout.ContentSize(style.Size.Width, cbWidth, frameX, style.Box.BoxSizing == BoxSizing.BorderBox) ?? blockSize;
        width = BlockLayout.Clamp(width, BlockLayout.ContentSize(style.Size.MinWidth, cbWidth, frameX, style.Box.BoxSizing == BoxSizing.BorderBox) ?? 0,
            BlockLayout.ContentSize(style.Size.MaxWidth, cbWidth, frameX, style.Box.BoxSizing == BoxSizing.BorderBox) ?? float.PositiveInfinity);

        var (left, top) = (border.LeftWidth + padding.Left, border.TopWidth + padding.Top);
        var children = new List<ChildFragment>();
        foreach (var line in lines)
        {
            var x = style.Text.WritingMode == WritingMode.VerticalLr ? line.Y : width - line.Y - line.Fragment.Height;
            children.Add(new ChildFragment(left + x, top + line.X, Turn(line.Fragment)));
        }
        return new Fragment(box, width + frameX, inlineSize + frameY, children)
        {
            MarginLeft = BlockLayout.Margin(style.Spacing.MarginLeft, cbWidth),
            MarginRight = BlockLayout.Margin(style.Spacing.MarginRight, cbWidth),
            TopMargins = MarginStrut.Of(BlockLayout.Margin(style.Spacing.MarginTop, cbWidth)),
            BottomMargins = MarginStrut.Of(BlockLayout.Margin(style.Spacing.MarginBottom, cbWidth)),
            Exclusions = space.Exclusions,
        };
    }

    /// <summary>The width a vertical box's lines take, for its min-content and max-content contributions.</summary>
    public static float BlockSize(BlockContainerBox box, InlineFormattingContext inline, LayoutContext context) =>
        LayOutLines(box, inline, InlineSize(box, inline, null, null, context), context).Bottom;

    // The length of its lines: the box's content height when definite, else its content's max-content length, within its
    // min and max heights.
    private static float InlineSize(BlockContainerBox box, InlineFormattingContext inline, float? fixedHeight, float? containingHeight, LayoutContext context)
    {
        var style = box.Style;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;
        var frameY = style.Border.TopWidth + style.Border.BottomWidth
                     + BlockLayout.Resolve(style.Spacing.PaddingTop, 0) + BlockLayout.Resolve(style.Spacing.PaddingBottom, 0);
        var size = fixedHeight is { } h ? Math.Max(0, h - frameY)
            : BlockLayout.ContentSize(style.Size.Height, containingHeight, frameY, borderBox) ?? InlineLayout.Measure(box, inline, context).Max;
        return BlockLayout.Clamp(size, BlockLayout.ContentSize(style.Size.MinHeight, containingHeight, frameY, borderBox) ?? 0,
            BlockLayout.ContentSize(style.Size.MaxHeight, containingHeight, frameY, borderBox) ?? float.PositiveInfinity);
    }

    private static (List<ChildFragment> Lines, float Bottom, bool HasLineBoxes) LayOutLines(BlockContainerBox box, InlineFormattingContext inline,
                                                                                            float inlineSize, LayoutContext context) =>
        InlineLayout.Layout(box, inline, inlineSize, 0,
            new InlineLayout.Environment((_, _) => (0, inlineSize), (_, _) => null, (_, _) => { }, (_, _, _) => { }), context);

    // A fragment laid out horizontally, turned a quarter clockwise in place: what was its top edge is now its right one.
    // Text is marked turned for painting; atomic inlines are placed but not turned.
    private static Fragment Turn(Fragment fragment) =>
        new(fragment.Box, fragment.Height, fragment.Width,
            fragment.Children.Select(c => new ChildFragment(fragment.Height - c.Y - c.Fragment.Height, c.X,
                c.Fragment.Kind == FragmentKind.Box && c.Fragment.Box is not InlineBox ? c.Fragment : Turn(c.Fragment))).ToList())
        {
            Kind = fragment.Kind,
            Baseline = fragment.Baseline,
            Text = fragment.Text is { } text ? text with { Turned = true } : null,
        };
}

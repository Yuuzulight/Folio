using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Positioned boxes (docs/study/10-layout-positioning-overflow-stacking.md): relative offsets, and absolutely and
/// fixed positioned boxes laid out against their containing block's padding box (CSS 2.2 §10.3.7, §10.6.4).
/// </summary>
internal static class PositionedLayout
{
    /// <summary>The offset of a relatively positioned box from its place in flow (CSS 2.2 §9.4.3).</summary>
    // ponytail: sticky boxes stay at their place in flow; their scroll-dependent offset comes with scrolling (study 10).
    public static (float X, float Y) RelativeOffset(Box box, float cbWidth, float? cbHeight)
    {
        if (box.Style.Box.Position != Position.Relative)
            return (0, 0);
        var spacing = box.Style.Spacing;
        var x = Inset(spacing.Left, cbWidth) ?? -Inset(spacing.Right, cbWidth) ?? 0;
        var y = Inset(spacing.Top, cbHeight) ?? -Inset(spacing.Bottom, cbHeight) ?? 0;
        return (x, y);
    }

    /// <summary>
    /// Lays out an absolutely or fixed positioned box in a containing block of the given padding-box size. The
    /// static position and the result are from the padding box's origin.
    /// </summary>
    // ponytail: an auto width fills the space the insets leave instead of shrinking to fit, until intrinsic sizes exist.
    public static ChildFragment LayoutAbsolute(Box box, float cbWidth, float cbHeight, float staticX, float staticY)
    {
        var style = box.Style;
        var spacing = style.Spacing;
        var border = style.Border;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;
        var frameX = border.LeftWidth + BlockLayout.Resolve(spacing.PaddingLeft, cbWidth) + BlockLayout.Resolve(spacing.PaddingRight, cbWidth) + border.RightWidth;
        var frameY = border.TopWidth + BlockLayout.Resolve(spacing.PaddingTop, cbWidth) + BlockLayout.Resolve(spacing.PaddingBottom, cbWidth) + border.BottomWidth;

        // Horizontal (§10.3.7), then max-width and min-width by solving again with the limit as the width (§10.4).
        var (left, right) = (Inset(spacing.Left, cbWidth), Inset(spacing.Right, cbWidth));
        var h = Solve(left, BlockLayout.ContentSize(style.Size.Width, cbWidth, frameX, borderBox), right, spacing.MarginLeft, spacing.MarginRight,
            cbWidth, frameX, cbWidth, staticX, horizontal: true);
        if (BlockLayout.ContentSize(style.Size.MaxWidth, cbWidth, frameX, borderBox) is { } max && h.Size > max)
            h = Solve(left, max, right, spacing.MarginLeft, spacing.MarginRight, cbWidth, frameX, cbWidth, staticX, horizontal: true);
        if (BlockLayout.ContentSize(style.Size.MinWidth, cbWidth, frameX, borderBox) is { } min && h.Size < min)
            h = Solve(left, min, right, spacing.MarginLeft, spacing.MarginRight, cbWidth, frameX, cbWidth, staticX, horizontal: true);

        // Vertical (§10.6.4): a specified height, or top and bottom stretching an auto height, is known before layout;
        // otherwise the content decides it.
        var (top, bottom) = (Inset(spacing.Top, cbHeight), Inset(spacing.Bottom, cbHeight));
        var minHeight = BlockLayout.ContentSize(style.Size.MinHeight, cbHeight, frameY, borderBox) ?? 0;
        var maxHeight = BlockLayout.ContentSize(style.Size.MaxHeight, cbHeight, frameY, borderBox) ?? float.PositiveInfinity;
        var height = BlockLayout.ContentSize(style.Size.Height, cbHeight, frameY, borderBox);
        if (height is null && top is not null && bottom is not null)
        {
            var stretched = cbHeight - top.Value - bottom.Value - frameY
                - BlockLayout.Margin(spacing.MarginTop, cbWidth) - BlockLayout.Margin(spacing.MarginBottom, cbWidth);
            height = Math.Max(0, stretched);
        }
        float? fixedHeight = height is { } known ? BlockLayout.Clamp(known, minHeight, maxHeight) + frameY : null;

        var fragment = BlockLayout.Layout(box, new ConstraintSpace(cbWidth, cbHeight, FixedWidth: h.Size + frameX, FixedHeight: fixedHeight));

        var v = Solve(top, fragment.Height - frameY, bottom, spacing.MarginTop, spacing.MarginBottom,
            cbWidth, frameY, cbHeight, staticY, horizontal: false, sizeWasAuto: style.Size.Height.Kind != SizeKind.Length);
        return new ChildFragment(h.Start + h.MarginStart, v.Start + v.MarginStart, fragment);
    }

    // One axis of §10.3.7 / §10.6.4: start + margin-start + frame + size + margin-end + end = the containing block.
    private static (float Start, float Size, float MarginStart, float MarginEnd) Solve(
        float? start, float? size, float? end, SizeValue marginStart, SizeValue marginEnd,
        float marginBasis, float frame, float cb, float staticStart, bool horizontal, bool sizeWasAuto = false)
    {
        var ms = BlockLayout.Margin(marginStart, marginBasis);
        var me = BlockLayout.Margin(marginEnd, marginBasis);
        if (start is null && end is null)
            start = staticStart;

        if (start is { } s && size is { } w && end is { } e && !sizeWasAuto)
        {
            var free = cb - s - e - w - frame - ms - me;
            return (marginStart.Kind == SizeKind.Auto, marginEnd.Kind == SizeKind.Auto) switch
            {
                // Both auto: centred; a horizontal negative remainder goes to the end margin (ltr).
                (true, true) when horizontal && free + ms + me < 0 => (s, w, 0, free + me),
                (true, true) => (s, w, (free + ms + me) / 2, (free + ms + me) / 2),
                (true, false) => (s, w, free + ms, me),
                (false, true) => (s, w, ms, free + me),
                _ => (s, w, ms, me), // over-constrained: the end inset is ignored
            };
        }

        // Auto margins are zero from here on (they came back as zero from Margin).
        var available = Math.Max(0, cb - (start ?? 0) - (end ?? 0) - frame - ms - me);
        var used = size ?? available;
        if (start is null)
            start = cb - end!.Value - used - frame - ms - me;
        return (start.Value, used, ms, me);
    }

    // An inset: null when auto, or when a percentage has no definite basis.
    private static float? Inset(SizeValue value, float? basis) =>
        value.Kind != SizeKind.Length || value.Length.HasPercent && basis is null ? null : value.Length.Resolve(basis ?? 0);
}

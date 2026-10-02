using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Flexible box layout (https://www.w3.org/TR/css-flexbox-1/#layout-algorithm, docs/study/07-layout-flexbox.md):
/// the numbered steps of §9 as methods named after them, working in main and cross coordinates.
/// </summary>
// ponytail: the automatic minimum size uses the content size suggestion (no transferred size without aspect-ratio),
// and visibility: collapse on items acts as hidden (study 07).
internal static class FlexLayout
{
    private sealed class Item(Box box)
    {
        public Box Box { get; } = box;
        public FlexGroup Flex => Box.Style.Flex;
        public float MarginMainStart, MarginMainEnd, MarginCrossStart, MarginCrossEnd;
        public bool AutoMainStart, AutoMainEnd, AutoCrossStart, AutoCrossEnd;
        public float FrameMain, FrameCross; // border and padding
        public float BaseSize, Hypothetical, Min, Max, Target; // main-axis content sizes
        public bool Frozen;
        public float Cross; // border-box cross size once laid out
        public Fragment Fragment; // default until laid out
        public float Baseline; // from the margin-box cross start, for baseline alignment
        public float MainPosition, CrossPosition; // margin-box offsets from the content box
        public float OuterMain(float size) => MarginMainStart + MarginMainEnd + FrameMain + size;
        public float OuterCross => MarginCrossStart + MarginCrossEnd + Cross;
    }

    private sealed class Line(List<Item> items)
    {
        public List<Item> Items { get; } = items;
        public float Cross;
        public float Position;
    }

    /// <summary>Lays out a flex container's items inside its content box.</summary>
    /// <returns>Item fragments (content-box coordinates), the content height, and positioned children.</returns>
    public static (List<ChildFragment> Items, float Height, List<Box> OutOfFlow) Layout(
        FlexContainerBox box, float innerWidth, float? innerHeight, float minHeight, float maxHeight, LayoutContext context)
    {
        var style = box.Style;
        var flex = style.Flex;
        var row = flex.Direction is FlexDirection.Row or FlexDirection.RowReverse;
        var rtl = style.Text.Direction == Direction.Rtl;
        var mainReversed = (flex.Direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse) ^ (row && rtl);
        var crossReversed = (flex.Wrap == FlexWrap.WrapReverse) ^ (!row && rtl);
        var columnGap = flex.ColumnGap.Resolve(innerWidth);
        var rowGap = innerHeight is { } h ? flex.RowGap.Resolve(h) : flex.RowGap.HasPercent ? 0 : flex.RowGap.Px;
        var (gapMain, gapCross) = row ? (columnGap, rowGap) : (rowGap, columnGap);
        float? mainSpace = row ? innerWidth : innerHeight;
        float? crossSpace = row ? innerHeight : innerWidth;

        var outOfFlow = box.Children.Where(c => c.IsAbsolutelyPositioned).ToList();
        // §9.1: items in order-modified document order.
        var items = box.Children.Where(c => !c.IsAbsolutelyPositioned).Select(c => new Item(c)).OrderBy(i => i.Flex.Order).ToList();
        foreach (var item in items)
            DetermineFlexBaseSizeAndHypotheticalMainSize(item, row, innerWidth, mainSpace, crossSpace, context);

        // §9.3: lines; a column container with an indefinite height is one line as tall as its items.
        var lines = CollectFlexItemsIntoFlexLines(items, flex.Wrap != FlexWrap.Nowrap && mainSpace is not null, mainSpace ?? float.PositiveInfinity, gapMain);
        var mainSize = mainSpace ?? Math.Clamp(lines.Max(l => l.Items.Sum(i => i.OuterMain(i.Hypothetical)) + gapMain * (l.Items.Count - 1)), minHeight, maxHeight);
        if (items.Count == 0)
            mainSize = mainSpace ?? Math.Clamp(0, minHeight, maxHeight);
        foreach (var line in lines)
            ResolveFlexibleLengths(line, mainSize, gapMain);

        // §9.4: cross sizes.
        foreach (var item in items)
            DetermineHypotheticalCrossSize(item, row, innerWidth, crossSpace, context, stretchTo: null);
        foreach (var line in lines)
            CalculateLineCrossSize(line, row, lines.Count == 1 && flex.Wrap == FlexWrap.Nowrap ? crossSpace : null);
        // A row container with an auto height is as tall as its lines, within its min and max heights.
        var crossSize = crossSpace ?? Math.Clamp(lines.Sum(l => l.Cross) + gapCross * Math.Max(0, lines.Count - 1), minHeight, maxHeight);
        HandleAlignContentStretch(lines, flex, crossSize, gapCross);
        foreach (var line in lines)
        {
            foreach (var item in line.Items)
            {
                if (IsStretched(item, style))
                    DetermineHypotheticalCrossSize(item, row, innerWidth, crossSpace, context,
                        stretchTo: Math.Max(0, line.Cross - item.MarginCrossStart - item.MarginCrossEnd));
            }
        }

        // §9.5 and §9.6: alignment.
        foreach (var line in lines)
            DistributeMainAxisSpace(line, flex.JustifyContent, mainSize, gapMain);
        DistributeLines(lines, flex, crossSize, gapCross);
        foreach (var line in lines)
        {
            foreach (var item in line.Items)
                AlignInCrossAxis(item, line, style);
        }

        var fragments = new List<ChildFragment>(items.Count);
        foreach (var line in lines)
        {
            foreach (var item in line.Items)
            {
                var main = item.MainPosition + item.MarginMainStart;
                var mainExtent = item.FrameMain + item.Target;
                if (mainReversed)
                    main = mainSize - main - mainExtent;
                var cross = line.Position + item.CrossPosition + item.MarginCrossStart;
                if (crossReversed)
                    cross = crossSize - cross - item.Cross;
                fragments.Add(row ? new ChildFragment(main, cross, item.Fragment!) : new ChildFragment(cross, main, item.Fragment!));
            }
        }
        return (fragments, row ? crossSize : mainSize, outOfFlow);
    }

    // §9.2 step 3 and §4.5 (automatic minimum size).
    private static void DetermineFlexBaseSizeAndHypotheticalMainSize(Item item, bool row, float innerWidth, float? mainSpace, float? crossSpace, LayoutContext context)
    {
        var style = item.Box.Style;
        var spacing = style.Spacing;
        var border = style.Border;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;
        // Margins and padding percentages refer to the container's inline size in both axes.
        float M(SizeValue v) => BlockLayout.Margin(v, innerWidth);
        float P(LengthPercentage v) => BlockLayout.Resolve(v, innerWidth);
        (item.MarginMainStart, item.MarginMainEnd, item.MarginCrossStart, item.MarginCrossEnd) = row
            ? (M(spacing.MarginLeft), M(spacing.MarginRight), M(spacing.MarginTop), M(spacing.MarginBottom))
            : (M(spacing.MarginTop), M(spacing.MarginBottom), M(spacing.MarginLeft), M(spacing.MarginRight));
        (item.AutoMainStart, item.AutoMainEnd, item.AutoCrossStart, item.AutoCrossEnd) = row
            ? (spacing.MarginLeft.Kind == SizeKind.Auto, spacing.MarginRight.Kind == SizeKind.Auto, spacing.MarginTop.Kind == SizeKind.Auto, spacing.MarginBottom.Kind == SizeKind.Auto)
            : (spacing.MarginTop.Kind == SizeKind.Auto, spacing.MarginBottom.Kind == SizeKind.Auto, spacing.MarginLeft.Kind == SizeKind.Auto, spacing.MarginRight.Kind == SizeKind.Auto);
        var frameX = border.LeftWidth + border.RightWidth + P(spacing.PaddingLeft) + P(spacing.PaddingRight);
        var frameY = border.TopWidth + border.BottomWidth + P(spacing.PaddingTop) + P(spacing.PaddingBottom);
        (item.FrameMain, item.FrameCross) = row ? (frameX, frameY) : (frameY, frameX);

        var mainProperty = row ? style.Size.Width : style.Size.Height;
        var basis = item.Flex.Basis.Kind == SizeKind.Auto ? mainProperty : item.Flex.Basis;
        var contentMin = 0f;
        float ContentMain(bool min)
        {
            if (row)
            {
                var (minContent, maxContent) = IntrinsicSizes.Content(item.Box, context);
                return min ? minContent : maxContent;
            }
            // A column item's content height at its cross size.
            var width = row ? 0 : CrossForMeasuring(item, innerWidth, crossSpace, context);
            var fragment = BlockLayout.Layout(item.Box, new ConstraintSpace(innerWidth, null, FixedWidth: width + item.FrameCross), context);
            return Math.Max(0, fragment.Height - item.FrameMain);
        }
        var specified = BlockLayout.ContentSize(basis, mainSpace, item.FrameMain, borderBox)
            ?? (basis.Kind is SizeKind.MinContent ? ContentMain(min: true) : null);
        item.BaseSize = specified ?? ContentMain(min: false);

        item.Max = BlockLayout.ContentSize(row ? style.Size.MaxWidth : style.Size.MaxHeight, mainSpace, item.FrameMain, borderBox) ?? float.PositiveInfinity;
        var minProperty = row ? style.Size.MinWidth : style.Size.MinHeight;
        if (minProperty.Kind == SizeKind.Auto)
        {
            // Automatic minimum: the content size suggestion, capped by a specified main size, for visible overflow.
            var scrolls = style.Box.OverflowX != Overflow.Visible || style.Box.OverflowY != Overflow.Visible;
            if (!scrolls)
            {
                contentMin = ContentMain(min: true);
                if (BlockLayout.ContentSize(mainProperty, mainSpace, item.FrameMain, borderBox) is { } size)
                    contentMin = Math.Min(contentMin, size);
                item.Min = Math.Min(contentMin, item.Max);
            }
        }
        else
        {
            item.Min = BlockLayout.ContentSize(minProperty, mainSpace, item.FrameMain, borderBox) ?? 0;
        }
        item.Hypothetical = Math.Max(item.Min, Math.Min(item.BaseSize, item.Max));
    }

    // The cross size a column item is measured at: its specified width, else stretched or shrink-to-fit.
    private static float CrossForMeasuring(Item item, float innerWidth, float? crossSpace, LayoutContext context)
    {
        var style = item.Box.Style;
        var available = Math.Max(0, (crossSpace ?? innerWidth) - item.MarginCrossStart - item.MarginCrossEnd - item.FrameCross);
        if (BlockLayout.ContentSize(style.Size.Width, innerWidth, item.FrameCross, style.Box.BoxSizing == BoxSizing.BorderBox) is { } width)
            return width;
        return IsStretched(item, item.Box.Parent!.Style) ? available : IntrinsicSizes.FitContent(item.Box, available, context);
    }

    // §9.3 step 5.
    private static List<Line> CollectFlexItemsIntoFlexLines(List<Item> items, bool multiLine, float mainSpace, float gap)
    {
        var lines = new List<Line>();
        var current = new List<Item>();
        var used = 0f;
        foreach (var item in items)
        {
            var outer = item.OuterMain(item.Hypothetical);
            if (multiLine && current.Count > 0 && used + gap + outer > mainSpace + 0.01f)
            {
                lines.Add(new Line(current));
                (current, used) = ([], 0);
            }
            used += (current.Count > 0 ? gap : 0) + outer;
            current.Add(item);
        }
        lines.Add(new Line(current));
        return lines;
    }

    // §9.7.
    private static void ResolveFlexibleLengths(Line line, float mainSize, float gap)
    {
        var items = line.Items;
        if (items.Count == 0)
            return;
        var available = mainSize - gap * (items.Count - 1);
        var grow = items.Sum(i => i.OuterMain(i.Hypothetical)) < available;
        foreach (var item in items)
        {
            item.Target = item.Hypothetical;
            item.Frozen = (grow ? item.Flex.Grow : item.Flex.Shrink) == 0
                || grow && item.BaseSize > item.Hypothetical
                || !grow && item.BaseSize < item.Hypothetical;
        }
        float Remaining() => available - items.Sum(i => i.OuterMain(i.Frozen ? i.Target : i.BaseSize));
        var initialFree = Remaining();

        for (var round = 0; round <= items.Count && items.Any(i => !i.Frozen); round++)
        {
            var unfrozen = items.Where(i => !i.Frozen).ToList();
            var free = Remaining();
            var factors = unfrozen.Sum(i => grow ? i.Flex.Grow : i.Flex.Shrink);
            if (factors < 1 && Math.Abs(initialFree * factors) < Math.Abs(free))
                free = initialFree * factors;

            if (grow)
            {
                foreach (var item in unfrozen)
                    item.Target = item.BaseSize + (factors > 0 ? free * item.Flex.Grow / factors : 0);
            }
            else
            {
                var scaled = unfrozen.Sum(i => i.Flex.Shrink * i.BaseSize);
                foreach (var item in unfrozen)
                    item.Target = item.BaseSize + (scaled > 0 ? free * item.Flex.Shrink * item.BaseSize / scaled : 0);
            }

            // Fix min/max violations and freeze.
            var total = 0f;
            var adjustments = new Dictionary<Item, float>();
            foreach (var item in unfrozen)
            {
                var clamped = Math.Max(item.Min, Math.Min(item.Target, item.Max));
                clamped = Math.Max(0, clamped);
                adjustments[item] = clamped - item.Target;
                total += clamped - item.Target;
                item.Target = clamped;
            }
            foreach (var item in unfrozen)
            {
                if (Math.Abs(total) < 0.001f || total > 0 && adjustments[item] > 0 || total < 0 && adjustments[item] < 0)
                    item.Frozen = true;
            }
        }
    }

    // §9.4 steps 7 and 11: the item laid out at its used main size (and, when stretched, its cross size).
    private static void DetermineHypotheticalCrossSize(Item item, bool row, float innerWidth, float? crossSpace, LayoutContext context, float? stretchTo)
    {
        var style = item.Box.Style;
        var borderBox = style.Box.BoxSizing == BoxSizing.BorderBox;
        float? fixedCross = null;
        if (stretchTo is { } stretch)
        {
            var min = BlockLayout.ContentSize(row ? style.Size.MinHeight : style.Size.MinWidth, row ? crossSpace : innerWidth, item.FrameCross, borderBox) ?? 0;
            var max = BlockLayout.ContentSize(row ? style.Size.MaxHeight : style.Size.MaxWidth, row ? crossSpace : innerWidth, item.FrameCross, borderBox) ?? float.PositiveInfinity;
            fixedCross = Math.Max(min, Math.Min(Math.Max(0, stretch - item.FrameCross), max)) + item.FrameCross;
        }
        else if (!row)
        {
            fixedCross = CrossForMeasuring(item, innerWidth, crossSpace, context) + item.FrameCross;
        }

        var mainBorderBox = item.Target + item.FrameMain;
        var space = row
            ? new ConstraintSpace(innerWidth, crossSpace, FixedWidth: mainBorderBox, FixedHeight: fixedCross)
            : new ConstraintSpace(innerWidth, null, FixedWidth: fixedCross, FixedHeight: mainBorderBox);
        item.Fragment = BlockLayout.Layout(item.Box, space, context);
        item.Cross = row ? item.Fragment.Height : item.Fragment.Width;
        item.Baseline = item.MarginCrossStart + (row && FirstBaseline(item.Fragment) is { } b ? b : item.Cross);
    }

    // §9.4 step 8: the tallest item, or the items' baselines, or the container's definite size for a single line.
    private static void CalculateLineCrossSize(Line line, bool row, float? singleLineCross)
    {
        if (singleLineCross is { } definite)
        {
            line.Cross = definite;
            return;
        }
        var parent = line.Items.FirstOrDefault()?.Box.Parent?.Style;
        float ascent = 0, descent = 0, largest = 0;
        foreach (var item in line.Items)
        {
            if (row && parent is not null && Alignment(item, parent) == ItemAlign.Baseline && !item.AutoCrossStart && !item.AutoCrossEnd)
            {
                ascent = Math.Max(ascent, item.Baseline);
                descent = Math.Max(descent, item.OuterCross - item.Baseline);
            }
            else
            {
                largest = Math.Max(largest, item.OuterCross);
            }
        }
        line.Cross = Math.Max(largest, ascent + descent);
    }

    // §9.4 step 9: align-content: normal and stretch share out the free cross space among the lines.
    private static void HandleAlignContentStretch(List<Line> lines, FlexGroup flex, float crossSize, float gap)
    {
        if (flex.Wrap == FlexWrap.Nowrap || flex.AlignContent is not (ContentAlign.Normal or ContentAlign.Stretch))
            return;
        var free = crossSize - lines.Sum(l => l.Cross) - gap * (lines.Count - 1);
        if (free <= 0)
            return;
        foreach (var line in lines)
            line.Cross += free / lines.Count;
    }

    private static ItemAlign Alignment(Item item, ComputedStyle container)
    {
        var align = item.Flex.AlignSelf == ItemAlign.Auto ? container.Flex.AlignItems : item.Flex.AlignSelf;
        return align == ItemAlign.Normal ? ItemAlign.Stretch : align;
    }

    // §9.4 step 11: stretched items have an auto cross size and no auto cross margins.
    private static bool IsStretched(Item item, ComputedStyle container)
    {
        var row = container.Flex.Direction is FlexDirection.Row or FlexDirection.RowReverse;
        var crossProperty = row ? item.Box.Style.Size.Height : item.Box.Style.Size.Width;
        return Alignment(item, container) == ItemAlign.Stretch && crossProperty.Kind == SizeKind.Auto && !item.AutoCrossStart && !item.AutoCrossEnd;
    }

    // §9.5 step 12: auto margins, then justify-content.
    private static void DistributeMainAxisSpace(Line line, ContentAlign justify, float mainSize, float gap)
    {
        var items = line.Items;
        var free = mainSize - items.Sum(i => i.OuterMain(i.Target)) - gap * Math.Max(0, items.Count - 1);
        var autoMargins = items.Sum(i => (i.AutoMainStart ? 1 : 0) + (i.AutoMainEnd ? 1 : 0));
        float start = 0, between = gap;
        if (autoMargins > 0 && free > 0)
        {
            var share = free / autoMargins;
            foreach (var item in items)
            {
                if (item.AutoMainStart)
                    item.MarginMainStart = share;
                if (item.AutoMainEnd)
                    item.MarginMainEnd = share;
            }
        }
        else
        {
            var n = items.Count;
            (start, between) = justify switch
            {
                ContentAlign.FlexEnd or ContentAlign.End or ContentAlign.Right => (free, gap),
                ContentAlign.Center => (free / 2, gap),
                ContentAlign.SpaceBetween when n > 1 && free > 0 => (0, gap + free / (n - 1)),
                ContentAlign.SpaceAround when free > 0 => (free / n / 2, gap + free / n),
                ContentAlign.SpaceEvenly when free > 0 => (free / (n + 1), gap + free / (n + 1)),
                ContentAlign.SpaceAround or ContentAlign.SpaceEvenly => (free / 2, gap),
                _ => (0, gap),
            };
        }
        var position = start;
        foreach (var item in items)
        {
            item.MainPosition = position;
            position += item.OuterMain(item.Target) + between;
        }
    }

    // §9.6 step 15: align-content positions the lines.
    private static void DistributeLines(List<Line> lines, FlexGroup flex, float crossSize, float gap)
    {
        var free = crossSize - lines.Sum(l => l.Cross) - gap * Math.Max(0, lines.Count - 1);
        var n = lines.Count;
        var (start, between) = flex.Wrap == FlexWrap.Nowrap ? (0, gap) : flex.AlignContent switch
        {
            ContentAlign.FlexEnd or ContentAlign.End => (free, gap),
            ContentAlign.Center => (free / 2, gap),
            ContentAlign.SpaceBetween when n > 1 && free > 0 => (0, gap + free / (n - 1)),
            ContentAlign.SpaceAround when free > 0 => (free / n / 2, gap + free / n),
            ContentAlign.SpaceEvenly when free > 0 => (free / (n + 1), gap + free / (n + 1)),
            ContentAlign.SpaceAround or ContentAlign.SpaceEvenly => (free / 2, gap),
            _ => (0, gap),
        };
        var position = start;
        foreach (var line in lines)
        {
            line.Position = position;
            position += line.Cross + between;
        }
    }

    // §9.6 steps 13 and 14: auto margins in the cross axis, then align-self.
    private static void AlignInCrossAxis(Item item, Line line, ComputedStyle container)
    {
        var free = line.Cross - item.OuterCross;
        if ((item.AutoCrossStart || item.AutoCrossEnd) && free > 0)
        {
            item.CrossPosition = item.AutoCrossStart && item.AutoCrossEnd ? free / 2 : item.AutoCrossStart ? free : 0;
            return;
        }
        item.CrossPosition = Alignment(item, container) switch
        {
            ItemAlign.FlexEnd or ItemAlign.End or ItemAlign.SelfEnd => free,
            ItemAlign.Center => free / 2,
            ItemAlign.Baseline => line.Items.Where(i => Alignment(i, container) == ItemAlign.Baseline).Max(i => i.Baseline) - item.Baseline,
            _ => 0,
        };
    }

    // An item's first baseline (its first line box's), from its border-box top; null without line boxes.
    private static float? FirstBaseline(Fragment fragment)
    {
        foreach (var child in fragment.Children)
        {
            if (child.Fragment.Kind == FragmentKind.Line && child.Fragment.Height > 0)
                return child.Y + child.Fragment.Baseline;
            if (child.Fragment.Kind == FragmentKind.Box && child.Fragment.Box is { IsFloat: false, IsAbsolutelyPositioned: false }
                && FirstBaseline(child.Fragment) is { } inner)
                return child.Y + inner;
        }
        return null;
    }
}

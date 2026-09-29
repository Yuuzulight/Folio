using Folio.Css;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Grid layout (https://www.w3.org/TR/css-grid-1/, docs/study/08-layout-grid.md, option A): placement (§8.5), the track
/// sizing algorithm (§11) for columns then rows, and alignment, as steps named after the spec.
/// </summary>
// ponytail: named lines and areas act as auto, repeat(auto-fill/auto-fit) and positioned children placed by grid areas
// come next; baseline alignment falls back to start (study 08); an item's min-content contribution is not re-checked
// after the rows are sized.
internal static class GridLayout
{
    private sealed class Item(Box box)
    {
        public Box Box { get; } = box;
        public int Row, RowSpan = 1, Column, ColumnSpan = 1;
        public float MarginTop, MarginRight, MarginBottom, MarginLeft;
        public Fragment? Fragment;
    }

    private sealed class Track(TrackSize size)
    {
        public TrackSize Size { get; } = size;
        public float Base, Limit, Position;
    }

    private enum Constraint { Definite, MinContent, MaxContent }

    public static (List<ChildFragment> Items, float Height, List<Box> OutOfFlow) Layout(
        GridContainerBox box, float innerWidth, float? innerHeight, float minHeight, float maxHeight, LayoutContext context)
    {
        var style = box.Style;
        var (items, columns, rows) = Place(box);
        var columnGap = style.Flex.ColumnGap.Resolve(innerWidth);
        var rowGap = innerHeight is { } h ? style.Flex.RowGap.Resolve(h) : style.Flex.RowGap.HasPercent ? 0 : style.Flex.RowGap.Px;
        foreach (var item in items)
            ResolveMargins(item, innerWidth);

        // §11.3 step 1: columns, from the items' width contributions.
        SizeTracks(columns, innerWidth, Constraint.Definite, columnGap, items, i => (i.Column, i.ColumnSpan),
            i => IntrinsicSizes.Contribution(i.Box, context), style.Flex.JustifyContent);
        Distribute(columns, innerWidth, columnGap, style.Flex.JustifyContent);

        // Step 2: rows, from each item's height at its column area's width.
        (float, float) Height(Item item)
        {
            item.Fragment = LayOutItem(item, AreaSize(columns, item.Column, item.ColumnSpan, columnGap), null, style, context);
            var outer = item.MarginTop + item.Fragment.Height + item.MarginBottom;
            return (outer, outer);
        }
        SizeTracks(rows, innerHeight, innerHeight is null ? Constraint.MaxContent : Constraint.Definite, rowGap, items,
            i => (i.Row, i.RowSpan), Height, style.Flex.AlignContent);
        var height = innerHeight ?? Math.Clamp(Sum(rows, rowGap), minHeight, maxHeight);
        if (innerHeight is null && height > Sum(rows, rowGap) + 0.01f)
        {
            // min-height made the grid taller: its rows are sized again against that height.
            SizeTracks(rows, height, Constraint.Definite, rowGap, items, i => (i.Row, i.RowSpan), Height, style.Flex.AlignContent);
        }
        Distribute(rows, height, rowGap, style.Flex.AlignContent);

        // Items in their areas, with justify-self and align-self.
        var fragments = new List<ChildFragment>(items.Count);
        foreach (var item in items)
        {
            var (areaWidth, areaHeight) = (AreaSize(columns, item.Column, item.ColumnSpan, columnGap), AreaSize(rows, item.Row, item.RowSpan, rowGap));
            var fragment = item.Fragment = LayOutItem(item, areaWidth, areaHeight, style, context);
            var x = columns[item.Column].Position + item.MarginLeft + Offset(item, areaWidth - item.MarginLeft - item.MarginRight - fragment.Width, horizontal: true, style);
            var y = rows[item.Row].Position + item.MarginTop + Offset(item, areaHeight - item.MarginTop - item.MarginBottom - fragment.Height, horizontal: false, style);
            fragments.Add(new ChildFragment(x, y, fragment));
        }
        var outOfFlow = box.Children.Where(c => c.IsAbsolutelyPositioned).ToList();
        return (fragments, innerHeight ?? height, outOfFlow);
    }

    /// <summary>The min-content and max-content widths of a grid container: its columns sized under each constraint.</summary>
    public static (float Min, float Max) IntrinsicWidths(GridContainerBox box, LayoutContext context)
    {
        var style = box.Style;
        var gap = style.Flex.ColumnGap.HasPercent ? 0 : style.Flex.ColumnGap.Px;
        float Measure(Constraint constraint)
        {
            var (items, columns, _) = Place(box);
            SizeTracks(columns, null, constraint, gap, items, i => (i.Column, i.ColumnSpan), i => IntrinsicSizes.Contribution(i.Box, context), style.Flex.JustifyContent);
            return Sum(columns, gap);
        }
        return (Measure(Constraint.MinContent), Measure(Constraint.MaxContent));
    }

    private static float Sum(List<Track> tracks, float gap) => tracks.Sum(t => t.Base) + gap * Math.Max(0, tracks.Count - 1);

    private static float AreaSize(List<Track> tracks, int start, int span, float gap) =>
        tracks.Skip(start).Take(span).Sum(t => t.Base) + gap * (span - 1);

    private static void ResolveMargins(Item item, float innerWidth)
    {
        var spacing = item.Box.Style.Spacing;
        (item.MarginTop, item.MarginRight, item.MarginBottom, item.MarginLeft) = (BlockLayout.Margin(spacing.MarginTop, innerWidth),
            BlockLayout.Margin(spacing.MarginRight, innerWidth), BlockLayout.Margin(spacing.MarginBottom, innerWidth), BlockLayout.Margin(spacing.MarginLeft, innerWidth));
    }

    private static ItemAlign JustifyOf(Item item, ComputedStyle container)
    {
        var align = item.Box.Style.Grid.JustifySelf == ItemAlign.Auto ? container.Grid.JustifyItems : item.Box.Style.Grid.JustifySelf;
        return align == ItemAlign.Normal ? ItemAlign.Stretch : align;
    }

    private static ItemAlign AlignOf(Item item, ComputedStyle container)
    {
        var align = item.Box.Style.Flex.AlignSelf == ItemAlign.Auto ? container.Flex.AlignItems : item.Box.Style.Flex.AlignSelf;
        return align == ItemAlign.Normal ? ItemAlign.Stretch : align;
    }

    // An item laid out in its area (css-align-3 §6): stretched to fill it when its size is auto and its margins are not,
    // otherwise shrunk to fit (width) or as tall as its content (height).
    private static Fragment LayOutItem(Item item, float areaWidth, float? areaHeight, ComputedStyle container, LayoutContext context)
    {
        var style = item.Box.Style;
        var spacing = style.Spacing;
        float? width = null, height = null;
        if (style.Size.Width.Kind == SizeKind.Auto && spacing.MarginLeft.Kind != SizeKind.Auto && spacing.MarginRight.Kind != SizeKind.Auto)
        {
            var outer = Math.Max(0, areaWidth - item.MarginLeft - item.MarginRight);
            if (JustifyOf(item, container) == ItemAlign.Stretch)
            {
                width = outer;
            }
            else
            {
                var frame = style.Border.LeftWidth + style.Border.RightWidth + BlockLayout.Resolve(spacing.PaddingLeft, areaWidth) + BlockLayout.Resolve(spacing.PaddingRight, areaWidth);
                width = IntrinsicSizes.FitContent(item.Box, Math.Max(0, outer - frame), context) + frame;
            }
        }
        if (areaHeight is { } area && style.Size.Height.Kind == SizeKind.Auto && AlignOf(item, container) == ItemAlign.Stretch
            && spacing.MarginTop.Kind != SizeKind.Auto && spacing.MarginBottom.Kind != SizeKind.Auto)
            height = Math.Max(0, area - item.MarginTop - item.MarginBottom);
        return BlockLayout.Layout(item.Box, new ConstraintSpace(areaWidth, areaHeight, FixedWidth: width, FixedHeight: height), context);
    }

    // The offset of an item inside its area: auto margins first, then justify-self or align-self.
    private static float Offset(Item item, float free, bool horizontal, ComputedStyle container)
    {
        var spacing = item.Box.Style.Spacing;
        var (startAuto, endAuto) = horizontal
            ? (spacing.MarginLeft.Kind == SizeKind.Auto, spacing.MarginRight.Kind == SizeKind.Auto)
            : (spacing.MarginTop.Kind == SizeKind.Auto, spacing.MarginBottom.Kind == SizeKind.Auto);
        if (startAuto || endAuto)
            return free <= 0 ? 0 : startAuto && endAuto ? free / 2 : startAuto ? free : 0;
        return (horizontal ? JustifyOf(item, container) : AlignOf(item, container)) switch
        {
            ItemAlign.End or ItemAlign.FlexEnd or ItemAlign.SelfEnd => free,
            ItemAlign.Center => free / 2,
            _ => 0,
        };
    }

    // §8.5: items in order-modified document order, placed on an occupancy map; the grid grows with them.
    private static (List<Item> Items, List<Track> Columns, List<Track> Rows) Place(GridContainerBox box)
    {
        var grid = box.Style.Grid;
        var items = box.Children.Where(c => !c.IsAbsolutelyPositioned).Select(c => new Item(c)).OrderBy(i => i.Box.Style.Flex.Order).ToList();
        var (explicitColumns, explicitRows) = (grid.TemplateColumns.Tracks.Count, grid.TemplateRows.Tracks.Count);

        var resolved = items.Select(i =>
        {
            var g = i.Box.Style.Grid;
            return (Item: i, Row: ResolveLines(g.RowStart, g.RowEnd, explicitRows), Column: ResolveLines(g.ColumnStart, g.ColumnEnd, explicitColumns));
        }).ToList();
        // Implicit tracks before the explicit grid shift every line.
        var rowOffset = -Math.Min(0, resolved.Where(r => r.Row.Start is not null).Select(r => r.Row.Start!.Value).DefaultIfEmpty(0).Min());
        var columnOffset = -Math.Min(0, resolved.Where(r => r.Column.Start is not null).Select(r => r.Column.Start!.Value).DefaultIfEmpty(0).Min());

        // The flow axis is the major one; the other has a fixed count while placing.
        var columnFlow = grid.AutoFlowColumn;
        var minorCount = columnFlow
            ? Math.Max(explicitRows + rowOffset, resolved.Count == 0 ? 0 : resolved.Max(r => r.Row.Start is { } s ? s + rowOffset + r.Row.Span : r.Row.Span))
            : Math.Max(explicitColumns + columnOffset, resolved.Count == 0 ? 0 : resolved.Max(r => r.Column.Start is { } s ? s + columnOffset + r.Column.Span : r.Column.Span));
        minorCount = Math.Clamp(minorCount, 1, GridParsing.MaxTracks);

        var occupied = new HashSet<(int Major, int Minor)>();
        bool Fits(int major, int minor, int majorSpan, int minorSpan)
        {
            if (minor < 0 || minor + minorSpan > minorCount)
                return false;
            for (var a = major; a < major + majorSpan; a++)
            {
                for (var b = minor; b < minor + minorSpan; b++)
                {
                    if (occupied.Contains((a, b)))
                        return false;
                }
            }
            return true;
        }
        void Occupy(Item item, int major, int minor, int majorSpan, int minorSpan)
        {
            major = Math.Min(major, GridParsing.MaxTracks - 1);
            for (var a = major; a < major + majorSpan; a++)
            {
                for (var b = minor; b < minor + minorSpan; b++)
                    occupied.Add((a, b));
            }
            if (columnFlow)
                (item.Column, item.ColumnSpan, item.Row, item.RowSpan) = (major, majorSpan, minor, minorSpan);
            else
                (item.Row, item.RowSpan, item.Column, item.ColumnSpan) = (major, majorSpan, minor, minorSpan);
        }

        var placements = resolved.Select(r =>
        {
            var (major, minor) = columnFlow ? (r.Column, r.Row) : (r.Row, r.Column);
            var (majorOffset, minorOffset) = columnFlow ? (columnOffset, rowOffset) : (rowOffset, columnOffset);
            return (r.Item, MajorStart: major.Start + majorOffset, MajorSpan: Math.Min(major.Span, GridParsing.MaxTracks),
                    MinorStart: minor.Start + minorOffset, MinorSpan: Math.Min(minor.Span, minorCount));
        }).ToList();

        // Step 1: fully definite items. Step 2: items locked to a major line.
        foreach (var p in placements.Where(p => p.MajorStart is not null && p.MinorStart is not null))
            Occupy(p.Item, p.MajorStart!.Value, p.MinorStart!.Value, p.MajorSpan, p.MinorSpan);
        var lineCursor = new Dictionary<int, int>();
        foreach (var p in placements.Where(p => p.MajorStart is not null && p.MinorStart is null))
        {
            var major = p.MajorStart!.Value;
            var minor = grid.Dense ? 0 : lineCursor.GetValueOrDefault(major);
            while (minor + p.MinorSpan <= minorCount && !Fits(major, minor, p.MajorSpan, p.MinorSpan))
                minor++;
            if (minor + p.MinorSpan > minorCount)
                minor = 0; // no room: overlaps rather than growing the fixed axis
            Occupy(p.Item, major, minor, p.MajorSpan, p.MinorSpan);
            lineCursor[major] = minor + p.MinorSpan;
        }
        // Step 4: the rest, with the auto-placement cursor.
        var (cursorMajor, cursorMinor) = (0, 0);
        foreach (var p in placements.Where(p => p.MajorStart is null))
        {
            if (grid.Dense)
                (cursorMajor, cursorMinor) = (0, 0);
            if (p.MinorStart is { } minorStart)
            {
                if (minorStart < cursorMinor && !grid.Dense)
                    cursorMajor++;
                cursorMinor = minorStart;
                while (!Fits(cursorMajor, cursorMinor, p.MajorSpan, p.MinorSpan) && cursorMajor < GridParsing.MaxTracks)
                    cursorMajor++;
            }
            else
            {
                while (!Fits(cursorMajor, cursorMinor, p.MajorSpan, p.MinorSpan) && cursorMajor < GridParsing.MaxTracks)
                {
                    cursorMinor++;
                    if (cursorMinor + p.MinorSpan > minorCount)
                        (cursorMajor, cursorMinor) = (cursorMajor + 1, 0);
                }
            }
            Occupy(p.Item, cursorMajor, cursorMinor, p.MajorSpan, p.MinorSpan);
        }

        var rowCount = Math.Max(explicitRows + rowOffset, items.Count == 0 ? 0 : items.Max(i => i.Row + i.RowSpan));
        var columnCount = Math.Max(explicitColumns + columnOffset, items.Count == 0 ? 0 : items.Max(i => i.Column + i.ColumnSpan));
        return (items, Tracks(grid.TemplateColumns.Tracks, grid.AutoColumns, columnOffset, Math.Min(columnCount, GridParsing.MaxTracks)),
                Tracks(grid.TemplateRows.Tracks, grid.AutoRows, rowOffset, Math.Min(rowCount, GridParsing.MaxTracks)));
    }

    // §8.3.1: a start line (0-based, from the explicit grid's first line; null when auto) and a span.
    private static (int? Start, int Span) ResolveLines(GridLine start, GridLine end, int explicitCount)
    {
        int? Line(GridLine line) => line is { Kind: GridLineKind.Line, Number: not 0 } l
            ? l.Number > 0 ? l.Number - 1 : explicitCount + 1 + l.Number
            : null; // auto, and (for now) named lines
        var (s, e) = (Line(start), Line(end));
        if (s is { } a && e is { } b)
            return b == a ? (a, 1) : (Math.Min(a, b), Math.Abs(b - a));
        if (s is { } a2)
            return (a2, end.Kind == GridLineKind.Span ? end.Number : 1);
        if (e is { } b2)
        {
            var span = start.Kind == GridLineKind.Span ? start.Number : 1;
            return (b2 - span, span);
        }
        return (null, start.Kind == GridLineKind.Span ? start.Number : end.Kind == GridLineKind.Span ? end.Number : 1);
    }

    // Explicit tracks from the template, implicit ones from grid-auto-rows/columns repeated in both directions.
    private static List<Track> Tracks(IReadOnlyList<TrackSize> template, IReadOnlyList<TrackSize> auto, int offset, int count)
    {
        var tracks = new List<Track>(count);
        for (var i = 0; i < count; i++)
        {
            var e = i - offset;
            var size = e >= 0 && e < template.Count ? template[e]
                : e >= template.Count ? auto[(e - template.Count) % auto.Count]
                : auto[((e % auto.Count) + auto.Count) % auto.Count];
            tracks.Add(new Track(size));
        }
        return tracks;
    }

    // §11.3 to §11.8 for one axis.
    private static void SizeTracks(List<Track> tracks, float? available, Constraint constraint, float gap, List<Item> items,
                                   Func<Item, (int Start, int Span)> span, Func<Item, (float Min, float Max)> contribution, ContentAlign alignment)
    {
        if (tracks.Count == 0)
            return;
        // Percentages of an indefinite size behave as auto.
        TrackBreadth Usable(TrackBreadth b) => b.Kind is TrackKind.Length or TrackKind.FitContent && b.Length.HasPercent && available is null
            ? new TrackBreadth(TrackKind.Auto) : b;
        float Resolve(TrackBreadth b) => b.Length.Resolve(available ?? 0);

        // §11.4 Initialize Track Sizes.
        foreach (var track in tracks)
        {
            var (min, max) = (Usable(track.Size.Min), Usable(track.Size.Max));
            track.Base = min.Kind == TrackKind.Length ? Resolve(min) : 0;
            track.Limit = max.Kind == TrackKind.Length ? Resolve(max) : float.PositiveInfinity;
            track.Limit = Math.Max(track.Limit, track.Base);
        }

        // §11.5 Resolve Intrinsic Track Sizes: items by increasing span, those crossing flexible tracks last.
        var measured = items.Select(i => (Item: i, Span: span(i))).Where(x => x.Span.Start < tracks.Count).ToList();
        bool CrossesFlex((int Start, int Span) s) => tracks.Skip(s.Start).Take(s.Span).Any(t => t.Size.Max.Kind == TrackKind.Flex);
        var sizes = new Dictionary<Item, (float Min, float Max)>();
        foreach (var (item, s) in measured.OrderBy(x => x.Span.Span))
        {
            var spanned = tracks.Skip(s.Start).Take(s.Span).ToList();
            var (min, max) = sizes[item] = contribution(item);
            if (CrossesFlex(s))
                continue;
            var gaps = gap * (spanned.Count - 1);
            // Base sizes of tracks with an intrinsic minimum.
            var intrinsicMin = spanned.Where(t => Usable(t.Size.Min).IsIntrinsic).ToList();
            var needed = (constraint == Constraint.MinContent || intrinsicMin.All(t => Usable(t.Size.Min).Kind != TrackKind.MaxContent) ? min : max)
                - spanned.Sum(t => t.Base) - gaps;
            if (needed > 0 && intrinsicMin.Count > 0)
            {
                foreach (var t in intrinsicMin)
                    t.Base += needed / intrinsicMin.Count;
            }
            // Growth limits of tracks with an intrinsic maximum.
            var intrinsicMax = spanned.Where(t => Usable(t.Size.Max).IsIntrinsic).ToList();
            if (intrinsicMax.Count > 0)
            {
                var target = intrinsicMax.All(t => Usable(t.Size.Max).Kind == TrackKind.MinContent) ? min : max;
                if (spanned.Count == 1 && Usable(spanned[0].Size.Max) is { Kind: TrackKind.FitContent } fit)
                    target = Math.Max(min, Math.Min(max, Resolve(fit)));
                var current = spanned.Sum(t => float.IsPositiveInfinity(t.Limit) ? t.Base : t.Limit) + gaps;
                foreach (var t in intrinsicMax.Where(t => float.IsPositiveInfinity(t.Limit)))
                    t.Limit = t.Base;
                if (target > current)
                {
                    foreach (var t in intrinsicMax)
                        t.Limit += (target - current) / intrinsicMax.Count;
                }
            }
            foreach (var t in spanned)
                t.Limit = Math.Max(t.Limit, t.Base);
        }
        // §11.5 step 4: items crossing flexible tracks grow those tracks' base sizes in proportion to their factors.
        foreach (var (item, s) in measured.Where(x => CrossesFlex(x.Span)))
        {
            var spanned = tracks.Skip(s.Start).Take(s.Span).ToList();
            var flexible = spanned.Where(t => t.Size.Max.Kind == TrackKind.Flex).ToList();
            var needed = sizes[item].Min - spanned.Sum(t => t.Base) - gap * (spanned.Count - 1);
            var factors = flexible.Sum(t => t.Size.Max.Flex);
            if (needed > 0)
            {
                foreach (var t in flexible)
                    t.Base += factors > 0 ? needed * t.Size.Max.Flex / factors : needed / flexible.Count;
            }
        }
        foreach (var t in tracks.Where(t => float.IsPositiveInfinity(t.Limit)))
            t.Limit = t.Base;

        // §11.6 Maximize Tracks: free space shared up to the growth limits (all of it under max-content).
        var free = constraint switch
        {
            Constraint.MaxContent => float.PositiveInfinity,
            Constraint.MinContent => 0,
            _ => available!.Value - Sum(tracks, gap),
        };
        if (float.IsPositiveInfinity(free))
        {
            foreach (var t in tracks)
                t.Base = Math.Max(t.Base, t.Limit);
        }
        else
        {
            for (var round = 0; free > 0.001f && round < tracks.Count; round++)
            {
                var growing = tracks.Where(t => t.Limit > t.Base + 0.001f).ToList();
                if (growing.Count == 0)
                    break;
                var share = free / growing.Count;
                foreach (var t in growing)
                {
                    var add = Math.Min(share, t.Limit - t.Base);
                    t.Base += add;
                    free -= add;
                }
            }
        }

        // §11.7 Expand Flexible Tracks.
        var flexTracks = tracks.Where(t => t.Size.Max.Kind == TrackKind.Flex).ToList();
        if (flexTracks.Count > 0 && constraint != Constraint.MinContent)
        {
            float fraction;
            if (constraint == Constraint.Definite)
            {
                fraction = FlexFraction(tracks, available!.Value - gap * (tracks.Count - 1));
            }
            else
            {
                fraction = flexTracks.Max(t => t.Size.Max.Flex > 1 ? t.Base / t.Size.Max.Flex : t.Base);
                foreach (var (item, s) in measured.Where(x => CrossesFlex(x.Span)))
                {
                    var spanned = tracks.Skip(s.Start).Take(s.Span).ToList();
                    fraction = Math.Max(fraction, FlexFraction(spanned, sizes[item].Max - gap * (spanned.Count - 1)));
                }
            }
            foreach (var t in flexTracks)
                t.Base = Math.Max(t.Base, fraction * t.Size.Max.Flex);
        }

        // §11.8 Stretch auto Tracks: with normal or stretch content alignment and a definite size.
        if (constraint == Constraint.Definite && alignment is ContentAlign.Normal or ContentAlign.Stretch)
        {
            var remaining = available!.Value - Sum(tracks, gap);
            var autoTracks = tracks.Where(t => Usable(t.Size.Max).Kind == TrackKind.Auto).ToList();
            if (remaining > 0 && autoTracks.Count > 0)
            {
                foreach (var t in autoTracks)
                    t.Base += remaining / autoTracks.Count;
            }
        }
    }

    // §11.7.1 Find the Size of an fr: tracks too large for their share count as inflexible, until none are.
    private static float FlexFraction(List<Track> tracks, float space)
    {
        var inflexible = new HashSet<Track>(tracks.Where(t => t.Size.Max.Kind != TrackKind.Flex));
        while (true)
        {
            var leftover = space - inflexible.Sum(t => t.Base);
            var factors = tracks.Where(t => !inflexible.Contains(t)).Sum(t => t.Size.Max.Flex);
            var fraction = Math.Max(0, leftover / Math.Max(1, factors));
            var tooBig = tracks.Where(t => !inflexible.Contains(t) && t.Base > fraction * t.Size.Max.Flex).ToList();
            if (tooBig.Count == 0)
                return fraction;
            inflexible.UnionWith(tooBig);
        }
    }

    // justify-content / align-content over the tracks (css-align-3 §5.1), giving each track its position.
    private static void Distribute(List<Track> tracks, float size, float gap, ContentAlign alignment)
    {
        var free = size - Sum(tracks, gap);
        var n = tracks.Count;
        var (start, between) = alignment switch
        {
            ContentAlign.End or ContentAlign.FlexEnd or ContentAlign.Right => (free, gap),
            ContentAlign.Center => (free / 2, gap),
            ContentAlign.SpaceBetween when n > 1 && free > 0 => (0, gap + free / (n - 1)),
            ContentAlign.SpaceAround when free > 0 => (free / n / 2, gap + free / n),
            ContentAlign.SpaceEvenly when free > 0 => (free / (n + 1), gap + free / (n + 1)),
            _ => (0, gap),
        };
        var position = start;
        foreach (var t in tracks)
        {
            t.Position = position;
            position += t.Base + between;
        }
    }
}

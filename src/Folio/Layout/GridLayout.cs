using Folio.Css;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Tracks a subgrid takes from its parent grid in one axis (https://www.w3.org/TR/css-grid-2/#subgrid-sizing): fixed
/// tracks of the parent's sizes, less the subgrid's margin, border and padding at the edges so its lines meet the
/// parent's, the parent's line names with its own, and the parent's gap.
/// </summary>
internal sealed record AdoptedTracks(TrackList List, float Gap);

/// <summary>
/// Grid layout (https://www.w3.org/TR/css-grid-1/, docs/study/08-layout-grid.md, option A): placement (§8.5), the track
/// sizing algorithm (§11) for columns then rows, and alignment, as steps named after the spec.
/// </summary>
// ponytail: baseline alignment falls back to start (study 08); an item's min-content contribution is not re-checked
// after the rows are sized; with an indefinite height, auto-fill rows repeat to fit max-height, or once.
internal static class GridLayout
{
    private sealed class Item(Box box)
    {
        public Box Box { get; } = box;
        public int Row, RowSpan = 1, Column, ColumnSpan = 1;
        public float MarginTop, MarginRight, MarginBottom, MarginLeft;
        public Fragment Fragment; // default until laid out
    }

    private sealed class Track(TrackSize size)
    {
        public TrackSize Size { get; } = size;
        public float Base, Limit, Position;
        public bool Collapsed; // an empty auto-fit track: no size and no gaps
    }

    /// <summary>
    /// One axis of the explicit grid: its tracks and line names with auto-fill/auto-fit repetitions expanded, the
    /// implicit names of named areas added, and which tracks came from auto-fit. Count is at least the areas' extent.
    /// </summary>
    private sealed record Axis(List<TrackSize> Tracks, List<List<string>> Names, HashSet<int> AutoFit, int Count);

    private static Axis BuildAxis(TrackList list, GridAreas areas, bool rows, float? space, float gap)
    {
        // subgrid, where no parent grid lends tracks, is none.
        if (list.Subgrid)
            list = TrackList.None;
        var tracks = new List<TrackSize>();
        var names = new List<List<string>>();
        var autoFit = new HashSet<int>();
        var repetitions = list.Repeat is { } repeat && space is { } s ? Repetitions(list, repeat, s, gap) : 1;
        for (var i = 0; i <= list.Tracks.Count; i++)
        {
            List<string> line = [.. list.LineNames[i]];
            if (list.Repeat is { } r && r.Index == i)
            {
                // The repeated block's first names join the line before it, its last ones the line after it.
                for (var k = 0; k < repetitions; k++)
                {
                    for (var t = 0; t < r.Tracks.Count; t++)
                    {
                        List<string> before = t == 0 && k == 0 ? [.. r.Names[0]]
                            : t == 0 ? [.. r.Names[^1], .. r.Names[0]] : [.. r.Names[t]];
                        names.Add(before);
                        if (r.Fit)
                            autoFit.Add(tracks.Count);
                        tracks.Add(r.Tracks[t]);
                    }
                }
                line = [.. r.Names[^1], .. line];
            }
            if (i < list.Tracks.Count)
            {
                names.Add(line);
                tracks.Add(list.Tracks[i]);
            }
            else
            {
                names.Add(line);
            }
        }
        var count = Math.Max(tracks.Count, rows ? areas.Rows : areas.Columns);
        while (names.Count < count + 1)
            names.Add([]);
        foreach (var (name, area) in areas.Areas)
        {
            var (start, end) = rows ? (area.RowStart, area.RowEnd) : (area.ColumnStart, area.ColumnEnd);
            names[start].Add(name + "-start");
            names[end].Add(name + "-end");
        }
        return new Axis(tracks, names, autoFit, count);
    }

    // How many times repeat(auto-fill | auto-fit) fits (css-grid-1 §7.2.3.2): at least once; every track has a fixed size.
    private static int Repetitions(TrackList list, AutoRepeat repeat, float space, float gap)
    {
        static float Fixed(TrackSize t, float basis) => t.Max.Kind == TrackKind.Length ? t.Max.Length.Resolve(basis)
            : t.Min.Kind == TrackKind.Length ? t.Min.Length.Resolve(basis) : 0;
        var others = list.Tracks.Sum(t => Fixed(t, space));
        var each = repeat.Tracks.Sum(t => Fixed(t, space)) + gap * repeat.Tracks.Count;
        var room = space - others - gap * (list.Tracks.Count - 1);
        return each <= 0 ? 1 : Math.Clamp((int)Math.Floor((room + 0.001f) / each), 1, GridParsing.MaxTracks / repeat.Tracks.Count);
    }

    private enum Constraint { Definite, MinContent, MaxContent }

    public static (List<ChildFragment> Items, float Height, List<(Box Box, float? Left, float? Top, float? Right, float? Bottom)> Positioned) Layout(
        GridContainerBox box, float innerWidth, float? innerHeight, float minHeight, float maxHeight, LayoutContext context)
    {
        var style = box.Style;
        // As a subgrid, the grid takes its tracks in an axis from its parent, which has sized them (css-grid-2 §9).
        context.Subgrids.TryGetValue(box, out var adopted);
        var columnGap = adopted.Columns?.Gap ?? style.Flex.ColumnGap.Resolve(innerWidth);
        var rowGap = adopted.Rows?.Gap ?? (innerHeight is { } h ? style.Flex.RowGap.Resolve(h) : style.Flex.RowGap.HasPercent ? 0 : style.Flex.RowGap.Px);
        var columnAlign = adopted.Columns is null ? style.Flex.JustifyContent : ContentAlign.Start;
        var rowAlign = adopted.Rows is null ? style.Flex.AlignContent : ContentAlign.Start;
        var columnAxis = BuildAxis(adopted.Columns?.List ?? style.Grid.TemplateColumns, style.Grid.Areas, rows: false, innerWidth, columnGap);
        var rowAxis = BuildAxis(adopted.Rows?.List ?? style.Grid.TemplateRows, style.Grid.Areas, rows: true, innerHeight ?? (float.IsFinite(maxHeight) ? maxHeight : null), rowGap);
        var (items, columns, rows, offsets) = Place(box, columnAxis, rowAxis);
        if (adopted.Columns is not null)
            KeepWithin(items, columns, columnAxis.Count, rows: false);
        if (adopted.Rows is not null)
            KeepWithin(items, rows, rowAxis.Count, rows: true);
        foreach (var item in items)
            ResolveMargins(item, innerWidth);

        // Subgrid items lend their own items to the tracks they span in the axes they adopt, with the subgrid's margin,
        // border and padding added at its edges (css-grid-2 §9.4); the subgrid itself does not contribute there.
        // A row subgrid is laid out once to measure its items, where any other item is laid out to measure its height,
        // so nested subgrids cost what nested grids do.
        var subgrids = items.Where(i => Subgridded(i, rows: false) || Subgridded(i, rows: true)).ToList();
        var lent = new Dictionary<Item, float>();
        var (columnItems, rowItems) = (items.Where(i => !Subgridded(i, rows: false)).ToList(), items.Where(i => !Subgridded(i, rows: true)).ToList());
        var subgridItems = new Dictionary<Item, List<Item>>();
        foreach (var s in subgrids)
        {
            context.Subgrids.Remove(s.Box);
            subgridItems[s] = PlaceSubgrid(s, columnAxis, rowAxis, offsets);
            if (!Subgridded(s, rows: false))
                continue;
            var (start, end) = Frame(s, rows: false, innerWidth);
            foreach (var c in subgridItems[s])
            {
                var proxy = new Item(c.Box) { Column = s.Column + c.Column, ColumnSpan = c.ColumnSpan, Row = s.Row, RowSpan = s.RowSpan };
                lent[proxy] = (c.Column == 0 ? start : 0) + (c.Column + c.ColumnSpan == s.ColumnSpan ? end : 0);
                columnItems.Add(proxy);
            }
        }

        // §11.3 step 1: columns, from the items' width contributions.
        (float, float) Width(Item item)
        {
            var (min, max) = IntrinsicSizes.Contribution(item.Box, context);
            var e = lent.GetValueOrDefault(item);
            return (min + e, max + e);
        }
        SizeTracks(columns, innerWidth, Constraint.Definite, columnGap, columnItems, i => (i.Column, i.ColumnSpan), Width, columnAlign);
        Distribute(columns, innerWidth, columnGap, columnAlign);
        foreach (var s in subgrids.Where(s => Subgridded(s, rows: false)))
            context.Subgrids[s.Box] = (Lend(s, columns, columnAxis, offsets.Column, columnGap, innerWidth, rows: false), null);

        // Row subgrids' items, each at its height in the subgrid laid out at its width.
        var lentHeights = new Dictionary<Item, float>();
        foreach (var s in subgrids.Where(s => Subgridded(s, rows: true)))
        {
            var spacing = s.Box.Style.Spacing;
            var border = s.Box.Style.Border;
            var area = AreaSize(columns, s.Column, s.ColumnSpan, columnGap);
            var inner = Math.Max(0, area - s.MarginLeft - s.MarginRight - border.LeftWidth - border.RightWidth
                - BlockLayout.Resolve(spacing.PaddingLeft, area) - BlockLayout.Resolve(spacing.PaddingRight, area));
            var measured = new Dictionary<Box, Fragment>();
            foreach (var f in Layout((GridContainerBox)s.Box, inner, null, 0, float.PositiveInfinity, context).Items)
            {
                if (f.Fragment.Box is { } b)
                    measured[b] = f.Fragment;
            }
            var (start, end) = Frame(s, rows: true, innerWidth);
            foreach (var c in subgridItems[s])
            {
                if (!measured.TryGetValue(c.Box, out var fragment))
                    continue;
                var margins = BlockLayout.Margin(c.Box.Style.Spacing.MarginTop, inner) + BlockLayout.Margin(c.Box.Style.Spacing.MarginBottom, inner);
                var proxy = new Item(c.Box) { Row = s.Row + c.Row, RowSpan = c.RowSpan, Column = s.Column, ColumnSpan = s.ColumnSpan };
                lentHeights[proxy] = fragment.Height + margins + (c.Row == 0 ? start : 0) + (c.Row + c.RowSpan == s.RowSpan ? end : 0);
                rowItems.Add(proxy);
            }
        }

        // Step 2: rows, from each item's height at its column area's width.
        (float, float) Height(Item item)
        {
            if (lentHeights.TryGetValue(item, out var lentHeight))
                return (lentHeight, lentHeight);
            item.Fragment = LayOutItem(item, AreaSize(columns, item.Column, item.ColumnSpan, columnGap), null, style, context);
            var outer = item.MarginTop + item.Fragment.Height + item.MarginBottom;
            return (outer, outer);
        }
        SizeTracks(rows, innerHeight, innerHeight is null ? Constraint.MaxContent : Constraint.Definite, rowGap, rowItems,
            i => (i.Row, i.RowSpan), Height, rowAlign);
        var height = innerHeight ?? Math.Clamp(Sum(rows, rowGap), minHeight, maxHeight);
        if (innerHeight is null && height > Sum(rows, rowGap) + 0.01f)
        {
            // min-height made the grid taller: its rows are sized again against that height.
            SizeTracks(rows, height, Constraint.Definite, rowGap, rowItems, i => (i.Row, i.RowSpan), Height, rowAlign);
        }
        Distribute(rows, height, rowGap, rowAlign);
        foreach (var s in subgrids.Where(s => Subgridded(s, rows: true)))
            context.Subgrids[s.Box] = (context.Subgrids.GetValueOrDefault(s.Box).Columns, Lend(s, rows, rowAxis, offsets.Row, rowGap, innerWidth, rows: true));

        // Items in their areas, with justify-self and align-self.
        var fragments = new List<ChildFragment>(items.Count);
        foreach (var item in items)
        {
            var (areaWidth, areaHeight) = (AreaSize(columns, item.Column, item.ColumnSpan, columnGap), AreaSize(rows, item.Row, item.RowSpan, rowGap));
            var fragment = item.Fragment = LayOutItem(item, areaWidth, areaHeight, style, context);
            var x = columns[item.Column].Position + item.MarginLeft + Offset(item, areaWidth - item.MarginLeft - item.MarginRight - fragment.Width, horizontal: true, style);
            var y = rows[item.Row].Position + item.MarginTop + Offset(item, areaHeight - item.MarginTop - item.MarginBottom - fragment.Height, horizontal: false, style);
            // Columns run from the inline start: right to left in a right-to-left grid (css-grid-1 §7.1).
            if (style.Text.Direction == Direction.Rtl)
                x = innerWidth - x - fragment.Width;
            fragments.Add(new ChildFragment(x, y, fragment));
        }
        // Positioned children: the area their lines name (css-grid-1 §9.1); auto lines are the padding edges (null).
        var positioned = new List<(Box Box, float? Left, float? Top, float? Right, float? Bottom)>();
        foreach (var child in box.Children.Where(c => c.IsAbsolutelyPositioned))
        {
            var g = child.Style.Grid;
            var (columnStart, columnEnd) = AbsoluteLines(g.ColumnStart, g.ColumnEnd, columnAxis);
            var (rowStart, rowEnd) = AbsoluteLines(g.RowStart, g.RowEnd, rowAxis);
            positioned.Add((child, Edge(columns, columnStart, offsets.Column, start: true), Edge(rows, rowStart, offsets.Row, start: true),
                Edge(columns, columnEnd, offsets.Column, start: false), Edge(rows, rowEnd, offsets.Row, start: false)));
        }
        return (fragments, innerHeight ?? height, positioned);
    }

    // Whether an item is a subgrid in an axis: a grid whose template there is subgrid.
    private static bool Subgridded(Item item, bool rows) =>
        item.Box is GridContainerBox { Style.Grid: var g } && (rows ? g.TemplateRows : g.TemplateColumns).Subgrid;

    // A subgrid's margin, border and padding at the start and end of an axis.
    private static (float Start, float End) Frame(Item s, bool rows, float basis)
    {
        var (spacing, border) = (s.Box.Style.Spacing, s.Box.Style.Border);
        return rows
            ? (s.MarginTop + border.TopWidth + BlockLayout.Resolve(spacing.PaddingTop, basis), s.MarginBottom + border.BottomWidth + BlockLayout.Resolve(spacing.PaddingBottom, basis))
            : (s.MarginLeft + border.LeftWidth + BlockLayout.Resolve(spacing.PaddingLeft, basis), s.MarginRight + border.RightWidth + BlockLayout.Resolve(spacing.PaddingRight, basis));
    }

    // The names of a subgrid's lines in an axis it adopts: the parent's names of the lines it spans, and its own
    // (css-grid-2 §9.3).
    private static List<List<string>> SubgridNames(Item s, Axis parent, int offset, bool rows)
    {
        var (start, span) = rows ? (s.Row, s.RowSpan) : (s.Column, s.ColumnSpan);
        var own = rows ? s.Box.Style.Grid.TemplateRows.LineNames : s.Box.Style.Grid.TemplateColumns.LineNames;
        var names = new List<List<string>>(span + 1);
        for (var i = 0; i <= span; i++)
        {
            var line = start - offset + i;
            List<string> merged = line >= 0 && line < parent.Names.Count ? [.. parent.Names[line]] : [];
            if (i < own.Count)
                merged.AddRange(own[i]);
            names.Add(merged);
        }
        return names;
    }

    // A subgrid's items placed in its own grid, with as many tracks as it spans in the axes it adopts; items that would
    // make implicit tracks there stay inside (css-grid-2 §9.2).
    private static List<Item> PlaceSubgrid(Item s, Axis columnAxis, Axis rowAxis, (int Row, int Column) offsets)
    {
        var sub = (GridContainerBox)s.Box;
        var g = sub.Style.Grid;
        Axis Lent(bool rows)
        {
            var span = rows ? s.RowSpan : s.ColumnSpan;
            return new Axis([.. Enumerable.Repeat(TrackSize.Auto, span)], SubgridNames(s, rows ? rowAxis : columnAxis, rows ? offsets.Row : offsets.Column, rows), [], span);
        }
        var columns = Subgridded(s, rows: false) ? Lent(rows: false) : BuildAxis(g.TemplateColumns, g.Areas, rows: false, null, 0);
        var rowsAxis = Subgridded(s, rows: true) ? Lent(rows: true) : BuildAxis(g.TemplateRows, g.Areas, rows: true, null, 0);
        var (items, columnTracks, rowTracks, _) = Place(sub, columns, rowsAxis);
        if (Subgridded(s, rows: false))
            KeepWithin(items, columnTracks, s.ColumnSpan, rows: false);
        if (Subgridded(s, rows: true))
            KeepWithin(items, rowTracks, s.RowSpan, rows: true);
        return items;
    }

    // Items moved and shortened to fit within the first count tracks of an axis, and the tracks past them dropped.
    private static void KeepWithin(List<Item> items, List<Track> tracks, int count, bool rows)
    {
        count = Math.Max(1, count);
        foreach (var item in items)
        {
            if (rows)
                (item.Row, item.RowSpan) = (Math.Min(item.Row, count - 1), Math.Max(1, Math.Min(item.RowSpan, count - Math.Min(item.Row, count - 1))));
            else
                (item.Column, item.ColumnSpan) = (Math.Min(item.Column, count - 1), Math.Max(1, Math.Min(item.ColumnSpan, count - Math.Min(item.Column, count - 1))));
        }
        if (tracks.Count > count)
            tracks.RemoveRange(count, tracks.Count - count);
    }

    // The tracks a subgrid takes in an axis: fixed tracks of the sizes it spans, less its margin, border and padding at
    // its edges so that its lines meet the parent's; the parent's line names with its own; the parent's gap.
    // ponytail: space that align-content or justify-content put between the parent's tracks is not lent, and the
    // subgrid's own gap is ignored.
    private static AdoptedTracks Lend(Item s, List<Track> tracks, Axis axis, int offset, float gap, float basis, bool rows)
    {
        var (start, span) = rows ? (s.Row, s.RowSpan) : (s.Column, s.ColumnSpan);
        var sizes = tracks.Skip(start).Take(span).Select(t => t.Base).ToList();
        var (frameStart, frameEnd) = Frame(s, rows, basis);
        if (sizes.Count > 0)
        {
            sizes[0] -= frameStart;
            sizes[^1] -= frameEnd;
        }
        var fixedTracks = sizes.Select(size => new TrackBreadth(TrackKind.Length, new LengthPercentage(Math.Max(0, size))))
            .Select(b => new TrackSize(b, b)).ToList();
        var names = SubgridNames(s, axis, offset, rows);
        return new AdoptedTracks(new TrackList(fixedTracks, [.. names.Take(fixedTracks.Count + 1)]), gap);
    }

    // A positioned child's lines, without auto-placement: auto (and a span against auto) stays null.
    private static (int? Start, int? End) AbsoluteLines(GridLine start, GridLine end, Axis axis)
    {
        var (s, e) = (LineIndex(start, axis, isStart: true), LineIndex(end, axis, isStart: false));
        if (s is { } a && e is null && end.Kind == GridLineKind.Span)
            e = SpanFrom(end, axis, a, forward: true);
        if (e is { } b && s is null && start.Kind == GridLineKind.Span)
            s = SpanFrom(start, axis, b, forward: false);
        return (s, e);
    }

    // The position of a grid line in the content box, or null for auto; lines outside the grid clamp to its edges.
    private static float? Edge(List<Track> tracks, int? line, int offset, bool start)
    {
        if (line is not { } l || tracks.Count == 0)
            return null;
        var index = Math.Clamp(l + offset, 0, tracks.Count);
        return index == tracks.Count ? tracks[^1].Position + tracks[^1].Base
            : start || index == 0 ? tracks[index].Position : tracks[index - 1].Position + tracks[index - 1].Base;
    }

    /// <summary>The min-content and max-content widths of a grid container: its columns sized under each constraint.</summary>
    public static (float Min, float Max) IntrinsicWidths(GridContainerBox box, LayoutContext context)
    {
        var style = box.Style;
        var gap = style.Flex.ColumnGap.HasPercent ? 0 : style.Flex.ColumnGap.Px;
        float Measure(Constraint constraint)
        {
            var axis = BuildAxis(style.Grid.TemplateColumns, style.Grid.Areas, rows: false, null, gap);
            var (items, columns, _, _) = Place(box, axis, BuildAxis(style.Grid.TemplateRows, style.Grid.Areas, rows: true, null, 0));
            SizeTracks(columns, null, constraint, gap, items, i => (i.Column, i.ColumnSpan), i => IntrinsicSizes.Contribution(i.Box, context), style.Flex.JustifyContent);
            return Sum(columns, gap);
        }
        return (Measure(Constraint.MinContent), Measure(Constraint.MaxContent));
    }

    // Gaps sit between tracks that are not collapsed.
    private static float Sum(List<Track> tracks, float gap) =>
        tracks.Sum(t => t.Base) + gap * Math.Max(0, tracks.Count(t => !t.Collapsed) - 1);

    private static float AreaSize(List<Track> tracks, int start, int span, float gap)
    {
        var area = tracks.Skip(start).Take(span).ToList();
        return area.Sum(t => t.Base) + gap * Math.Max(0, area.Count(t => !t.Collapsed) - 1);
    }

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
        // A grid lays each item out to measure its row's height and again to place it. An item's own grid does the same
        // in both of those layouts, so without reuse a grid nested d deep lays its innermost items out 2^d times; a layout
        // for the same space is the same, and is reused.
        // A subgrid's layout also depends on the tracks its parent lends it.
        var space = new ConstraintSpace(areaWidth, areaHeight, FixedWidth: width, FixedHeight: height);
        var key = (item.Box, space, context.Subgrids.GetValueOrDefault(item.Box));
        if (!context.GridItems.TryGetValue(key, out var fragment))
            context.GridItems[key] = fragment = BlockLayout.Layout(item.Box, space, context);
        return fragment;
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
    private static (List<Item> Items, List<Track> Columns, List<Track> Rows, (int Row, int Column) Offsets) Place(GridContainerBox box, Axis columnAxis, Axis rowAxis)
    {
        var grid = box.Style.Grid;
        var items = box.Children.Where(c => !c.IsAbsolutelyPositioned).Select(c => new Item(c)).OrderBy(i => i.Box.Style.Flex.Order).ToList();
        var (explicitColumns, explicitRows) = (columnAxis.Count, rowAxis.Count);

        var resolved = items.Select(i =>
        {
            var g = i.Box.Style.Grid;
            return (Item: i, Row: ResolveLines(g.RowStart, g.RowEnd, rowAxis), Column: ResolveLines(g.ColumnStart, g.ColumnEnd, columnAxis));
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
        var columns = Tracks(columnAxis.Tracks, grid.AutoColumns, columnOffset, Math.Min(columnCount, GridParsing.MaxTracks));
        var rows = Tracks(rowAxis.Tracks, grid.AutoRows, rowOffset, Math.Min(rowCount, GridParsing.MaxTracks));
        // auto-fit: repeated tracks no item occupies collapse (css-grid-1 §7.2.3.2).
        foreach (var index in columnAxis.AutoFit.Where(i => i + columnOffset < columns.Count))
            columns[index + columnOffset].Collapsed = !items.Any(it => it.Column <= index + columnOffset && index + columnOffset < it.Column + it.ColumnSpan);
        foreach (var index in rowAxis.AutoFit.Where(i => i + rowOffset < rows.Count))
            rows[index + rowOffset].Collapsed = !items.Any(it => it.Row <= index + rowOffset && index + rowOffset < it.Row + it.RowSpan);
        return (items, columns, rows, (rowOffset, columnOffset));
    }

    // §8.3.1: a start line (0-based, from the explicit grid's first line; null when auto) and a span.
    private static (int? Start, int Span) ResolveLines(GridLine start, GridLine end, Axis axis)
    {
        var (s, e) = (LineIndex(start, axis, isStart: true), LineIndex(end, axis, isStart: false));
        if (s is { } a && e is { } b)
            return b == a ? (a, 1) : (Math.Min(a, b), Math.Abs(b - a));
        if (s is { } a2)
            return (a2, end.Kind == GridLineKind.Span ? Math.Max(1, (SpanFrom(end, axis, a2, forward: true) ?? a2 + 1) - a2) : 1);
        if (e is { } b2)
        {
            var from = start.Kind == GridLineKind.Span ? SpanFrom(start, axis, b2, forward: false) ?? b2 - 1 : b2 - 1;
            return (from, Math.Max(1, b2 - from));
        }
        // A span against auto: a named span counts as one track.
        return (null, start is { Kind: GridLineKind.Span, Name: null } ? start.Number : end is { Kind: GridLineKind.Span, Name: null } ? end.Number : 1);
    }

    // A line (not a span) as a 0-based index from the explicit grid's first line; null for auto and spans.
    private static int? LineIndex(GridLine line, Axis axis, bool isStart)
    {
        if (line.Kind != GridLineKind.Line)
            return null;
        if (line.Name is null)
            return line.Number > 0 ? line.Number - 1 : axis.Count + 1 + line.Number;
        // A lone name prefers the area's implicit line (name-start or name-end).
        if (line.Number == 0 && Named(axis, line.Name + (isStart ? "-start" : "-end")).Count > 0)
            return Nth(axis, line.Name + (isStart ? "-start" : "-end"), 1);
        return Nth(axis, line.Name, line.Number == 0 ? 1 : line.Number);
    }

    private static List<int> Named(Axis axis, string name) =>
        Enumerable.Range(0, axis.Names.Count).Where(i => axis.Names[i].Contains(name, StringComparer.Ordinal)).ToList();

    // The nth line with a name (negative counts from the end); when there are too few, implicit lines take the name.
    private static int Nth(Axis axis, string name, int n)
    {
        var lines = Named(axis, name);
        if (n > 0)
            return lines.Count >= n ? lines[n - 1] : axis.Count + (n - lines.Count);
        var m = -n;
        return lines.Count >= m ? lines[lines.Count - m] : -(m - lines.Count);
    }

    // The far edge of a span from a definite line: n tracks, or the nth line with the span's name in that direction.
    private static int? SpanFrom(GridLine span, Axis axis, int from, bool forward)
    {
        if (span.Name is null)
            return forward ? from + span.Number : from - span.Number;
        var lines = Named(axis, span.Name).Where(i => forward ? i > from : i < from).ToList();
        if (!forward)
            lines.Reverse();
        var n = Math.Max(1, span.Number);
        if (lines.Count >= n)
            return lines[n - 1];
        return forward ? Math.Max(axis.Count, from) + (n - lines.Count) : Math.Min(0, from) - (n - lines.Count);
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
            track.Base = min.Kind == TrackKind.Length && !track.Collapsed ? Resolve(min) : 0;
            track.Limit = track.Collapsed ? 0 : max.Kind == TrackKind.Length ? Resolve(max) : float.PositiveInfinity;
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
            var autoTracks = tracks.Where(t => Usable(t.Size.Max).Kind == TrackKind.Auto && !t.Collapsed).ToList();
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
            position += t.Base + (t.Collapsed ? 0 : between);
        }
    }
}

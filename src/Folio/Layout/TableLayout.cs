using System.Globalization;
using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Table layout (https://www.w3.org/TR/css-tables-3/, docs/study/09-layout-tables.md, option A): the table grid from
/// rows, cells (colspan, rowspan) and columns, fixed and auto column widths, row heights, cell vertical alignment,
/// border-spacing, and captions around the table grid box in the table wrapper.
/// </summary>
// ponytail: with collapsed borders, conflicts ignore columns and column groups.
internal static class TableLayout
{
    private sealed class Cell(TablePartBox box)
    {
        public TablePartBox Box { get; } = box;
        public int Row, Column, RowSpan = 1, ColumnSpan = 1;
        public float Min, Max;
        public Fragment Fragment; // default until laid out
        public float Baseline;
    }

    private sealed class Grid
    {
        public List<(TablePartBox? Group, List<TablePartBox> Rows)> Groups { get; } = [];
        public List<TablePartBox> Rows { get; } = [];
        public List<Cell> Cells { get; } = [];
        public List<float?> ColumnWidths { get; } = []; // from col and colgroup
        public List<float> ColumnPercents { get; } = []; // percentage widths from col and colgroup, 0 for none
        public int Columns;
    }

    // A table's grid, collapsed borders and column ranges, worked out for its intrinsic widths and handed to the layout
    // that follows in the same pass, so a large table forms its grid and resolves its borders once (#196).
    private sealed record Prepared(Grid Grid, Dictionary<Cell, BorderGroup>? Collapsed, (float[] Min, float[] Max) Ranges);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LayoutContext, Dictionary<TablePartBox, Prepared>> Kept = [];

    /// <summary>Lays out a table wrapper's captions and table grid box in its content box.</summary>
    public static (List<ChildFragment> Children, float Height) LayoutWrapper(TableWrapperBox wrapper, float width, LayoutContext context)
    {
        TablePartBox? table = null;
        List<TablePartBox>? captions = null;
        for (var i = 0; i < wrapper.Children.Count; i++)
        {
            if (wrapper.Children[i] is TablePartBox part)
            {
                if (part.Part == TablePart.Table && table is null) table = part;
                else if (part.Part == TablePart.Caption) (captions ??= []).Add(part);
            }
        }
        var children = new List<ChildFragment>();
        var y = 0f;
        void Captions(CaptionSide side)
        {
            if (captions is null) return;
            for (var i = 0; i < captions.Count; i++)
            {
                var caption = captions[i];
                if (caption.Style.Text.CaptionSide != side) continue;
                var fragment = BlockLayout.Layout(caption, new ConstraintSpace(width, null), context);
                var (mt, mb) = (BlockLayout.Margin(caption.Style.Spacing.MarginTop, width), BlockLayout.Margin(caption.Style.Spacing.MarginBottom, width));
                children.Add(new ChildFragment(fragment.MarginLeft, y + mt, fragment));
                y += mt + fragment.Height + mb;
            }
        }
        Captions(CaptionSide.Top);
        if (table is not null)
        {
            var fragment = LayoutTable(table, width, context);
            children.Add(new ChildFragment(0, y, fragment));
            y += fragment.Height;
        }
        Captions(CaptionSide.Bottom);
        return (children, y);
    }

    /// <summary>A table's min-content and max-content widths (its border box), at least its captions' min-content.</summary>
    public static (float Min, float Max) IntrinsicWidths(TableWrapperBox wrapper, LayoutContext context)
    {
        TablePartBox? table = null;
        List<TablePartBox>? captions = null;
        for (var i = 0; i < wrapper.Children.Count; i++)
        {
            if (wrapper.Children[i] is TablePartBox part)
            {
                if (part.Part == TablePart.Table && table is null) table = part;
                else if (part.Part == TablePart.Caption) (captions ??= []).Add(part);
            }
        }
        if (table is null)
            return (0, 0);
        var grid = Build(table);
        var collapsed = table.Style.Text.BorderCollapse == BorderCollapse.Collapse ? ResolveCollapsedBorders(grid, table) : null;
        var (min, max) = ColumnRanges(grid, table.Style, context, collapsed);
        Kept.GetOrCreateValue(context)[table] = new Prepared(grid, collapsed, (min, max));
        var (frame, spacing) = Frame(table, 0);
        if (collapsed is not null)
            frame = CollapsedFrame(grid, collapsed);
        var extra = frame.Horizontal + spacing.X * (grid.Columns + 1);
        float maxCaptionMin = 0;
        if (captions is not null)
        {
            for (var i = 0; i < captions.Count; i++)
            {
                var cMin = IntrinsicSizes.Contribution(captions[i], context).Min;
                if (cMin > maxCaptionMin) maxCaptionMin = cMin;
            }
        }
        float minSum = 0, maxSum = 0;
        for (var i = 0; i < min.Length; i++) { minSum += min[i]; maxSum += max[i]; }
        var specified = table.Style.Size.Width is { Kind: SizeKind.Length } w && !w.Length.HasPercent ? w.Length.Px : 0;
        return (Math.Max(Math.Max(minSum + extra, maxCaptionMin), specified), Math.Max(Math.Max(maxSum + extra, maxCaptionMin), specified));
    }

    private readonly record struct Edges(float Left, float Right, float Top, float Bottom)
    {
        public float Horizontal => Left + Right;
        public float Vertical => Top + Bottom;
    }

    // The table grid box's border and padding (no padding with collapsed borders), and its border spacing.
    private static (Edges Frame, (float X, float Y) Spacing) Frame(TablePartBox table, float cbWidth)
    {
        var style = table.Style;
        var collapse = style.Text.BorderCollapse == BorderCollapse.Collapse;
        float P(LengthPercentage p) => collapse ? 0 : BlockLayout.Resolve(p, cbWidth);
        var s = style.Spacing;
        var frame = new Edges(style.Border.LeftWidth + P(s.PaddingLeft), style.Border.RightWidth + P(s.PaddingRight),
            style.Border.TopWidth + P(s.PaddingTop), style.Border.BottomWidth + P(s.PaddingBottom));
        return (frame, collapse ? (0, 0) : (style.Text.BorderSpacingX, style.Text.BorderSpacingY));
    }

    private static Fragment LayoutTable(TablePartBox table, float width, LayoutContext context)
    {
        var style = table.Style;
        var kept = Kept.TryGetValue(context, out var tables) && tables.Remove(table, out var prepared) ? prepared : null;
        var grid = kept?.Grid ?? Build(table);
        var (frame, spacing) = Frame(table, width);
        var collapsed = kept is not null ? kept.Collapsed
            : style.Text.BorderCollapse == BorderCollapse.Collapse ? ResolveCollapsedBorders(grid, table) : null;
        if (collapsed is not null)
            frame = CollapsedFrame(grid, collapsed);
        // Cells that share a resolved border share its half too.
        var halves = new Dictionary<BorderGroup, BorderGroup>(ReferenceEqualityComparer.Instance);
        BorderGroup? Half(Cell cell)
        {
            if (collapsed is null)
                return null;
            var border = collapsed[cell];
            if (!halves.TryGetValue(border, out var half))
            {
                halves[border] = half = border with
                {
                    TopWidthPx = border.TopWidth / 2, RightWidthPx = border.RightWidth / 2, BottomWidthPx = border.BottomWidth / 2, LeftWidthPx = border.LeftWidth / 2,
                };
            }
            return half;
        }
        var n = grid.Columns;
        var columns = ColumnWidths(grid, style, Math.Max(0, width - frame.Horizontal - spacing.X * (n + 1)), context, collapsed, kept?.Ranges);
        float colSum = 0;
        for (var c = 0; c < n; c++) colSum += columns[c];
        var tableWidth = Math.Max(width, colSum + frame.Horizontal + spacing.X * (n + 1));
        // Running sums, so a span's extent is one subtraction however many rows or columns the table has.
        var columnX = new float[n + 1];
        columnX[0] = frame.Left + spacing.X;
        for (var c = 1; c <= n; c++)
            columnX[c] = columnX[c - 1] + columns[c - 1] + spacing.X;
        float SpanWidth(Cell cell) => columnX[Math.Min(n, cell.Column + cell.ColumnSpan)] - columnX[cell.Column] - spacing.X;
        // Columns run from the inline start: from the right in a right-to-left table (CSS 2.2 §17.5), mirrored inside
        // the table's own right border and spacing.
        var rtl = style.Text.Direction == Direction.Rtl;
        float Mirror(float x, float width) => rtl ? tableWidth - frame.Right - (x - frame.Left) - width : x;
        float CellX(Cell cell) => Mirror(columnX[cell.Column], SpanWidth(cell));

        // Row heights: the tallest single-row cell (baseline-aligned cells by their baselines), the row's own height,
        // then taller row-spanning cells stretch their last row.
        var rowCount = grid.Rows.Count;
        var heights = new float[rowCount];
        var ascents = new float[rowCount];
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var cell = grid.Cells[i];
            var w = SpanWidth(cell);
            cell.Fragment = BlockLayout.Layout(cell.Box, new ConstraintSpace(w, null, FixedWidth: w, Border: Half(cell)), context);
            cell.Baseline = FirstBaseline(cell.Fragment) ?? cell.Fragment.Height - (Half(cell) ?? cell.Box.Style.Border).BottomWidth
                - BlockLayout.Resolve(cell.Box.Style.Spacing.PaddingBottom, w);
            if (cell.RowSpan == 1 && IsBaseline(cell))
                ascents[cell.Row] = Math.Max(ascents[cell.Row], cell.Baseline);
        }
        for (var r = 0; r < rowCount; r++)
        {
            var row = grid.Rows[r];
            heights[r] = row.Style.Size.Height is { Kind: SizeKind.Length } h && !h.Length.HasPercent ? h.Length.Px : 0;
        }
        List<Cell>? spanningRows = null;
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var cell = grid.Cells[i];
            if (cell.RowSpan == 1)
            {
                var needed = IsBaseline(cell) ? ascents[cell.Row] - cell.Baseline + cell.Fragment!.Height : cell.Fragment!.Height;
                heights[cell.Row] = Math.Max(heights[cell.Row], Math.Max(needed, SpecifiedHeight(cell, Half(cell), SpanWidth(cell))));
            }
            else if (cell.RowSpan > 1)
            {
                (spanningRows ??= []).Add(cell);
            }
        }
        if (spanningRows is not null)
        {
            spanningRows.Sort((a, b) => a.RowSpan.CompareTo(b.RowSpan));
            for (var i = 0; i < spanningRows.Count; i++)
            {
                var cell = spanningRows[i];
                float spanned = 0;
                for (var r = cell.Row; r < cell.Row + cell.RowSpan; r++)
                    spanned += heights[r];
                spanned += spacing.Y * (cell.RowSpan - 1);
                var needed = Math.Max(cell.Fragment!.Height, SpecifiedHeight(cell, Half(cell), SpanWidth(cell)));
                if (needed > spanned)
                    heights[cell.Row + cell.RowSpan - 1] += needed - spanned;
            }
        }
        // A taller specified table height is shared out among the rows.
        float heightsSum = 0;
        for (var r = 0; r < rowCount; r++) heightsSum += heights[r];
        var tableHeight = frame.Vertical + heightsSum + spacing.Y * (rowCount + 1);
        if (style.Size.Height is { Kind: SizeKind.Length } th && !th.Length.HasPercent && th.Length.Px > tableHeight && rowCount > 0)
        {
            var extra = th.Length.Px - tableHeight;
            for (var r = 0; r < rowCount; r++)
                heights[r] += extra / rowCount;
            tableHeight = th.Length.Px;
        }
        var rowY = new float[rowCount + 1];
        rowY[0] = frame.Top + spacing.Y;
        for (var r = 1; r <= rowCount; r++)
            rowY[r] = rowY[r - 1] + heights[r - 1] + spacing.Y;
        var cellsByRow = new List<Cell>[rowCount];
        foreach (var cell in grid.Cells)
            (cellsByRow[cell.Row] ??= []).Add(cell);

        // Fragments: row groups hold rows, rows hold the cells that start in them.
        var gridSpan = n == 0 ? 0 : columnX[n - 1] + columns[n - 1] - columnX[0];
        var (gridLeft, gridRight) = (Mirror(columnX[0], gridSpan), Mirror(columnX[0], gridSpan) + gridSpan);
        var groupFragments = new List<ChildFragment>();
        var carried = new List<OutOfFlowBox>(); // positioned descendants of cells, for the containing block further up
        var rowIndex = 0;
        foreach (var (group, rows) in grid.Groups)
        {
            var first = rowIndex;
            var rowFragments = new List<ChildFragment>();
            foreach (var row in rows)
            {
                var r = rowIndex++;
                var cells = new List<ChildFragment>();
                if (cellsByRow[r] is { } rowCells)
                {
                    for (var ci = 0; ci < rowCells.Count; ci++)
                    {
                        var cell = rowCells[ci];
                {
                    var height = rowY[r + cell.RowSpan] - rowY[r] - spacing.Y;
                    var content = cell.Fragment!;
                    var offset = cell.Box.Style.Box.VerticalAlign.Kind switch
                    {
                        VerticalAlignKind.Middle => (height - content.Height) / 2,
                        VerticalAlignKind.Bottom => height - content.Height,
                        VerticalAlignKind.Top => 0,
                        _ when IsBaseline(cell) && cell.RowSpan == 1 => ascents[r] - cell.Baseline,
                        _ => 0,
                    };
                    // empty-cells: hide leaves empty cells undecorated in the separated border model.
                    var hidden = collapsed is null && cell.Box.Style.Text.EmptyCells == EmptyCells.Hide && content.Children.Count == 0;
                    // Loops, not Select, below: a lambda here would make a closure for every cell, whether it is used or not.
                    var children = content.Children;
                    if (offset != 0)
                    {
                        var moved = new ChildFragment[children.Count];
                        for (var c = 0; c < moved.Length; c++)
                            moved[c] = children[c] with { Y = children[c].Y + offset };
                        children = moved;
                    }
                    var placed = new Fragment(cell.Box, content.Width, height, children)
                    {
                        PaintedBorder = collapsed?[cell],
                        SkipsDecorations = hidden,
                    };
                    cells.Add(new ChildFragment(CellX(cell) - gridLeft, 0, placed));
                    for (var o = 0; o < content.OutOfFlow.Count; o++)
                    {
                        var positioned = content.OutOfFlow[o];
                        carried.Add(positioned with { StaticX = positioned.StaticX + CellX(cell), StaticY = positioned.StaticY + rowY[r] + offset });
                    }
                }
                    }
                }
                rowFragments.Add(new ChildFragment(0, rowY[r] - rowY[first],
                    new Fragment(row, gridRight - gridLeft, heights[r], cells) { PaintedBorder = collapsed is null ? null : ComputedStyle.Initial.Border }));
            }
            if (rows.Count == 0)
                continue;
            var groupHeight = rowY[rowIndex - 1] + heights[rowIndex - 1] - rowY[first];
            groupFragments.Add(new ChildFragment(gridLeft, rowY[first],
                new Fragment(group!, gridRight - gridLeft, groupHeight, rowFragments) { PaintedBorder = collapsed is null ? null : ComputedStyle.Initial.Border }));
        }
        return new Fragment(table, tableWidth, tableHeight, groupFragments)
        {
            OutOfFlow = carried,
            PaintedBorder = collapsed is null ? null : ComputedStyle.Initial.Border,
        };
    }

    // The table's border box holds half of each outer collapsed border (CSS 2.2 §17.6.2).
    private static Edges CollapsedFrame(Grid grid, Dictionary<Cell, BorderGroup> collapsed)
    {
        float maxLeft = 0, maxRight = 0, maxTop = 0, maxBottom = 0;
        var cols = grid.Columns;
        var rows = grid.Rows.Count;
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var c = grid.Cells[i];
            var b = collapsed[c];
            if (c.Column == 0 && b.LeftWidth > maxLeft) maxLeft = b.LeftWidth;
            if (c.Column + c.ColumnSpan == cols && b.RightWidth > maxRight) maxRight = b.RightWidth;
            if (c.Row == 0 && b.TopWidth > maxTop) maxTop = b.TopWidth;
            if (c.Row + c.RowSpan == rows && b.BottomWidth > maxBottom) maxBottom = b.BottomWidth;
        }
        return new Edges(maxLeft / 2, maxRight / 2, maxTop / 2, maxBottom / 2);
    }

    // One side of a border in the collapsing model, with the priority of the box it comes from.
    private readonly record struct Side(float Width, BorderStyle Style, CssColor Color, int Priority);

    private enum Edge { Top, Right, Bottom, Left }

    private static Side SideOf(Box box, Edge edge, int priority)
    {
        var b = box.Style.Border;
        var current = box.Style.Inherited.Color;
        return edge switch
        {
            Edge.Top => new Side(b.TopWidth, b.TopStyle, b.TopColor.Resolve(current), priority),
            Edge.Right => new Side(b.RightWidth, b.RightStyle, b.RightColor.Resolve(current), priority),
            Edge.Bottom => new Side(b.BottomWidth, b.BottomStyle, b.BottomColor.Resolve(current), priority),
            _ => new Side(b.LeftWidth, b.LeftStyle, b.LeftColor.Resolve(current), priority),
        };
    }

    // CSS 2.2 §17.6.2.1: hidden wins, then the wider border, then the style (double down to inset), then the box
    // (cell, row, row group, table); on a full tie the first one.
    private static Side Winner(Side a, Side b)
    {
        if (a.Style == BorderStyle.Hidden)
            return a;
        if (b.Style == BorderStyle.Hidden)
            return b;
        if (b.Style == BorderStyle.None)
            return a;
        if (a.Style == BorderStyle.None)
            return b;
        if (a.Width != b.Width)
            return a.Width > b.Width ? a : b;
        if (Rank(a.Style) != Rank(b.Style))
            return Rank(a.Style) > Rank(b.Style) ? a : b;
        return b.Priority > a.Priority ? b : a;

        static int Rank(BorderStyle s) => s switch
        {
            BorderStyle.Double => 8,
            BorderStyle.Solid => 7,
            BorderStyle.Dashed => 6,
            BorderStyle.Dotted => 5,
            BorderStyle.Ridge => 4,
            BorderStyle.Outset => 3,
            BorderStyle.Groove => 2,
            BorderStyle.Inset => 1,
            _ => 0,
        };
    }

    // Each cell's collapsed border: every side resolved against the neighbouring cell, the rows and row groups the
    // edge runs along, and the table at the outer edges.
    private static Dictionary<Cell, BorderGroup> ResolveCollapsedBorders(Grid grid, TablePartBox table)
    {
        var rowCount = grid.Rows.Count;
        var colCount = grid.Columns;
        var slots = new Cell?[rowCount, colCount];
        foreach (var cell in grid.Cells)
        {
            var maxR = Math.Min(rowCount, cell.Row + cell.RowSpan);
            var maxC = Math.Min(colCount, cell.Column + cell.ColumnSpan);
            for (var r = cell.Row; r < maxR; r++)
            {
                for (var c = cell.Column; c < maxC; c++)
                    slots[r, c] = cell;
            }
        }
        var groupOf = new TablePartBox[rowCount];
        var index = 0;
        foreach (var (group, rows) in grid.Groups)
        {
            foreach (var _ in rows)
            {
                if (index < rowCount) groupOf[index++] = group!;
            }
        }
        var (lastRow, lastColumn) = (rowCount - 1, colCount - 1);

        Side Resolve(Cell cell, Edge edge)
        {
            var side = SideOf(cell.Box, edge, 4);
            var (top, bottom) = (cell.Row, cell.Row + cell.RowSpan - 1);
            var (left, right) = (cell.Column, cell.Column + cell.ColumnSpan - 1);
            switch (edge)
            {
                case Edge.Top:
                    side = Winner(side, SideOf(grid.Rows[top], Edge.Top, 3));
                    if (top == 0 || groupOf[top] != groupOf[top - 1])
                        side = Winner(side, SideOf(groupOf[top], Edge.Top, 2));
                    if (top > 0)
                    {
                        side = Winner(side, SideOf(grid.Rows[top - 1], Edge.Bottom, 3));
                        var above = (left >= 0 && left < colCount) ? slots[top - 1, left] : null;
                        if (above is not null)
                            side = Winner(side, SideOf(above.Box, Edge.Bottom, 4));
                        if (groupOf[top] != groupOf[top - 1])
                            side = Winner(side, SideOf(groupOf[top - 1], Edge.Bottom, 2));
                    }
                    else
                    {
                        side = Winner(side, SideOf(table, Edge.Top, 0));
                    }
                    break;
                case Edge.Bottom:
                    side = Winner(side, SideOf(grid.Rows[bottom], Edge.Bottom, 3));
                    if (bottom == lastRow || groupOf[bottom] != groupOf[bottom + 1])
                        side = Winner(side, SideOf(groupOf[bottom], Edge.Bottom, 2));
                    if (bottom < lastRow)
                    {
                        side = Winner(side, SideOf(grid.Rows[bottom + 1], Edge.Top, 3));
                        var below = (left >= 0 && left < colCount) ? slots[bottom + 1, left] : null;
                        if (below is not null)
                            side = Winner(side, SideOf(below.Box, Edge.Top, 4));
                        if (groupOf[bottom] != groupOf[bottom + 1])
                            side = Winner(side, SideOf(groupOf[bottom + 1], Edge.Top, 2));
                    }
                    else
                    {
                        side = Winner(side, SideOf(table, Edge.Bottom, 0));
                    }
                    break;
                case Edge.Left:
                    var before = (left > 0 && top >= 0 && top < rowCount) ? slots[top, left - 1] : null;
                    if (before is not null)
                        side = Winner(side, SideOf(before.Box, Edge.Right, 4));
                    else if (left == 0)
                        side = Winner(Winner(Winner(side, SideOf(grid.Rows[top], Edge.Left, 3)), SideOf(groupOf[top], Edge.Left, 2)), SideOf(table, Edge.Left, 0));
                    break;
                default:
                    var after = (right < lastColumn && top >= 0 && top < rowCount) ? slots[top, right + 1] : null;
                    if (after is not null)
                        side = Winner(side, SideOf(after.Box, Edge.Left, 4));
                    else if (right == lastColumn)
                        side = Winner(Winner(Winner(side, SideOf(grid.Rows[top], Edge.Right, 3)), SideOf(groupOf[top], Edge.Right, 2)), SideOf(table, Edge.Right, 0));
                    break;
            }
            return side;
        }

        // Most cells of a large table resolve to the same few borders: they share one instance of each, found by the
        // cell's own border (which keeps the radii) and the winning sides, before making one.
        var result = new Dictionary<Cell, BorderGroup>();
        var shared = new Dictionary<(BorderGroup Own, Side Top, Side Right, Side Bottom, Side Left), BorderGroup>();
        foreach (var cell in grid.Cells)
        {
            var (t, r, b, l) = (Resolve(cell, Edge.Top), Resolve(cell, Edge.Right), Resolve(cell, Edge.Bottom), Resolve(cell, Edge.Left));
            var key = (cell.Box.Style.Border, t with { Priority = 0 }, r with { Priority = 0 }, b with { Priority = 0 }, l with { Priority = 0 });
            if (!shared.TryGetValue(key, out var border))
            {
                shared[key] = border = cell.Box.Style.Border with
                {
                    TopWidthPx = t.Width, RightWidthPx = r.Width, BottomWidthPx = b.Width, LeftWidthPx = l.Width,
                    TopStyle = t.Style, RightStyle = r.Style, BottomStyle = b.Style, LeftStyle = l.Style,
                    TopColor = t.Color, RightColor = r.Color, BottomColor = b.Color, LeftColor = l.Color,
                };
            }
            result[cell] = border;
        }
        return result;
    }

    // A cell's specified height as a border-box height, for the row it is in (0 when auto or a percentage).
    private static float SpecifiedHeight(Cell cell, BorderGroup? collapsedHalf, float width)
    {
        var style = cell.Box.Style;
        if (style.Size.Height is not { Kind: SizeKind.Length, Length: { HasPercent: false } height })
            return 0;
        var border = collapsedHalf ?? style.Border;
        var frame = border.TopWidth + border.BottomWidth + BlockLayout.Resolve(style.Spacing.PaddingTop, width)
            + BlockLayout.Resolve(style.Spacing.PaddingBottom, width);
        return style.Box.BoxSizing == BoxSizing.BorderBox ? Math.Max(height.Px, frame) : height.Px + frame;
    }

    private static bool IsBaseline(Cell cell) =>
        cell.Box.Style.Box.VerticalAlign.Kind is not (VerticalAlignKind.Top or VerticalAlignKind.Middle or VerticalAlignKind.Bottom);

    // The table grid (css-tables-3 §3.9, "forming the table grid"): the header group first, the footer group last,
    // cells in slots skipping those taken by earlier row spans.
    private static Grid Build(TablePartBox table)
    {
        var grid = new Grid();
        TablePartBox? header = null;
        TablePartBox? footer = null;
        var bodyGroups = new List<TablePartBox>();

        for (var i = 0; i < table.Children.Count; i++)
        {
            if (table.Children[i] is TablePartBox part)
            {
                if (part.Part == TablePart.HeaderGroup && header is null)
                    header = part;
                else if (part.Part == TablePart.FooterGroup && footer is null)
                    footer = part;
                else if (part.Part is TablePart.RowGroup)
                    bodyGroups.Add(part);
                else if (part.Part is TablePart.Column or TablePart.ColumnGroup)
                {
                    if (part.Part == TablePart.ColumnGroup && part.Children.Count > 0)
                    {
                        for (var c = 0; c < part.Children.Count; c++)
                        {
                            if (part.Children[c] is TablePartBox col && col.Part == TablePart.Column)
                                AddCol(grid, col);
                        }
                    }
                    else
                    {
                        AddCol(grid, part);
                    }
                }
            }
        }

        var ordered = new List<TablePartBox>(bodyGroups.Count + 2);
        if (header is not null) ordered.Add(header);
        ordered.AddRange(bodyGroups);
        if (footer is not null) ordered.Add(footer);

        var taken = new HashSet<(int Row, int Column)>();
        var maxColSeen = 0;
        foreach (var group in ordered)
        {
            var rows = new List<TablePartBox>();
            for (var i = 0; i < group.Children.Count; i++)
            {
                if (group.Children[i] is TablePartBox r && r.Part == TablePart.Row)
                    rows.Add(r);
            }
            var groupStart = grid.Rows.Count;
            grid.Groups.Add((group, rows));
            foreach (var row in rows)
            {
                var r = grid.Rows.Count;
                grid.Rows.Add(row);
                var column = 0;
                for (var i = 0; i < row.Children.Count; i++)
                {
                    if (row.Children[i] is TablePartBox box && box.Part == TablePart.Cell)
                    {
                        while (taken.Contains((r, column)))
                            column++;
                        var rowSpan = Attribute(box, "rowspan", 1, 0, 65534);
                        var cell = new Cell(box)
                        {
                            Row = r,
                            Column = column,
                            ColumnSpan = Attribute(box, "colspan", 1, 1, 1000),
                            RowSpan = rowSpan == 0 ? groupStart + rows.Count - r : Math.Min(rowSpan, groupStart + rows.Count - r),
                        };
                        for (var a = r; a < r + cell.RowSpan; a++)
                        {
                            for (var b = column; b < column + cell.ColumnSpan; b++)
                                taken.Add((a, b));
                        }
                        if (column + cell.ColumnSpan > maxColSeen)
                            maxColSeen = column + cell.ColumnSpan;
                        grid.Cells.Add(cell);
                        column += cell.ColumnSpan;
                    }
                }
            }
        }
        grid.Columns = Math.Max(grid.ColumnWidths.Count, maxColSeen);
        return grid;

        static void AddCol(Grid grid, TablePartBox column)
        {
            float? width = column.Style.Size.Width is { Kind: SizeKind.Length } w && !w.Length.HasPercent ? w.Length.Px : null;
            var percent = column.Style.Size.Width is { Kind: SizeKind.Length, Length: { Calc: null, Px: 0, Percent: > 0 } p } ? p.Percent : 0;
            var span = Attribute(column, "span", 1, 1, 1000);
            for (var i = 0; i < span; i++)
            {
                grid.ColumnWidths.Add(width);
                grid.ColumnPercents.Add(percent);
            }
        }
    }

    // An HTML table attribute (https://html.spec.whatwg.org/multipage/tables.html#attributes-common-to-td-and-th-elements).
    private static int Attribute(TablePartBox box, string name, int fallback, int min, int max) =>
        box.Node is ElementNode e && e.GetAttribute(name) is { } text && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    // Each column's min-content and max-content width (css-tables-3 §3.9.3): single-column cells first, then spanning
    // cells share out what their columns lack, in proportion to the columns' max-content widths.
    private static (float[] Min, float[] Max) ColumnRanges(Grid grid, ComputedStyle tableStyle, LayoutContext context,
                                                        Dictionary<Cell, BorderGroup>? collapsed)
    {
        var n = grid.Columns;
        var (min, max) = (new float[n], new float[n]);
        var spacing = tableStyle.Text.BorderCollapse == BorderCollapse.Collapse ? 0 : tableStyle.Text.BorderSpacingX;
        List<Cell>? spanning = null;
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var cell = grid.Cells[i];
            (cell.Min, cell.Max) = IntrinsicSizes.Contribution(cell.Box, context);
            // With collapsed borders a cell holds half of each resolved border between it and its neighbours, not its own.
            // A border-box width or min-width already includes them, so it stays as given.
            if (collapsed?[cell] is { } border)
            {
                var style = cell.Box.Style;
                var delta = (border.LeftWidth + border.RightWidth) / 2 - (style.Border.LeftWidth + style.Border.RightWidth);
                var given = style.Box.BoxSizing != BoxSizing.BorderBox ? 0 : Math.Max(Px(style.Size.Width), Px(style.Size.MinWidth));
                (cell.Min, cell.Max) = (Math.Max(given, cell.Min + delta), Math.Max(given, cell.Max + delta));
            }
            if (cell.ColumnSpan == 1)
            {
                if (cell.Column < n)
                {
                    if (cell.Min > min[cell.Column]) min[cell.Column] = cell.Min;
                    if (cell.Max > max[cell.Column]) max[cell.Column] = cell.Max;
                }
            }
            else if (cell.ColumnSpan > 1)
            {
                (spanning ??= []).Add(cell);
            }
        }
        for (var c = 0; c < Math.Min(n, grid.ColumnWidths.Count); c++)
        {
            if (grid.ColumnWidths[c] is { } w)
                max[c] = Math.Max(min[c], w);
        }
        if (spanning is not null)
        {
            spanning.Sort((a, b) => a.ColumnSpan.CompareTo(b.ColumnSpan));
            for (var i = 0; i < spanning.Count; i++)
            {
                var cell = spanning[i];
                var spanCount = Math.Min(cell.ColumnSpan, n - cell.Column);
                var gaps = spacing * (spanCount - 1);
                Share(min, cell.Min - gaps, cell.Column, spanCount);
                Share(max, cell.Max - gaps, cell.Column, spanCount);
            }
        }
        for (var c = 0; c < n; c++)
            max[c] = Math.Max(max[c], min[c]);
        return (min, max);

        void Share(float[] sizes, float needed, int startCol, int count)
        {
            float have = 0;
            for (var i = 0; i < count; i++)
                have += sizes[startCol + i];
            if (needed <= have)
                return;
            float weights = 0;
            for (var i = 0; i < count; i++)
                weights += max[startCol + i];
            for (var i = 0; i < count; i++)
            {
                var col = startCol + i;
                sizes[col] += (needed - have) * (weights > 0 ? max[col] / weights : 1f / count);
            }
        }
    }

    private static float Px(SizeValue size) => size is { Kind: SizeKind.Length, Length: { HasPercent: false } length } ? length.Px : 0;

    private enum ColumnKind { Auto, Fixed, Percent }

    /// <summary>
    /// Column widths for the space the table gives its columns (css-tables-3 §3.9.4, simplified to min and max
    /// phases). A column whose cells or col give it a percentage width takes that share of the space (at least its
    /// minimum); the others go from their minimum towards their maximum widths, and space beyond all maximums goes to
    /// the columns with no specified width, in proportion to their maximums; only without such columns does it go to
    /// columns with a length width, and then to percentage columns.
    /// </summary>
    private static float[] ColumnWidths(Grid grid, ComputedStyle style, float target, LayoutContext context, Dictionary<Cell, BorderGroup>? collapsed,
                                        (float[] Min, float[] Max)? ranges)
    {
        var n = grid.Columns;
        if (n == 0)
            return [];
        if (style.Box.TableLayout == TableLayoutMode.Fixed && style.Size.Width.Kind == SizeKind.Length)
            return FixedWidths(grid, target);
        var (min, max) = ranges ?? ColumnRanges(grid, style, context, collapsed);
        float minSum = 0;
        for (var c = 0; c < n; c++) minSum += min[c];
        if (target <= minSum)
            return min;

        var kinds = new ColumnKind[n];
        var percents = new float[n];
        for (var c = 0; c < Math.Min(n, grid.ColumnWidths.Count); c++)
        {
            if (grid.ColumnWidths[c] is not null)
                kinds[c] = ColumnKind.Fixed;
            else if (grid.ColumnPercents[c] > 0)
                (kinds[c], percents[c]) = (ColumnKind.Percent, grid.ColumnPercents[c]);
        }
        for (var i = 0; i < grid.Cells.Count; i++)
        {
            var cell = grid.Cells[i];
            if (cell.ColumnSpan != 1 || cell.Column >= n) continue;
            if (cell.Box.Style.Size.Width is not { Kind: SizeKind.Length, Length: var w })
                continue;
            if (w is { Calc: null, Px: 0, Percent: > 0 })
                (kinds[cell.Column], percents[cell.Column]) = (ColumnKind.Percent, Math.Max(percents[cell.Column], w.Percent));
            else if (!w.HasPercent && kinds[cell.Column] == ColumnKind.Auto)
                kinds[cell.Column] = ColumnKind.Fixed;
        }

        var widths = new float[n];
        for (var c = 0; c < n; c++)
        {
            if (kinds[c] == ColumnKind.Percent)
                widths[c] = Math.Max(min[c], percents[c] / 100 * target);
        }

        float widthsSum = 0;
        for (var c = 0; c < n; c++)
            widthsSum += widths[c];
        var rest = target - widthsSum;

        float othersMin = 0, othersMax = 0;
        for (var c = 0; c < n; c++)
        {
            if (kinds[c] != ColumnKind.Percent)
            {
                othersMin += min[c];
                othersMax += max[c];
            }
        }
        if (rest < othersMin)
        {
            float pMinSum = 0, pWantSum = 0;
            for (var c = 0; c < n; c++)
            {
                if (kinds[c] == ColumnKind.Percent)
                {
                    pMinSum += min[c];
                    pWantSum += widths[c];
                }
                else
                {
                    widths[c] = min[c];
                }
            }
            var space = target - othersMin;
            var factor = pWantSum > pMinSum ? Math.Max(0, space - pMinSum) / (pWantSum - pMinSum) : 0;
            for (var c = 0; c < n; c++)
            {
                if (kinds[c] == ColumnKind.Percent)
                    widths[c] = min[c] + (widths[c] - min[c]) * factor;
            }
            return widths;
        }
        if (rest <= othersMax)
        {
            var factor = othersMax > othersMin ? (rest - othersMin) / (othersMax - othersMin) : 0;
            for (var c = 0; c < n; c++)
            {
                if (kinds[c] != ColumnKind.Percent)
                    widths[c] = min[c] + (max[c] - min[c]) * factor;
            }
            return widths;
        }
        for (var c = 0; c < n; c++)
        {
            if (kinds[c] != ColumnKind.Percent)
                widths[c] = max[c];
        }
        var extra = rest - othersMax;
        ColumnKind? targetKind = null;
        if (HasKind(kinds, widths, ColumnKind.Auto)) targetKind = ColumnKind.Auto;
        else if (HasKind(kinds, widths, ColumnKind.Fixed)) targetKind = ColumnKind.Fixed;
        else if (HasKind(kinds, widths, ColumnKind.Percent)) targetKind = ColumnKind.Percent;

        float weight = 0;
        var takersCount = 0;
        for (var c = 0; c < n; c++)
        {
            if (targetKind is null ? true : (kinds[c] == targetKind && widths[c] > 0))
            {
                weight += widths[c];
                takersCount++;
            }
        }
        if (takersCount == 0)
        {
            takersCount = n;
            for (var c = 0; c < n; c++) weight += widths[c];
        }
        for (var c = 0; c < n; c++)
        {
            if (targetKind is null ? true : (kinds[c] == targetKind && widths[c] > 0))
            {
                widths[c] += extra * (weight > 0 ? widths[c] / weight : 1f / takersCount);
            }
        }
        return widths;

        static bool HasKind(ColumnKind[] kinds, float[] widths, ColumnKind k)
        {
            for (var c = 0; c < kinds.Length; c++)
                if (kinds[c] == k && widths[c] > 0) return true;
            return false;
        }
    }

    // table-layout: fixed (css-tables-3 §3.9.3.1): widths from the columns, else the first row's cells; the rest share
    // what is left equally.
    private static float[] FixedWidths(Grid grid, float target)
    {
        var n = grid.Columns;
        var widths = new float?[n];
        for (var c = 0; c < Math.Min(n, grid.ColumnWidths.Count); c++)
            widths[c] = grid.ColumnWidths[c];
        foreach (var cell in grid.Cells.Where(c => c.Row == 0))
        {
            var style = cell.Box.Style;
            if (widths[cell.Column] is null && style.Size.Width is { Kind: SizeKind.Length } w && !w.Length.HasPercent)
            {
                var frame = style.Border.LeftWidth + style.Border.RightWidth + Math.Max(0, style.Spacing.PaddingLeft.Px) + Math.Max(0, style.Spacing.PaddingRight.Px);
                var outer = (style.Box.BoxSizing == BoxSizing.BorderBox ? w.Length.Px : w.Length.Px + frame) / cell.ColumnSpan;
                for (var c = cell.Column; c < Math.Min(n, cell.Column + cell.ColumnSpan); c++)
                    widths[c] = outer;
            }
        }
        var unset = widths.Count(w => w is null);
        var left = Math.Max(0, target - widths.Sum(w => w ?? 0));
        return widths.Select(w => w ?? (unset > 0 ? left / unset : 0)).ToArray();
    }

    // A cell's first baseline: its first line box's, from its top; null without line boxes.
    private static float? FirstBaseline(Fragment fragment)
    {
        var children = fragment.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Fragment.Kind == FragmentKind.Line && child.Fragment.Height > 0)
                return child.Y + child.Fragment.Baseline;
            if (child.Fragment.Kind == FragmentKind.Box && child.Fragment.Box is { IsFloat: false, IsAbsolutelyPositioned: false }
                && FirstBaseline(child.Fragment) is { } inner)
                return child.Y + inner;
        }
        return null;
    }
}

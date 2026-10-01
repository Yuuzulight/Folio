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
// ponytail: with collapsed borders, intrinsic widths use the cells'
// own borders and conflicts ignore columns and column groups.
internal static class TableLayout
{
    private sealed class Cell(TablePartBox box)
    {
        public TablePartBox Box { get; } = box;
        public int Row, Column, RowSpan = 1, ColumnSpan = 1;
        public float Min, Max;
        public Fragment? Fragment;
        public float Baseline;
    }

    private sealed class Grid
    {
        public List<(TablePartBox? Group, List<TablePartBox> Rows)> Groups { get; } = [];
        public List<TablePartBox> Rows { get; } = [];
        public List<Cell> Cells { get; } = [];
        public List<float?> ColumnWidths { get; } = []; // from col and colgroup
        public int Columns;
    }

    /// <summary>Lays out a table wrapper's captions and table grid box in its content box.</summary>
    public static (List<ChildFragment> Children, float Height) LayoutWrapper(TableWrapperBox wrapper, float width, LayoutContext context)
    {
        var table = wrapper.Children.OfType<TablePartBox>().FirstOrDefault(p => p.Part == TablePart.Table);
        var captions = wrapper.Children.OfType<TablePartBox>().Where(p => p.Part == TablePart.Caption).ToList();
        var children = new List<ChildFragment>();
        var y = 0f;
        void Captions(CaptionSide side)
        {
            foreach (var caption in captions.Where(c => c.Style.Text.CaptionSide == side))
            {
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
        if (wrapper.Children.OfType<TablePartBox>().FirstOrDefault(p => p.Part == TablePart.Table) is not { } table)
            return (0, 0);
        var grid = Build(table);
        var (min, max) = ColumnRanges(grid, table.Style, context);
        var (frame, spacing) = Frame(table, 0);
        if (table.Style.Text.BorderCollapse == BorderCollapse.Collapse)
            frame = CollapsedFrame(grid, ResolveCollapsedBorders(grid, table));
        var extra = frame.Horizontal + spacing.X * (grid.Columns + 1);
        var captions = wrapper.Children.OfType<TablePartBox>().Where(p => p.Part == TablePart.Caption)
            .Select(c => IntrinsicSizes.Contribution(c, context).Min).DefaultIfEmpty(0).Max();
        var specified = table.Style.Size.Width is { Kind: SizeKind.Length } w && !w.Length.HasPercent ? w.Length.Px : 0;
        return (Math.Max(Math.Max(min.Sum() + extra, captions), specified), Math.Max(Math.Max(max.Sum() + extra, captions), specified));
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
        var grid = Build(table);
        var (frame, spacing) = Frame(table, width);
        var collapsed = style.Text.BorderCollapse == BorderCollapse.Collapse ? ResolveCollapsedBorders(grid, table) : null;
        if (collapsed is not null)
            frame = CollapsedFrame(grid, collapsed);
        BorderGroup? Half(Cell cell) => collapsed?[cell] is { } b
            ? b with { TopWidthPx = b.TopWidth / 2, RightWidthPx = b.RightWidth / 2, BottomWidthPx = b.BottomWidth / 2, LeftWidthPx = b.LeftWidth / 2 }
            : null;
        var n = grid.Columns;
        var columns = ColumnWidths(grid, style, Math.Max(0, width - frame.Horizontal - spacing.X * (n + 1)), context);
        var tableWidth = Math.Max(width, columns.Sum() + frame.Horizontal + spacing.X * (n + 1));
        var columnX = new float[n + 1];
        for (var c = 0; c <= n; c++)
            columnX[c] = frame.Left + spacing.X * (c + 1) + columns.Take(c).Sum();
        float SpanWidth(Cell cell) => columns.Skip(cell.Column).Take(cell.ColumnSpan).Sum() + spacing.X * (cell.ColumnSpan - 1);

        // Row heights: the tallest single-row cell (baseline-aligned cells by their baselines), the row's own height,
        // then taller row-spanning cells stretch their last row.
        var rowCount = grid.Rows.Count;
        var heights = new float[rowCount];
        var ascents = new float[rowCount];
        foreach (var cell in grid.Cells)
        {
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
        foreach (var cell in grid.Cells.Where(c => c.RowSpan == 1))
        {
            var needed = IsBaseline(cell) ? ascents[cell.Row] - cell.Baseline + cell.Fragment!.Height : cell.Fragment!.Height;
            heights[cell.Row] = Math.Max(heights[cell.Row], Math.Max(needed, SpecifiedHeight(cell, Half(cell), SpanWidth(cell))));
        }
        foreach (var cell in grid.Cells.Where(c => c.RowSpan > 1).OrderBy(c => c.RowSpan))
        {
            var spanned = heights.Skip(cell.Row).Take(cell.RowSpan).Sum() + spacing.Y * (cell.RowSpan - 1);
            var needed = Math.Max(cell.Fragment!.Height, SpecifiedHeight(cell, Half(cell), SpanWidth(cell)));
            if (needed > spanned)
                heights[cell.Row + cell.RowSpan - 1] += needed - spanned;
        }
        // A taller specified table height is shared out among the rows.
        var tableHeight = frame.Vertical + heights.Sum() + spacing.Y * (rowCount + 1);
        if (style.Size.Height is { Kind: SizeKind.Length } th && !th.Length.HasPercent && th.Length.Px > tableHeight && rowCount > 0)
        {
            var extra = th.Length.Px - tableHeight;
            for (var r = 0; r < rowCount; r++)
                heights[r] += extra / rowCount;
            tableHeight = th.Length.Px;
        }
        var rowY = new float[rowCount + 1];
        for (var r = 0; r <= rowCount; r++)
            rowY[r] = frame.Top + spacing.Y * (r + 1) + heights.Take(r).Sum();

        // Fragments: row groups hold rows, rows hold the cells that start in them.
        var (gridLeft, gridRight) = (columnX[0], n > 0 ? columnX[n - 1] + columns[n - 1] : columnX[0]);
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
                foreach (var cell in grid.Cells.Where(c => c.Row == r))
                {
                    var height = heights.Skip(r).Take(cell.RowSpan).Sum() + spacing.Y * (cell.RowSpan - 1);
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
                    var placed = new Fragment(cell.Box, content.Width, height, content.Children.Select(c => c with { Y = c.Y + offset }).ToList())
                    {
                        PaintedBorder = collapsed?[cell],
                        SkipsDecorations = hidden,
                    };
                    cells.Add(new ChildFragment(columnX[cell.Column] - gridLeft, 0, placed));
                    carried.AddRange(content.OutOfFlow.Select(o => o with { StaticX = o.StaticX + columnX[cell.Column], StaticY = o.StaticY + rowY[r] + offset }));
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
        float Outer(Func<Cell, bool> onEdge, Func<BorderGroup, float> width) =>
            grid.Cells.Where(onEdge).Select(c => width(collapsed[c])).DefaultIfEmpty(0).Max() / 2;
        return new Edges(Outer(c => c.Column == 0, b => b.LeftWidth), Outer(c => c.Column + c.ColumnSpan == grid.Columns, b => b.RightWidth),
            Outer(c => c.Row == 0, b => b.TopWidth), Outer(c => c.Row + c.RowSpan == grid.Rows.Count, b => b.BottomWidth));
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
        var slots = new Dictionary<(int Row, int Column), Cell>();
        foreach (var cell in grid.Cells)
        {
            for (var r = cell.Row; r < cell.Row + cell.RowSpan; r++)
            {
                for (var c = cell.Column; c < cell.Column + cell.ColumnSpan; c++)
                    slots[(r, c)] = cell;
            }
        }
        var groupOf = new Dictionary<int, TablePartBox>();
        var index = 0;
        foreach (var (group, rows) in grid.Groups)
        {
            foreach (var _ in rows)
                groupOf[index++] = group!;
        }
        var (lastRow, lastColumn) = (grid.Rows.Count - 1, grid.Columns - 1);

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
                        if (slots.TryGetValue((top - 1, left), out var above))
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
                        if (slots.TryGetValue((bottom + 1, left), out var below))
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
                    if (left > 0 && slots.TryGetValue((top, left - 1), out var before))
                        side = Winner(side, SideOf(before.Box, Edge.Right, 4));
                    else if (left == 0)
                        side = Winner(Winner(Winner(side, SideOf(grid.Rows[top], Edge.Left, 3)), SideOf(groupOf[top], Edge.Left, 2)), SideOf(table, Edge.Left, 0));
                    break;
                default:
                    if (right < lastColumn && slots.TryGetValue((top, right + 1), out var after))
                        side = Winner(side, SideOf(after.Box, Edge.Left, 4));
                    else if (right == lastColumn)
                        side = Winner(Winner(Winner(side, SideOf(grid.Rows[top], Edge.Right, 3)), SideOf(groupOf[top], Edge.Right, 2)), SideOf(table, Edge.Right, 0));
                    break;
            }
            return side;
        }

        var result = new Dictionary<Cell, BorderGroup>();
        foreach (var cell in grid.Cells)
        {
            var (t, r, b, l) = (Resolve(cell, Edge.Top), Resolve(cell, Edge.Right), Resolve(cell, Edge.Bottom), Resolve(cell, Edge.Left));
            result[cell] = cell.Box.Style.Border with
            {
                TopWidthPx = t.Width, RightWidthPx = r.Width, BottomWidthPx = b.Width, LeftWidthPx = l.Width,
                TopStyle = t.Style, RightStyle = r.Style, BottomStyle = b.Style, LeftStyle = l.Style,
                TopColor = t.Color, RightColor = r.Color, BottomColor = b.Color, LeftColor = l.Color,
            };
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
        var parts = table.Children.OfType<TablePartBox>().ToList();
        var groups = parts.Where(p => p.Part is TablePart.RowGroup or TablePart.HeaderGroup or TablePart.FooterGroup).ToList();
        var header = groups.FirstOrDefault(g => g.Part == TablePart.HeaderGroup);
        var footer = groups.FirstOrDefault(g => g.Part == TablePart.FooterGroup);
        var ordered = new List<TablePartBox>();
        if (header is not null)
            ordered.Add(header);
        ordered.AddRange(groups.Where(g => g != header && g != footer));
        if (footer is not null)
            ordered.Add(footer);

        foreach (var part in parts.Where(p => p.Part is TablePart.Column or TablePart.ColumnGroup))
        {
            var columns = part.Part == TablePart.ColumnGroup && part.Children.OfType<TablePartBox>().Any()
                ? part.Children.OfType<TablePartBox>().Where(c => c.Part == TablePart.Column).ToList()
                : [part];
            foreach (var column in columns)
            {
                float? width = column.Style.Size.Width is { Kind: SizeKind.Length } w && !w.Length.HasPercent ? w.Length.Px : null;
                for (var i = 0; i < Attribute(column, "span", 1, 1, 1000); i++)
                    grid.ColumnWidths.Add(width);
            }
        }

        var taken = new HashSet<(int Row, int Column)>();
        foreach (var group in ordered)
        {
            var rows = group.Children.OfType<TablePartBox>().Where(r => r.Part == TablePart.Row).ToList();
            var groupStart = grid.Rows.Count;
            grid.Groups.Add((group, rows));
            foreach (var row in rows)
            {
                var r = grid.Rows.Count;
                grid.Rows.Add(row);
                var column = 0;
                foreach (var box in row.Children.OfType<TablePartBox>().Where(c => c.Part == TablePart.Cell))
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
                    grid.Cells.Add(cell);
                    column += cell.ColumnSpan;
                }
            }
        }
        grid.Columns = Math.Max(grid.ColumnWidths.Count, taken.Count == 0 ? 0 : taken.Max(t => t.Column) + 1);
        return grid;
    }

    // An HTML table attribute (https://html.spec.whatwg.org/multipage/tables.html#attributes-common-to-td-and-th-elements).
    private static int Attribute(TablePartBox box, string name, int fallback, int min, int max) =>
        box.Node is ElementNode e && e.GetAttribute(name) is { } text && int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;

    // Each column's min-content and max-content width (css-tables-3 §3.9.3): single-column cells first, then spanning
    // cells share out what their columns lack, in proportion to the columns' max-content widths.
    private static (float[] Min, float[] Max) ColumnRanges(Grid grid, ComputedStyle tableStyle, LayoutContext context)
    {
        var n = grid.Columns;
        var (min, max) = (new float[n], new float[n]);
        for (var c = 0; c < Math.Min(n, grid.ColumnWidths.Count); c++)
        {
            if (grid.ColumnWidths[c] is { } w)
                (min[c], max[c]) = (w, w);
        }
        var spacing = tableStyle.Text.BorderCollapse == BorderCollapse.Collapse ? 0 : tableStyle.Text.BorderSpacingX;
        foreach (var cell in grid.Cells)
            (cell.Min, cell.Max) = IntrinsicSizes.Contribution(cell.Box, context);
        foreach (var cell in grid.Cells.Where(c => c.ColumnSpan == 1))
            (min[cell.Column], max[cell.Column]) = (Math.Max(min[cell.Column], cell.Min), Math.Max(max[cell.Column], cell.Max));
        foreach (var cell in grid.Cells.Where(c => c.ColumnSpan > 1).OrderBy(c => c.ColumnSpan))
        {
            var span = Enumerable.Range(cell.Column, Math.Min(cell.ColumnSpan, n - cell.Column)).ToList();
            var gaps = spacing * (span.Count - 1);
            Share(min, cell.Min - gaps);
            Share(max, cell.Max - gaps);

            void Share(float[] sizes, float needed)
            {
                var have = span.Sum(c => sizes[c]);
                if (needed <= have)
                    return;
                var weights = span.Sum(c => max[c]);
                foreach (var c in span)
                    sizes[c] += (needed - have) * (weights > 0 ? max[c] / weights : 1f / span.Count);
            }
        }
        for (var c = 0; c < n; c++)
            max[c] = Math.Max(max[c], min[c]);
        return (min, max);
    }

    private enum ColumnKind { Auto, Fixed, Percent }

    /// <summary>
    /// Column widths for the space the table gives its columns (css-tables-3 §3.9.4, simplified to min and max
    /// phases). A column whose cells or col give it a percentage width takes that share of the space (at least its
    /// minimum); the others go from their minimum towards their maximum widths, and space beyond all maximums goes to
    /// the columns with no specified width, in proportion to their maximums; only without such columns does it go to
    /// columns with a length width, and then to percentage columns.
    /// </summary>
    private static float[] ColumnWidths(Grid grid, ComputedStyle style, float target, LayoutContext context)
    {
        var n = grid.Columns;
        if (n == 0)
            return [];
        if (style.Box.TableLayout == TableLayoutMode.Fixed && style.Size.Width.Kind == SizeKind.Length)
            return FixedWidths(grid, target);
        var (min, max) = ColumnRanges(grid, style, context);
        if (target <= min.Sum())
            return min;

        var (kinds, percents) = (new ColumnKind[n], new float[n]);
        for (var c = 0; c < Math.Min(n, grid.ColumnWidths.Count); c++)
        {
            if (grid.ColumnWidths[c] is not null)
                kinds[c] = ColumnKind.Fixed;
        }
        foreach (var cell in grid.Cells.Where(c => c.ColumnSpan == 1))
        {
            if (cell.Box.Style.Size.Width is not { Kind: SizeKind.Length, Length: var w })
                continue;
            if (w is { Calc: null, Px: 0, Percent: > 0 })
                (kinds[cell.Column], percents[cell.Column]) = (ColumnKind.Percent, Math.Max(percents[cell.Column], w.Percent));
            else if (!w.HasPercent && kinds[cell.Column] == ColumnKind.Auto)
                kinds[cell.Column] = ColumnKind.Fixed;
        }

        var widths = new float[n];
        var others = Enumerable.Range(0, n).Where(c => kinds[c] != ColumnKind.Percent).ToList();
        foreach (var c in Enumerable.Range(0, n).Except(others))
            widths[c] = Math.Max(min[c], percents[c] / 100 * target);
        var rest = target - widths.Sum();
        var (othersMin, othersMax) = (others.Sum(c => min[c]), others.Sum(c => max[c]));
        if (rest < othersMin)
        {
            // The percentages leave the other columns too little: every column goes back to its share of the minimums.
            var sumMin = min.Sum();
            return [.. min.Select(m => m + (target - sumMin) * (sumMin > 0 ? m / sumMin : 1f / n))];
        }
        if (rest <= othersMax)
        {
            foreach (var c in others)
                widths[c] = min[c] + (max[c] - min[c]) * (othersMax > othersMin ? (rest - othersMin) / (othersMax - othersMin) : 0);
            return widths;
        }
        foreach (var c in others)
            widths[c] = max[c];
        var extra = rest - othersMax;
        var takers = new[] { ColumnKind.Auto, ColumnKind.Fixed, ColumnKind.Percent }
            .Select(kind => Enumerable.Range(0, n).Where(c => kinds[c] == kind && widths[c] > 0).ToList())
            .FirstOrDefault(list => list.Count > 0) ?? [.. Enumerable.Range(0, n)];
        var weight = takers.Sum(c => widths[c]);
        foreach (var c in takers)
            widths[c] += extra * (weight > 0 ? widths[c] / weight : 1f / takers.Count);
        return widths;
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

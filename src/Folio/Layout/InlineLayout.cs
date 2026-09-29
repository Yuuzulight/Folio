using System.Globalization;
using Folio.Style;
using Folio.Typography;

namespace Folio.Layout;

/// <summary>
/// An inline formatting context (docs/study/06-layout-block-and-inline.md, inline layout steps 3 to 6): text shaped
/// per font run with per-cluster fallback, break opportunities from UAX #14 tailored by <c>white-space</c>, greedy
/// line filling beside floats, line boxes sized from the strut and each piece's font and <c>line-height</c> with
/// everything on the baseline, and <c>text-align</c>.
/// </summary>
// ponytail: vertical-align (all baseline), bidi reordering, justification (as start), text-indent, soft hyphens shown
// at breaks, and shrink-to-fit widths for atomic inlines arrive in the next inline layout changes.
internal static class InlineLayout
{
    /// <summary>What the enclosing block layout provides: floats and positioned boxes are its to place.</summary>
    /// <param name="Available">The free horizontal space (content-box x from and to) beside floats for a band of lines.</param>
    /// <param name="NextFloatBottom">The first float bottom below a band's top, among floats beside it, or null.</param>
    /// <param name="PlaceFloat">Places a float found on a line, at that line's top.</param>
    /// <param name="AddOutOfFlow">Records a positioned box's static position (content-box coordinates).</param>
    public sealed record Environment(
        Func<float, float, (float Left, float Right)> Available,
        Func<float, float, float?> NextFloatBottom,
        Action<Box, float> PlaceFloat,
        Action<Box, float, float> AddOutOfFlow);

    /// <summary>Lines of an inline formatting context, from <paramref name="top"/> down, in content-box coordinates.</summary>
    public static (List<ChildFragment> Lines, float Bottom, bool HasLineBoxes) Layout(
        BlockContainerBox block, InlineFormattingContext ifc, float width, float top, Environment environment, LayoutContext context)
    {
        var units = Units(block, ifc, width, context);
        var strut = Metrics(block.Style, context);
        var lines = new List<ChildFragment>();
        var hasLineBoxes = false;
        var placedFloats = new HashSet<Box>();
        var openBoxes = new List<(InlineBox Box, ComputedStyle Style)>();
        var y = top;

        for (var u = 0; u < units.Count;)
        {
            var bandHeight = strut.Above + strut.Below;
            var (left, right) = environment.Available(y, y + bandHeight);
            var lineUnits = new List<Unit>();
            var x = 0f;
            while (u < units.Count)
            {
                var unit = units[u];
                foreach (var piece in unit.Pieces)
                {
                    if (piece.Kind == PieceKind.Float && placedFloats.Add(piece.Box!))
                    {
                        environment.PlaceFloat(piece.Box!, y);
                        (left, right) = environment.Available(y, y + bandHeight);
                    }
                }
                var fits = x + unit.Width - unit.TrailingSpace <= right - left + 0.01f;
                if (!fits && lineUnits.Count > 0)
                    break;
                if (!fits && environment.NextFloatBottom(y, y + bandHeight) is { } below)
                {
                    // Too wide beside the floats: the line moves down past them.
                    y = below;
                    (left, right) = environment.Available(y, y + bandHeight);
                    continue;
                }
                lineUnits.Add(unit);
                x += unit.Width;
                u++;
                if (unit.MandatoryBreakAfter)
                    break;
            }

            var line = BuildLine(block, lineUnits, openBoxes, right - left, width, strut, context, (box, px) => environment.AddOutOfFlow(box, left + px, y));
            if (line.Height > 0 || line.Children.Count > 0)
            {
                hasLineBoxes |= line.Height > 0;
                lines.Add(new ChildFragment(left, y, line));
            }
            y += line.Height;
        }
        return (lines, y, hasLineBoxes);
    }

    private enum PieceKind { Text, BoxStart, BoxEnd, Atomic, Float, OutOfFlow }

    // One piece of line content: glyphs of a run, an inline box edge, an atomic inline, or a marker.
    private sealed class Piece(PieceKind kind, ComputedStyle style, float width)
    {
        public PieceKind Kind { get; } = kind;
        public ComputedStyle Style { get; } = style;
        public float Width { get; } = width;
        public Box? Box { get; init; }
        public ShapedRun? Run { get; init; }
        public int GlyphStart { get; init; }
        public int GlyphEnd { get; init; }
        public Fragment? Atomic { get; init; }
        public float AtomicMarginLeft { get; init; }
        public float AtomicMarginTop { get; init; }
        public float AtomicMarginBottom { get; init; }
        public bool Visible { get; init; } // content that makes the line box a real one
    }

    // Content between two break opportunities: never broken inside.
    private sealed class Unit
    {
        public List<Piece> Pieces { get; } = [];
        public float Width { get; set; }
        public float TrailingSpace { get; set; } // collapsible or preserved spaces at the end, which hang at a line end
        public bool MandatoryBreakAfter { get; set; }
    }

    // Break the content into units; atomic inlines are laid out only for layout, not for measuring.
    private static List<Unit> Units(BlockContainerBox block, InlineFormattingContext ifc, float width, LayoutContext context, bool layOutAtomics = true)
    {
        var text = ifc.Text;
        var breaks = LineBreaker.Find(text);
        var nowrap = new bool[text.Length];
        foreach (var item in ifc.Items)
        {
            if (item.Kind == InlineItemKind.Text && item.Style.Text.TextWrapMode == TextWrapMode.Nowrap)
                Array.Fill(nowrap, true, item.Start, item.Length);
        }
        // A soft wrap opportunity at an offset, unless the text before it does not wrap; hard breaks always count.
        BreakKind BreakAt(int offset) => offset <= 0 || offset >= text.Length ? BreakKind.None
            : breaks[offset] == BreakKind.Mandatory ? BreakKind.Mandatory
            : breaks[offset] == BreakKind.Allowed && !nowrap[offset - 1] ? BreakKind.Allowed : BreakKind.None;

        var units = new List<Unit>();
        var unit = new Unit();
        var brokeAt = -1;
        void Close(bool mandatory = false)
        {
            unit.MandatoryBreakAfter |= mandatory;
            if (unit.Pieces.Count > 0 || mandatory)
                units.Add(unit);
            unit = new Unit();
        }
        void Add(Piece piece)
        {
            unit.Pieces.Add(piece);
            unit.Width += piece.Width;
            unit.TrailingSpace = 0;
        }

        foreach (var item in ifc.Items)
        {
            switch (item.Kind)
            {
                case InlineItemKind.OpenBox:
                    if (BreakAt(item.Start) is var b && b != BreakKind.None && brokeAt != item.Start)
                    {
                        Close(b == BreakKind.Mandatory);
                        brokeAt = item.Start;
                    }
                    Add(new Piece(PieceKind.BoxStart, item.Style, item.Continuation ? 0 : InlineStart(item.Style, width)) { Box = item.Box, Visible = !item.Continuation && InlineStart(item.Style, width) > 0 });
                    break;
                case InlineItemKind.CloseBox:
                    Add(new Piece(PieceKind.BoxEnd, item.Style, item.Continuation ? 0 : InlineEnd(item.Style, width)) { Box = item.Box, Visible = !item.Continuation && InlineEnd(item.Style, width) > 0 });
                    break;
                case InlineItemKind.Text:
                    foreach (var run in Shape(text, item.Start, item.Length, item.Style, context))
                    {
                        var start = 0;
                        for (var g = 0; g <= run.Glyphs.Length; g++)
                        {
                            var offset = g < run.Glyphs.Length ? run.Clusters[g] : -1;
                            var kind = offset >= 0 && brokeAt != offset ? BreakAt(offset) : BreakKind.None;
                            if (g == run.Glyphs.Length || kind != BreakKind.None)
                            {
                                if (g > start)
                                    AddGlyphs(run, start, g, item.Style);
                                if (kind != BreakKind.None)
                                {
                                    Close(kind == BreakKind.Mandatory);
                                    brokeAt = offset;
                                }
                                start = g;
                            }
                        }
                    }
                    break;
                case InlineItemKind.Atomic:
                {
                    Close();
                    var box = item.Box!;
                    if (!layOutAtomics)
                    {
                        Add(new Piece(PieceKind.Atomic, box.Style, 0) { Box = box, Visible = true });
                        Close();
                        break;
                    }
                    var fragment = BlockLayout.Layout(box, new ConstraintSpace(width, null), context);
                    var spacing = box.Style.Spacing;
                    var (ml, mr) = (BlockLayout.Margin(spacing.MarginLeft, width), BlockLayout.Margin(spacing.MarginRight, width));
                    var (mt, mb) = (BlockLayout.Margin(spacing.MarginTop, width), BlockLayout.Margin(spacing.MarginBottom, width));
                    Add(new Piece(PieceKind.Atomic, box.Style, ml + fragment.Width + mr)
                    {
                        Box = box, Atomic = fragment, AtomicMarginLeft = ml, AtomicMarginTop = mt, AtomicMarginBottom = mb, Visible = true,
                    });
                    Close();
                    break;
                }
                case InlineItemKind.ForcedBreak:
                    Close(mandatory: true);
                    break;
                case InlineItemKind.BreakOpportunity:
                    if (item.Style.Text.TextWrapMode == TextWrapMode.Wrap)
                        Close();
                    break;
                case InlineItemKind.Float:
                    Add(new Piece(PieceKind.Float, item.Style, 0) { Box = item.Box });
                    break;
                case InlineItemKind.OutOfFlow:
                    Add(new Piece(PieceKind.OutOfFlow, item.Style, 0) { Box = item.Box });
                    break;
            }
        }
        Close();
        return units;

        void AddGlyphs(ShapedRun run, int from, int to, ComputedStyle style)
        {
            var w = 0f;
            for (var g = from; g < to; g++)
                w += run.Advances[g];
            // Spaces at the end hang when the line ends here (css-text-3 §4.1.3).
            var trailing = 0f;
            var g2 = to - 1;
            for (; g2 >= from && text[run.Clusters[g2]] is ' ' or '\u3000'; g2--)
                trailing += run.Advances[g2];
            var visible = g2 >= from;
            visible |= style.Text.WhiteSpaceCollapse is WhiteSpaceCollapse.Preserve or WhiteSpaceCollapse.BreakSpaces && to > from;
            Add(new Piece(PieceKind.Text, style, w) { Run = run, GlyphStart = from, GlyphEnd = to, Visible = visible });
            unit.TrailingSpace = trailing;
        }
    }

    /// <summary>
    /// Min-content (the widest unit) and max-content (the widest line with only forced breaks) widths
    /// (css-sizing-3 §5.1); atomic inlines count with their own contributions, floats on their own.
    /// </summary>
    public static (float Min, float Max) Measure(BlockContainerBox block, InlineFormattingContext ifc, LayoutContext context)
    {
        float min = 0, max = 0, line = 0;
        foreach (var unit in Units(block, ifc, 0, context, layOutAtomics: false))
        {
            var (unitMin, unitMax) = (unit.Width - unit.TrailingSpace, unit.Width);
            foreach (var piece in unit.Pieces)
            {
                if (piece.Kind is PieceKind.Atomic or PieceKind.Float)
                {
                    var c = IntrinsicSizes.Contribution(piece.Box!, context);
                    if (piece.Kind == PieceKind.Float)
                    {
                        (min, max) = (Math.Max(min, c.Min), Math.Max(max, c.Max));
                        continue;
                    }
                    (unitMin, unitMax) = (unitMin + c.Min, unitMax + c.Max);
                }
            }
            min = Math.Max(min, unitMin);
            max = Math.Max(max, line + unitMax - unit.TrailingSpace);
            line += unitMax;
            if (unit.MandatoryBreakAfter)
                line = 0;
        }
        return (min, max);
    }

    private static float InlineStart(ComputedStyle style, float cbWidth) =>
        BlockLayout.Margin(style.Spacing.MarginLeft, cbWidth) + style.Border.LeftWidth + BlockLayout.Resolve(style.Spacing.PaddingLeft, cbWidth);

    private static float InlineEnd(ComputedStyle style, float cbWidth) =>
        BlockLayout.Margin(style.Spacing.MarginRight, cbWidth) + style.Border.RightWidth + BlockLayout.Resolve(style.Spacing.PaddingRight, cbWidth);

    // Shapes a stretch of text in runs of one face each, choosing the face per grapheme cluster (study 11, fallback).
    // ponytail: every run goes through SimpleShaper until complex shaping lands (#35); faces missing everywhere show
    // the first family's .notdef, or half-em blanks when no font is available at all.
    private static List<ShapedRun> Shape(string text, int start, int length, ComputedStyle style, LayoutContext context)
    {
        var font = style.Font;
        var faceStyle = FaceStyleOf(font.Style);
        var primary = PrimaryFace(style, context);
        var runs = new List<ShapedRun>();
        var runStart = start;
        FontFace? runFace = null;
        var end = start + length;
        for (var i = start; i < end;)
        {
            var clusterLength = Math.Min(StringInfo.GetNextTextElementLength(text, i), end - i);
            var face = context.Fonts.FaceForCluster(font.Family, faceStyle, font.Weight, font.Stretch, text.AsSpan(i, clusterLength)) ?? primary;
            if (i > runStart && face != runFace)
            {
                runs.Add(ShapeRun(text, runStart, i - runStart, runFace, font.Size));
                runStart = i;
            }
            runFace = face;
            i += clusterLength;
        }
        if (end > runStart)
            runs.Add(ShapeRun(text, runStart, end - runStart, runFace, font.Size));
        return runs;
    }

    private static ShapedRun ShapeRun(string text, int start, int length, FontFace? face, float size)
    {
        var run = face is not null ? SimpleShaper.Shape(text, start, length, face, size)
            : new ShapedRun(null, size, new ushort[length], [.. Enumerable.Range(start, length)], [.. Enumerable.Repeat(size / 2, length)]);
        // Controls and format characters take no space; a tab is eight spaces (tab-size's initial value).
        for (var g = 0; g < run.Glyphs.Length; g++)
        {
            var c = text[run.Clusters[g]];
            if (c is '\n' or '\r' || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                run.Advances[g] = 0;
            else if (c == '\t')
                run.Advances[g] = 8 * (face is not null ? face.Advance(face.GlyphFor(' ')) * size / face.UnitsPerEm : size / 2);
        }
        return run;
    }

    private static FaceStyle FaceStyleOf(Style.FontStyle style) => style switch
    {
        Style.FontStyle.Italic => FaceStyle.Italic,
        Style.FontStyle.Oblique => FaceStyle.Oblique,
        _ => FaceStyle.Normal,
    };

    private static FontFace? PrimaryFace(ComputedStyle style, LayoutContext context)
    {
        foreach (var family in style.Font.Family)
        {
            if (context.Fonts.Match(family, FaceStyleOf(style.Font.Style), style.Font.Weight, style.Font.Stretch) is { } face)
                return face;
        }
        return null;
    }

    // An inline box's layout bounds around the baseline (CSS 2.2 §10.8.1): the font's ascent and descent, with half
    // the leading from line-height added above and below.
    private readonly record struct LineMetrics(float Ascent, float Descent, float Above, float Below);

    private static LineMetrics Metrics(ComputedStyle style, LayoutContext context) => Metrics(style, PrimaryFace(style, context));

    private static LineMetrics Metrics(ComputedStyle style, FontFace? face)
    {
        var size = style.Font.Size;
        var (ascent, descent, gap) = face is null ? (0.8f * size, 0.2f * size, 0.2f * size)
            : (face.Ascent * size / face.UnitsPerEm, -face.Descent * size / face.UnitsPerEm, face.LineGap * size / face.UnitsPerEm);
        var lineHeight = style.Font.LineHeight switch
        {
            { IsNormal: true } => ascent + descent + gap,
            { Px: { } px } => px,
            var l => l.Number * size,
        };
        var halfLeading = (lineHeight - ascent - descent) / 2;
        return new LineMetrics(ascent, descent, ascent + halfLeading, descent + halfLeading);
    }

    private static Fragment BuildLine(BlockContainerBox block, List<Unit> units, List<(InlineBox Box, ComputedStyle Style)> openBoxes,
                                      float available, float cbWidth, LineMetrics strut, LayoutContext context, Action<Box, float> addOutOfFlow)
    {
        var pieces = units.SelectMany(u => u.Pieces).ToList();
        var contentWidth = units.Sum(u => u.Width) - (units.Count > 0 ? units[^1].TrailingSpace : 0);
        var visible = pieces.Any(p => p.Visible) || openBoxes.Count > 0 && pieces.Any(p => p.Kind == PieceKind.Text);

        // Vertical extent: everything sits on the baseline.
        float above = strut.Above, below = strut.Below;
        void Extend(LineMetrics m)
        {
            above = Math.Max(above, m.Above);
            below = Math.Max(below, m.Below);
        }
        foreach (var (_, style) in openBoxes)
            Extend(Metrics(style, context));
        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case PieceKind.Text:
                    Extend(Metrics(piece.Style, piece.Run!.Face));
                    break;
                case PieceKind.BoxStart:
                    Extend(Metrics(piece.Style, context));
                    break;
                case PieceKind.Atomic:
                    // Its bottom margin edge sits on the baseline (the baseline of inline-blocks comes with vertical-align).
                    above = Math.Max(above, piece.AtomicMarginTop + piece.Atomic!.Height + piece.AtomicMarginBottom);
                    break;
            }
        }
        if (!visible)
            above = below = 0; // a line with nothing to show has no height (css-inline-3 \u00A72.1, CSS 2.2 \u00A79.4.2)
        var baseline = above;

        var free = available - contentWidth;
        var shift = block.Style.Text.TextAlign switch
        {
            TextAlign.Right or TextAlign.End => free,
            TextAlign.Center => free / 2,
            _ => 0,
        };

        var boxFragments = new List<ChildFragment>();
        var textFragments = new List<ChildFragment>();
        var boxStarts = openBoxes.Select(_ => shift).ToList();
        var x = shift;
        void CloseBox(int index, float end)
        {
            var (box, style) = openBoxes[index];
            var m = Metrics(style, context);
            var (bt, bb) = (style.Border.TopWidth, style.Border.BottomWidth);
            var (pt, pb) = (BlockLayout.Resolve(style.Spacing.PaddingTop, cbWidth), BlockLayout.Resolve(style.Spacing.PaddingBottom, cbWidth));
            var top = baseline - m.Ascent - pt - bt;
            boxFragments.Add(new ChildFragment(boxStarts[index], top, new Fragment(box, Math.Max(0, end - boxStarts[index]), m.Ascent + m.Descent + pt + pb + bt + bb, [])));
        }

        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case PieceKind.BoxStart:
                    openBoxes.Add(((InlineBox)piece.Box!, piece.Style));
                    boxStarts.Add(x + BlockLayout.Margin(piece.Style.Spacing.MarginLeft, cbWidth));
                    break;
                case PieceKind.BoxEnd:
                {
                    var index = openBoxes.FindLastIndex(o => o.Box == piece.Box);
                    if (index >= 0)
                    {
                        CloseBox(index, x + piece.Width - BlockLayout.Margin(piece.Style.Spacing.MarginRight, cbWidth));
                        openBoxes.RemoveAt(index);
                        boxStarts.RemoveAt(index);
                    }
                    break;
                }
                case PieceKind.Text:
                {
                    var m = Metrics(piece.Style, piece.Run!.Face);
                    textFragments.Add(new ChildFragment(x, baseline - m.Ascent, new Fragment(block, piece.Width, m.Ascent + m.Descent, [])
                    {
                        Kind = FragmentKind.Text,
                        Text = new TextRun(piece.Run, piece.GlyphStart, piece.GlyphEnd, m.Ascent),
                    }));
                    break;
                }
                case PieceKind.Atomic:
                    textFragments.Add(new ChildFragment(x + piece.AtomicMarginLeft, baseline - piece.AtomicMarginBottom - piece.Atomic!.Height, piece.Atomic));
                    break;
                case PieceKind.OutOfFlow:
                    addOutOfFlow(piece.Box!, x);
                    break;
            }
            x += piece.Width;
        }
        // Boxes still open continue on the next line; their fragment on this line ends here.
        for (var i = 0; i < openBoxes.Count; i++)
            CloseBox(i, x);
        boxStarts.Clear();

        return new Fragment(block, available, above + below, [.. boxFragments, .. textFragments])
        {
            Kind = FragmentKind.Line,
            Baseline = baseline,
        };
    }
}

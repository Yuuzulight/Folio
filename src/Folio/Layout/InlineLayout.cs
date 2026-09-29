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
// ponytail: justification (as start), text-indent, soft hyphens shown at breaks, and bidi's L1 reset of whitespace
// at soft line ends are not done yet.
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
        var levels = BidiLevels(block, ifc);
        var units = Units(block, ifc, width, context, levels);
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

            var line = BuildLine(block, lineUnits, openBoxes, right - left, width, strut, levels.Paragraph, context, (box, px) => environment.AddOutOfFlow(box, left + px, y));
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
        public byte Level { get; init; } // bidi embedding level (text and atomic inlines)
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
    private static List<Unit> Units(BlockContainerBox block, InlineFormattingContext ifc, float width, LayoutContext context, Levels levels,
                                    bool layOutAtomics = true)
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
                    foreach (var run in ShapeByLevel(text, item.Start, item.Length, item.Style, context, levels.Text))
                    {
                        var start = 0;
                        for (var g = 0; g <= run.Glyphs.Length; g++)
                        {
                            var offset = g < run.Glyphs.Length ? run.Clusters[g] : -1;
                            var kind = offset >= 0 && brokeAt != offset ? BreakAt(offset) : BreakKind.None;
                            if (g == run.Glyphs.Length || kind != BreakKind.None)
                            {
                                if (g > start)
                                    AddGlyphs(run, start, g, item.Style, levels.Text[run.Clusters[start]]);
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
                        Add(new Piece(PieceKind.Atomic, box.Style, 0) { Box = box, Visible = true, Level = levels.Atomics.GetValueOrDefault(box) });
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
                        Level = levels.Atomics.GetValueOrDefault(box),
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

        void AddGlyphs(ShapedRun run, int from, int to, ComputedStyle style, byte level)
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
            Add(new Piece(PieceKind.Text, style, w) { Run = run, GlyphStart = from, GlyphEnd = to, Visible = visible, Level = level });
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
        foreach (var unit in Units(block, ifc, 0, context, BidiLevels(block, ifc), layOutAtomics: false))
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

    // Bidi levels of a paragraph's text (per UTF-16 offset) and atomic inlines, with its paragraph level.
    private sealed record Levels(byte[] Text, Dictionary<Box, byte> Atomics, int Paragraph);

    /// <summary>
    /// Resolves the paragraph's bidi levels (UAX #9) with the embeddings, isolates and overrides its inline boxes ask
    /// for (css-writing-modes-3 §2.4.2); atomic inlines are U+FFFC and forced breaks U+2029.
    /// </summary>
    private static Levels BidiLevels(BlockContainerBox block, InlineFormattingContext ifc)
    {
        var text = ifc.Text;
        int? paragraph = block.Style.Box.UnicodeBidi == UnicodeBidi.Plaintext ? null : block.Style.Text.Direction == Direction.Rtl ? 1 : 0;
        var codePoints = new List<int>(text.Length + 8);
        var textIndex = new int[text.Length];
        var atomicIndex = new Dictionary<Box, int>();
        var needed = paragraph != 0;
        foreach (var item in ifc.Items)
        {
            switch (item.Kind)
            {
                case InlineItemKind.OpenBox or InlineItemKind.CloseBox:
                {
                    var (open, close) = Controls(item.Style);
                    codePoints.AddRange(item.Kind == InlineItemKind.OpenBox ? open : close);
                    needed |= open.Length > 0;
                    break;
                }
                case InlineItemKind.Text:
                    for (var i = item.Start; i < item.Start + item.Length; i++)
                    {
                        textIndex[i] = codePoints.Count;
                        var cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];
                        if (cp > 0xFFFF)
                            textIndex[++i] = codePoints.Count;
                        codePoints.Add(cp);
                        needed |= UnicodeData.Bidi(cp) is BidiClass.R or BidiClass.AL or BidiClass.AN;
                    }
                    break;
                case InlineItemKind.Atomic:
                    atomicIndex[item.Box!] = codePoints.Count;
                    codePoints.Add(0xFFFC);
                    break;
                case InlineItemKind.ForcedBreak:
                    codePoints.Add(0x2029);
                    break;
            }
        }
        if (!needed)
            return new Levels(new byte[text.Length], [], 0);

        var (levels, resolved) = Bidi.Resolve(codePoints.ToArray(), paragraph);
        byte Level(int index) => levels[index] == Bidi.Removed ? (byte)resolved : levels[index];
        var textLevels = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
            textLevels[i] = Level(textIndex[i]);
        return new Levels(textLevels, atomicIndex.ToDictionary(a => a.Key, a => Level(a.Value)), resolved);
    }

    // The controls an inline box's unicode-bidi and direction stand for, at its start and end.
    private static (int[] Open, int[] Close) Controls(ComputedStyle style)
    {
        var rtl = style.Text.Direction == Direction.Rtl;
        return style.Box.UnicodeBidi switch
        {
            UnicodeBidi.Embed => ([rtl ? 0x202B : 0x202A], [0x202C]),
            UnicodeBidi.Isolate => ([rtl ? 0x2067 : 0x2066], [0x2069]),
            UnicodeBidi.BidiOverride => ([rtl ? 0x202E : 0x202D], [0x202C]),
            UnicodeBidi.IsolateOverride => ([rtl ? 0x2067 : 0x2066, rtl ? 0x202E : 0x202D], [0x202C, 0x2069]),
            UnicodeBidi.Plaintext => ([0x2068], [0x2069]),
            _ => ([], []),
        };
    }

    // The visual order of a line's pieces (L2); edges and markers borrow a neighbour's level.
    private static List<int> VisualOrder(List<Piece> pieces, int paragraphLevel)
    {
        var levels = new byte[pieces.Count];
        var any = false;
        for (var i = 0; i < pieces.Count; i++)
        {
            levels[i] = pieces[i].Kind is PieceKind.Text or PieceKind.Atomic ? pieces[i].Level : byte.MaxValue;
            any |= levels[i] != byte.MaxValue && levels[i] != 0;
        }
        if (!any && paragraphLevel == 0)
            return [.. Enumerable.Range(0, pieces.Count)];
        for (var i = 0; i < pieces.Count; i++)
        {
            if (levels[i] != byte.MaxValue)
                continue;
            // Box starts look ahead, everything else looks back, then the other way, then the paragraph level.
            var ahead = pieces[i].Kind == PieceKind.BoxStart;
            levels[i] = Neighbour(i, ahead ? 1 : -1) ?? Neighbour(i, ahead ? -1 : 1) ?? (byte)paragraphLevel;
        }
        return Bidi.Reorder(levels, 0, levels.Length);

        byte? Neighbour(int i, int step)
        {
            for (var j = i + step; j >= 0 && j < pieces.Count; j += step)
            {
                if (pieces[j].Kind is PieceKind.Text or PieceKind.Atomic)
                    return pieces[j].Level;
            }
            return null;
        }
    }

    // Shapes text in runs of one bidi level each; right-to-left runs use mirrored glyphs (UAX #9 L4).
    private static IEnumerable<ShapedRun> ShapeByLevel(string text, int start, int length, ComputedStyle style, LayoutContext context, byte[] levels)
    {
        var end = start + length;
        for (var runStart = start; runStart < end;)
        {
            var runEnd = runStart + 1;
            while (runEnd < end && levels[runEnd] == levels[runStart])
                runEnd++;
            foreach (var run in Shape(text, runStart, runEnd - runStart, style, context))
            {
                if (levels[runStart] % 2 == 1 && run.Face is { } face)
                {
                    for (var g = 0; g < run.Glyphs.Length; g++)
                    {
                        if (UnicodeData.Mirror(char.ConvertToUtf32(text, run.Clusters[g])) is { } mirror && face.Covers(mirror))
                            run.Glyphs[g] = face.GlyphFor(mirror);
                    }
                }
                yield return run;
            }
            runStart = runEnd;
        }
    }

    // An inline box's margin, border and padding at its inline start and end: left and right, swapped in rtl.
    private static float InlineStart(ComputedStyle style, float cbWidth) => style.Text.Direction == Direction.Rtl ? Right(style, cbWidth) : Left(style, cbWidth);

    private static float InlineEnd(ComputedStyle style, float cbWidth) => style.Text.Direction == Direction.Rtl ? Left(style, cbWidth) : Right(style, cbWidth);

    private static float Left(ComputedStyle style, float cbWidth) =>
        BlockLayout.Margin(style.Spacing.MarginLeft, cbWidth) + style.Border.LeftWidth + BlockLayout.Resolve(style.Spacing.PaddingLeft, cbWidth);

    private static float Right(ComputedStyle style, float cbWidth) =>
        BlockLayout.Margin(style.Spacing.MarginRight, cbWidth) + style.Border.RightWidth + BlockLayout.Resolve(style.Spacing.PaddingRight, cbWidth);

    private static float StartMargin(ComputedStyle style, float cbWidth) =>
        BlockLayout.Margin(style.Text.Direction == Direction.Rtl ? style.Spacing.MarginRight : style.Spacing.MarginLeft, cbWidth);

    private static float EndMargin(ComputedStyle style, float cbWidth) =>
        BlockLayout.Margin(style.Text.Direction == Direction.Rtl ? style.Spacing.MarginLeft : style.Spacing.MarginRight, cbWidth);

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
    private readonly record struct LineMetrics(float Ascent, float Descent, float Above, float Below, float XHeight, float Size, float LineHeight);

    private static LineMetrics Metrics(ComputedStyle style, LayoutContext context) => Metrics(style, PrimaryFace(style, context));

    private static LineMetrics Metrics(ComputedStyle style, FontFace? face)
    {
        var size = style.Font.Size;
        var (ascent, descent, gap) = face is null ? (0.8f * size, 0.2f * size, 0.2f * size)
            : (face.Ascent * size / face.UnitsPerEm, -face.Descent * size / face.UnitsPerEm, face.LineGap * size / face.UnitsPerEm);
        var xHeight = face is { XHeight: > 0 } ? face.XHeight * size / face.UnitsPerEm : size / 2;
        var lineHeight = style.Font.LineHeight switch
        {
            { IsNormal: true } => ascent + descent + gap,
            { Px: { } px } => px,
            var l => l.Number * size,
        };
        var halfLeading = (lineHeight - ascent - descent) / 2;
        return new LineMetrics(ascent, descent, ascent + halfLeading, descent + halfLeading, xHeight, size, lineHeight);
    }

    // The line as a tree: the root (the block's strut), inline boxes, and text and atomic leaves. Each node's
    // baseline is raised by Shift above its parent's (vertical-align), or aligned with the line box's top or bottom.
    private sealed class Node(Node? parent, ComputedStyle style, LineMetrics metrics)
    {
        public Node? Parent { get; } = parent;
        public ComputedStyle Style { get; } = style;
        public LineMetrics Metrics { get; } = metrics;
        public List<Node> Children { get; } = [];
        public InlineBox? Box { get; init; }
        public Piece? Piece { get; init; }
        public float X { get; set; }
        public float End { get; set; }
        public float Shift { get; set; }
        public VerticalAlignKind? Edge { get; set; }
        public float Above { get; set; }
        public float Below { get; set; }
        public float Baseline { get; set; }
    }

    private static Fragment BuildLine(BlockContainerBox block, List<Unit> units, List<(InlineBox Box, ComputedStyle Style)> openBoxes,
                                      float available, float cbWidth, LineMetrics strut, int paragraphLevel, LayoutContext context,
                                      Action<Box, float> addOutOfFlow)
    {
        var pieces = units.SelectMany(u => u.Pieces).ToList();
        var contentWidth = units.Sum(u => u.Width) - (units.Count > 0 ? units[^1].TrailingSpace : 0);
        var visible = pieces.Any(p => p.Visible) || openBoxes.Count > 0 && pieces.Any(p => p.Kind == PieceKind.Text);
        var free = available - contentWidth;
        var rtl = paragraphLevel == 1;
        var align = block.Style.Text.TextAlign;
        var x = align == TextAlign.Center ? free / 2
            : align == TextAlign.Right || align == TextAlign.End && !rtl || align is TextAlign.Start or TextAlign.Justify && rtl ? free
            : 0;

        // Horizontal: pieces in visual order (UAX #9 L2 over the line). Box edges and markers take the level of the
        // content next to them, so an inline box's start edge follows its content's direction.
        var order = VisualOrder(pieces, paragraphLevel);
        var pieceX = new Dictionary<Piece, float>(pieces.Count);
        foreach (var index in order)
        {
            pieceX[pieces[index]] = x;
            x += pieces[index].Width;
        }
        var lineEnd = x;

        // Build the tree in logical order; every open box collects the pieces inside it.
        var root = new Node(null, block.Style, strut);
        var current = root;
        var boxes = new List<(Node Node, List<Piece> Pieces, Piece? Start, Piece? End)>();
        void OpenBox(InlineBox box, ComputedStyle style, Piece? start)
        {
            var node = new Node(current, style, Metrics(style, context)) { Box = box };
            current.Children.Add(node);
            boxes.Add((node, [], start, null));
            current = node;
        }
        void Collect(Piece piece)
        {
            for (var i = boxes.Count - 1; i >= 0; i--)
            {
                if (IsOpen(boxes[i].Node))
                    boxes[i].Pieces.Add(piece);
            }
        }
        bool IsOpen(Node node)
        {
            for (var n = current; n is not null; n = n.Parent)
            {
                if (n == node)
                    return true;
            }
            return false;
        }
        foreach (var (box, style) in openBoxes)
            OpenBox(box, style, null);
        foreach (var piece in pieces)
        {
            switch (piece.Kind)
            {
                case PieceKind.BoxStart:
                    OpenBox((InlineBox)piece.Box!, piece.Style, piece);
                    Collect(piece);
                    openBoxes.Add(((InlineBox)piece.Box!, piece.Style));
                    break;
                case PieceKind.BoxEnd:
                    Collect(piece);
                    for (var node = current; node != root; node = node.Parent!)
                    {
                        if (node.Box == piece.Box)
                        {
                            var i = boxes.FindIndex(b => b.Node == node);
                            boxes[i] = boxes[i] with { End = piece };
                            current = node.Parent!;
                            openBoxes.RemoveAt(openBoxes.FindLastIndex(o => o.Box == piece.Box));
                            break;
                        }
                    }
                    break;
                case PieceKind.Text:
                    Collect(piece);
                    current.Children.Add(new Node(current, current.Style, Metrics(current.Style, piece.Run!.Face)) { Piece = piece, X = pieceX[piece] });
                    break;
                case PieceKind.Atomic:
                {
                    Collect(piece);
                    // Its baseline is its last line's, or its bottom margin edge (CSS 2.2 §10.8.1).
                    var fragment = piece.Atomic!;
                    var baseline = AtomicBaseline(fragment);
                    var (above, below) = baseline is { } b
                        ? (piece.AtomicMarginTop + b, fragment.Height - b + piece.AtomicMarginBottom)
                        : (piece.AtomicMarginTop + fragment.Height + piece.AtomicMarginBottom, 0f);
                    current.Children.Add(new Node(current, piece.Style, new LineMetrics(above, below, above, below, 0, 0, above + below))
                    {
                        Piece = piece, X = pieceX[piece] + piece.AtomicMarginLeft,
                    });
                    break;
                }
                case PieceKind.OutOfFlow:
                    addOutOfFlow(piece.Box!, pieceX[piece]);
                    break;
            }
        }
        // Each box spans its pieces; its start and end margins sit outside its border, on whichever side they fell.
        foreach (var (node, boxPieces, start, end) in boxes)
        {
            if (boxPieces.Count == 0)
            {
                node.X = node.End = lineEnd;
                continue;
            }
            var left = boxPieces.Min(p => pieceX[p]);
            var right = boxPieces.Max(p => pieceX[p] + p.Width);
            if (start is not null)
            {
                var margin = StartMargin(start.Style, cbWidth);
                if (pieceX[start] <= left)
                    left += margin;
                else
                    right -= margin;
            }
            if (end is not null)
            {
                var margin = EndMargin(end.Style, cbWidth);
                if (pieceX[end] + end.Width >= right)
                    right -= margin;
                else
                    left += margin;
            }
            (node.X, node.End) = (left, right);
        }

        // Vertical: shifts, then extents bottom-up, then the line box and every baseline.
        var edges = new List<Node>();
        Measure(root);
        float lineAbove = root.Above, lineBelow = root.Below;
        foreach (var edge in edges)
        {
            if (edge.Above + edge.Below > lineAbove + lineBelow)
            {
                if (edge.Edge == VerticalAlignKind.Top)
                    lineBelow = edge.Above + edge.Below - lineAbove;
                else
                    lineAbove = edge.Above + edge.Below - lineBelow;
            }
        }
        if (!visible)
            lineAbove = lineBelow = 0; // a line with nothing to show has no height (css-inline-3 \u00A72.1, CSS 2.2 \u00A79.4.2)
        var height = lineAbove + lineBelow;
        root.Baseline = lineAbove;
        Place(root);

        var boxFragments = new List<ChildFragment>();
        var contentFragments = new List<ChildFragment>();
        Emit(root);
        return new Fragment(block, available, height, [.. boxFragments, .. contentFragments])
        {
            Kind = FragmentKind.Line,
            Baseline = root.Baseline,
        };

        void Measure(Node node)
        {
            float above = node.Metrics.Above, below = node.Metrics.Below;
            foreach (var child in node.Children)
            {
                Measure(child);
                Align(child, node);
                if (child.Edge is not null)
                {
                    edges.Add(child);
                    continue;
                }
                above = Math.Max(above, child.Shift + child.Above);
                below = Math.Max(below, child.Below - child.Shift);
            }
            (node.Above, node.Below) = (above, below);
        }

        // vertical-align of a box or atomic inline relative to its parent (text follows its box).
        // ponytail: sub and super move by fixed fractions of the parent's font size, not the font's own offsets.
        void Align(Node node, Node parent)
        {
            if (node.Box is null && node.Piece?.Kind != PieceKind.Atomic)
                return;
            var align = node.Style.Box.VerticalAlign;
            var (own, p) = (node.Metrics, parent.Metrics);
            node.Shift = align.Kind switch
            {
                VerticalAlignKind.Sub => -p.Size / 5,
                VerticalAlignKind.Super => p.Size / 3,
                VerticalAlignKind.TextTop => p.Ascent - own.Above,
                VerticalAlignKind.TextBottom => own.Below - p.Descent,
                VerticalAlignKind.Middle => p.XHeight / 2 - (own.Above - own.Below) / 2,
                VerticalAlignKind.Length => align.Length.Resolve(own.LineHeight),
                _ => 0,
            };
            if (align.Kind is VerticalAlignKind.Top or VerticalAlignKind.Bottom)
                node.Edge = align.Kind;
        }

        void Place(Node node)
        {
            foreach (var child in node.Children)
            {
                child.Baseline = child.Edge switch
                {
                    VerticalAlignKind.Top => child.Above,
                    VerticalAlignKind.Bottom => height - child.Below,
                    _ => node.Baseline - child.Shift,
                };
                Place(child);
            }
        }

        void Emit(Node node)
        {
            foreach (var child in node.Children)
            {
                if (child.Box is { } box)
                {
                    var m = child.Metrics;
                    var style = child.Style;
                    var (bt, bb) = (style.Border.TopWidth, style.Border.BottomWidth);
                    var (pt, pb) = (BlockLayout.Resolve(style.Spacing.PaddingTop, cbWidth), BlockLayout.Resolve(style.Spacing.PaddingBottom, cbWidth));
                    boxFragments.Add(new ChildFragment(child.X, child.Baseline - m.Ascent - pt - bt,
                        new Fragment(box, Math.Max(0, child.End - child.X), m.Ascent + m.Descent + pt + pb + bt + bb, [])));
                    Emit(child);
                }
                else if (child.Piece is { Kind: PieceKind.Text } text)
                {
                    var m = child.Metrics;
                    contentFragments.Add(new ChildFragment(child.X, child.Baseline - m.Ascent, new Fragment(block, text.Width, m.Ascent + m.Descent, [])
                    {
                        Kind = FragmentKind.Text,
                        Text = new TextRun(text.Run!, text.GlyphStart, text.GlyphEnd, m.Ascent, text.Level % 2 == 1),
                    }));
                }
                else if (child.Piece is { Kind: PieceKind.Atomic } atomic)
                {
                    contentFragments.Add(new ChildFragment(child.X, child.Baseline - child.Above + atomic.AtomicMarginTop, atomic.Atomic!));
                }
            }
        }
    }

    // The baseline of an inline-block: its last in-flow line box's, from its top; none when it has no line boxes or
    // clips its overflow.
    private static float? AtomicBaseline(Fragment fragment)
    {
        if (fragment.Box is { } box && (box.Style.Box.OverflowX != Overflow.Visible || box.Style.Box.OverflowY != Overflow.Visible || box is not BlockContainerBox))
            return null;
        return LastBaseline(fragment);

        static float? LastBaseline(Fragment f)
        {
            for (var i = f.Children.Count - 1; i >= 0; i--)
            {
                var (child, y) = (f.Children[i].Fragment, f.Children[i].Y);
                if (child.Kind == FragmentKind.Line && child.Height > 0)
                    return y + child.Baseline;
                if (child.Kind == FragmentKind.Box && child.Box is { IsFloat: false, IsAbsolutelyPositioned: false } && LastBaseline(child) is { } inner)
                    return y + inner;
            }
            return null;
        }
    }
}

using System.Globalization;
using Folio.Style;
using Folio.Typography;

namespace Folio.Layout;

/// <summary>
/// An inline formatting context (docs/study/06-layout-block-and-inline.md, inline layout steps 3 to 6): text shaped
/// per font run with per-cluster fallback, break opportunities from UAX #14 tailored by <c>white-space</c>, greedy
/// line filling beside floats, line boxes sized from the strut and each piece's font and <c>line-height</c> with
/// everything on the baseline, <c>text-indent</c>, <c>text-align</c> with justification, and hyphens shown where lines
/// break at soft hyphens.
/// </summary>
// ponytail: bidi's L1 reset of whitespace at soft line ends is not done yet.
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
        var indent = block.Node is Dom.ElementNode ? block.Style.Text.TextIndent : default; // not in anonymous blocks
        var rtl = levels.Paragraph == 1;
        var afterForcedBreak = true;

        // line-clamp (css-overflow-4 §4): only that many lines are laid out; the last one ends with an ellipsis when
        // content was left out.
        var clamp = block.Style.Box.LineClamp;
        for (var u = 0; u < units.Count && (clamp is not { } most || lines.Count < most);)
        {
            var bandHeight = strut.Above + strut.Below;
            // text-indent moves the first line's start edge (or every other line's, when hanging; each-line counts lines
            // after forced breaks as first lines too).
            var first = lines.Count == 0 || indent.EachLine && afterForcedBreak;
            var shift = first != indent.Hanging ? indent.Length.Resolve(width) : 0;
            (float Left, float Right) Band()
            {
                var (l, r) = environment.Available(y, y + bandHeight);
                return rtl ? (l, r - shift) : (l + shift, r);
            }
            var (left, right) = Band();
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
                        (left, right) = Band();
                    }
                }
                var fits = x + unit.Width - unit.TrailingSpace + unit.HyphenWidth <= right - left + 0.01f;
                if (!fits && lineUnits.Count > 0)
                    break;
                if (!fits && environment.NextFloatBottom(y, y + bandHeight) is { } below)
                {
                    // Too wide beside the floats: the line moves down past them.
                    y = below;
                    (left, right) = Band();
                    continue;
                }
                if (!fits && SplitToFit(unit, right - left) is var (head, tail))
                {
                    // Alone and still too wide: overflow-wrap breaks it where it would otherwise overflow.
                    (units[u], unit) = (head, head);
                    units.Insert(u + 1, tail);
                }
                lineUnits.Add(unit);
                x += unit.Width;
                u++;
                if (unit.MandatoryBreakAfter)
                    break;
            }

            afterForcedBreak = lineUnits.Count > 0 && lineUnits[^1].MandatoryBreakAfter;
            var clipped = clamp is { } limit && lines.Count == limit - 1 && u < units.Count;
            var line = BuildLine(block, ifc.Text, lineUnits, u == units.Count || afterForcedBreak, openBoxes, right - left, width, strut,
                levels.Paragraph, context, (box, px) => environment.AddOutOfFlow(box, left + px, y), clipped);
            if (line.Height > 0 || line.Children.Count > 0)
            {
                hasLineBoxes |= line.Height > 0;
                lines.Add(new ChildFragment(left, y, line));
            }
            y += line.Height;
        }
        return (lines, y, hasLineBoxes);
    }

    private const char SoftHyphen = '\u00AD';

    private enum PieceKind { Text, BoxStart, BoxEnd, Atomic, Float, OutOfFlow }

    // One piece of line content: glyphs of a run, an inline box edge, an atomic inline, or a marker.
    private sealed class Piece(PieceKind kind, ComputedStyle style, float width)
    {
        public PieceKind Kind { get; } = kind;
        public ComputedStyle Style { get; } = style;
        public float Width { get; set; } = width;
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
        public string? Replacement { get; init; } // text of an inserted ellipsis, whose run is not the context's text
    }

    // Content between two break opportunities: never broken inside.
    private sealed class Unit
    {
        public List<Piece> Pieces { get; } = [];
        public float Width { get; set; }
        public float TrailingSpace { get; set; } // collapsible or preserved spaces at the end, which hang at a line end
        public bool MandatoryBreakAfter { get; set; }

        // A unit ending at a soft hyphen: the text piece with it, and the hyphen shown there if the line breaks here.
        public Piece? Hyphen { get; set; }
        public ushort HyphenGlyph { get; set; }
        public float HyphenWidth { get; set; }
    }

    // Break the content into units; atomic inlines are laid out only for layout, not for measuring.
    private static List<Unit> Units(BlockContainerBox block, InlineFormattingContext ifc, float width, LayoutContext context, Levels levels,
                                    bool layOutAtomics = true)
    {
        var text = ifc.Text;
        var breaks = LineBreaker.Find(text);
        var nowrap = new bool[text.Length];
        var noHyphens = new bool[text.Length];
        var wordBreak = new WordBreakStyle[text.Length];
        foreach (var item in ifc.Items)
        {
            if (item.Kind != InlineItemKind.Text)
                continue;
            if (item.Style.Text.TextWrapMode == TextWrapMode.Nowrap)
                Array.Fill(nowrap, true, item.Start, item.Length);
            if (item.Style.Text.Hyphens == Hyphens.None)
                Array.Fill(noHyphens, true, item.Start, item.Length);
            Array.Fill(wordBreak, item.Style.TextSpacing.WordBreak, item.Start, item.Length);
        }
        // A soft wrap opportunity at an offset, unless the text before it does not wrap (or is a soft hyphen with
        // hyphens: none, https://www.w3.org/TR/css-text-3/#hyphens-property); hard breaks always count. word-break
        // (css-text-3 §5.2): break-all adds one between any two letters, keep-all removes those between letters.
        BreakKind BreakAt(int offset)
        {
            if (offset <= 0 || offset >= text.Length)
                return BreakKind.None;
            if (breaks[offset] == BreakKind.Mandatory)
                return BreakKind.Mandatory;
            if (nowrap[offset - 1] || text[offset - 1] == SoftHyphen && noHyphens[offset - 1])
                return BreakKind.None;
            var (before, after) = (text[offset - 1], text[offset]);
            var letters = !char.IsWhiteSpace(before) && !char.IsWhiteSpace(after) && !char.IsLowSurrogate(after)
                          && CharUnicodeInfo.GetUnicodeCategory(after) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                              or UnicodeCategory.EnclosingMark or UnicodeCategory.Format);
            return breaks[offset] == BreakKind.Allowed ? (wordBreak[offset - 1] == WordBreakStyle.KeepAll && letters && char.IsLetterOrDigit(before) && char.IsLetterOrDigit(after) ? BreakKind.None : BreakKind.Allowed)
                : wordBreak[offset - 1] == WordBreakStyle.BreakAll && letters ? BreakKind.Allowed
                : BreakKind.None;
        }

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
        // Closes the unit at a break opportunity. One right after a soft hyphen shows a hyphen if the line breaks there,
        // from the soft hyphen's font: U+2010, else U+002D.
        void Break(BreakKind kind)
        {
            if (kind == BreakKind.Allowed && unit.Pieces.LastOrDefault(p => p.Kind == PieceKind.Text) is { Run.Face: { } face } last
                && text[last.Run.Clusters[last.GlyphEnd - 1]] == SoftHyphen
                && (face.Covers(0x2010) ? face.GlyphFor(0x2010) : face.GlyphFor('-')) is var hyphen and not 0)
            {
                unit.Hyphen = last;
                unit.HyphenGlyph = hyphen;
                unit.HyphenWidth = face.Advance(hyphen) * last.Run.Size / face.UnitsPerEm;
            }
            Close(kind == BreakKind.Mandatory);
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
                        Break(b);
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
                                    Break(kind);
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

    // Whether overflow-wrap (or word-break: break-word) lets the text break where it would otherwise overflow.
    private static bool WrapsAnywhere(ComputedStyle style, bool forMinContent = false) =>
        style.TextSpacing.OverflowWrap == OverflowWrap.Anywhere || style.TextSpacing.WordBreak == WordBreakStyle.BreakWord
        || !forMinContent && style.TextSpacing.OverflowWrap == OverflowWrap.BreakWord;

    /// <summary>
    /// Splits a unit that is too wide for an empty line at the last character boundary that fits, inside text that
    /// may wrap anywhere (https://www.w3.org/TR/css-text-3/#overflow-wrap-property), keeping at least one character.
    /// Null when no such text is where the unit overflows.
    /// </summary>
    private static (Unit Head, Unit Tail)? SplitToFit(Unit unit, float available)
    {
        var x = 0f;
        for (var i = 0; i < unit.Pieces.Count; i++)
        {
            var piece = unit.Pieces[i];
            if (x + piece.Width <= available || piece.Kind != PieceKind.Text || !WrapsAnywhere(piece.Style))
            {
                x += piece.Width;
                continue;
            }
            var run = piece.Run!;
            // The last boundary between characters (not inside a cluster) that fits, else the first one.
            var (split, first, width) = (-1, -1, x);
            for (var g = piece.GlyphStart + 1; g < piece.GlyphEnd; g++)
            {
                width += run.Advances[g - 1];
                if (run.Clusters[g] == run.Clusters[g - 1])
                    continue;
                if (first < 0)
                    first = g;
                if (width > available)
                    break;
                split = g;
            }
            split = split < 0 ? first : split;
            if (split < 0)
                return null;
            var (headWidth, tailWidth) = (0f, 0f);
            for (var g = piece.GlyphStart; g < piece.GlyphEnd; g++)
            {
                if (g < split)
                    headWidth += run.Advances[g];
                else
                    tailWidth += run.Advances[g];
            }
            var head = new Unit();
            var tail = new Unit { TrailingSpace = unit.TrailingSpace, MandatoryBreakAfter = unit.MandatoryBreakAfter, Hyphen = unit.Hyphen,
                HyphenGlyph = unit.HyphenGlyph, HyphenWidth = unit.HyphenWidth };
            head.Pieces.AddRange(unit.Pieces.Take(i));
            head.Pieces.Add(new Piece(PieceKind.Text, piece.Style, headWidth) { Run = run, GlyphStart = piece.GlyphStart, GlyphEnd = split, Visible = true, Level = piece.Level });
            tail.Pieces.Add(new Piece(PieceKind.Text, piece.Style, tailWidth) { Run = run, GlyphStart = split, GlyphEnd = piece.GlyphEnd, Visible = piece.Visible, Level = piece.Level });
            tail.Pieces.AddRange(unit.Pieces.Skip(i + 1));
            head.Width = head.Pieces.Sum(p => p.Width);
            tail.Width = tail.Pieces.Sum(p => p.Width);
            return (head, tail);
        }
        return null;
    }

    /// <summary>
    /// Ends the line with an ellipsis in the block's font (https://www.w3.org/TR/css-overflow-3/#text-overflow): text
    /// and atomic inlines are cut at the last character boundary that leaves room for it. Inline box edges stay, so
    /// boxes still open and close.
    /// </summary>
    // ponytail: the ellipsis goes at the logical end, which is the visual end except in mixed-direction lines.
    private static void Ellipsize(List<Piece> pieces, float available, ComputedStyle style, LayoutContext context, int paragraphLevel)
    {
        const string Ellipsis = "\u2026";
        var runs = Shape(Ellipsis, 0, Ellipsis.Length, style, context);
        if (runs is not [var run] || run.Face is { } face && !face.Covers(0x2026))
            runs = Shape("...", 0, 3, style, context);
        var ellipsisRun = runs[0];
        var room = available - ellipsisRun.Width;

        var x = 0f;
        var cut = pieces.Count;
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            if (piece.Kind is not (PieceKind.Text or PieceKind.Atomic) || x + piece.Width <= room)
            {
                x += piece.Width;
                continue;
            }
            cut = i;
            if (piece.Kind == PieceKind.Text)
            {
                // Keep the characters that fit, whole clusters only.
                var (end, width, kept) = (piece.GlyphStart, x, x);
                for (var g = piece.GlyphStart; g < piece.GlyphEnd; g++)
                {
                    width += piece.Run!.Advances[g];
                    var boundary = g + 1 == piece.GlyphEnd || piece.Run.Clusters[g + 1] != piece.Run.Clusters[g];
                    if (width > room)
                        break;
                    if (boundary)
                        (end, kept) = (g + 1, width);
                }
                if (end > piece.GlyphStart)
                {
                    pieces[i] = new Piece(PieceKind.Text, piece.Style, kept - x)
                    {
                        Run = piece.Run, GlyphStart = piece.GlyphStart, GlyphEnd = end, Visible = true, Level = piece.Level,
                    };
                    cut = i + 1;
                }
            }
            break;
        }
        // Drop the content after the cut, keeping inline box edges (with their margins, borders and padding).
        for (var i = pieces.Count - 1; i >= cut; i--)
        {
            if (pieces[i].Kind is PieceKind.Text or PieceKind.Atomic)
                pieces.RemoveAt(i);
        }
        pieces.Insert(Math.Min(cut, pieces.Count), new Piece(PieceKind.Text, style, ellipsisRun.Width)
        {
            Run = ellipsisRun, GlyphStart = 0, GlyphEnd = ellipsisRun.Glyphs.Length, Visible = true, Level = (byte)paragraphLevel,
            Replacement = ellipsisRun.Glyphs.Length == 1 ? Ellipsis : "...",
        });
    }

    /// <summary>
    /// Min-content (the widest unit) and max-content (the widest line with only forced breaks) widths
    /// (css-sizing-3 §5.1); atomic inlines count with their own contributions, floats on their own.
    /// </summary>
    public static (float Min, float Max) Measure(BlockContainerBox block, InlineFormattingContext ifc, LayoutContext context)
    {
        float min = 0, max = 0, line = 0;
        var first = true;
        foreach (var unit in Units(block, ifc, 0, context, BidiLevels(block, ifc), layOutAtomics: false))
        {
            var (unitMin, unitMax) = (unit.Width - unit.TrailingSpace + unit.HyphenWidth, unit.Width);
            // overflow-wrap: anywhere (and word-break: break-word) may break between any two characters for min-content;
            // break-word only when laying out.
            if (unit.Pieces.Any(p => p.Kind == PieceKind.Text && WrapsAnywhere(p.Style, forMinContent: true)))
                unitMin = unit.Pieces.Where(p => p.Kind == PieceKind.Text).SelectMany(p => p.Run!.Advances[p.GlyphStart..p.GlyphEnd]).DefaultIfEmpty().Max();
            if (first && block.Node is Dom.ElementNode && !block.Style.Text.TextIndent.Hanging)
            {
                // The first line's indent (percentages count as zero with no width to resolve them against).
                var indent = block.Style.Text.TextIndent.Length.Resolve(0);
                (unitMin, line) = (unitMin + indent, line + indent);
            }
            first = false;
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
            foreach (var run in Shape(text, runStart, runEnd - runStart, style, context, levels[runStart] % 2 == 1))
            {
                if (levels[runStart] % 2 == 1 && run.Face is { } face && run.Offsets is null)
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
    private static List<ShapedRun> Shape(string text, int start, int length, ComputedStyle style, LayoutContext context, bool rightToLeft = false)
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
                runs.Add(ShapeRun(text, runStart, i - runStart, runFace, style, context, rightToLeft));
                runStart = i;
            }
            runFace = face;
            i += clusterLength;
        }
        if (end > runStart)
            runs.Add(ShapeRun(text, runStart, end - runStart, runFace, style, context, rightToLeft));
        return runs;
    }

    private static ShapedRun ShapeRun(string text, int start, int length, FontFace? face, ComputedStyle style, LayoutContext context, bool rightToLeft)
    {
        var size = style.Font.Size;
        ShapedRun run;
        if (face is null)
        {
            run = new ShapedRun(null, size, new ushort[length], [.. Enumerable.Range(start, length)], [.. Enumerable.Repeat(size / 2, length)]);
        }
        else if (context.Shaper is { } shaper && !SimpleShaper.CanShape(text.AsSpan(start, length), face))
        {
            var shaped = shaper.Shape(text, start, length, face, size, rightToLeft, null);
            run = new ShapedRun(face, size, shaped.Glyphs, shaped.Clusters, shaped.Advances) { Offsets = shaped.Offsets };
        }
        else
        {
            run = SimpleShaper.Shape(text, start, length, face, size);
        }
        // Controls and format characters take no space; a tab is tab-size spaces or a length. letter-spacing follows
        // every typographic character unit and word-spacing every word separator (css-text-3 §8.1, css-text-4 §9).
        // ponytail: tabs are a fixed width, not stops measured from the line start.
        var spacing = style.TextSpacing;
        var space = face is not null ? face.Advance(face.GlyphFor(' ')) * size / face.UnitsPerEm : size / 2;
        for (var g = 0; g < run.Glyphs.Length; g++)
        {
            var c = text[run.Clusters[g]];
            var lastOfCluster = g + 1 == run.Glyphs.Length || run.Clusters[g + 1] != run.Clusters[g];
            if (c == '\t')
                run.Advances[g] = spacing.TabSize.IsLength ? spacing.TabSize.Value : spacing.TabSize.Value * (space + spacing.LetterSpacing + spacing.WordSpacing);
            else if (lastOfCluster && !(c is '\n' or '\r' || char.GetUnicodeCategory(c) == UnicodeCategory.Format))
                run.Advances[g] += spacing.LetterSpacing + (c is ' ' or '\u00A0' or '\u3000' ? spacing.WordSpacing : 0);
            if (c is '\n' or '\r' || char.GetUnicodeCategory(c) == UnicodeCategory.Format)
            {
                // Nothing is drawn for them either: the space glyph stands in for whatever the font has there.
                run.Advances[g] = 0;
                if (face is not null && face.GlyphFor(' ') is var blank and not 0)
                    run.Glyphs[g] = blank;
            }
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

    /// <param name="lastLine">The paragraph's last line, or one ending at a forced break: text-align-last applies.</param>
    private static Fragment BuildLine(BlockContainerBox block, string text, List<Unit> units, bool lastLine, List<(InlineBox Box, ComputedStyle Style)> openBoxes,
                                      float available, float cbWidth, LineMetrics strut, int paragraphLevel, LayoutContext context,
                                      Action<Box, float> addOutOfFlow, bool clamped = false)
    {
        // A line broken at a soft hyphen ends with a hyphen, drawn in place of the soft hyphen's glyph.
        if (!lastLine && units is [.., { Hyphen: { } hyphenated } hyphenUnit])
        {
            var g = hyphenated.GlyphEnd - 1;
            hyphenated.Run!.Glyphs[g] = hyphenUnit.HyphenGlyph;
            hyphenated.Run.Advances[g] = hyphenUnit.HyphenWidth;
            hyphenated.Width += hyphenUnit.HyphenWidth;
            hyphenUnit.Width += hyphenUnit.HyphenWidth;
        }
        var pieces = units.SelectMany(u => u.Pieces).ToList();
        var contentWidth = units.Sum(u => u.Width) - (units.Count > 0 ? units[^1].TrailingSpace : 0);
        // text-overflow: ellipsis on a box that clips its inline overflow, and the last line of a clamped block.
        var clips = block.Style.Box.OverflowX != Overflow.Visible;
        if (clamped || clips && block.Style.Box.TextOverflow == TextOverflow.Ellipsis && contentWidth > available + 0.01f)
        {
            Ellipsize(pieces, available, block.Style, context, paragraphLevel);
            contentWidth = pieces.Sum(p => p.Width);
        }
        // A line ended by a forced break (br, or a preserved segment break) is not empty, even with nothing on it
        // (CSS 2.2 §9.4.2), so blank lines in pre keep their height.
        var visible = pieces.Any(p => p.Visible) || openBoxes.Count > 0 && pieces.Any(p => p.Kind == PieceKind.Text)
                      || units.Count > 0 && units[^1].MandatoryBreakAfter;
        var free = available - contentWidth;
        var rtl = paragraphLevel == 1;
        var textStyle = block.Style.Text;
        var align = !lastLine ? textStyle.TextAlign
            : textStyle.TextAlignLast ?? (textStyle.TextAlign == TextAlign.Justify ? TextAlign.Start : textStyle.TextAlign);
        if (align == TextAlign.Justify && free > 0 && Justify(pieces, text, free))
            free = 0;
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
                        Text = new TextRun(text.Run!, text.GlyphStart, text.GlyphEnd, m.Ascent, text.Level % 2 == 1, child.Style, text.Replacement),
                    }));
                }
                else if (child.Piece is { Kind: PieceKind.Atomic } atomic)
                {
                    contentFragments.Add(new ChildFragment(child.X, child.Baseline - child.Above + atomic.AtomicMarginTop, atomic.Atomic!));
                }
            }
        }
    }

    /// <summary>
    /// Shares the free space out among the line's word separators (spaces and no-break spaces), widening their glyphs
    /// (https://www.w3.org/TR/css-text-3/#justify-algos, text-justify: auto). Spaces hanging at the end do not count.
    /// Returns false when there is nothing to widen.
    /// </summary>
    // ponytail: no expansion between letters of scripts without word separators (CJK), and none in atomic inlines.
    private static bool Justify(List<Piece> pieces, string text, float free)
    {
        var separators = new List<(Piece Piece, int Glyph)>();
        foreach (var piece in pieces)
        {
            if (piece.Kind != PieceKind.Text)
                continue;
            for (var g = piece.GlyphStart; g < piece.GlyphEnd; g++)
            {
                if (text[piece.Run!.Clusters[g]] is ' ' or '\u00A0')
                    separators.Add((piece, g));
            }
        }
        // Drop the trailing spaces: separators after the last other glyph or atomic inline.
        for (var i = pieces.Count - 1; i >= 0; i--)
        {
            var piece = pieces[i];
            if (piece.Kind == PieceKind.Atomic)
                break;
            if (piece.Kind != PieceKind.Text)
                continue;
            var g = piece.GlyphEnd - 1;
            while (g >= piece.GlyphStart && separators.Count > 0 && separators[^1] == (piece, g))
            {
                separators.RemoveAt(separators.Count - 1);
                g--;
            }
            if (g >= piece.GlyphStart)
                break;
        }
        if (separators.Count == 0)
            return false;
        var extra = free / separators.Count;
        foreach (var (piece, g) in separators)
        {
            piece.Run!.Advances[g] += extra;
            piece.Width += extra;
        }
        return true;
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

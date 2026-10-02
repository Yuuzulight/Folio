using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// A ruby base and its annotation (https://www.w3.org/TR/css-ruby-1/): each laid out on one line at its max-content
/// width, the narrower spread across the wider (ruby-align: space-around, §4.1), and the annotation over the base with
/// the bottom of its content area on the top of the base's ideographic em box (ruby-position: over, §3.1).
/// </summary>
// ponytail: annotations go over their bases only; ruby-position: under and inter-character are not done.
internal static class RubyLayout
{
    public static Fragment Layout(RubyColumnBox column, float containingWidth, LayoutContext context)
    {
        var rubyBase = LayOut(column.Base, containingWidth, context);
        var annotation = column.Annotation is { } a ? LayOut(a, containingWidth, context) : null;
        var width = Math.Max(rubyBase.Width, annotation?.Width ?? 0);
        var strut = InlineLayout.Strut(column.Base.Style, context);
        var baseline = FirstBaseline(rubyBase) ?? strut.Above;
        var children = new List<ChildFragment>();
        var over = float.NegativeInfinity;
        var overhang = 0f;
        if (annotation is not null && FirstBaseline(annotation) is { } annotationBaseline)
        {
            // The em boxes are those of the fonts the text is drawn in.
            var style = column.Annotation!.Style;
            var annotationFace = UsedFace(annotation);
            var bottom = baseline - MathF.Floor(InlineLayout.EmTop(column.Base.Style, context, UsedFace(rubyBase)));
            // Across a vertical line the annotation's em box sits on the base's, each centred on its upright glyphs.
            var under = style.Text.IsVertical ? InlineLayout.UprightEmBottom(style, context, annotationFace)
                : annotationFace is null ? InlineLayout.FontMetrics(style, context).Descent : InlineLayout.Descent(style, annotationFace);
            var y = bottom - under - annotationBaseline;
            over = InlineLayout.EmTop(style, context, annotationFace) - (y + annotationBaseline);
            children.Add(Spread(annotation, width, y));
            // ruby-overhang (css-ruby-1 §4.4, auto): an annotation wider than its base may overhang the text beside it
            // by up to half its font size on each side.
            if (annotation.Width > rubyBase.Width)
                overhang = Math.Min(style.Font.Size / 2, (annotation.Width - rubyBase.Width) / 2);
        }
        children.Add(Spread(rubyBase, width, 0));
        var height = FirstBaseline(rubyBase) is null ? strut.Above + strut.Below : rubyBase.Height;
        return new Fragment(column, width, height, children) { Baseline = baseline, RubyOver = over, RubyOverhang = overhang };
    }

    // At the max-content width, so the content stays on one line.
    private static Fragment LayOut(BlockContainerBox box, float containingWidth, LayoutContext context) =>
        BlockLayout.Layout(box, new ConstraintSpace(containingWidth, null, FixedWidth: IntrinsicSizes.Contribution(box, context).Max), context);

    // The face of the first glyphs on the block's first line, or null when it has none.
    private static Typography.FontFace? UsedFace(Fragment block) =>
        block.Children is [{ Fragment: { Kind: FragmentKind.Line } line }, ..]
            ? line.Children.Select(c => c.Fragment.Text?.Run.Face).FirstOrDefault(f => f is not null) : null;

    private static float? FirstBaseline(Fragment block) =>
        block.Children is [{ Fragment: { Kind: FragmentKind.Line, Height: > 0 } line } child, ..] ? child.Y + line.Baseline : null;

    /// <summary>
    /// The content spread across the column's width: the free space shared out among the justification opportunities
    /// with half a share at each end, or the content centred when it has none (css-ruby-1 §4.1, css-text-3 §7.3:
    /// between CJK characters and after word separators, never inside other words).
    /// </summary>
    private static ChildFragment Spread(Fragment block, float width, float y)
    {
        var free = width - block.Width;
        if (free <= 0.01f || block.Box is not BlockContainerBox { Inline.Text: { } text }
            || block.Children is not [{ Fragment: { Kind: FragmentKind.Line } line } lineChild])
            return new ChildFragment(Math.Max(0, free) / 2, y, block);

        // The clusters in line order, each as the text fragment and last glyph it ends with.
        var runs = line.Children.Select((c, i) => (c, i)).Where(c => c.c.Fragment.Kind == FragmentKind.Text).OrderBy(c => c.c.X).ToList();
        var clusters = new List<(int Run, int Glyph, int Offset)>();
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r].c.Fragment.Text!;
            for (var g = run.GlyphStart; g < run.GlyphEnd; g++)
            {
                if (g + 1 == run.GlyphEnd || run.Run.Clusters[g + 1] != run.Run.Clusters[g])
                    clusters.Add((r, g, run.Run.Clusters[g]));
            }
        }
        var opportunities = new bool[clusters.Count];
        var count = 0;
        for (var i = 0; i + 1 < clusters.Count; i++)
        {
            var (before, after) = (CodePoint(text, clusters[i].Offset), CodePoint(text, clusters[i + 1].Offset));
            opportunities[i] = IsCjk(before) || IsCjk(after) || before is ' ' or ' ' or '　';
            count += opportunities[i] ? 1 : 0;
        }
        if (count == 0)
            return new ChildFragment(free / 2, y, block);

        var share = free / (count + 1);
        var added = new float[runs.Count];
        for (var i = 0; i < clusters.Count; i++)
        {
            if (!opportunities[i])
                continue;
            var run = runs[clusters[i].Run].c.Fragment.Text!.Run;
            run.Advances[clusters[i].Glyph] += share;
            added[clusters[i].Run] += share;
        }
        var children = line.Children.ToArray();
        var shift = share / 2;
        for (var r = 0; r < runs.Count; r++)
        {
            var (child, index) = runs[r];
            var glyphs = child.Fragment;
            children[index] = new ChildFragment(child.X + shift, child.Y, new Fragment(glyphs.Box, glyphs.Width + added[r], glyphs.Height, [])
            {
                Kind = FragmentKind.Text,
                Text = glyphs.Text,
            });
            shift += added[r];
        }
        var spread = new Fragment(line.Box, line.Width + free, line.Height, children) { Kind = FragmentKind.Line, Baseline = line.Baseline };
        return new ChildFragment(0, y, new Fragment(block.Box, width, block.Height, [lineChild with { Fragment = spread }]));
    }

    private static int CodePoint(string text, int i) => char.IsSurrogatePair(text, i) ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];

    // Ideographs, kana, Hangul, CJK symbols and punctuation, and full-width forms.
    internal static bool IsCjk(int c) =>
        c is >= 0x2E80 and <= 0x9FFF or >= 0xAC00 and <= 0xD7AF or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x3FFFF;
}

using System.Numerics;
using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Layout;
using Folio.Style;
using Folio.Typography;

namespace Folio.Svg;

/// <summary>Glyphs of one font at one size and colour, each at its baseline origin in user units.</summary>
internal sealed record SvgGlyphRun(IFontHandle Font, float Size, ushort[] Glyphs, Vector2[] Origins, CssColor Color);

/// <summary>A text element's glyphs, positioned and anchored, and the box of their cells (its bounding box).</summary>
internal sealed record SvgTextNode(Matrix3x2 Transform, float Opacity, IReadOnlyList<SvgGlyphRun> Runs, SvgRect? Bounds = null)
    : SvgRenderNode(Transform, Opacity);

/// <summary>
/// Lays out a text element (https://www.w3.org/TR/SVG2/text.html#TextLayoutAlgorithm) on one line: the characters of
/// it and its tspan descendants with white space processed, absolute (x, y) and relative (dx, dy) positions per
/// character, inner elements overriding outer ones, text chunks started by absolute positions and aligned by
/// text-anchor, and the dominant baseline moved to the position's y.
/// </summary>
// ponytail: horizontal left-to-right text without bidi reordering; rotate, textLength, textPath and stroked glyphs are
// not drawn yet (issue #97).
internal static class SvgText
{
    // A character's positioning: absolute coordinates and relative shifts, each when given.
    private struct Position
    {
        public float? X, Y, Dx, Dy;
    }

    private sealed record Span(int Start, int End, ComputedStyle Style, ElementNode Element);

    /// <param name="clipping">Building a clip path's text: every visible glyph is drawn opaque, whatever its fill.</param>
    public static SvgTextNode? Build(ElementNode text, ComputedStyle style, Matrix3x2 transform, Vector2 viewport, SvgContext context, bool clipping = false)
    {
        var chars = new StringBuilder();
        var spans = new List<Span>();
        var lists = new List<(ElementNode Element, ComputedStyle Style, int Start, int End)>();
        var afterSpace = true; // collapsible spaces at the start are removed
        Collect(text, style);
        // And a collapsible space at the end.
        if (chars.Length > 0 && chars[^1] == ' ' && spans[^1].Style.Text.WhiteSpaceCollapse == WhiteSpaceCollapse.Collapse)
        {
            chars.Length--;
            spans[^1] = spans[^1] with { End = chars.Length };
        }
        if (chars.Length == 0)
            return null;

        // Positions from the outermost element in, so inner lists override outer ones.
        var positions = new Position[chars.Length];
        foreach (var (element, elementStyle, start, end) in lists)
        {
            var size = elementStyle.Font.Size;
            Assign(element.GetAttribute("x"), SvgAxis.Horizontal, (ref Position p, float v) => p.X = v);
            Assign(element.GetAttribute("y"), SvgAxis.Vertical, (ref Position p, float v) => p.Y = v);
            Assign(element.GetAttribute("dx"), SvgAxis.Horizontal, (ref Position p, float v) => p.Dx = v);
            Assign(element.GetAttribute("dy"), SvgAxis.Vertical, (ref Position p, float v) => p.Dy = v);

            void Assign(string? attribute, SvgAxis axis, Setter set)
            {
                if (SvgGeometry.Lengths(attribute, axis, viewport, size) is not { } values)
                    return;
                for (var i = 0; i < values.Count && start + i < Math.Min(end, positions.Length); i++)
                    set(ref positions[start + i], values[i]);
            }
        }

        var content = chars.ToString();
        var glyphs = new List<(FontFace? Face, float Size, ushort Glyph, Vector2 Origin, CssColor? Color, int Chunk, float Advance, float Ascent, float Descent)>();
        var chunks = new List<(float Start, float End, TextAnchor Anchor)>();
        var pen = Vector2.Zero;
        foreach (var span in spans)
        {
            var spanStyle = span.Style;
            var color = spanStyle.Inherited.Visibility != Visibility.Visible ? null : clipping ? CssColor.Black : Paint(spanStyle);
            var shift = BaselineShift(spanStyle, AlignmentBaseline(span.Element, text) ?? spanStyle.Svg.DominantBaseline, context.Layout);
            var (ascent, descent, _) = InlineLayout.FontMetrics(spanStyle, context.Layout);
            foreach (var run in InlineLayout.Shape(content, span.Start, span.End - span.Start, spanStyle, context.Layout))
            {
                for (var g = 0; g < run.Glyphs.Length; g++)
                {
                    var c = run.Clusters[g];
                    if (g == 0 || run.Clusters[g - 1] != c)
                    {
                        var p = positions[c];
                        // An absolute position starts a new text chunk; the first character always does.
                        if (p.X is not null || p.Y is not null || chunks.Count == 0)
                            chunks.Add((p.X ?? pen.X, 0, spanStyle.Svg.TextAnchor));
                        pen = new Vector2(p.X ?? pen.X, p.Y ?? pen.Y) + new Vector2(p.Dx ?? 0, p.Dy ?? 0);
                    }
                    var origin = pen + new Vector2(0, shift) + (run.Offsets?[g] ?? Vector2.Zero);
                    // The glyph's cell: its advance across, and the font's ascent above and descent below the baseline.
                    glyphs.Add((run.Face, run.Size, run.Glyphs[g], origin, color, chunks.Count - 1, run.Advances[g], ascent, descent));
                    pen.X += run.Advances[g];
                    chunks[^1] = chunks[^1] with { End = pen.X };
                }
            }
        }

        // text-anchor moves each chunk so its start, middle or end is at the chunk's start position.
        var runs = new List<SvgGlyphRun>();
        var pending = new List<(ushort Glyph, Vector2 Origin)>();
        (FontFace Face, float Size, CssColor Color)? current = null;
        var (min, max) = (new Vector2(float.PositiveInfinity), new Vector2(float.NegativeInfinity));
        foreach (var glyph in glyphs)
        {
            var chunk = chunks[glyph.Chunk];
            var offset = chunk.Anchor switch
            {
                TextAnchor.Middle => (chunk.Start - chunk.End) / 2,
                TextAnchor.End => chunk.Start - chunk.End,
                _ => 0,
            };
            // The bounding box takes every glyph cell, painted or not.
            var cell = glyph.Origin + new Vector2(offset, 0);
            (min, max) = (Vector2.Min(min, cell - new Vector2(0, glyph.Ascent)), Vector2.Max(max, cell + new Vector2(glyph.Advance, glyph.Descent)));
            if (glyph.Face is not { } face || glyph.Color is not { } fill)
                continue;
            if (current != (face, glyph.Size, fill))
            {
                Flush();
                current = (face, glyph.Size, fill);
            }
            pending.Add((glyph.Glyph, glyph.Origin + new Vector2(offset, 0)));
        }
        Flush();
        var bounds = float.IsFinite(min.X) ? new SvgRect(min.X, min.Y, max.X - min.X, max.Y - min.Y) : (SvgRect?)null;
        return runs.Count > 0 ? new SvgTextNode(transform, clipping ? 1 : style.Box.Opacity, runs, bounds) : null;

        void Flush()
        {
            if (current is { } c && pending.Count > 0)
                runs.Add(new SvgGlyphRun(c.Face, c.Size, [.. pending.Select(p => p.Glyph)], [.. pending.Select(p => p.Origin)], c.Color));
            pending.Clear();
        }

        // The characters of an element and its tspan (and a) descendants, white space collapsed across them as CSS text
        // does on one line (https://www.w3.org/TR/SVG2/text.html#WhiteSpace); preserved white space still has its
        // newlines and tabs shown as spaces.
        void Collect(ElementNode element, ComputedStyle elementStyle)
        {
            var start = chars.Length;
            var index = lists.Count;
            lists.Add((element, elementStyle, start, start));
            for (var child = element.FirstChild; child is not null; child = child.NextSibling)
            {
                if (child is Dom.Text node)
                {
                    var spanStart = chars.Length;
                    var collapse = elementStyle.Text.WhiteSpaceCollapse == WhiteSpaceCollapse.Collapse;
                    foreach (var ch in node.Data)
                    {
                        var space = ch is ' ' or '\t' or '\n' or '\r' or '\f';
                        if (space && collapse && afterSpace)
                            continue;
                        chars.Append(space ? ' ' : ch);
                        afterSpace = space;
                    }
                    if (chars.Length > spanStart)
                        spans.Add(new Span(spanStart, chars.Length, elementStyle, element));
                }
                else if (child is ElementNode { LocalName: "tspan" or "a" } inner && inner.Name.Namespace == Namespaces.Svg
                         && inner.ComputedStyle() is { Box.Display: not Display.None } innerStyle
                         && System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
                {
                    Collect(inner, innerStyle);
                }
            }
            lists[index] = (element, elementStyle, start, chars.Length);
        }
    }

    private delegate void Setter(ref Position position, float value);

    // A fill colour with fill-opacity, or null when nothing is painted.
    private static CssColor? Paint(ComputedStyle style)
    {
        if (style.Svg.Fill.Color is not { } color)
            return null;
        var c = color.Resolve(style.Inherited.Color);
        var alpha = c.A * style.Svg.FillOpacity;
        return alpha > 0 ? c with { A = alpha } : null;
    }

    // alignment-baseline written on the element or an ancestor within the text element, which SVG files use on text
    // like dominant-baseline (https://www.w3.org/TR/css-inline-3/#alignment-baseline-property), with SVG 1.1's names.
    // ponytail: read from the attribute only, not from CSS.
    private static DominantBaseline? AlignmentBaseline(ElementNode element, ElementNode text)
    {
        for (var e = element; ; e = (ElementNode)e.Parent!)
        {
            if (e.GetAttribute("alignment-baseline")?.Trim() is { } value && AlignmentKeywords.TryGetValue(value, out var baseline))
                return baseline;
            if (e == text)
                return null;
        }
    }

    private static readonly Dictionary<string, DominantBaseline?> AlignmentKeywords = new()
    {
        ["auto"] = null, ["baseline"] = null, ["alphabetic"] = DominantBaseline.Alphabetic, ["ideographic"] = DominantBaseline.Ideographic,
        ["middle"] = DominantBaseline.Middle, ["central"] = DominantBaseline.Central, ["mathematical"] = DominantBaseline.Mathematical,
        ["hanging"] = DominantBaseline.Hanging, ["text-top"] = DominantBaseline.TextTop, ["text-before-edge"] = DominantBaseline.TextTop,
        ["before-edge"] = DominantBaseline.TextTop, ["top"] = DominantBaseline.TextTop, ["text-bottom"] = DominantBaseline.TextBottom,
        ["text-after-edge"] = DominantBaseline.TextBottom, ["after-edge"] = DominantBaseline.TextBottom, ["bottom"] = DominantBaseline.TextBottom,
    };

    // How far below the position's y the alphabetic baseline goes so the given baseline sits there
    // (https://www.w3.org/TR/css-inline-3/#dominant-baseline-property).
    // ponytail: hanging and mathematical baselines are estimated from the ascent rather than read from a BASE table.
    private static float BaselineShift(ComputedStyle style, DominantBaseline baseline, LayoutContext context)
    {
        if (baseline is DominantBaseline.Auto or DominantBaseline.Alphabetic)
            return 0;
        var (ascent, descent, xHeight) = InlineLayout.FontMetrics(style, context);
        return baseline switch
        {
            DominantBaseline.Middle => xHeight / 2,
            DominantBaseline.Central => (ascent - descent) / 2,
            DominantBaseline.Hanging => 0.8f * ascent,
            DominantBaseline.Mathematical => 0.5f * ascent,
            DominantBaseline.TextTop => ascent,
            _ => -descent, // text-bottom, ideographic
        };
    }
}

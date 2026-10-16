using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Layout;
using Folio.Style;
using Folio.Typography;

namespace Folio.Hosting;

/// <summary>Glyphs of one font at one size on a line of canvas text; origins are from the text's start, on the alphabetic baseline.</summary>
internal sealed record CanvasGlyphRun(IFontHandle Font, float Size, ushort[] Glyphs, Vector2[] Origins);

/// <summary>A line of canvas text shaped and measured.</summary>
/// <param name="Width">The advance width of the whole line.</param>
/// <param name="Ascent">The primary font's ascent above the baseline, in CSS pixels.</param>
/// <param name="Descent">The primary font's descent below the baseline, in CSS pixels.</param>
/// <param name="EmSize">The font size.</param>
internal sealed record CanvasTextLayout(IReadOnlyList<CanvasGlyphRun> Runs, float Width, float Ascent, float Descent, float EmSize);

/// <summary>
/// Resolves the CSS a canvas context takes as text and shapes its text with the document's fonts and shaper (HTML
/// §4.12.5.1.10 "text"): the <c>font</c> and <c>filter</c> values are resolved by the CSS engine itself, so the shorthand,
/// system fonts and units mean what they do in a stylesheet, and the document's <c>@font-face</c> fonts are available.
/// </summary>
internal sealed class CanvasStyleEngine(FontCollection fonts, ITextShaper? shaper)
{
    private readonly Dictionary<string, ComputedStyle?> _styles = new(StringComparer.Ordinal);
    private readonly LayoutContext _layout = new(fonts, shaper);
    private DocumentNode? _scratch;
    private ElementNode? _span;

    /// <summary>The style a <c>font</c> value computes to; null when it is not valid (a script's assignment is then ignored).</summary>
    public ComputedStyle? Resolve(string font)
    {
        if (_styles.TryGetValue(font, out var known))
            return known;
        ComputedStyle? style = null;
        // A declaration of its own: anything that would end it early or add another is not a font value.
        if (font.Trim().Length > 0 && Conditions.SupportsDeclaration("font", font))
        {
            EnsureScratch();
            _span!.SetAttribute("style", "font:" + font);
            StyleResolver.Resolve(_scratch!, new MediaContext(300, 150));
            style = _span.ComputedStyle();
        }
        if (_styles.Count > 256)
            _styles.Clear();
        return _styles[font] = style;
    }

    private readonly Dictionary<(string Filter, Painting.Rgba Color), Painting.Filter[]?> _filters = new();

    /// <summary>
    /// The filter primitives a <c>filter</c> value computes to, <c>currentcolor</c> being <paramref name="current"/>: none
    /// for <c>none</c>, and null when the value is not a valid filter list (a script's assignment is then ignored).
    /// </summary>
    public (bool Valid, Painting.Filter[]? Filters) ResolveFilter(string filter, Painting.Rgba current)
    {
        if (_filters.TryGetValue((filter, current), out var known))
            return (true, known);
        if (filter.Trim().Length == 0 || !Conditions.SupportsDeclaration("filter", filter))
            return (false, null);
        EnsureScratch();
        _span!.SetAttribute("style", "filter:" + filter);
        StyleResolver.Resolve(_scratch!, new MediaContext(300, 150));
        var list = _span.ComputedStyle()?.Effects.Filter;
        var primitives = list is null ? null : Painting.FilterPrimitives.Of(list, new CssColor(current.R, current.G, current.B, current.A));
        if (_filters.Count > 256)
            _filters.Clear();
        _filters[(filter, current)] = primitives;
        return (true, primitives);
    }

    private void EnsureScratch()
    {
        if (_span is not null)
            return;
        _scratch = TreeBuilder.Parse("<!DOCTYPE html><html><body><span>x</span></body></html>");
        _span = _scratch.DocumentElement!.Descendants.OfType<ElementNode>().First(e => e.LocalName == "span");
    }

    public CanvasTextLayout Layout(string text, ComputedStyle style, bool rightToLeft)
    {
        var size = style.Font.Size;
        var runs = text.Length == 0 ? [] : InlineLayout.Shape(text, 0, text.Length, style, _layout, rightToLeft);
        var glyphRuns = new List<CanvasGlyphRun>();
        var width = runs.Sum(run => run.Advances.Sum());
        var x = rightToLeft ? width : 0f;
        foreach (var run in runs)
        {
            var glyphs = run.Glyphs;
            var origins = new Vector2[glyphs.Length];
            for (var i = 0; i < glyphs.Length; i++)
            {
                if (rightToLeft)
                    x -= run.Advances[i];
                origins[i] = new Vector2(x, 0) + (run.Offsets?[i] ?? Vector2.Zero);
                if (!rightToLeft)
                    x += run.Advances[i];
            }
            if (run.Face is { } face && glyphs.Length > 0)
                glyphRuns.Add(new CanvasGlyphRun(face, run.Size, glyphs, origins));
        }
        var primary = PrimaryFace(style);
        var scale = primary is null ? 0 : size / primary.UnitsPerEm;
        return new CanvasTextLayout(glyphRuns, width, primary is null ? size * 0.8f : primary.Ascent * scale, primary is null ? size * 0.2f : -primary.Descent * scale, size);
    }

    private FontFace? PrimaryFace(ComputedStyle style)
    {
        var faceStyle = style.Font.Style switch
        {
            Style.FontStyle.Italic => FaceStyle.Italic,
            Style.FontStyle.Oblique => FaceStyle.Oblique,
            _ => FaceStyle.Normal,
        };
        foreach (var family in style.Font.Family)
            if (fonts.Match(family, faceStyle, style.Font.Weight, style.Font.Stretch) is { } face)
                return face;
        return null;
    }
}

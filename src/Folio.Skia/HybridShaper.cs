using Folio.Typography;

namespace Folio.Skia;

/// <summary>
/// Selects Folio's own simple shaper when the font and text satisfy its proof conditions, and
/// delegates complex scripts to HarfBuzz until their Folio-owned replacements reach differential
/// parity. The decision is per run, so a paragraph can mix both paths safely.
/// </summary>
public sealed class HybridShaper : ITextShaper
{
    private readonly HarfBuzzShaper _complex = new();

    public ShapedText Shape(string text, int start, int length, IFontHandle font, float size, bool rightToLeft, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, text.Length);

        if (font is FontFace face && SimpleShaper.CanShape(text.AsSpan(start, length), face))
        {
            var simple = SimpleShaper.Shape(text, start, length, face, size);
            return new ShapedText(simple.Glyphs, simple.Clusters, simple.Advances,
                simple.Offsets ?? new System.Numerics.Vector2[simple.Glyphs.Length]);
        }

        return _complex.Shape(text, start, length, font, size, rightToLeft, language);
    }
}

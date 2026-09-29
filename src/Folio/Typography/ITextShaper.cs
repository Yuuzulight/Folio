using System.Numerics;

namespace Folio.Typography;

/// <summary>Turns a run of text in one font into positioned glyphs (docs/study/11-text.md, shaping option B).</summary>
/// <remarks>
/// Swap-out interface, sketched in docs/dependencies.md. Folio's own SimpleShaper handles simple scripts; an
/// implementation of this interface handles everything else (complex scripts, marks, contextual forms).
/// </remarks>
public interface ITextShaper
{
    /// <summary>
    /// Shapes <paramref name="length"/> UTF-16 code units of <paramref name="text"/> from <paramref name="start"/> in one
    /// font at a size in CSS pixels. The text around the range is context only. Glyphs come back in logical order,
    /// right-to-left runs included.
    /// </summary>
    /// <param name="language">A BCP 47 language tag, or null when unknown.</param>
    ShapedText Shape(string text, int start, int length, IFontHandle font, float size, bool rightToLeft, string? language);
}

/// <summary>
/// A shaper's output, one entry per glyph in logical order: its id in the font, the UTF-16 index of the first
/// character of its cluster (non-decreasing), its advance, and its offset from its pen position (CSS pixels, y down).
/// </summary>
public sealed class ShapedText(ushort[] glyphs, int[] clusters, float[] advances, Vector2[] offsets)
{
    public ushort[] Glyphs { get; } = glyphs;
    public int[] Clusters { get; } = clusters;
    public float[] Advances { get; } = advances;
    public Vector2[] Offsets { get; } = offsets;
}

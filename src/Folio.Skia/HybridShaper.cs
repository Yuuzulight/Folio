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
    private readonly IReadOnlySet<string> _folioOwnedScripts;

    /// <summary>
    /// Creates the hybrid shaper. By default only scripts proven by <see cref="SimpleShaper.CanShape"/>
    /// use Folio's implementation. A script name in <paramref name="folioOwnedScripts"/> forces that
    /// script through Folio's implementation, which is used by the differential harness while promoting
    /// a script; production callers should leave the set empty until parity is proven.
    /// </summary>
    public HybridShaper(IReadOnlySet<string>? folioOwnedScripts = null)
    {
        _folioOwnedScripts = folioOwnedScripts is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(folioOwnedScripts, StringComparer.Ordinal);
    }

    public ShapedText Shape(string text, int start, int length, IFontHandle font, float size, bool rightToLeft, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, text.Length);

        if (font is FontFace face)
        {
            var script = ScriptClassifier.Classify(text.AsSpan(start, length));
            if (SimpleShaper.CanShape(text.AsSpan(start, length), face))
            {
                var simple = SimpleShaper.Shape(text, start, length, face, size);
                return new ShapedText(simple.Glyphs, simple.Clusters, simple.Advances,
                    simple.Offsets ?? new System.Numerics.Vector2[simple.Glyphs.Length]);
            }

            if (_folioOwnedScripts.Contains(script))
                return ComplexShaper.Instance.Shape(text, start, length, face, size, rightToLeft, language);
        }

        return _complex.Shape(text, start, length, font, size, rightToLeft, language);
    }
}

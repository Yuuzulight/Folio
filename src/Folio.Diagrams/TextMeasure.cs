using Folio.Typography;

namespace Folio.Diagrams;

/// <summary>
/// Text widths from Folio's own font code: the bundled Source Sans 3 face shaped by Folio's SimpleShaper, the same advances the
/// layout engine uses. Text that face cannot shape (other scripts, missing font files) falls back to a per-character estimate.
/// </summary>
internal static class TextMeasure
{
    private static readonly Lazy<FontCollection?> Fonts = new(() =>
    {
        try { return Directory.Exists(Path.Combine(AppContext.BaseDirectory, "fonts")) ? FontCollection.For(BundledFontSource.Settings) : null; }
        catch (IOException) { return null; }
    });

    /// <summary>Set when a width came from the estimate instead of the font, so tests can tell which path ran.</summary>
    public static bool UsedEstimate { get; private set; }

    public static double Width(string text, double size, bool bold)
    {
        if (text.Length == 0) return 0;
        try
        {
            if (Fonts.Value?.Match("Source Sans 3", FaceStyle.Normal, bold ? 600 : 400, 100) is { } face && SimpleShaper.CanShape(text, face))
                return SimpleShaper.Shape(text, 0, text.Length, face, (float)size).Advances.Sum(a => (double)a);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { }
        UsedEstimate = true;
        return Estimate(text, size);
    }

    /// <summary>Fallback: 0.57 em per character, 1 em for wide (East Asian) characters.</summary>
    public static double Estimate(string text, double size)
    {
        var width = 0.0;
        foreach (var c in text) width += c >= 0x2E80 ? size : size * 0.57;
        return width;
    }
}

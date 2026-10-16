namespace Folio.Css;

/// <summary>Colours from text outside a stylesheet (canvas styles).</summary>
internal static class ColorText
{
    /// <summary>
    /// A <c>&lt;color&gt;</c> that is all of <paramref name="text"/>, leading and trailing space ignored; null when it is
    /// anything else. <c>currentcolor</c> stays symbolic (see <see cref="CssColor.IsCurrentColor"/>).
    /// </summary>
    public static CssColor? Parse(string text)
    {
        var (source, values) = CssParser.ParseComponentValues(text);
        var reader = new ValueReader(source, values);
        return reader.Color() is { } color && reader.AtEnd ? color : null;
    }
}

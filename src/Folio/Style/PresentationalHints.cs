using System.Globalization;
using System.Text;
using Folio.Css;
using Folio.Dom;

namespace Folio.Style;

/// <summary>
/// Presentational hints from HTML attributes (https://html.spec.whatwg.org/multipage/rendering.html): declarations
/// in the author origin with zero specificity, before every author rule and below every author layer
/// (css-cascade-5 §6.1). M1 maps the attributes tables and images rely on.
/// </summary>
// ponytail: legacy colour parsing accepts CSS colours and bare hex digits only; font, body margin and list attributes
// are not mapped yet.
internal static class PresentationalHints
{
    /// <summary>The element's hints as cascade declarations, or null when it has none.</summary>
    public static List<CascadeDeclaration>? For(ElementNode element)
    {
        // Cells also take hints from their table's attributes; every other element only from its own.
        if (element.Name.Namespace != Namespaces.Html || element.Attributes.IsEmpty && element.LocalName is not ("td" or "th"))
            return null;
        var css = t_css ??= new StringBuilder();
        css.Clear();
        switch (element.LocalName)
        {
            case "table":
                Dimension(css, element, "width", "width");
                Dimension(css, element, "height", "height");
                Color(css, element, "bgcolor", "background-color");
                if (Pixels(element.GetAttribute("cellspacing")) is { } spacing)
                    css.Append($"border-spacing: {spacing}px;");
                if (element.GetAttribute("border") is { } border)
                {
                    var width = Pixels(border) ?? 1;
                    css.Append($"border-width: {width}px; border-style: {(width > 0 ? "outset" : "none")}; border-color: gray;");
                }
                switch (element.GetAttribute("align")?.Trim().ToLowerInvariant())
                {
                    case "center":
                        css.Append("margin-left: auto; margin-right: auto;");
                        break;
                    case "left" or "right":
                        css.Append($"float: {element.GetAttribute("align")!.Trim().ToLowerInvariant()};");
                        break;
                }
                break;
            case "td" or "th":
                Dimension(css, element, "width", "width");
                Dimension(css, element, "height", "height");
                Color(css, element, "bgcolor", "background-color");
                Align(css, element);
                if (element.GetAttribute("nowrap") is not null)
                    css.Append("white-space: nowrap;");
                if (Table(element) is { } table)
                {
                    if (Pixels(table.GetAttribute("cellpadding")) is { } padding)
                        css.Append($"padding: {padding}px;");
                    if (Pixels(table.GetAttribute("border")) is > 0 || table.GetAttribute("border") is "")
                        css.Append("border: 1px inset gray;");
                }
                break;
            case "tr" or "thead" or "tbody" or "tfoot":
                Color(css, element, "bgcolor", "background-color");
                Dimension(css, element, "height", "height");
                Align(css, element);
                break;
            case "col" or "colgroup":
                Dimension(css, element, "width", "width");
                Align(css, element);
                break;
            case "body":
                Color(css, element, "bgcolor", "background-color");
                Color(css, element, "text", "color");
                break;
            case "img" or "iframe" or "video" or "embed" or "object":
                Dimension(css, element, "width", "width");
                Dimension(css, element, "height", "height");
                break;
        }
        if (css.Length == 0)
            return null;
        var (source, block) = CssParser.ParseBlockContents(css.ToString());
        return CascadeData.Parse(source, block.Declarations);
    }

    // Reused for every element: most have no hints, and style resolution runs on one thread per document.
    [ThreadStatic]
    private static StringBuilder? t_css;

    // The nearest table element above a cell.
    private static ElementNode? Table(ElementNode cell)
    {
        for (var node = cell.Parent; node is not null; node = node.Parent)
        {
            if (node is ElementNode { LocalName: "table" } table && table.Name.Namespace == Namespaces.Html)
                return table;
        }
        return null;
    }

    // align and valign on table parts: text-align and vertical-align.
    private static void Align(StringBuilder css, ElementNode element)
    {
        var align = element.GetAttribute("align")?.Trim().ToLowerInvariant();
        if (align is "left" or "right" or "center" or "justify")
            css.Append($"text-align: {align};");
        var valign = element.GetAttribute("valign")?.Trim().ToLowerInvariant();
        if (valign is "top" or "middle" or "bottom" or "baseline")
            css.Append($"vertical-align: {valign};");
    }

    // A dimension attribute (https://html.spec.whatwg.org/multipage/rendering.html#maps-to-the-dimension-property):
    // a non-negative number, optionally a percentage.
    private static void Dimension(StringBuilder css, ElementNode element, string attribute, string property)
    {
        var text = element.GetAttribute(attribute)?.Trim();
        if (string.IsNullOrEmpty(text))
            return;
        var digits = 0;
        while (digits < text.Length && (char.IsAsciiDigit(text[digits]) || text[digits] == '.'))
            digits++;
        if (digits == 0 || !float.TryParse(text[..digits], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return;
        var unit = digits < text.Length && text[digits] == '%' ? "%" : "px";
        css.Append($"{property}: {value.ToString(CultureInfo.InvariantCulture)}{unit};");
    }

    // A legacy colour: a CSS colour, or hex digits without the hash.
    private static void Color(StringBuilder css, ElementNode element, string attribute, string property)
    {
        var text = element.GetAttribute(attribute)?.Trim();
        if (string.IsNullOrEmpty(text) || text.Any(c => c is ';' or '{' or '}' or '"' or '\'' or '\\'))
            return;
        if (text.Length is 3 or 6 && text.All(char.IsAsciiHexDigit))
            text = "#" + text;
        css.Append($"{property}: {text};");
    }

    // A non-negative integer attribute value (rules for parsing non-negative integers, leading digits only).
    private static int? Pixels(string? text)
    {
        if (text is null)
            return null;
        text = text.Trim();
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;
        return digits > 0 && int.TryParse(text[..Math.Min(digits, 9)], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}

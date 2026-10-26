using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

/// <summary>
/// The declaration <c>getComputedStyle</c> returns (https://www.w3.org/TR/cssom-1/#resolved-values): read-only and live. A property
/// measured in layout answers with its used value; the rest answer with the computed value, written the way CSSOM writes it. An
/// element that is not in the document has nothing to resolve, so every property reads as empty.
/// </summary>
internal sealed class LiveComputedStyle(ElementNode element, DomContext context, PseudoElement pseudo = PseudoElement.None)
{
    public ElementNode Element { get; } = element;
    public IReadOnlyList<Property> Properties => Folio.Css.Properties.All;

    private ComputedStyle? Style => (pseudo == PseudoElement.None ? Element.ComputedStyle() : Element.PseudoStyle(pseudo)) ?? Folio.Style.ComputedStyle.Initial;

    // Lengths the property table stores as a bare number of pixels.
    private static readonly HashSet<string> PixelNumbers = new(StringComparer.Ordinal)
    {
        "font-size", "letter-spacing", "word-spacing", "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "outline-width", "outline-offset", "column-width", "column-rule-width", "text-decoration-thickness", "text-underline-offset", "perspective",
    };

    private static readonly Dictionary<string, string[]> Sides = new(StringComparer.Ordinal)
    {
        ["margin"] = ["margin-top", "margin-right", "margin-bottom", "margin-left"],
        ["padding"] = ["padding-top", "padding-right", "padding-bottom", "padding-left"],
        ["inset"] = ["top", "right", "bottom", "left"],
        ["border-width"] = ["border-top-width", "border-right-width", "border-bottom-width", "border-left-width"],
        ["border-style"] = ["border-top-style", "border-right-style", "border-bottom-style", "border-left-style"],
        ["border-color"] = ["border-top-color", "border-right-color", "border-bottom-color", "border-left-color"],
        ["scroll-margin"] = ["scroll-margin-top", "scroll-margin-right", "scroll-margin-bottom", "scroll-margin-left"],
        ["scroll-padding"] = ["scroll-padding-top", "scroll-padding-right", "scroll-padding-bottom", "scroll-padding-left"],
    };

    private static readonly string[] Corners = ["border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius"];

    /// <summary>Whether the name is a property a script can read as an attribute, whatever it reads as.</summary>
    public static bool Knows(string name)
    {
        name = LiveStyleDeclaration.NormalizeName(name);
        return Folio.Css.Properties.Find(name) is not null || Sides.ContainsKey(name)
            || name is "border-radius" or "gap" or "overflow" or "border-top" or "border-right" or "border-bottom" or "border-left" or "border" or "outline" or "flex-flow"
            || name is "inline-size" or "block-size" || name.Contains("-inline") || name.Contains("-block");
    }

    public string Get(string name)
    {
        if (!Element.IsConnected || Style is not { } style)
            return "";
        if (name.StartsWith("--", StringComparison.Ordinal))
            return style.Custom.GetValueOrDefault(name) ?? "";
        name = LiveStyleDeclaration.NormalizeName(name);
        if (Sides.TryGetValue(name, out var sides))
            return Collapse(sides.Select(Longhand).ToArray());
        return name switch
        {
            "border-radius" => Radius(),
            "gap" => Collapse([Longhand("row-gap"), Longhand("column-gap")]),
            "overflow" => Collapse([Longhand("overflow-x"), Longhand("overflow-y")]),
            "border-top" or "border-right" or "border-bottom" or "border-left" => Edge(name),
            "border" => Border(),
            "outline" => $"{Longhand("outline-width")} {Longhand("outline-style")} {Longhand("outline-color")}",
            "flex-flow" => $"{Longhand("flex-direction")} {Longhand("flex-wrap")}",
            _ => Longhand(name),
        };
    }

    private string Longhand(string name)
    {
        var host = pseudo == PseudoElement.None ? context.HostDocument : null;
        var physical = host?.Physical(Element, name) ?? name;
        if (host?.UsedValue(Element, physical) is { } used)
            return used;
        if (Folio.Css.Properties.Find(physical) is not { } property)
            return "";
        var value = property.Describe(Style ?? Folio.Style.ComputedStyle.Initial);
        if (!PixelNumbers.Contains(physical))
            return value;
        if (physical is "letter-spacing" or "word-spacing" && value == "0")
            return "normal";
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? value + "px" : value;
    }

    // top right bottom left, written with as few values as say the same thing.
    private static string Collapse(string[] values)
    {
        if (values.Length == 2)
            return values[0] == values[1] ? values[0] : string.Join(' ', values);
        var (top, right, bottom, left) = (values[0], values[1], values[2], values[3]);
        if (top == right && right == bottom && bottom == left)
            return top;
        if (top == bottom && right == left)
            return $"{top} {right}";
        return left == right ? $"{top} {right} {bottom}" : $"{top} {right} {bottom} {left}";
    }

    // Corner radii are one value a corner, or two (horizontal vertical); the shorthand writes the horizontals, a slash, then the verticals.
    private string Radius()
    {
        var corners = Corners.Select(Longhand).Select(text => text.Split(' ', 2)).ToArray();
        var horizontal = Collapse(corners.Select(c => c[0]).ToArray());
        if (corners.All(c => c.Length == 1 || c[0] == c[1]))
            return horizontal;
        return $"{horizontal} / {Collapse(corners.Select(c => c.Length > 1 ? c[1] : c[0]).ToArray())}";
    }

    private string Edge(string name) => $"{Longhand(name + "-width")} {Longhand(name + "-style")} {Longhand(name + "-color")}";

    private string Border()
    {
        var edges = new[] { "border-top", "border-right", "border-bottom", "border-left" }.Select(Edge).ToArray();
        return edges.All(e => e == edges[0]) ? edges[0] : "";
    }
}

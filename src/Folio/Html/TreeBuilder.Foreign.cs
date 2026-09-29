using Folio.Dom;

namespace Folio.Html;

// https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inforeign and the foreign-element adjustments.
internal sealed partial class TreeBuilder
{
    private string ForeignCharacters(string text)
    {
        if (text.Contains('\0'))
        {
            foreach (var c in text)
            {
                if (c == '\0')
                    Error();
            }
            text = text.Replace('\0', '\uFFFD');
        }
        InsertCharacters(text);
        return "";
    }

    private static readonly HashSet<string> BreakoutTags =
    [
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2",
        "h3", "h4", "h5", "h6", "head", "hr", "i", "img", "li", "listing", "menu", "meta", "nobr", "ol", "p", "pre",
        "ruby", "s", "small", "span", "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var",
    ];

    private void ForeignToken(Token t)
    {
        switch (t.Kind)
        {
            case TokenKind.Comment:
                InsertComment(t.Data);
                return;
            case TokenKind.Doctype:
                Error();
                return;
        }

        var breakout = t.Kind == TokenKind.StartTag
            ? BreakoutTags.Contains(t.Name) || (t.Name == "font" && t.Attributes.Exists(a => a.Name is "color" or "face" or "size"))
            : t.Name is "br" or "p";
        if (breakout)
        {
            Error();
            while (!IsMathMLTextIntegrationPoint(CurrentNode) && !IsHtmlIntegrationPoint(CurrentNode) && !IsHtml(CurrentNode))
                Pop();
            Process(_mode, t);
            return;
        }

        if (t.Kind == TokenKind.StartTag)
        {
            var ns = CurrentNode.Name.Namespace;
            if (ns == Namespaces.Svg && SvgTagNames.TryGetValue(t.Name, out var svgName))
                t.Name = svgName;
            InsertForeign(t, ns);
            return;
        }

        // Any other end tag (an SVG script end tag only pops, as scripts never run here).
        var index = _open.Count - 1;
        var node = _open[index];
        if (!node.LocalName.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
            Error();
        while (index > 0)
        {
            if (node.LocalName.Equals(t.Name, StringComparison.OrdinalIgnoreCase))
            {
                PopUntil(node);
                return;
            }
            node = _open[--index];
            if (IsHtml(node))
            {
                Process(_mode, t);
                return;
            }
        }
    }

    // "Insert a foreign element" with the MathML/SVG attribute adjustments; self-closing foreign elements are popped.
    private void InsertForeign(Token t, Atom ns)
    {
        Func<string, (Atom, string)> adjust =
            ns == Namespaces.MathML ? AdjustMathMLAttribute : ns == Namespaces.Svg ? AdjustSvgAttribute : AdjustForeignAttribute;
        InsertElement(CreateElement(t.Name, ns, t.Attributes, adjust));
        if (t.SelfClosing)
        {
            Pop();
            _selfClosingAcknowledged = true;
        }
    }

    private static (Atom, string) AdjustMathMLAttribute(string name) =>
        name == "definitionurl" ? (Atom.None, "definitionURL") : AdjustForeignAttribute(name);

    private static (Atom, string) AdjustSvgAttribute(string name) =>
        SvgAttributeNames.TryGetValue(name, out var adjusted) ? (Atom.None, adjusted) : AdjustForeignAttribute(name);

    // https://html.spec.whatwg.org/multipage/parsing.html#adjust-foreign-attributes
    private static (Atom, string) AdjustForeignAttribute(string name) => name switch
    {
        "xlink:actuate" or "xlink:arcrole" or "xlink:href" or "xlink:role" or "xlink:show" or "xlink:title" or "xlink:type"
            => (Namespaces.XLink, name),
        "xml:lang" or "xml:space" => (Namespaces.Xml, name),
        "xmlns" or "xmlns:xlink" => (Namespaces.Xmlns, name),
        _ => (Atom.None, name),
    };

    // https://html.spec.whatwg.org/multipage/parsing.html#parsing-main-inforeign (SVG tag name table)
    private static readonly Dictionary<string, string> SvgTagNames = ToLowerKeyed(
        "altGlyph", "altGlyphDef", "altGlyphItem", "animateColor", "animateMotion", "animateTransform", "clipPath",
        "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite", "feConvolveMatrix", "feDiffuseLighting",
        "feDisplacementMap", "feDistantLight", "feDropShadow", "feFlood", "feFuncA", "feFuncB", "feFuncG", "feFuncR",
        "feGaussianBlur", "feImage", "feMerge", "feMergeNode", "feMorphology", "feOffset", "fePointLight",
        "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence", "foreignObject", "glyphRef", "linearGradient",
        "radialGradient", "textPath");

    // https://html.spec.whatwg.org/multipage/parsing.html#adjust-svg-attributes
    private static readonly Dictionary<string, string> SvgAttributeNames = ToLowerKeyed(
        "attributeName", "attributeType", "baseFrequency", "baseProfile", "calcMode", "clipPathUnits",
        "diffuseConstant", "edgeMode", "filterUnits", "glyphRef", "gradientTransform", "gradientUnits", "kernelMatrix",
        "kernelUnitLength", "keyPoints", "keySplines", "keyTimes", "lengthAdjust", "limitingConeAngle", "markerHeight",
        "markerUnits", "markerWidth", "maskContentUnits", "maskUnits", "numOctaves", "pathLength",
        "patternContentUnits", "patternTransform", "patternUnits", "pointsAtX", "pointsAtY", "pointsAtZ",
        "preserveAlpha", "preserveAspectRatio", "primitiveUnits", "refX", "refY", "repeatCount", "repeatDur",
        "requiredExtensions", "requiredFeatures", "specularConstant", "specularExponent", "spreadMethod",
        "startOffset", "stdDeviation", "stitchTiles", "surfaceScale", "systemLanguage", "tableValues", "targetX",
        "targetY", "textLength", "viewBox", "viewTarget", "xChannelSelector", "yChannelSelector", "zoomAndPan");

    private static Dictionary<string, string> ToLowerKeyed(params string[] names) =>
        names.ToDictionary(n => n.ToLowerInvariant(), StringComparer.Ordinal);
}

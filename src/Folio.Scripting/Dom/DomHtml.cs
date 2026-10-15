using System.Text;
using Folio.Dom;
using Folio.Html;

namespace Folio.Scripting.Dom;

/// <summary>
/// Implements HTML serialization and fragment mutation algorithms (WHATWG HTML §13.3, §13.4; study 02, #454).
/// </summary>
internal static class DomHtml
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr"
    };

    private static readonly HashSet<string> RawTextParentElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "script", "xmp", "iframe", "noembed", "noframes", "plaintext"
    };

    /// <summary>
    /// Serializes the children of the element (or .content for template) according to WHATWG HTML §13.3.
    /// </summary>
    public static string SerializeInnerHtml(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var sb = new StringBuilder();
        var container = element is TemplateElement template ? (ContainerNode)template.Content : element;
        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            SerializeNode(sb, child);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serializes the element itself and its descendants according to WHATWG HTML §13.3.
    /// </summary>
    public static string SerializeOuterHtml(ElementNode element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var sb = new StringBuilder();
        SerializeNode(sb, element);
        return sb.ToString();
    }

    private static void SerializeNode(StringBuilder sb, Node node)
    {
        switch (node)
        {
            case ElementNode element:
                SerializeElement(sb, element);
                break;
            case Text text:
                SerializeText(sb, text);
                break;
            case Comment comment:
                sb.Append("<!--");
                sb.Append(comment.Data);
                sb.Append("-->");
                break;
            case DocumentType doctype:
                sb.Append("<!DOCTYPE ");
                sb.Append(doctype.Name);
                sb.Append('>');
                break;
            case DocumentFragment fragment:
                for (var child = fragment.FirstChild; child is not null; child = child.NextSibling)
                {
                    SerializeNode(sb, child);
                }
                break;
        }
    }

    private static void SerializeElement(StringBuilder sb, ElementNode element)
    {
        var local = element.LocalName;
        sb.Append('<');
        sb.Append(local);

        foreach (var attr in element.Attributes)
        {
            sb.Append(' ');
            sb.Append(element.OwnerDocument.TextOf(attr.Name));
            sb.Append("=\"");
            EscapeAttributeValue(sb, attr.Value);
            sb.Append('"');
        }

        sb.Append('>');

        if (VoidElements.Contains(local))
            return;

        if (element is TemplateElement template)
        {
            for (var child = template.Content.FirstChild; child is not null; child = child.NextSibling)
            {
                SerializeNode(sb, child);
            }
        }
        else
        {
            for (var child = element.FirstChild; child is not null; child = child.NextSibling)
            {
                SerializeNode(sb, child);
            }
        }

        sb.Append("</");
        sb.Append(local);
        sb.Append('>');
    }

    private static void SerializeText(StringBuilder sb, Text text)
    {
        if (text.Parent is ElementNode parent && RawTextParentElements.Contains(parent.LocalName))
        {
            sb.Append(text.Data);
        }
        else
        {
            EscapeTextData(sb, text.Data);
        }
    }

    private static void EscapeTextData(StringBuilder sb, string data)
    {
        foreach (var c in data)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\u00A0': sb.Append("&nbsp;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    private static void EscapeAttributeValue(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\u00A0': sb.Append("&nbsp;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>
    /// Sets innerHTML on an element according to WHATWG HTML §13.4.
    /// </summary>
    public static void SetInnerHtml(ElementNode target, string markup)
    {
        ArgumentNullException.ThrowIfNull(target);
        var container = target is TemplateElement template ? (ContainerNode)template.Content : target;

        var parsedNodes = TreeBuilder.ParseFragment(markup, target, target.OwnerDocument);

        var fragment = target.OwnerDocument.CreateFragment();
        foreach (var node in parsedNodes)
            fragment.AppendChild(node);
        container.ReplaceAll(fragment);
    }

    /// <summary>
    /// Sets outerHTML on an element according to WHATWG HTML §13.4.
    /// Throws NoModificationAllowedError if target has no parent or parent is a Document.
    /// </summary>
    public static void SetOuterHtml(ElementNode target, string markup)
    {
        ArgumentNullException.ThrowIfNull(target);

        var parent = target.Parent;
        if (parent is null || parent is DocumentNode)
        {
            throw WebIdlException.DomException(
                DomExceptionNames.NoModificationAllowedError,
                "The element has no parent or its parent is a Document.");
        }

        var context = parent as ElementNode;
        var parsedNodes = TreeBuilder.ParseFragment(markup, context, target.OwnerDocument);

        var next = target.NextSibling;
        parent.RemoveChild(target);

        foreach (var node in parsedNodes)
        {
            parent.InsertBefore(node, next);
        }
    }

    /// <summary>
    /// Implements insertAdjacentHTML(position, text) on Element (WHATWG HTML §13.4).
    /// </summary>
    public static void InsertAdjacentHtml(ElementNode target, string position, string markup)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(position);

        var pos = position.ToLowerInvariant();
        switch (pos)
        {
            case "beforebegin":
            {
                var parent = target.Parent;
                if (parent is null || parent is DocumentNode)
                {
                    throw WebIdlException.DomException(
                        DomExceptionNames.NoModificationAllowedError,
                        "The element has no parent or its parent is a Document.");
                }
                var context = parent as ElementNode;
                var parsed = TreeBuilder.ParseFragment(markup, context, target.OwnerDocument);
                foreach (var node in parsed)
                {
                    parent.InsertBefore(node, target);
                }
                break;
            }

            case "afterbegin":
            {
                var container = target is TemplateElement template ? (ContainerNode)template.Content : target;
                var parsed = TreeBuilder.ParseFragment(markup, target, target.OwnerDocument);
                var first = container.FirstChild;
                foreach (var node in parsed)
                {
                    container.InsertBefore(node, first);
                }
                break;
            }

            case "beforeend":
            {
                var container = target is TemplateElement template ? (ContainerNode)template.Content : target;
                var parsed = TreeBuilder.ParseFragment(markup, target, target.OwnerDocument);
                foreach (var node in parsed)
                {
                    container.AppendChild(node);
                }
                break;
            }

            case "afterend":
            {
                var parent = target.Parent;
                if (parent is null || parent is DocumentNode)
                {
                    throw WebIdlException.DomException(
                        DomExceptionNames.NoModificationAllowedError,
                        "The element has no parent or its parent is a Document.");
                }
                var context = parent as ElementNode;
                var parsed = TreeBuilder.ParseFragment(markup, context, target.OwnerDocument);
                var next = target.NextSibling;
                foreach (var node in parsed)
                {
                    parent.InsertBefore(node, next);
                }
                break;
            }

            default:
                throw WebIdlException.DomException(
                    DomExceptionNames.SyntaxError,
                    $"The position '{position}' is not one of 'beforebegin', 'afterbegin', 'beforeend', or 'afterend'.");
        }
    }
}

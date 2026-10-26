using System.Text;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

/// <summary>
/// <c>innerText</c> and <c>outerText</c> (https://html.spec.whatwg.org/multipage/dom.html#the-innertext-idl-attribute): the text of an
/// element as it is rendered, with white space collapsed the way CSS collapses it, lines broken where blocks, paragraphs and
/// <c>br</c> break them, and what is hidden left out.
/// </summary>
internal static class DomText
{
    private readonly record struct Item(string? Text, int Breaks, WhiteSpaceCollapse Mode, TextTransform Transform, bool Literal);

    private static readonly HashSet<string> HiddenTags = new(StringComparer.Ordinal) { "head", "script", "style", "template", "noscript", "title", "meta", "link", "base", "datalist" };

    private static readonly HashSet<string> BlockTags = new(StringComparer.Ordinal)
    {
        "html", "body", "address", "article", "aside", "blockquote", "details", "dialog", "dd", "div", "dl", "dt", "fieldset", "figcaption", "figure",
        "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hgroup", "hr", "main", "menu", "nav", "ol", "p", "pre", "section", "summary", "ul", "li", "table", "caption",
    };

    /// <summary>The element's text as rendered, or its text content when it is not rendered.</summary>
    public static string InnerText(ElementNode element)
    {
        if (!IsRendered(element))
            return element.TextContent ?? "";
        var items = new List<Item>();
        foreach (var child in element.Children)
            Collect(child, items);
        return Flatten(items);
    }

    /// <summary>Replaces an element's children with the text, a <c>br</c> for each line break in it.</summary>
    public static void SetInnerText(ElementNode element, string text)
    {
        while (element.FirstChild is { } child)
            element.RemoveChild(child);
        foreach (var node in Fragment(element.OwnerDocument, text))
            element.AppendChild(node);
    }

    /// <summary>Replaces the element itself with the text; an element with no parent to put it in cannot be.</summary>
    public static void SetOuterText(ElementNode element, string text)
    {
        if (element.Parent is not { } parent)
        {
            throw WebIdlException.DomException(DomExceptionNames.NoModificationAllowedError, "The element has no parent.");
        }
        var (previous, next) = (element.PreviousSibling, element.NextSibling);
        parent.RemoveChild(element);
        foreach (var node in Fragment(element.OwnerDocument, text))
            parent.InsertBefore(node, next);
        // Text that ends up beside text is one text node.
        Merge(parent, next?.PreviousSibling);
        Merge(parent, previous);

        static void Merge(ContainerNode parent, Node? left)
        {
            if (left is Text a && a.NextSibling is Text b)
            {
                a.Data += b.Data;
                parent.RemoveChild(b);
            }
        }
    }

    private static IEnumerable<Node> Fragment(DocumentNode document, string text)
    {
        var run = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\n' or '\r'))
            {
                run.Append(c);
                continue;
            }
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            if (run.Length > 0)
                yield return document.CreateText(run.ToString());
            run.Clear();
            yield return document.CreateElement(Namespaces.Html, "br");
        }
        if (run.Length > 0)
            yield return document.CreateText(run.ToString());
    }

    private static Display DisplayOf(ElementNode element)
    {
        if (element.ComputedStyle() is { } style)
            return style.Box.Display;
        if (HiddenTags.Contains(element.LocalName) || element.GetAttribute("hidden") is not null)
            return Display.None;
        return element.LocalName switch
        {
            "li" => Display.ListItem,
            "tr" => Display.TableRow,
            "td" or "th" => Display.TableCell,
            "table" => Display.Table,
            "caption" => Display.TableCaption,
            _ => BlockTags.Contains(element.LocalName) ? Display.Block : Display.Inline,
        };
    }

    private static bool IsRendered(ElementNode element)
    {
        if (!element.IsConnected)
            return false;
        for (Node? node = element; node is ElementNode e; node = node.Parent)
        {
            if (DisplayOf(e) == Display.None)
                return false;
        }
        return true;
    }

    private static bool Visible(ElementNode element) => (element.ComputedStyle()?.Inherited.Visibility ?? Visibility.Visible) == Visibility.Visible;

    // https://html.spec.whatwg.org/multipage/dom.html#rendered-text-collection-steps
    private static void Collect(Node node, List<Item> items)
    {
        if (node is Text text)
        {
            if (text.Parent is not ElementNode owner || !Visible(owner))
                return;
            var style = owner.ComputedStyle();
            items.Add(new Item(text.Data, 0, style?.Text.WhiteSpaceCollapse ?? WhiteSpaceCollapse.Collapse, style?.TextSpacing.Transform ?? TextTransform.None, false));
            return;
        }
        if (node is not ElementNode element)
            return;
        var display = DisplayOf(element);
        if (display == Display.None)
            return;
        var start = items.Count;
        foreach (var child in element.Children)
            Collect(child, items);
        if (!Visible(element) || display == Display.Contents)
            return;
        if (element.Name.Namespace == Namespaces.Html && element.LocalName == "br")
            items.Add(new Item("\n", 0, WhiteSpaceCollapse.Preserve, TextTransform.None, true));
        if (display == Display.TableCell && HasLaterSibling(element, Display.TableCell))
            items.Add(new Item("\t", 0, WhiteSpaceCollapse.Preserve, TextTransform.None, true));
        if (display == Display.TableRow && !IsLastRow(element))
            items.Add(new Item("\n", 0, WhiteSpaceCollapse.Preserve, TextTransform.None, true));
        var breaks = element.Name.Namespace == Namespaces.Html && element.LocalName == "p" ? 2
            : display is Display.Block or Display.FlowRoot or Display.ListItem or Display.Flex or Display.Grid or Display.Table or Display.TableCaption ? 1 : 0;
        if (breaks > 0)
        {
            items.Insert(start, new Item(null, breaks, default, default, false));
            items.Add(new Item(null, breaks, default, default, false));
        }
    }

    private static bool HasLaterSibling(ElementNode element, Display display)
    {
        for (var sibling = element.NextSibling; sibling is not null; sibling = sibling.NextSibling)
        {
            if (sibling is ElementNode e && DisplayOf(e) == display)
                return true;
        }
        return false;
    }

    private static bool IsLastRow(ElementNode row)
    {
        ElementNode? table = null;
        for (var cursor = row.Parent as ElementNode; cursor is not null; cursor = cursor.Parent as ElementNode)
        {
            if (DisplayOf(cursor) is Display.Table or Display.InlineTable)
            {
                table = cursor;
                break;
            }
        }
        if (table is null)
            return true;
        ElementNode? last = null;
        void Walk(ElementNode scope)
        {
            foreach (var child in scope.Children.OfType<ElementNode>())
            {
                var display = DisplayOf(child);
                if (display == Display.TableRow)
                    last = child;
                else if (display is Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup)
                    Walk(child);
            }
        }
        Walk(table);
        return last == row;
    }

    // Joins the items: white space collapsed, and each run of required line breaks written as the largest of them.
    private static string Flatten(List<Item> items)
    {
        var sb = new StringBuilder();
        var pending = 0;
        var atLineStart = true;
        var endsInCollapsibleSpace = false;

        void Flush()
        {
            if (pending > 0 && sb.Length > 0)
                sb.Append('\n', pending);
            pending = 0;
        }

        void TrimSpace()
        {
            if (endsInCollapsibleSpace)
                sb.Length--;
            endsInCollapsibleSpace = false;
        }

        void Emit(char c, bool collapsibleSpace)
        {
            Flush();
            sb.Append(c);
            endsInCollapsibleSpace = collapsibleSpace;
            atLineStart = c == '\n';
        }

        foreach (var item in items)
        {
            if (item.Text is null)
            {
                TrimSpace();
                pending = Math.Max(pending, item.Breaks);
                atLineStart = true;
                continue;
            }
            if (item.Literal)
            {
                if (item.Text == "\n")
                    TrimSpace();
                foreach (var c in item.Text)
                    Emit(c, false);
                continue;
            }
            foreach (var c in Transform(item.Text, item.Transform))
            {
                var white = c is ' ' or '\t' or '\n' or '\r' or '\f';
                switch (item.Mode)
                {
                    case WhiteSpaceCollapse.Collapse:
                        if (!white)
                            Emit(c, false);
                        else if (!atLineStart && !endsInCollapsibleSpace)
                            Emit(' ', true);
                        break;
                    case WhiteSpaceCollapse.PreserveBreaks:
                        if (c == '\n')
                        {
                            TrimSpace();
                            Emit('\n', false);
                        }
                        else if (!white)
                            Emit(c, false);
                        else if (!atLineStart && !endsInCollapsibleSpace)
                            Emit(' ', true);
                        break;
                    default:
                        if (c != '\r')
                            Emit(c, false);
                        break;
                }
            }
        }
        TrimSpace();
        return sb.ToString();
    }

    private static string Transform(string text, TextTransform transform) => transform switch
    {
        TextTransform.Uppercase => text.ToUpperInvariant(),
        TextTransform.Lowercase => text.ToLowerInvariant(),
        TextTransform.Capitalize => Capitalize(text),
        _ => text,
    };

    private static string Capitalize(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsLetter(chars[i]) && (i == 0 || char.IsWhiteSpace(chars[i - 1]) || char.IsPunctuation(chars[i - 1])))
                chars[i] = char.ToUpperInvariant(chars[i]);
        }
        return new string(chars);
    }
}

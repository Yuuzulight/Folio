using System.Text;
using Folio.Dom;
using Folio.Style;

namespace Folio.Interaction;

/// <summary>
/// Serializes a DOM range into plain text (following the HTML innerText algorithm)
/// or into a well-formed HTML fragment.
/// </summary>
internal static class RangeSerializer
{
    public static string ToPlainText(DomRange range)
    {
        if (range.IsCollapsed)
            return "";

        var sb = new StringBuilder();
        var context = new PlainTextContext();

        if (range.Start.Node == range.End.Node && range.Start.Node is Text text)
        {
            var s = Math.Clamp(range.Start.Offset, 0, text.Data.Length);
            var e = Math.Clamp(range.End.Offset, 0, text.Data.Length);
            if (s >= e)
                return "";

            AppendTextSlice(sb, text, s, e, context);
            TrimTrailingWhitespace(sb);
            return sb.ToString();
        }

        if (range.Start.Node == range.End.Node && range.Start.Node is ElementNode elNode)
        {
            var val = elNode.Value ?? elNode.GetAttribute("value") ?? (elNode.LocalName == "textarea" ? elNode.TextContent : null) ?? "";
            var s = Math.Clamp(range.Start.Offset, 0, val.Length);
            var e = Math.Clamp(range.End.Offset, 0, val.Length);
            if (s >= e)
                return "";
            return val.Substring(s, e - s);
        }

        var commonAncestor = GetCommonAncestor(range.Start.Node, range.End.Node);
        if (commonAncestor is not ContainerNode container)
            return "";

        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            SerializePlainTextSubtree(child, range, sb, context);
        }

        TrimTrailingWhitespace(sb);
        return sb.ToString();
    }

    public static string ToHtml(DomRange range)
    {
        if (range.IsCollapsed)
            return "";

        var sb = new StringBuilder();

        if (range.Start.Node == range.End.Node && range.Start.Node is Text text)
        {
            var s = Math.Clamp(range.Start.Offset, 0, text.Data.Length);
            var e = Math.Clamp(range.End.Offset, 0, text.Data.Length);
            if (s >= e)
                return "";

            var textContent = EscapeHtml(text.Data.Substring(s, e - s));

            var enclosing = new List<ElementNode>();
            for (Node? curr = text.Parent; curr is not null; curr = curr.Parent)
            {
                if (curr is ElementNode el)
                {
                    if (IsBlock(el) || el.LocalName is "body" or "html")
                        break;
                    enclosing.Add(el);
                }
            }
            enclosing.Reverse();

            foreach (var el in enclosing)
                AppendOpeningTag(sb, el);

            sb.Append(textContent);

            for (var i = enclosing.Count - 1; i >= 0; i--)
                AppendClosingTag(sb, enclosing[i]);

            return sb.ToString();
        }

        if (range.Start.Node == range.End.Node && range.Start.Node is ElementNode elNode)
        {
            var val = elNode.Value ?? elNode.GetAttribute("value") ?? (elNode.LocalName == "textarea" ? elNode.TextContent : null) ?? "";
            var s = Math.Clamp(range.Start.Offset, 0, val.Length);
            var e = Math.Clamp(range.End.Offset, 0, val.Length);
            if (s >= e)
                return "";
            return EscapeHtml(val.Substring(s, e - s));
        }

        var commonAncestor = GetCommonAncestor(range.Start.Node, range.End.Node);
        if (commonAncestor is not ContainerNode container)
            return "";

        var commonEnclosing = new List<ElementNode>();
        if (commonAncestor is ElementNode commonEl && !IsBlock(commonEl) && commonEl.LocalName is not "body" and not "html")
        {
            for (Node? curr = commonEl; curr is not null; curr = curr.Parent)
            {
                if (curr is ElementNode el)
                {
                    if (IsBlock(el) || el.LocalName is "body" or "html")
                        break;
                    commonEnclosing.Add(el);
                }
            }
            commonEnclosing.Reverse();
        }

        foreach (var el in commonEnclosing)
            AppendOpeningTag(sb, el);

        for (var child = container.FirstChild; child is not null; child = child.NextSibling)
        {
            SerializeHtmlSubtree(child, range, sb);
        }

        for (var i = commonEnclosing.Count - 1; i >= 0; i--)
            AppendClosingTag(sb, commonEnclosing[i]);

        return sb.ToString();
    }

    /// <summary>
    /// Wraps an HTML fragment in the standard Windows CF_HTML clipboard format with byte offsets.
    /// </summary>
    public static string ToWindowsClipboardHtml(string fragment)
    {
        const string headerTemplate =
            "Version:0.9\r\n" +
            "StartHTML:{0:D8}\r\n" +
            "EndHTML:{1:D8}\r\n" +
            "StartFragment:{2:D8}\r\n" +
            "EndFragment:{3:D8}\r\n";

        const string pre = "<html><body>\r\n<!--StartFragment-->";
        const string post = "<!--EndFragment-->\r\n</body></html>";

        var dummyHeader = string.Format(headerTemplate, 0, 0, 0, 0);
        var headerBytes = Encoding.UTF8.GetByteCount(dummyHeader);
        var preBytes = Encoding.UTF8.GetByteCount(pre);
        var fragmentBytes = Encoding.UTF8.GetByteCount(fragment);
        var postBytes = Encoding.UTF8.GetByteCount(post);

        var startHtml = headerBytes;
        var startFrag = startHtml + preBytes;
        var endFrag = startFrag + fragmentBytes;
        var endHtml = endFrag + postBytes;

        var header = string.Format(headerTemplate, startHtml, endHtml, startFrag, endFrag);
        return header + pre + fragment + post;
    }

    private static void SerializePlainTextSubtree(Node node, DomRange range, StringBuilder sb, PlainTextContext ctx)
    {
        if (node is Text text)
        {
            var startOffset = 0;
            if (node == range.Start.Node)
                startOffset = Math.Clamp(range.Start.Offset, 0, text.Data.Length);
            else if (NodePrecedes(node, range.Start))
                return;

            var endOffset = text.Data.Length;
            if (node == range.End.Node)
                endOffset = Math.Clamp(range.End.Offset, 0, text.Data.Length);
            else if (NodeFollows(node, range.End))
                return;

            if (startOffset < endOffset)
            {
                AppendTextSlice(sb, text, startOffset, endOffset, ctx);
            }
            return;
        }

        if (node is ElementNode el)
        {
            if (!IsRendered(el))
                return;

            if (NodePrecedes(el, range.Start))
                return;
            if (NodeFollows(el, range.End))
                return;

            if (el.LocalName == "br")
            {
                TrimTrailingSpaces(sb);
                sb.Append('\n');
                ctx.EndsWithNewline = true;
                ctx.EndsWithWhitespace = true;
                return;
            }

            var isBlock = IsBlock(el);
            var isTableRow = el.LocalName == "tr";
            var isTableCell = el.LocalName is "td" or "th";

            if (isBlock && !isTableCell)
            {
                if (sb.Length > 0 && !ctx.EndsWithNewline)
                {
                    TrimTrailingSpaces(sb);
                    sb.Append('\n');
                    ctx.EndsWithNewline = true;
                    ctx.EndsWithWhitespace = true;
                }
            }

            var previousCellsInRow = ctx.CellsInRow;
            if (isTableRow)
            {
                ctx.CellsInRow = 0;
            }
            else if (isTableCell)
            {
                if (ctx.CellsInRow > 0)
                {
                    TrimTrailingSpaces(sb);
                    sb.Append('\t');
                    ctx.EndsWithWhitespace = true;
                    ctx.EndsWithNewline = false;
                }
                ctx.CellsInRow++;
            }

            for (var child = el.FirstChild; child is not null; child = child.NextSibling)
            {
                SerializePlainTextSubtree(child, range, sb, ctx);
            }

            if (isTableRow)
            {
                ctx.CellsInRow = previousCellsInRow;
            }

            if (isBlock && !isTableCell)
            {
                if (sb.Length > 0 && !ctx.EndsWithNewline)
                {
                    TrimTrailingSpaces(sb);
                    sb.Append('\n');
                    ctx.EndsWithNewline = true;
                    ctx.EndsWithWhitespace = true;
                }
            }
        }
    }

    private static void SerializeHtmlSubtree(Node node, DomRange range, StringBuilder sb)
    {
        if (node is Text text)
        {
            var startOffset = 0;
            if (node == range.Start.Node)
                startOffset = Math.Clamp(range.Start.Offset, 0, text.Data.Length);
            else if (NodePrecedes(node, range.Start))
                return;

            var endOffset = text.Data.Length;
            if (node == range.End.Node)
                endOffset = Math.Clamp(range.End.Offset, 0, text.Data.Length);
            else if (NodeFollows(node, range.End))
                return;

            if (startOffset < endOffset)
            {
                sb.Append(EscapeHtml(text.Data.Substring(startOffset, endOffset - startOffset)));
            }
            return;
        }

        if (node is ElementNode el)
        {
            if (!IsRendered(el))
                return;

            if (NodePrecedes(el, range.Start))
                return;
            if (NodeFollows(el, range.End))
                return;

            if (IsVoidElement(el.LocalName))
            {
                AppendOpeningTag(sb, el);
                return;
            }

            AppendOpeningTag(sb, el);

            for (var child = el.FirstChild; child is not null; child = child.NextSibling)
            {
                SerializeHtmlSubtree(child, range, sb);
            }

            AppendClosingTag(sb, el);
        }
    }

    private static void AppendTextSlice(StringBuilder sb, Text text, int startOffset, int endOffset, PlainTextContext ctx)
    {
        var slice = text.Data.Substring(startOffset, endOffset - startOffset);
        if (slice.Length == 0)
            return;

        var isPre = IsPreformatted(text);
        var isPreserveBreaks = IsPreserveBreaks(text);

        if (isPre)
        {
            sb.Append(slice);
            ctx.EndsWithNewline = slice.EndsWith('\n');
            ctx.EndsWithWhitespace = char.IsWhiteSpace(slice[^1]);
            return;
        }

        foreach (var c in slice)
        {
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                if (isPreserveBreaks && c == '\n')
                {
                    TrimTrailingSpaces(sb);
                    sb.Append('\n');
                    ctx.EndsWithNewline = true;
                    ctx.EndsWithWhitespace = true;
                }
                else if (!ctx.EndsWithWhitespace)
                {
                    sb.Append(' ');
                    ctx.EndsWithWhitespace = true;
                    ctx.EndsWithNewline = false;
                }
            }
            else
            {
                sb.Append(c);
                ctx.EndsWithNewline = false;
                ctx.EndsWithWhitespace = false;
            }
        }
    }

    private static void TrimTrailingSpaces(StringBuilder sb)
    {
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t'))
            sb.Length--;
    }

    private static void TrimTrailingWhitespace(StringBuilder sb)
    {
        while (sb.Length > 0 && (sb[^1] == ' ' || sb[^1] == '\t' || sb[^1] == '\n' || sb[^1] == '\r'))
            sb.Length--;
    }

    private static bool IsRendered(ElementNode el)
    {
        if (el.LocalName is "script" or "style" or "head" or "meta" or "link" or "title" or "template")
            return false;

        var style = el.ComputedStyle();
        if (style is not null)
        {
            if (style.Box.Display == Display.None)
                return false;
            if (style.Inherited.Visibility != Visibility.Visible)
                return false;
        }
        return true;
    }

    private static bool IsBlock(ElementNode el)
    {
        var style = el.ComputedStyle();
        if (style is not null)
        {
            return style.Box.Display is Display.Block or Display.FlowRoot or Display.ListItem
                or Display.Table or Display.TableRow or Display.Flex or Display.Grid;
        }
        return el.LocalName is "div" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "ul" or "ol" or "li" or "blockquote" or "pre" or "header" or "footer"
            or "section" or "article" or "nav" or "aside" or "table" or "tr" or "form"
            or "figure" or "figcaption" or "hr";
    }

    private static bool IsPreformatted(Node node)
    {
        for (Node? curr = node; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el)
            {
                if (el.LocalName == "pre")
                    return true;
                var collapse = el.ComputedStyle()?.Text.WhiteSpaceCollapse;
                if (collapse is WhiteSpaceCollapse.Preserve or WhiteSpaceCollapse.PreserveSpaces or WhiteSpaceCollapse.BreakSpaces)
                    return true;
            }
        }
        return false;
    }

    private static bool IsPreserveBreaks(Node node)
    {
        for (Node? curr = node; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el)
            {
                var collapse = el.ComputedStyle()?.Text.WhiteSpaceCollapse;
                if (collapse == WhiteSpaceCollapse.PreserveBreaks)
                    return true;
            }
        }
        return false;
    }

    private static bool IsVoidElement(string localName) => localName switch
    {
        "area" or "base" or "br" or "col" or "embed" or "hr" or "img"
            or "input" or "link" or "meta" or "param" or "source" or "track" or "wbr" => true,
        _ => false,
    };

    private static Node GetCommonAncestor(Node a, Node b)
    {
        if (a == b)
            return a;

        var aAncestors = new HashSet<Node>();
        for (Node? curr = a; curr is not null; curr = curr.Parent)
            aAncestors.Add(curr);

        for (Node? curr = b; curr is not null; curr = curr.Parent)
        {
            if (aAncestors.Contains(curr))
                return curr;
        }

        return a.OwnerDocument;
    }

    private static RangePoint GetStartOfNode(Node node) => new(node, 0);

    private static RangePoint GetEndOfNode(Node node)
    {
        if (node is Text t)
            return new RangePoint(t, t.Data.Length);
        if (node is ContainerNode c)
        {
            var count = 0;
            for (var child = c.FirstChild; child is not null; child = child.NextSibling)
                count++;
            return new RangePoint(c, count);
        }
        return new RangePoint(node, 0);
    }

    private static bool NodePrecedes(Node node, RangePoint point)
    {
        if (node.IsInclusiveAncestorOf(point.Node))
            return false;
        return GetEndOfNode(node).CompareTo(point) < 0;
    }

    private static bool NodeFollows(Node node, RangePoint point)
    {
        if (node.IsInclusiveAncestorOf(point.Node))
            return false;
        return GetStartOfNode(node).CompareTo(point) >= 0;
    }

    private static void AppendOpeningTag(StringBuilder sb, ElementNode el)
    {
        sb.Append('<');
        sb.Append(el.LocalName);
        foreach (var attr in el.Attributes)
        {
            sb.Append(' ');
            sb.Append(el.OwnerDocument.TextOf(attr.Name));
            sb.Append("=\"");
            sb.Append(EscapeHtml(attr.Value));
            sb.Append('"');
        }
        sb.Append('>');
    }

    private static void AppendClosingTag(StringBuilder sb, ElementNode el)
    {
        sb.Append("</");
        sb.Append(el.LocalName);
        sb.Append('>');
    }

    private static string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    private sealed class PlainTextContext
    {
        public bool EndsWithNewline { get; set; } = true;
        public bool EndsWithWhitespace { get; set; } = true;
        public int CellsInRow { get; set; } = 0;
    }
}

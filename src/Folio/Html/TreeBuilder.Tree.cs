using Folio.Dom;

namespace Folio.Html;

// The stack of open elements, the list of active formatting elements, and node insertion.
internal sealed partial class TreeBuilder
{
    private Element CurrentNode => _open[^1];

    private static bool IsHtml(Element element) => element.Name.Namespace == Namespaces.Html;

    private static bool IsHtml(Element element, string name) => IsHtml(element) && element.LocalName == name;

    private static bool IsHtml(Element element, params ReadOnlySpan<string> names) =>
        IsHtml(element) && names.Contains(element.LocalName);

    private bool CurrentIsHtml(params ReadOnlySpan<string> names) => IsHtml(CurrentNode, names);

    private static bool IsMathMLTextIntegrationPoint(Element element) =>
        element.Name.Namespace == Namespaces.MathML && element.LocalName is "mi" or "mo" or "mn" or "ms" or "mtext";

    private static bool IsHtmlIntegrationPoint(Element element)
    {
        if (element.Name.Namespace == Namespaces.MathML && element.LocalName == "annotation-xml")
        {
            var encoding = element.GetAttribute("encoding");
            return encoding is not null
                && (encoding.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                    || encoding.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
        }
        return element.Name.Namespace == Namespaces.Svg && element.LocalName is "foreignObject" or "desc" or "title";
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#special
    private static readonly HashSet<string> SpecialHtml =
    [
        "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br",
        "button", "caption", "center", "col", "colgroup", "dd", "details", "dir", "div", "dl", "dt", "embed",
        "fieldset", "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6",
        "head", "header", "hgroup", "hr", "html", "iframe", "img", "input", "keygen", "li", "link", "listing", "main",
        "marquee", "menu", "meta", "nav", "noembed", "noframes", "noscript", "object", "ol", "p", "param", "plaintext",
        "pre", "script", "search", "section", "select", "source", "style", "summary", "table", "tbody", "td",
        "template", "textarea", "tfoot", "th", "thead", "title", "tr", "track", "ul", "wbr", "xmp",
    ];

    private static bool IsSpecial(Element element)
    {
        if (IsHtml(element))
            return SpecialHtml.Contains(element.LocalName);
        return IsMathMLTextIntegrationPoint(element)
            || (element.Name.Namespace == Namespaces.MathML && element.LocalName == "annotation-xml")
            || (element.Name.Namespace == Namespaces.Svg && element.LocalName is "foreignObject" or "desc" or "title");
    }

    private void Pop() => _open.RemoveAt(_open.Count - 1);

    private void PopUntil(params ReadOnlySpan<string> names)
    {
        while (_open.Count > 0)
        {
            var popped = CurrentNode;
            Pop();
            if (IsHtml(popped, names))
                return;
        }
    }

    private void PopUntil(Element element)
    {
        while (_open.Count > 0)
        {
            var popped = CurrentNode;
            Pop();
            if (popped == element)
                return;
        }
    }

    private bool OpenContains(string name) => _open.Exists(e => IsHtml(e, name));

    // https://html.spec.whatwg.org/multipage/parsing.html#has-an-element-in-the-specific-scope
    private enum Scope
    {
        Default,
        ListItem,
        Button,
        Table,
        Select,
    }

    private bool InScope(string name, Scope scope = Scope.Default) => InScope(e => IsHtml(e, name), scope);

    private bool InScope(Element target, Scope scope = Scope.Default) => InScope(e => e == target, scope);

    private bool InScope(Predicate<Element> isTarget, Scope scope)
    {
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var node = _open[i];
            if (isTarget(node))
                return true;
            if (IsScopeBoundary(node, scope))
                return false;
        }
        return false;
    }

    private static bool IsScopeBoundary(Element node, Scope scope)
    {
        switch (scope)
        {
            case Scope.Table:
                return IsHtml(node, "html", "table", "template");
            case Scope.Select:
                return !IsHtml(node, "optgroup", "option");
        }

        if (IsHtml(node))
        {
            if (node.LocalName is "applet" or "caption" or "html" or "table" or "td" or "th" or "marquee" or "object" or "template")
                return true;
            if (scope == Scope.ListItem && node.LocalName is "ol" or "ul")
                return true;
            if (scope == Scope.Button && node.LocalName == "button")
                return true;
            return false;
        }
        return IsMathMLTextIntegrationPoint(node)
            || (node.Name.Namespace == Namespaces.MathML && node.LocalName == "annotation-xml")
            || (node.Name.Namespace == Namespaces.Svg && node.LocalName is "foreignObject" or "desc" or "title");
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#generate-implied-end-tags
    private void GenerateImpliedEndTags(string? except = null)
    {
        while (_open.Count > 0 && CurrentIsHtml("dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc")
               && CurrentNode.LocalName != except)
            Pop();
    }

    private void GenerateAllImpliedEndTagsThoroughly()
    {
        while (_open.Count > 0 && CurrentIsHtml("caption", "colgroup", "dd", "dt", "li", "optgroup", "option", "p",
                   "rb", "rp", "rt", "rtc", "tbody", "td", "tfoot", "th", "thead", "tr"))
            Pop();
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#close-a-p-element
    private void ClosePElement()
    {
        GenerateImpliedEndTags("p");
        if (!CurrentIsHtml("p"))
            Error();
        PopUntil("p");
    }

    private void ClosePIfInButtonScope()
    {
        if (InScope("p", Scope.Button))
            ClosePElement();
    }

    // ---------------------------------------------------------------- insertion

    // https://html.spec.whatwg.org/multipage/parsing.html#appropriate-place-for-inserting-a-node
    private (ContainerNode Parent, Node? Before) AppropriatePlace(Element? overrideTarget = null)
    {
        var target = overrideTarget ?? CurrentNode;
        if (overrideTarget is null && _open.Count > _limits.MaxDepth)
        {
            // Past the depth limit, new nodes attach to the deepest allowed element (docs/study/01-html-parsing.md).
            target = _open[_limits.MaxDepth - 1];
            if (!_depthReported)
            {
                _depthReported = true;
                _parseError?.Invoke("nesting-depth-limit", _tokenizer.Position);
            }
        }

        (ContainerNode Parent, Node? Before) place;
        if (_fosterParenting && IsHtml(target, "table", "tbody", "tfoot", "thead", "tr"))
        {
            var lastTemplate = _open.FindLastIndex(e => IsHtml(e, "template"));
            var lastTable = _open.FindLastIndex(e => IsHtml(e, "table"));
            if (lastTemplate >= 0 && (lastTable < 0 || lastTemplate > lastTable))
                place = (_open[lastTemplate], null);
            else if (lastTable < 0)
                place = (_open[0], null);
            else if (_open[lastTable].Parent is { } tableParent)
                place = (tableParent, _open[lastTable]);
            else
                place = (_open[lastTable - 1], null);
        }
        else
        {
            place = (target, null);
        }

        if (place.Parent is TemplateElement template)
            place = (template.Content, null);
        return place;
    }

    private void InsertCharacters(string text)
    {
        var (parent, before) = AppropriatePlace();
        if (parent is Document)
            return;
        if ((before is null ? parent.LastChild : before.PreviousSibling) is Dom.Text previous)
        {
            previous.Data += text;
            return;
        }
        if (Count())
            parent.InsertBefore(_document.CreateText(text), before);
    }

    private void InsertComment(string data, ContainerNode? parent = null)
    {
        if (!Count())
            return;
        var comment = _document.CreateComment(data);
        if (parent is not null)
        {
            parent.AppendChild(comment);
            return;
        }
        var place = AppropriatePlace();
        place.Parent.InsertBefore(comment, place.Before);
    }

    // Counts a new node against the limit; false once it is reached.
    private bool Count()
    {
        if (++_nodes <= _limits.MaxNodes)
            return true;
        if (_nodes == _limits.MaxNodes + 1)
        {
            _parseError?.Invoke("node-count-limit", _tokenizer.Position);
            _tokenizer.Stop();
        }
        return false;
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#create-an-element-for-the-token
    private Element CreateElement(string localName, Atom ns, List<TokenAttribute>? attributes, Func<string, (Atom Ns, string Name)>? adjust = null)
    {
        var element = _document.CreateElement(ns, localName);
        if (attributes is { Count: > 0 })
        {
            var parsed = new Dom.Attribute[attributes.Count];
            for (var i = 0; i < parsed.Length; i++)
            {
                var (attrNs, name) = adjust is null ? (Atom.None, attributes[i].Name) : adjust(attributes[i].Name);
                parsed[i] = new Dom.Attribute(_document.Intern(name), attributes[i].Value, attrNs);
            }
            element.SetParsedAttributes(parsed);
        }
        return element;
    }

    private Element Clone(Element element)
    {
        var clone = _document.CreateElement(element.Name.Namespace, element.LocalName);
        clone.SetParsedAttributes(element.Attributes.ToArray());
        return clone;
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#insert-a-foreign-element
    private Element InsertElement(Element element)
    {
        var (parent, before) = AppropriatePlace();
        if (Count() && parent is not Document { DocumentElement: not null })
            parent.InsertBefore(element, before);
        _open.Add(element);
        return element;
    }

    private Element InsertHtmlElement(Token token) =>
        InsertElement(CreateElement(token.Name, Namespaces.Html, token.Attributes));

    private Element InsertHtmlElement(string name) => InsertElement(CreateElement(name, Namespaces.Html, null));

    private void InsertHtmlElementIntoDocument(Token? token)
    {
        var html = CreateElement("html", Namespaces.Html, token?.Attributes);
        if (Count())
            _document.AppendChild(html);
        _open.Add(html);
    }

    private void InsertVoid(Token token)
    {
        InsertHtmlElement(token);
        Pop();
        _selfClosingAcknowledged = true;
    }

    private void AddMissingAttributes(Element element, Token token)
    {
        foreach (var attribute in token.Attributes)
        {
            if (element.GetAttribute(attribute.Name) is null)
                element.SetAttribute(attribute.Name, attribute.Value);
        }
    }

    // ---------------------------------------------------------------- active formatting elements

    // https://html.spec.whatwg.org/multipage/parsing.html#push-onto-the-list-of-active-formatting-elements
    private void PushFormatting(Element element)
    {
        var matches = 0;
        var earliest = -1;
        for (var i = _formatting.Count - 1; i >= 0 && _formatting[i] is { } entry; i--)
        {
            if (SameElementAndAttributes(entry, element) && ++matches >= 3)
                earliest = i;
        }
        if (earliest >= 0)
            _formatting.RemoveAt(earliest);
        _formatting.Add(element);
    }

    private static bool SameElementAndAttributes(Element a, Element b)
    {
        if (a.Name != b.Name || a.Attributes.Length != b.Attributes.Length)
            return false;
        foreach (var attribute in a.Attributes)
        {
            var found = false;
            foreach (var other in b.Attributes)
            {
                if (other == attribute)
                {
                    found = true;
                    break;
                }
            }
            if (!found)
                return false;
        }
        return true;
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#reconstruct-the-active-formatting-elements
    private void ReconstructActiveFormattingElements()
    {
        if (_formatting.Count == 0 || _formatting[^1] is not { } last || _open.Contains(last))
            return;

        var index = _formatting.Count - 1;
        while (index > 0 && _formatting[index - 1] is { } previous && !_open.Contains(previous))
            index--;

        for (; index < _formatting.Count; index++)
            _formatting[index] = InsertElement(Clone(_formatting[index]!));
    }

    private void ClearFormattingToLastMarker()
    {
        while (_formatting.Count > 0)
        {
            var entry = _formatting[^1];
            _formatting.RemoveAt(_formatting.Count - 1);
            if (entry is null)
                return;
        }
    }

    private int LastFormattingAfterMarker(string name)
    {
        for (var i = _formatting.Count - 1; i >= 0 && _formatting[i] is { } entry; i--)
        {
            if (IsHtml(entry, name))
                return i;
        }
        return -1;
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#adoption-agency-algorithm
    // Returns false when the token must be handled as "any other end tag".
    private bool AdoptionAgency(string subject)
    {
        if (IsHtml(CurrentNode, subject) && !_formatting.Contains(CurrentNode))
        {
            Pop();
            return true;
        }

        for (var outer = 0; outer < 8; outer++)
        {
            var formattingIndex = LastFormattingAfterMarker(subject);
            if (formattingIndex < 0)
                return false;
            var formattingElement = _formatting[formattingIndex]!;

            var stackIndex = _open.IndexOf(formattingElement);
            if (stackIndex < 0)
            {
                Error();
                _formatting.RemoveAt(formattingIndex);
                return true;
            }
            if (!InScope(formattingElement))
            {
                Error();
                return true;
            }
            if (formattingElement != CurrentNode)
                Error();

            var furthestIndex = -1;
            for (var i = stackIndex + 1; i < _open.Count; i++)
            {
                if (IsSpecial(_open[i]))
                {
                    furthestIndex = i;
                    break;
                }
            }
            if (furthestIndex < 0)
            {
                PopUntil(formattingElement);
                _formatting.Remove(formattingElement);
                return true;
            }

            var furthestBlock = _open[furthestIndex];
            var commonAncestor = _open[stackIndex - 1];
            var bookmark = formattingIndex;
            var lastNode = furthestBlock;
            var nodeIndex = furthestIndex;

            for (var inner = 1; ; inner++)
            {
                nodeIndex--;
                var node = _open[nodeIndex];
                if (node == formattingElement)
                    break;

                var entry = _formatting.IndexOf(node);
                if (inner > 3 && entry >= 0)
                {
                    _formatting.RemoveAt(entry);
                    if (entry < bookmark)
                        bookmark--;
                    entry = -1;
                }
                if (entry < 0)
                {
                    _open.RemoveAt(nodeIndex);
                    continue;
                }

                var replacement = Clone(node);
                _formatting[entry] = replacement;
                _open[nodeIndex] = replacement;
                node = replacement;
                if (lastNode == furthestBlock)
                    bookmark = entry + 1;
                node.AppendChild(lastNode);
                lastNode = node;
            }

            var (parent, before) = AppropriatePlace(commonAncestor);
            parent.InsertBefore(lastNode, before);

            var newElement = Clone(formattingElement);
            while (furthestBlock.FirstChild is { } child)
                newElement.AppendChild(child);
            furthestBlock.AppendChild(newElement);

            var oldEntry = _formatting.IndexOf(formattingElement);
            _formatting.RemoveAt(oldEntry);
            if (oldEntry < bookmark)
                bookmark--;
            _formatting.Insert(Math.Min(bookmark, _formatting.Count), newElement);

            _open.Remove(formattingElement);
            _open.Insert(_open.IndexOf(furthestBlock) + 1, newElement);
        }
        return true;
    }

    // ---------------------------------------------------------------- insertion mode reset

    // https://html.spec.whatwg.org/multipage/parsing.html#reset-the-insertion-mode-appropriately
    private void ResetInsertionMode()
    {
        for (var i = _open.Count - 1; i >= 0; i--)
        {
            var node = _open[i];
            var last = i == 0;
            if (!IsHtml(node))
            {
                if (last)
                {
                    _mode = Mode.InBody;
                    return;
                }
                continue;
            }

            switch (node.LocalName)
            {
                case "select":
                    for (var j = i - 1; j > 0; j--)
                    {
                        if (IsHtml(_open[j], "template"))
                            break;
                        if (IsHtml(_open[j], "table"))
                        {
                            _mode = Mode.InSelectInTable;
                            return;
                        }
                    }
                    _mode = Mode.InSelect;
                    return;
                case "td" or "th" when !last:
                    _mode = Mode.InCell;
                    return;
                case "tr":
                    _mode = Mode.InRow;
                    return;
                case "tbody" or "thead" or "tfoot":
                    _mode = Mode.InTableBody;
                    return;
                case "caption":
                    _mode = Mode.InCaption;
                    return;
                case "colgroup":
                    _mode = Mode.InColumnGroup;
                    return;
                case "table":
                    _mode = Mode.InTable;
                    return;
                case "template":
                    _mode = _templateModes[^1];
                    return;
                case "head" when !last:
                    _mode = Mode.InHead;
                    return;
                case "body":
                    _mode = Mode.InBody;
                    return;
                case "html":
                    _mode = _head is null ? Mode.BeforeHead : Mode.AfterHead;
                    return;
            }
            if (last)
            {
                _mode = Mode.InBody;
                return;
            }
        }
    }
}

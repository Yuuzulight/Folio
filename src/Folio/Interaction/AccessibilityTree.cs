using System.Globalization;
using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Layout;
using Folio.Painting;
using Folio.Style;

namespace Folio.Interaction;

[Flags]
internal enum AccessibleStates
{
    None = 0,
    Checked = 1 << 0,
    Mixed = 1 << 1,
    Selected = 1 << 2,
    Disabled = 1 << 3,
    Required = 1 << 4,
    ReadOnly = 1 << 5,
    Expanded = 1 << 6,
    Collapsed = 1 << 7,
}

/// <summary>
/// One node of the accessibility tree: a WAI-ARIA 1.2 role (https://www.w3.org/TR/wai-aria-1.2/#role_definitions), or
/// "text" for a text leaf, with its accessible name, value, states and heading level.
/// </summary>
internal sealed class AccessibleNode(string role, Node node)
{
    public string Role { get; } = role;
    public Node Node { get; } = node;
    public string Name { get; init; } = "";
    public string? Value { get; init; }
    public int Level { get; init; }
    public AccessibleStates States { get; init; }

    /// <summary>The border boxes of the node's fragments joined, in page coordinates (CSS pixels); null when it has none.</summary>
    public RectF? Bounds { get; set; }

    public List<AccessibleNode> Children { get; } = [];

    /// <summary>The tree as indented lines: role, quoted name, then level, value and states when present.</summary>
    public string Dump()
    {
        var text = new StringBuilder();
        Write(this, 0);
        return text.ToString();

        void Write(AccessibleNode node, int depth)
        {
            text.Append(' ', depth * 2).Append(node.Role).Append(" \"").Append(node.Name).Append('"');
            if (node.Level > 0)
                text.Append(" level=").Append(node.Level);
            if (node.Value is { } value)
                text.Append(" value=\"").Append(value).Append('"');
            if (node.States != AccessibleStates.None)
                text.Append(" [").Append(node.States.ToString().ToLowerInvariant()).Append(']');
            text.Append('\n');
            foreach (var child in node.Children)
                Write(child, depth + 1);
        }
    }
}

/// <summary>
/// Builds the accessibility tree from the styled DOM and its layout: roles from HTML-AAM
/// (https://www.w3.org/TR/html-aam-1.0/#html-element-role-mappings) for the elements Folio exposes, names from Accessible
/// Name and Description Computation 1.2 (https://www.w3.org/TR/accname-1.2/#mapping_additional_nd_te). Elements without
/// one of those roles are not exposed; their content joins their parent. aria-hidden, display: none and hidden inputs
/// remove a subtree; visibility: hidden removes the element but not its visible descendants.
/// </summary>
internal static class AccessibilityTree
{
    private static readonly HashSet<string> KnownRoles =
    [
        "button", "caption", "cell", "checkbox", "columnheader", "combobox", "document", "group", "heading", "image", "img",
        "link", "list", "listbox", "listitem", "meter", "option", "paragraph", "progressbar", "radio", "row", "rowheader",
        "searchbox", "slider", "spinbutton", "switch", "table", "textbox",
    ];

    private static readonly HashSet<string> NameFromContent =
    [
        "button", "cell", "checkbox", "columnheader", "heading", "link", "option", "radio", "row", "rowheader", "switch",
    ];

    // Roles whose children are presentational (WAI-ARIA 1.2), or whose content is their value.
    private static readonly HashSet<string> LeafRoles =
    [
        "button", "checkbox", "combobox", "image", "meter", "option", "progressbar", "radio", "searchbox", "slider",
        "spinbutton", "switch", "textbox",
    ];

    /// <param name="page">The laid-out page the bounds come from; null leaves every node without bounds.</param>
    public static AccessibleNode Build(DocumentNode document, Fragment? page, string title)
    {
        var (boxes, texts) = page is { } laid ? BoundsOf(laid) : (new Dictionary<Node, RectF>(), new Dictionary<Node, RectF>());
        var root = new AccessibleNode("document", document) { Name = title };
        AddChildren(document, root);
        return root;

        void AddChildren(ContainerNode parent, AccessibleNode into)
        {
            foreach (var child in parent.Children)
            {
                if (child is Text text && parent is ElementNode owner && Visible(owner) && Collapse(text.Data) is { Length: > 0 } data)
                    into.Children.Add(new AccessibleNode("text", text) { Name = data, Bounds = texts.TryGetValue(owner, out var r) ? r : null });
                else if (child is ElementNode element && !Excluded(element))
                    AddElement(element, into);
            }
        }

        void AddElement(ElementNode element, AccessibleNode into)
        {
            if (element.Name.Namespace != Namespaces.Html)
            {
                // ponytail: SVG and MathML are exposed as one image when named, their content is not walked.
                if (element.LocalName == "svg" && Visible(element) && NameOf(element, "image") is { Length: > 0 } svgName)
                    into.Children.Add(new AccessibleNode("image", element) { Name = svgName, Bounds = boxes.TryGetValue(element, out var r) ? r : null });
                return;
            }
            if ((Visible(element) ? RoleOf(element) : null) is not { } role)
            {
                AddChildren(element, into);
                return;
            }
            var node = new AccessibleNode(role, element)
            {
                Name = NameOf(element, role),
                Value = ValueOf(element, role),
                Level = role == "heading" ? LevelOf(element) : 0,
                States = StatesOf(element, role),
            };
            into.Children.Add(node);
            if (!LeafRoles.Contains(role))
                AddChildren(element, node);
            // An element without a box of its own (display: contents) covers its children.
            node.Bounds = boxes.TryGetValue(element, out var bounds) ? bounds : Union(node.Children.Select(c => c.Bounds));
        }
    }

    // The border boxes of each element's fragments, and of the text fragments directly in each element, joined.
    // ponytail: transforms are ignored and every text leaf of an element shares its text bounds; per-node text ranges come with #413.
    private static (Dictionary<Node, RectF> Boxes, Dictionary<Node, RectF> Texts) BoundsOf(Fragment page)
    {
        var (boxes, texts) = (new Dictionary<Node, RectF>(), new Dictionary<Node, RectF>());
        Walk(page, 0, 0);
        return (boxes, texts);

        void Walk(Fragment fragment, float x, float y)
        {
            var rect = new RectF(x, y, fragment.Width, fragment.Height);
            if (fragment.Kind == FragmentKind.Box && fragment.Box is { Node: ElementNode element, PseudoElement: PseudoElement.None })
                Add(boxes, element, rect);
            else if (fragment.Kind == FragmentKind.Text && (fragment.Text?.Inline?.Node ?? fragment.Box?.Node) is ElementNode owner)
                Add(texts, owner, rect);
            foreach (var child in fragment.Children)
                Walk(child.Fragment, x + child.X, y + child.Y);
        }

        static void Add(Dictionary<Node, RectF> map, Node node, RectF rect) =>
            map[node] = map.TryGetValue(node, out var old) ? Union([old, rect])!.Value : rect;
    }

    private static RectF? Union(IEnumerable<RectF?> rects)
    {
        RectF? result = null;
        foreach (var rect in rects)
        {
            if (rect is not { } r)
                continue;
            result = result is not { } u ? r
                : RectFrom(Math.Min(u.X, r.X), Math.Min(u.Y, r.Y), Math.Max(u.Right, r.Right), Math.Max(u.Bottom, r.Bottom));
        }
        return result;

        static RectF RectFrom(float left, float top, float right, float bottom) => new(left, top, right - left, bottom - top);
    }

    // The subtree is not exposed: not rendered (display: none, or never styled), aria-hidden, or a hidden input.
    private static bool Excluded(ElementNode element) =>
        element.ComputedStyle() is not { } style || style.Box.Display == Display.None
        || Attr(element, "aria-hidden") == "true"
        || element.LocalName == "input" && Attr(element, "type") == "hidden";

    private static bool Visible(ElementNode element) => element.ComputedStyle()?.Inherited.Visibility == Visibility.Visible;

    // The attribute's value, trimmed and ASCII-lowercased (for enumerated attributes); null when absent.
    private static string? Attr(ElementNode element, string name) => element.GetAttribute(name)?.Trim().ToLowerInvariant();

    private static bool Has(ElementNode element, string name) => element.GetAttribute(name) is not null;

    private static string? RoleOf(ElementNode element)
    {
        if (Attr(element, "role")?.Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(KnownRoles.Contains) is { } role)
            return role == "img" ? "image" : role;
        if (Attr(element, "role") is "none" or "presentation")
            return null;
        return element.LocalName switch
        {
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "heading",
            "a" or "area" when Has(element, "href") => "link",
            "ul" or "ol" or "menu" => "list",
            "li" => "listitem",
            "table" => "table",
            "caption" => "caption",
            "tr" => "row",
            "td" => "cell",
            "th" => HeaderRole(element),
            "p" => "paragraph",
            "img" when element.GetAttribute("alt") is "" => null,
            "img" => "image",
            "input" => Attr(element, "type") switch
            {
                "checkbox" => "checkbox",
                "radio" => "radio",
                "range" => "slider",
                "number" => "spinbutton",
                "search" => "searchbox",
                "button" or "submit" or "reset" or "image" or "file" => "button",
                _ => "textbox",
            },
            "textarea" => "textbox",
            "select" => Has(element, "multiple") || int.TryParse(element.GetAttribute("size"), out var size) && size > 1 ? "listbox" : "combobox",
            "option" => "option",
            "button" => "button",
            "fieldset" => "group",
            "progress" => "progressbar",
            "meter" => "meter",
            _ => null,
        };
    }

    // https://www.w3.org/TR/html-aam-1.0/#el-th: a header of its row when scoped so, or when the row has data cells.
    private static string HeaderRole(ElementNode th) => Attr(th, "scope") switch
    {
        "row" or "rowgroup" => "rowheader",
        "col" or "colgroup" => "columnheader",
        _ => th.Parent is ElementNode { LocalName: "tr" } row && row.Children.OfType<ElementNode>().Any(c => c.LocalName == "td") ? "rowheader" : "columnheader",
    };

    private static int LevelOf(ElementNode element) =>
        int.TryParse(element.GetAttribute("aria-level"), NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level > 0 ? level
        : element.LocalName is ['h', >= '1' and <= '6' and var digit] ? digit - '0' : 2;

    private static string NameOf(ElementNode element, string role) => Collapse(Name(element, role, referenced: false, recursing: false, [element]));

    // https://www.w3.org/TR/accname-1.2/#computation-steps, steps 2B to 2I (2A, skipping hidden nodes, is the callers').
    private static string Name(ElementNode element, string? role, bool referenced, bool recursing, HashSet<Node> visited)
    {
        // 2B: aria-labelledby, not followed from inside another aria-labelledby.
        if (!referenced && element.GetAttribute("aria-labelledby") is { } ids)
        {
            var parts = ids.Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries)
                .Select(id => ById(element.OwnerDocument, id))
                .Where(target => target is not null && visited.Add(target))
                .Select(target => Collapse(Name(target!, RoleOf(target!), referenced: true, recursing: true, visited)));
            if (string.Join(' ', parts).Trim() is { Length: > 0 } labelled)
                return labelled;
        }
        // 2C: inside another name, an embedded control contributes its value.
        if (recursing && role is "textbox" or "searchbox" or "combobox" or "listbox" or "slider" or "spinbutton")
            return role == "listbox" ? string.Join(' ', SelectedOptions(element).Select(o => o.TextContent)) : ValueOf(element, role) ?? "";
        // 2D: aria-label.
        if (element.GetAttribute("aria-label")?.Trim() is { Length: > 0 } label)
            return label;
        // 2E: the host language's label (HTML-AAM accessible name computations).
        if (Native(element, visited) is { Length: > 0 } native)
            return native;
        // 2F to 2H: the content, for roles named by it and for anything inside another name.
        if (recursing || referenced || role is not null && NameFromContent.Contains(role))
        {
            if (Content(element, visited) is { } content && !string.IsNullOrWhiteSpace(content))
                return content;
        }
        // 2I: the tooltip attribute.
        return element.GetAttribute("title") ?? "";
    }

    private static string? Native(ElementNode element, HashSet<Node> visited)
    {
        switch (element.LocalName)
        {
            case "img" or "area":
                return element.GetAttribute("alt");
            case "input" when Attr(element, "type") is "button" or "submit" or "reset" or "image":
                return Attr(element, "type") switch
                {
                    "image" => element.GetAttribute("alt") ?? element.GetAttribute("value") ?? "Submit",
                    "submit" => element.GetAttribute("value") ?? "Submit",
                    "reset" => element.GetAttribute("value") ?? "Reset",
                    _ => element.GetAttribute("value"),
                };
            case "input" or "textarea" or "select" or "meter" or "progress" or "button":
                var labels = string.Join(' ', LabelsOf(element).Where(visited.Add).Select(l => Collapse(Content(l, visited))));
                return labels.Trim().Length > 0 ? labels
                    : element.LocalName is "input" or "textarea" && element.GetAttribute("title") is null ? element.GetAttribute("placeholder") : null;
            case "table":
                return FirstChild(element, "caption") is { } caption ? Content(caption, visited) : null;
            case "fieldset":
                return FirstChild(element, "legend") is { } legend ? Content(legend, visited) : null;
            default:
                return null;
        }
    }

    // https://html.spec.whatwg.org/multipage/forms.html#the-label-element: labels whose for names the control, or that hold
    // it with no for attribute.
    private static IEnumerable<ElementNode> LabelsOf(ElementNode control)
    {
        var id = control.GetAttribute("id");
        var root = control.OwnerDocument;
        for (Node? node = root; node is not null; node = node.NextInTree(root))
        {
            if (node is ElementNode { LocalName: "label" } label && label.Name.Namespace == Namespaces.Html
                && (label.GetAttribute("for") is { } target ? id is { Length: > 0 } && target == id : label.IsInclusiveAncestorOf(control)))
                yield return label;
        }
    }

    // The text alternatives of the children joined (2F); block-level children are set off with spaces.
    private static string Content(ContainerNode element, HashSet<Node> visited)
    {
        var text = new StringBuilder();
        foreach (var child in element.Children)
        {
            if (child is Text t && element is ElementNode owner && Visible(owner))
                text.Append(t.Data);
            else if (child is ElementNode e && !Excluded(e) && visited.Add(e))
            {
                var part = Visible(e) ? Name(e, RoleOf(e), referenced: false, recursing: true, visited) : Content(e, visited);
                var inline = e.ComputedStyle()?.Box.Display is Display.Inline or Display.Contents;
                text.Append(inline ? part : $" {part} ");
            }
        }
        return text.ToString();
    }

    private static string? ValueOf(ElementNode element, string role) => role switch
    {
        "textbox" or "searchbox" => element.LocalName == "textarea" ? element.TextContent
            : Attr(element, "type") == "password" ? new string('•', element.GetAttribute("value")?.Length ?? 0)
            : element.LocalName == "input" ? element.GetAttribute("value") ?? "" : null,
        "combobox" when element.LocalName == "select" => SelectedOptions(element).FirstOrDefault() is { } option ? Collapse(option.TextContent) : "",
        "slider" when element.LocalName == "input" => element.GetAttribute("value") ?? RangeMiddle(element),
        "spinbutton" or "progressbar" or "meter" => element.GetAttribute("value") ?? element.GetAttribute("aria-valuenow"),
        "slider" => element.GetAttribute("aria-valuenow"),
        _ => null,
    };

    // https://html.spec.whatwg.org/multipage/input.html#range-state-(type=range): the default value is the middle.
    private static string RangeMiddle(ElementNode range)
    {
        static float Parse(string? s, float fallback) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        var (min, max) = (Parse(range.GetAttribute("min"), 0), Parse(range.GetAttribute("max"), 100));
        return (max < min ? min : min + (max - min) / 2).ToString(CultureInfo.InvariantCulture);
    }

    // The options with a selected attribute; a drop-down without one shows its first option.
    private static IEnumerable<ElementNode> SelectedOptions(ElementNode select)
    {
        var options = new List<ElementNode>();
        for (Node? node = select.FirstChild; node is not null; node = node.NextInTree(select))
        {
            if (node is ElementNode { LocalName: "option" } option)
                options.Add(option);
        }
        var selected = options.Where(o => Has(o, "selected")).ToList();
        return selected.Count > 0 || Has(select, "multiple") ? selected : options.Take(1);
    }

    private static AccessibleStates StatesOf(ElementNode element, string role)
    {
        var states = AccessibleStates.None;
        var input = element.LocalName == "input";
        if (role is "checkbox" or "radio" or "switch")
        {
            var checkedValue = Attr(element, "aria-checked");
            if (input ? Has(element, "checked") : checkedValue == "true")
                states |= AccessibleStates.Checked;
            else if (checkedValue == "mixed")
                states |= AccessibleStates.Mixed;
        }
        if (role == "option" && (element.LocalName == "option" ? SelectOf(element) is { } select && SelectedOptions(select).Contains(element)
                                                               : Attr(element, "aria-selected") == "true"))
            states |= AccessibleStates.Selected;
        var formControl = element.LocalName is "input" or "button" or "select" or "textarea" or "option" or "fieldset";
        if (formControl && Has(element, "disabled") || Attr(element, "aria-disabled") == "true")
            states |= AccessibleStates.Disabled;
        if (element.LocalName is "input" or "select" or "textarea" && Has(element, "required") || Attr(element, "aria-required") == "true")
            states |= AccessibleStates.Required;
        if (element.LocalName is "input" or "textarea" && Has(element, "readonly") || Attr(element, "aria-readonly") == "true")
            states |= AccessibleStates.ReadOnly;
        states |= Attr(element, "aria-expanded") switch
        {
            "true" => AccessibleStates.Expanded,
            "false" => AccessibleStates.Collapsed,
            _ => AccessibleStates.None,
        };
        return states;
    }

    private static ElementNode? SelectOf(ElementNode option)
    {
        for (var node = option.Parent; node is ElementNode element; node = element.Parent)
        {
            if (element.LocalName == "select")
                return element;
        }
        return null;
    }

    private static ElementNode? FirstChild(ElementNode element, string localName) =>
        element.Children.OfType<ElementNode>().FirstOrDefault(c => c.LocalName == localName);

    private static ElementNode? ById(DocumentNode document, string id)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is ElementNode element && element.GetAttribute("id") == id)
                return element;
        }
        return null;
    }

    // ASCII whitespace collapsed to single spaces and trimmed (accname 1.2, step 2 final whitespace handling).
    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? "").Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries));
}

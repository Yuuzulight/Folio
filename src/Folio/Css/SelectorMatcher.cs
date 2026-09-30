using Folio.Dom;

namespace Folio.Css;

/// <summary>
/// State for one matching pass: the ancestor Bloom filter (when the caller maintains it during a tree walk) and
/// cached sibling indices for <c>:nth-child()</c> (docs/study/03-css-parsing-and-selectors.md).
/// </summary>
internal sealed class MatchContext
{
    private readonly Dictionary<ElementNode, int> _childIndex = [];
    private readonly Dictionary<ContainerNode, int> _childCount = [];

    /// <summary>Holds the current element's ancestors, or null when the caller does not maintain one.</summary>
    public AncestorFilter? Filter { get; set; }

    /// <summary>The element a <c>:has()</c> argument is being matched relative to; see <see cref="HasAnchorSelector"/>.</summary>
    public ElementNode? HasAnchor { get; set; }

    public int IndexAmongSiblings(ElementNode element, bool fromEnd)
    {
        var parent = element.Parent!;
        if (!_childIndex.TryGetValue(element, out var index))
        {
            var i = 0;
            foreach (var child in parent.Children)
            {
                if (child is ElementNode e)
                    _childIndex[e] = ++i;
            }
            _childCount[parent] = i;
            index = _childIndex[element];
        }
        return fromEnd ? _childCount[parent] - index + 1 : index;
    }
}

/// <summary>
/// A counting Bloom filter of the tags, ids and classes of the current element's ancestors; rejects most selectors
/// with descendant or child combinators without walking up the tree.
/// </summary>
internal sealed class AncestorFilter
{
    private const int Bits = 12;
    private const int Mask = (1 << Bits) - 1;
    private readonly byte[] _counts = new byte[1 << Bits];

    public void Push(ElementNode element) => Update(element, +1);

    public void Pop(ElementNode element) => Update(element, -1);

    public bool MightContainAll(int[] hashes)
    {
        foreach (var hash in hashes)
        {
            if (_counts[hash & Mask] == 0 || _counts[(hash >> Bits) & Mask] == 0)
                return false;
        }
        return true;
    }

    public static int Hash(Atom atom) => (int)((uint)atom.Value * 2654435761u >> 8);

    private void Update(ElementNode element, int delta)
    {
        Add(Hash(element.Name.LocalName), delta);
        if (!element.Id.IsNone)
            Add(Hash(element.Id), delta);
        foreach (var c in element.Classes)
            Add(Hash(c), delta);
    }

    // ponytail: saturating byte counters; a counter stuck at 255 only costs false positives, never wrong answers.
    private void Add(int hash, int delta)
    {
        foreach (var index in (ReadOnlySpan<int>)[hash & Mask, (hash >> Bits) & Mask])
        {
            var count = _counts[index];
            if (count != 255)
                _counts[index] = (byte)(count + delta);
        }
    }
}

/// <summary>Right-to-left selector matching (https://www.w3.org/TR/selectors-4/).</summary>
internal static class SelectorMatcher
{
    public static bool Matches(SelectorList list, ElementNode element, MatchContext context) =>
        list.Selectors.Exists(s => Matches(s, element, context));

    /// <summary>Matches ignoring the pseudo-element; callers compare <see cref="ComplexSelector.PseudoElement"/>.</summary>
    public static bool Matches(ComplexSelector selector, ElementNode element, MatchContext context) =>
        selector.PseudoElement != PseudoElement.Unknown && MatchFrom(selector, selector.Parts.Count - 1, element, context);

    private static bool MatchFrom(ComplexSelector selector, int index, ElementNode element, MatchContext context)
    {
        var (combinator, compound) = selector.Parts[index];
        if (!MatchesCompound(compound, element, context))
            return false;
        if (index == 0)
            return true;

        switch (combinator)
        {
            case Combinator.Descendant:
                for (var ancestor = element.Parent as ElementNode; ancestor is not null; ancestor = ancestor.Parent as ElementNode)
                {
                    if (MatchFrom(selector, index - 1, ancestor, context))
                        return true;
                }
                return false;
            case Combinator.Child:
                return element.Parent is ElementNode parent && MatchFrom(selector, index - 1, parent, context);
            case Combinator.NextSibling:
                return PreviousElement(element) is { } previous && MatchFrom(selector, index - 1, previous, context);
            case Combinator.SubsequentSibling:
                for (var sibling = PreviousElement(element); sibling is not null; sibling = PreviousElement(sibling))
                {
                    if (MatchFrom(selector, index - 1, sibling, context))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static ElementNode? PreviousElement(Node node)
    {
        for (var sibling = node.PreviousSibling; sibling is not null; sibling = sibling.PreviousSibling)
        {
            if (sibling is ElementNode e)
                return e;
        }
        return null;
    }

    private static ElementNode? NextElement(Node node)
    {
        for (var sibling = node.NextSibling; sibling is not null; sibling = sibling.NextSibling)
        {
            if (sibling is ElementNode e)
                return e;
        }
        return null;
    }

    private static bool MatchesCompound(CompoundSelector compound, ElementNode element, MatchContext context)
    {
        foreach (var simple in compound.Simples)
        {
            if (!MatchesSimple(simple, element, context))
                return false;
        }
        return true;
    }

    private static bool IsHtml(ElementNode element) => element.Name.Namespace == Namespaces.Html;

    private static bool Quirks(ElementNode element) => element.OwnerDocument.Mode == DocumentMode.Quirks;

    private static bool MatchesSimple(SimpleSelector simple, ElementNode element, MatchContext context)
    {
        switch (simple)
        {
            case TypeSelector type:
                return element.Name.LocalName == (IsHtml(element) ? type.Lower : type.Exact);
            case UniversalSelector:
                return true;
            case IdSelector id:
                return Quirks(element)
                    ? !element.Id.IsNone && element.OwnerDocument.TextOf(element.Id).Equals(id.Name, StringComparison.OrdinalIgnoreCase)
                    : element.Id == id.Atom;
            case ClassSelector c:
                if (Quirks(element))
                    return element.Classes.Any(x => element.OwnerDocument.TextOf(x).Equals(c.Name, StringComparison.OrdinalIgnoreCase));
                return Array.IndexOf(element.Classes, c.Atom) >= 0;
            case AttributeSelector attribute:
                return MatchesAttribute(attribute, element);
            case PseudoClassSelector pseudo:
                return MatchesPseudoClass(pseudo.Kind, element);
            case NthSelector nth:
                if (element.Parent is not ContainerNode)
                    return false;
                return nth.Matches(nth.OfType ? IndexOfType(element, nth.FromEnd) : context.IndexAmongSiblings(element, nth.FromEnd));
            case LangSelector lang:
                return MatchesLang(lang, element);
            case DirSelector dir:
                return IsRtl(element) == dir.Rtl;
            case LogicalSelector logical:
                var any = Matches(logical.Arguments, element, context);
                return logical.Kind == LogicalKind.Not ? !any : any;
            case HasSelector has:
                return MatchesHas(has, element, context);
            case HasAnchorSelector:
                return element == context.HasAnchor;
            case NestingSelector nesting:
                return nesting.Parent is null ? IsRoot(element) : Matches(nesting.Parent, element, context);
            default:
                return false;
        }
    }

    // https://www.w3.org/TR/selectors-4/#relational: some element reachable from the anchor through the relative
    // selector's combinators matches it. A leading descendant or child combinator keeps every match inside the anchor's
    // subtree; a leading sibling combinator keeps it among the following siblings (and their subtrees, if a descendant
    // or child combinator comes later).
    // ponytail: no result cache, so a :has() rule costs one subtree walk per candidate element on a full restyle;
    // cache per restyle pass if the benchmark shows it, and upward invalidation arrives with incremental restyle (M3).
    private static bool MatchesHas(HasSelector has, ElementNode element, MatchContext context)
    {
        var outer = context.HasAnchor;
        context.HasAnchor = element;
        try
        {
            foreach (var relative in has.Arguments.Selectors)
            {
                var last = relative.Parts.Count - 1;
                var leading = relative.Parts[1].Combinator;
                if (leading is Combinator.Descendant or Combinator.Child)
                {
                    // ":has(> a)" only needs the children.
                    var childrenOnly = leading == Combinator.Child && last == 1;
                    for (var node = FirstChildElement(element); node is not null; node = childrenOnly ? NextElement(node) : NextElementInTree(node, element))
                    {
                        if (MatchFrom(relative, last, node, context))
                            return true;
                    }
                }
                else
                {
                    var intoSubtrees = relative.Parts.Skip(2).Any(p => p.Combinator is Combinator.Descendant or Combinator.Child);
                    for (var sibling = NextElement(element); sibling is not null; sibling = NextElement(sibling))
                    {
                        if (MatchFrom(relative, last, sibling, context))
                            return true;
                        for (var node = intoSubtrees ? FirstChildElement(sibling) : null; node is not null; node = NextElementInTree(node, sibling))
                        {
                            if (MatchFrom(relative, last, node, context))
                                return true;
                        }
                    }
                }
            }
            return false;
        }
        finally
        {
            context.HasAnchor = outer;
        }
    }

    private static ElementNode? FirstChildElement(ElementNode element)
    {
        foreach (var child in element.Children)
        {
            if (child is ElementNode e)
                return e;
        }
        return null;
    }

    private static ElementNode? NextElementInTree(Node node, ElementNode root)
    {
        for (var next = node.NextInTree(root); next is not null; next = next.NextInTree(root))
        {
            if (next is ElementNode e)
                return e;
        }
        return null;
    }

    private static bool IsRoot(ElementNode element) => element.Parent is DocumentNode;

    private static int IndexOfType(ElementNode element, bool fromEnd)
    {
        var index = 1;
        for (var sibling = fromEnd ? NextElement(element) : PreviousElement(element);
             sibling is not null;
             sibling = fromEnd ? NextElement(sibling) : PreviousElement(sibling))
        {
            if (sibling.Name == element.Name)
                index++;
        }
        return index;
    }

    private static bool MatchesPseudoClass(PseudoClass kind, ElementNode element)
    {
        switch (kind)
        {
            case PseudoClass.Root or PseudoClass.Scope:
                return IsRoot(element);
            case PseudoClass.Empty:
                // Selectors 3 behaviour: any text makes the element non-empty (the Level 4 whitespace relaxation is at risk).
                return element.Children.All(n => n is Comment || n is Text { Data.Length: 0 });
            case PseudoClass.FirstChild:
                return element.Parent is not null && PreviousElement(element) is null;
            case PseudoClass.LastChild:
                return element.Parent is not null && NextElement(element) is null;
            case PseudoClass.OnlyChild:
                return element.Parent is not null && PreviousElement(element) is null && NextElement(element) is null;
            case PseudoClass.FirstOfType:
                return element.Parent is not null && IndexOfType(element, fromEnd: false) == 1;
            case PseudoClass.LastOfType:
                return element.Parent is not null && IndexOfType(element, fromEnd: true) == 1;
            case PseudoClass.OnlyOfType:
                return element.Parent is not null && IndexOfType(element, false) == 1 && IndexOfType(element, true) == 1;
            case PseudoClass.Link or PseudoClass.AnyLink:
                return IsHtml(element) && element.LocalName is "a" or "area" && element.GetAttribute("href") is not null;
            case PseudoClass.Hover:
                return (element.Flags & NodeFlags.Hover) != 0;
            case PseudoClass.Active:
                return (element.Flags & NodeFlags.Active) != 0;
            case PseudoClass.Focus:
                return (element.Flags & NodeFlags.Focus) != 0;
            case PseudoClass.FocusVisible:
                return (element.Flags & NodeFlags.FocusVisible) != 0;
            case PseudoClass.FocusWithin:
                for (Node? node = element; node is not null; node = node.NextInTree(element))
                {
                    if ((node.Flags & NodeFlags.Focus) != 0)
                        return true;
                }
                return false;
            case PseudoClass.Checked:
                return IsHtml(element) && element.LocalName switch
                {
                    "input" => element.GetAttribute("checked") is not null
                               && element.GetAttribute("type")?.ToLowerInvariant() is "checkbox" or "radio",
                    "option" => element.GetAttribute("selected") is not null,
                    _ => false,
                };
            case PseudoClass.Disabled:
                return IsDisabled(element);
            case PseudoClass.Enabled:
                return IsFormControl(element) && !IsDisabled(element);
            case PseudoClass.Defined:
                return true;
            default:
                return false; // :visited never matches; :target matches nothing until fragment navigation exists
        }
    }

    private static bool IsFormControl(ElementNode element) =>
        IsHtml(element) && element.LocalName is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset";

    // https://html.spec.whatwg.org/multipage/semantics-other.html#concept-element-disabled
    private static bool IsDisabled(ElementNode element)
    {
        if (!IsFormControl(element))
            return false;
        if (element.GetAttribute("disabled") is not null)
            return true;
        if (element.LocalName == "option" && element.Parent is ElementNode { LocalName: "optgroup" } group && group.GetAttribute("disabled") is not null)
            return true;
        if (element.LocalName is "optgroup" or "option")
            return false;

        // Inside a disabled fieldset, except within its first legend.
        Node child = element;
        for (var ancestor = element.Parent as ElementNode; ancestor is not null; child = ancestor, ancestor = ancestor.Parent as ElementNode)
        {
            if (IsHtml(ancestor) && ancestor.LocalName == "fieldset" && ancestor.GetAttribute("disabled") is not null)
            {
                var firstLegend = ancestor.Children.OfType<ElementNode>().FirstOrDefault(e => IsHtml(e) && e.LocalName == "legend");
                if (child != firstLegend)
                    return true;
            }
        }
        return false;
    }

    // https://www.w3.org/TR/selectors-4/#the-lang-pseudo (basic filtering on the element's language).
    private static bool MatchesLang(LangSelector lang, ElementNode element)
    {
        string? language = null;
        for (var e = element; e is not null && language is null; e = e.Parent as ElementNode)
            language = e.GetAttribute("xml:lang") ?? e.GetAttribute("lang");
        if (language is null)
            return false;
        foreach (var range in lang.Ranges)
        {
            if (language.Equals(range, StringComparison.OrdinalIgnoreCase)
                || (language.StartsWith(range, StringComparison.OrdinalIgnoreCase) && language.Length > range.Length && language[range.Length] == '-'))
                return true;
        }
        return false;
    }

    // https://html.spec.whatwg.org/multipage/dom.html#the-directionality: the nearest dir="ltr|rtl" wins.
    // ponytail: dir="auto" and bdi resolve as ltr until text direction analysis exists (study 11).
    private static bool IsRtl(ElementNode element)
    {
        for (var e = element; e is not null; e = e.Parent as ElementNode)
        {
            var dir = e.GetAttribute("dir");
            if (dir is not null && dir.Equals("rtl", StringComparison.OrdinalIgnoreCase))
                return true;
            if (dir is not null && dir.Equals("ltr", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return false;
    }

    private static bool MatchesAttribute(AttributeSelector selector, ElementNode element)
    {
        var html = IsHtml(element);
        string? value = null;
        foreach (var attribute in element.Attributes)
        {
            var name = element.OwnerDocument.TextOf(attribute.Name);
            if (html ? name.Equals(selector.Name, StringComparison.OrdinalIgnoreCase) : name == selector.Name)
            {
                value = attribute.Value;
                break;
            }
        }
        if (value is null)
            return false;
        if (selector.Operator == AttributeOperator.Exists)
            return true;

        var comparison = selector.Case switch
        {
            AttributeCase.Insensitive => StringComparison.OrdinalIgnoreCase,
            AttributeCase.Sensitive => StringComparison.Ordinal,
            _ => html && CaseInsensitiveHtmlAttributes.Contains(selector.Name) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal,
        };
        var wanted = selector.Value;
        return selector.Operator switch
        {
            AttributeOperator.Equals => value.Equals(wanted, comparison),
            AttributeOperator.Includes => wanted.Length > 0 && wanted.IndexOfAny(ElementNode.AsciiWhitespace) < 0
                && value.Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries).Any(v => v.Equals(wanted, comparison)),
            AttributeOperator.DashMatch => value.Equals(wanted, comparison)
                || (value.StartsWith(wanted, comparison) && value.Length > wanted.Length && value[wanted.Length] == '-'),
            AttributeOperator.Prefix => wanted.Length > 0 && value.StartsWith(wanted, comparison),
            AttributeOperator.Suffix => wanted.Length > 0 && value.EndsWith(wanted, comparison),
            AttributeOperator.Substring => wanted.Length > 0 && value.Contains(wanted, comparison),
            _ => false,
        };
    }

    // https://html.spec.whatwg.org/multipage/semantics-other.html#case-sensitivity-of-selectors
    private static readonly HashSet<string> CaseInsensitiveHtmlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept", "accept-charset", "align", "alink", "axis", "bgcolor", "charset", "checked", "clear", "codetype",
        "color", "compact", "declare", "defer", "dir", "direction", "disabled", "enctype", "face", "frame", "hreflang",
        "http-equiv", "lang", "language", "link", "media", "method", "multiple", "nohref", "noresize", "noshade",
        "nowrap", "readonly", "rel", "rev", "rules", "scope", "scrolling", "selected", "shape", "target", "text",
        "type", "valign", "valuetype", "vlink",
    };
}

using Folio.Dom;

namespace Folio.Css;

/// <summary>
/// Rules bucketed by the rightmost compound's id, else first class, else tag, else universal
/// (docs/study/03-css-parsing-and-selectors.md, matching option B).
/// </summary>
internal sealed class RuleIndex<T>
{
    /// <param name="Order">Position in the order rules were added, for "order of appearance".</param>
    /// <param name="AncestorHashes">Tags, ids and classes some ancestor must have, for the ancestor filter.</param>
    public sealed record Entry(ComplexSelector Selector, T Data, int Order, int[] AncestorHashes);

    private readonly Dictionary<Atom, List<Entry>> _byId = [];
    private readonly Dictionary<Atom, List<Entry>> _byClass = [];
    private readonly Dictionary<Atom, List<Entry>> _byTag = [];
    private readonly List<Entry> _universal = [];
    private readonly HashSet<PseudoElement> _pseudoElements = [];
    private int _count;

    public int Count => _count;

    /// <summary>Whether any rule targets this pseudo-element (so styles for it are worth computing).</summary>
    public bool HasRulesFor(PseudoElement pseudoElement) => _pseudoElements.Contains(pseudoElement);

    public void Add(ComplexSelector selector, T data)
    {
        var entry = new Entry(selector, data, _count++, AncestorHashes(selector));
        _pseudoElements.Add(selector.PseudoElement);
        var rightmost = selector.Rightmost.Simples;
        if (rightmost.OfType<IdSelector>().FirstOrDefault() is { } id)
            Bucket(_byId, id.Atom).Add(entry);
        else if (rightmost.OfType<ClassSelector>().FirstOrDefault() is { } c)
            Bucket(_byClass, c.Atom).Add(entry);
        else if (rightmost.OfType<TypeSelector>().FirstOrDefault() is { } type)
            AddByTag(type, entry);
        else if (rightmost.OfType<LogicalSelector>().FirstOrDefault(IsTypeList) is { } list)
        {
            // :is() or :where() over plain tag names (the UA's nested list rules): under each of those tags.
            foreach (var argument in list.Arguments.Selectors)
                AddByTag((TypeSelector)argument.Rightmost.Simples[0], entry);
        }
        else
            _universal.Add(entry);
    }

    // HTML elements match the lowercased name, others the name as written; no element matches both.
    private void AddByTag(TypeSelector type, Entry entry)
    {
        Bucket(_byTag, type.Lower).Add(entry);
        if (type.Exact != type.Lower)
            Bucket(_byTag, type.Exact).Add(entry);
    }

    private static bool IsTypeList(LogicalSelector selector) =>
        selector.Kind is LogicalKind.Is or LogicalKind.Where
        && selector.Arguments.Selectors.All(s => s.Parts.Count == 1 && s.Rightmost.Simples is [TypeSelector]);

    /// <summary>Appends the entries matching <paramref name="element"/> (and <paramref name="pseudoElement"/>), in order of appearance.</summary>
    public void Collect(ElementNode element, PseudoElement pseudoElement, MatchContext context, List<Entry> matches)
    {
        var start = matches.Count;
        if (!element.Id.IsNone && _byId.TryGetValue(element.Id, out var byId))
            CollectFrom(byId, element, pseudoElement, context, matches);
        foreach (var c in element.Classes)
        {
            if (_byClass.TryGetValue(c, out var byClass))
                CollectFrom(byClass, element, pseudoElement, context, matches);
        }
        if (_byTag.TryGetValue(element.Name.LocalName, out var byTag))
            CollectFrom(byTag, element, pseudoElement, context, matches);
        CollectFrom(_universal, element, pseudoElement, context, matches);

        // Entries from one bucket are in order already; only matches from several buckets need sorting.
        for (var i = start + 1; i < matches.Count; i++)
        {
            if (matches[i - 1].Order > matches[i].Order)
            {
                matches.Sort(start, matches.Count - start, ByOrder);
                break;
            }
        }
    }

    private static readonly Comparer<Entry> ByOrder = Comparer<Entry>.Create((x, y) => x.Order.CompareTo(y.Order));

    // ponytail: quirks-mode documents match ids and classes case-insensitively, so their rules may sit in a bucket
    // this lookup misses; they are rare for artifacts, and the universal bucket would be the fix if they matter.
    private static void CollectFrom(List<Entry> entries, ElementNode element, PseudoElement pseudoElement, MatchContext context, List<Entry> matches)
    {
        foreach (var entry in entries)
        {
            if (entry.Selector.PseudoElement != pseudoElement)
                continue;
            if (context.Filter is { } filter && !filter.MightContainAll(entry.AncestorHashes))
                continue;
            if (SelectorMatcher.Matches(entry.Selector, element, context))
                matches.Add(entry);
        }
    }

    private static List<Entry> Bucket(Dictionary<Atom, List<Entry>> buckets, Atom key)
    {
        if (!buckets.TryGetValue(key, out var list))
            buckets[key] = list = [];
        return list;
    }

    // Walking left from the rightmost compound: after a descendant or child combinator the compounds are ancestors,
    // until a sibling combinator leads away from the ancestor chain.
    private static int[] AncestorHashes(ComplexSelector selector)
    {
        var hashes = new List<int>();
        var ancestor = false;
        for (var i = selector.Parts.Count - 1; i > 0 && hashes.Count < 4; i--)
        {
            var combinator = selector.Parts[i].Combinator;
            if (combinator is Combinator.Descendant or Combinator.Child)
                ancestor = true;
            else if (ancestor)
                break;
            if (!ancestor)
                continue;
            foreach (var simple in selector.Parts[i - 1].Compound.Simples)
            {
                var atom = simple switch
                {
                    IdSelector id => id.Atom,
                    ClassSelector c => c.Atom,
                    TypeSelector t when t.Exact == t.Lower => t.Lower, // a mixed-case name may match either way
                    _ => Atom.None,
                };
                if (!atom.IsNone && hashes.Count < 4)
                    hashes.Add(AncestorFilter.Hash(atom));
            }
        }
        return [.. hashes];
    }
}

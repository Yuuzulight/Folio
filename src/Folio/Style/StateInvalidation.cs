using Folio.Css;
using Folio.Dom;

namespace Folio.Style;

/// <summary>
/// Which elements a change of a user-action state can restyle (docs/study/14-invalidation.md, "M3 adds"). At
/// stylesheet load, every place <c>:hover</c>, <c>:active</c>, <c>:focus</c>, <c>:focus-visible</c> or
/// <c>:focus-within</c> appears is recorded with where it sits relative to the selector's subject (the subject itself,
/// an ancestor, a preceding sibling) and what the subject must have (its tag, id and classes). A state change then
/// marks only the elements that can match: with no rule mentioning a state, changing it marks nothing.
/// </summary>
internal sealed class StateInvalidation
{
    [Flags]
    private enum Reach
    {
        Self = 1,
        Descendants = 2,
        Siblings = 4,
    }

    /// <summary>One place a state appears. Null tags, ids and classes: any subject.</summary>
    private sealed record Site(Reach Reach, Atom[] Tags, Atom[] Ids, Atom[] Classes);

    private static readonly PseudoClass[] States = [PseudoClass.Hover, PseudoClass.Active, PseudoClass.Focus, PseudoClass.FocusVisible, PseudoClass.FocusWithin];
    private static readonly Site AnyBelow = new(Reach.Self | Reach.Descendants | Reach.Siblings, [], [], []);

    private readonly Dictionary<PseudoClass, List<Site>> _sites = [];

    // ponytail: a state inside :has() can restyle ancestors and their relatives, so any change of that state restyles
    // the whole document; per-element upward sets if :has(:hover) shows up in real artifacts.
    private readonly HashSet<PseudoClass> _everywhere = [];

    public static StateInvalidation Empty { get; } = new();

    /// <summary>Whether <paramref name="selector"/> mentions a user-action state anywhere, arguments included.</summary>
    public static bool Mentions(ComplexSelector selector) => selector.Parts.Any(p => States.Any(s => Contains(p.Compound, s)));

    public static StateInvalidation Build(IEnumerable<ComplexSelector> selectors)
    {
        var sets = new StateInvalidation();
        foreach (var selector in selectors)
        {
            foreach (var state in States)
                sets.Record(selector, state);
        }
        return sets;
    }

    /// <summary>Whether any rule mentions <paramref name="state"/>.</summary>
    public bool Uses(PseudoClass state) => _sites.ContainsKey(state) || _everywhere.Contains(state);

    /// <summary>Whether a change of <paramref name="state"/> restyles the whole document.</summary>
    public bool Everywhere(PseudoClass state) => _everywhere.Contains(state);

    /// <summary>
    /// Adds to <paramref name="roots"/> the elements whose style can change when <paramref name="state"/> changes on
    /// <paramref name="changed"/>. Each is restyled with its subtree.
    /// </summary>
    public void Collect(ElementNode changed, PseudoClass state, HashSet<ElementNode> roots)
    {
        if (!_sites.TryGetValue(state, out var sites))
            return;
        var quirks = changed.OwnerDocument.Mode == DocumentMode.Quirks;
        foreach (var site in sites)
        {
            if ((site.Reach & Reach.Self) != 0 && Fits(changed, site, quirks))
                roots.Add(changed);
            if ((site.Reach & Reach.Descendants) != 0)
                Below(changed, site, quirks, roots);
            if ((site.Reach & Reach.Siblings) == 0)
                continue;
            for (var sibling = changed.NextSibling; sibling is not null; sibling = sibling.NextSibling)
            {
                if (sibling is not ElementNode element)
                    continue;
                if (Fits(element, site, quirks))
                    roots.Add(element);
                // .a:hover + .b .c: the sibling's descendants too.
                if ((site.Reach & Reach.Descendants) != 0)
                    Below(element, site, quirks, roots);
            }
        }
    }

    private static void Below(ElementNode element, Site site, bool quirks, HashSet<ElementNode> roots)
    {
        for (var node = element.NextInTree(element); node is not null; node = node.NextInTree(element))
        {
            if (node is ElementNode e && Fits(e, site, quirks))
                roots.Add(e);
        }
    }

    // What the subject must have, checked as a necessary condition. Quirks mode matches ids and classes ignoring
    // case, so there only the tag is checked.
    private static bool Fits(ElementNode element, Site site, bool quirks) =>
        (site.Tags.Length == 0 || Array.IndexOf(site.Tags, element.Name.LocalName) >= 0)
        && (quirks || site.Ids.All(id => element.Id == id))
        && (quirks || site.Classes.All(c => Array.IndexOf(element.Classes, c) >= 0));

    private void Record(ComplexSelector selector, PseudoClass state)
    {
        for (var i = 0; i < selector.Parts.Count; i++)
        {
            var compound = selector.Parts[i].Compound;
            if (compound.Simples.OfType<HasSelector>().Any(h => h.Arguments.Selectors.Any(a => a.Parts.Any(p => Contains(p.Compound, state)))))
            {
                _everywhere.Add(state);
                return;
            }
            var direct = compound.Simples.OfType<PseudoClassSelector>().Any(p => p.Kind == state);
            var nested = !direct && Contains(compound, state);
            if (!direct && !nested)
                continue;
            if (nested)
            {
                // :is(.a:hover .b) and the like: anything at or after the changed element.
                Add(state, AnyBelow);
                continue;
            }
            var reach = Reach.Self;
            if (i < selector.Parts.Count - 1)
            {
                reach = 0;
                foreach (var (combinator, _) in selector.Parts.Skip(i + 1))
                    reach |= combinator is Combinator.NextSibling or Combinator.SubsequentSibling ? Reach.Siblings : Reach.Descendants;
            }
            var subject = selector.Rightmost.Simples;
            Add(state, new Site(
                reach,
                [.. subject.OfType<TypeSelector>().SelectMany(t => t.Exact == t.Lower ? new[] { t.Lower } : [t.Lower, t.Exact])],
                [.. subject.OfType<IdSelector>().Select(s => s.Atom)],
                [.. subject.OfType<ClassSelector>().Select(s => s.Atom)]));
        }
    }

    private void Add(PseudoClass state, Site site)
    {
        if (!_sites.TryGetValue(state, out var list))
            _sites[state] = list = [];
        list.Add(site);
    }

    // The state as a simple selector of the compound, or anywhere inside its :is(), :where(), :not() or :has().
    private static bool Contains(CompoundSelector compound, PseudoClass state) => compound.Simples.Any(simple => simple switch
    {
        PseudoClassSelector p => p.Kind == state,
        LogicalSelector l => l.Arguments.Selectors.Any(s => s.Parts.Any(p => Contains(p.Compound, state))),
        HasSelector h => h.Arguments.Selectors.Any(s => s.Parts.Any(p => Contains(p.Compound, state))),
        NestingSelector { Parent: { } parent } => parent.Selectors.Any(s => s.Parts.Any(p => Contains(p.Compound, state))),
        _ => false,
    });
}

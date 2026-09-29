using Folio.Css;
using Folio.Dom;
using Folio.Html;

namespace Folio.Tests.Css;

public class SelectorTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Css", "Selectors"));

    public static TheoryData<string> ParseIds => new(AllCases.Value.Keys.Where(k => k.StartsWith("parse:", StringComparison.Ordinal)).Order());
    public static TheoryData<string> MatchIds => new(AllCases.Value.Keys.Where(k => k.StartsWith("match:", StringComparison.Ordinal)).Order());

    [Theory]
    [MemberData(nameof(ParseIds))]
    public void Parses(string id)
    {
        var test = AllCases.Value[id];
        var document = new DocumentNode();

        var list = Parse(test.Input, document, Directive(test, "parent"));

        Assert.Equal(test.Expected, list is null ? ["invalid"] : list.Selectors.Select(s => $"{s} {s.Specificity}"));
    }

    [Theory]
    [MemberData(nameof(MatchIds))]
    public void Matches(string id)
    {
        var test = AllCases.Value[id];
        var document = TreeBuilder.Parse(test.Input);
        var list = Parse(Directive(test, "selector")!, document, Directive(test, "parent"))!;
        var context = new MatchContext();

        var matched = Elements(document).Where(e => SelectorMatcher.Matches(list, e, context)).Select(Label);

        Assert.Equal(test.Expected, matched);
    }

    [Fact]
    public void RuleIndexWithAncestorFilterFindsWhatBruteForceFinds()
    {
        const string html = """
            <!DOCTYPE html><div id=app class="shell dark"><header class=top><h1 class=title>T</h1><nav><a href=#x class="link active">x</a>
            <a class=link>y</a></nav></header><main><section class=card><p class="lead">a</p><ul><li>1<li class=on>2<li>3</ul></section>
            <section class="card wide"><table><tr><td class=num>1<td>2</table><svg><circle class=dot /></svg></section></main></div>
            """;
        string[] selectors =
        [
            "p", ".card p", "#app .title", "header > h1", ".shell nav a.link", "li + li", "li ~ .on", "main section:nth-child(2) td",
            "div.dark .num", ".top a:not(.active)", "*", ":root body", "section ul > li:last-child", "svg circle.dot", "nav .missing a",
            ".wide td.num", "h1 ~ nav a", "a[href]", ":is(header, main) .card", "body > div > main > section.card",
        ];
        var document = TreeBuilder.Parse(html);
        var index = new RuleIndex<string>();
        foreach (var selector in selectors)
        {
            foreach (var complex in Parse(selector, document, null)!.Selectors)
                index.Add(complex, selector);
        }

        var filter = new AncestorFilter();
        var context = new MatchContext { Filter = filter };
        Walk(document.DocumentElement!, filter, element =>
        {
            var viaIndex = new List<RuleIndex<string>.Entry>();
            index.Collect(element, PseudoElement.None, context, viaIndex);
            var bruteForce = selectors.Where(s => SelectorMatcher.Matches(Parse(s, document, null)!, element, new MatchContext()));
            Assert.Equal(bruteForce, viaIndex.Select(e => e.Data));
        });
    }

    [Fact]
    public void PseudoElementRulesAreCollectedSeparately()
    {
        var document = TreeBuilder.Parse("<p class=x>");
        var index = new RuleIndex<string>();
        foreach (var complex in Parse(".x, .x::before", document, null)!.Selectors)
            index.Add(complex, complex.ToString());
        var p = Elements(document).Single(e => e.LocalName == "p");

        var before = new List<RuleIndex<string>.Entry>();
        index.Collect(p, PseudoElement.Before, new MatchContext(), before);

        Assert.Equal([".x::before"], before.Select(e => e.Data));
    }

    private static void Walk(Element element, AncestorFilter filter, Action<Element> visit)
    {
        visit(element);
        filter.Push(element);
        foreach (var child in element.Children.OfType<Element>())
            Walk(child, filter, visit);
        filter.Pop(element);
    }

    private static string? Directive(CaseFiles.Case test, string name) =>
        test.Directives.FirstOrDefault(d => d.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..];

    private static SelectorList? Parse(string text, DocumentNode document, string? parentText)
    {
        var parent = parentText is null ? null : Parse(parentText, document, null);
        var (source, values) = CssParser.ParseComponentValues(text);
        return SelectorParser.Parse(source, values, document.Intern, parent, nested: parent is not null);
    }

    private static IEnumerable<Element> Elements(DocumentNode document)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is Element element)
                yield return element;
        }
    }

    private static string Label(Element element) =>
        element.Id.IsNone ? element.LocalName : element.OwnerDocument.TextOf(element.Id);
}

using System.Globalization;
using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Style;

namespace Folio.Tests.Style;

public class CascadeTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Style", "Cascade"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void Cascades(string id)
    {
        var test = AllCases.Value[id];
        var viewport = test.Directives.FirstOrDefault(d => d.StartsWith("viewport ", StringComparison.Ordinal))?.Split(' ');
        var media = new MediaContext(
            viewport is null ? 800 : float.Parse(viewport[1], CultureInfo.InvariantCulture),
            viewport is null ? 600 : float.Parse(viewport[2], CultureInfo.InvariantCulture),
            DarkColorScheme: test.Directives.Contains("dark"));
        var document = TreeBuilder.Parse(test.Input);

        StyleResolver.Resolve(document, media);

        var actual = test.Expected.Select(line =>
        {
            var space = line.IndexOf(' ');
            var (label, property) = (line[..space], line[(space + 1)..line.IndexOf(':')]);
            var style = Find(document, label).ComputedStyle()!;
            // Custom properties print their computed text, or "none" when they have no value.
            var value = property.StartsWith("--", StringComparison.Ordinal)
                ? style.Custom.GetValueOrDefault(property) ?? "none"
                : Properties.Find(property)!.Describe(style);
            return $"{label} {property}: {value}";
        });
        Assert.Equal(test.Expected, actual);
    }

    [Fact]
    public void ElementsMatchingTheSameRulesUnderTheSameParentStyleShareOneStyle()
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html><style>tr:nth-child(odd) td { color: red } .x { background-color: blue }</style><table>" +
            "<tr id=r1><td id=a>1<td id=b>2<tr id=r2><td id=c>3<td id=d style='color: lime'>4<tr id=r3><td id=e>5<td id=f class=x>6</table>");

        StyleResolver.Resolve(document, new MediaContext(800, 600));

        ComputedStyle Style(string id) => Find(document, "#" + id).ComputedStyle()!;
        Assert.Same(Style("r1"), Style("r2"));                // same rules, same parent: one style
        Assert.Same(Style("a"), Style("b"));
        Assert.Same(Style("a"), Style("e"));                  // cousins, through the shared row style
        Assert.NotSame(Style("a"), Style("c"));               // an even row's cells match other rules
        Assert.Equal(new CssColor(1, 0, 0, 1), Style("a").Inherited.Color);
        Assert.Equal(new CssColor(0, 0, 0, 1), Style("c").Inherited.Color);
        Assert.Equal(new CssColor(0, 1, 0, 1), Style("d").Inherited.Color); // a style attribute is never shared
        Assert.NotSame(Style("e"), Style("f"));                // another class, another rule
        Assert.Equal(new CssColor(0, 0, 1, 1), Style("f").Background.Color);
        Assert.Equal(CssColor.Transparent, Style("e").Background.Color);
    }

    [Fact]
    public void IsOverTagNamesMatchesThoseTags()
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html><style>div :is(p, span) { color: red } :where(em) { color: blue }</style>" +
            "<div><p id=a>1</p><span id=b>2</span><b id=c>3</b><em id=d>4</em></div><p id=e>5</p>");

        StyleResolver.Resolve(document, new MediaContext(800, 600));

        Assert.Equal(new CssColor(1, 0, 0, 1), Find(document, "#a").ComputedStyle()!.Inherited.Color);
        Assert.Equal(new CssColor(1, 0, 0, 1), Find(document, "#b").ComputedStyle()!.Inherited.Color);
        Assert.Equal(new CssColor(0, 0, 0, 1), Find(document, "#c").ComputedStyle()!.Inherited.Color);
        Assert.Equal(new CssColor(0, 0, 1, 1), Find(document, "#d").ComputedStyle()!.Inherited.Color);
        Assert.Equal(new CssColor(0, 0, 0, 1), Find(document, "#e").ComputedStyle()!.Inherited.Color);
    }

    [Fact]
    public void UserStyleSheetSitsBetweenUserAgentAndAuthor()
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html><style>#b { color: blue }</style><p id=a>x<p id=b>y");

        StyleResolver.Resolve(document, new MediaContext(800, 600), userStyleSheet: "p { color: red; margin-top: 0 !important } #b { margin-top: 5px }");

        Assert.Equal(new CssColor(1, 0, 0, 1), Find(document, "#a").ComputedStyle()!.Inherited.Color);
        Assert.Equal(new CssColor(0, 0, 1, 1), Find(document, "#b").ComputedStyle()!.Inherited.Color);
        Assert.Equal(0, Find(document, "#b").ComputedStyle()!.Spacing.MarginTop.Length.Px);
    }

    [Fact]
    public void PresentationalHintsComeBeforeAuthorRules()
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html><style>#b { padding: 0 }</style><table cellpadding=4 border=2><tr>" +
            "<td id=a bgcolor=ff0000 align=center valign=top width=50%>x<td id=b>y</table>");

        StyleResolver.Resolve(document, new MediaContext(800, 600));

        var (table, a, b) = (Find(document, "table").ComputedStyle()!, Find(document, "#a").ComputedStyle()!, Find(document, "#b").ComputedStyle()!);
        Assert.Equal(2, table.Border.TopWidth);
        Assert.Equal(BorderStyle.Outset, table.Border.TopStyle);
        Assert.Equal(new CssColor(1, 0, 0, 1), a.Background.Color);
        Assert.Equal(TextAlign.Center, a.Text.TextAlign);
        Assert.Equal(VerticalAlignKind.Top, a.Box.VerticalAlign.Kind);
        Assert.Equal("50%", a.Size.Width.ToString());
        Assert.Equal(4, a.Spacing.PaddingTop.Px);
        Assert.Equal(1, a.Border.TopWidth);
        Assert.Equal(0, b.Spacing.PaddingTop.Px); // an author rule beats the hint
    }

    [Fact]
    public void ElementsWithoutOwnDeclarationsShareStyleGroups()
    {
        var document = TreeBuilder.Parse("<!DOCTYPE html><ul><li id=a>1<li id=b>2</ul>");

        StyleResolver.Resolve(document, new MediaContext(800, 600));

        var (a, b) = (Find(document, "#a").ComputedStyle()!, Find(document, "#b").ComputedStyle()!);
        Assert.Same(a.Box, b.Box);
        Assert.Same(a.Font, b.Font);
    }

    [Fact]
    public void DeepDocumentsResolveWithoutRecursion()
    {
        var document = new DocumentNode();
        ContainerNode parent = document.AppendChild(document.CreateElement(Namespaces.Html, "html"));
        for (var i = 0; i < 20_000; i++)
            parent = parent.AppendChild(document.CreateElement(Namespaces.Html, "div"));

        StyleResolver.Resolve(document, new MediaContext(800, 600));

        Assert.Equal(Display.Block, ((ElementNode)parent).ComputedStyle()!.Box.Display);
    }

    private static ElementNode Find(DocumentNode document, string label)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is ElementNode e && (label.StartsWith('#') ? e.GetAttribute("id") == label[1..] : e.LocalName == label))
                return e;
        }
        throw new InvalidOperationException($"No element {label}.");
    }
}

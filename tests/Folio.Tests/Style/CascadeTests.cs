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
            return $"{label} {property}: {Properties.Find(property)!.Describe(style)}";
        });
        Assert.Equal(test.Expected, actual);
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

        Assert.Equal(Display.Block, ((Element)parent).ComputedStyle()!.Box.Display);
    }

    private static Element Find(DocumentNode document, string label)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is Element e && (label.StartsWith('#') ? e.GetAttribute("id") == label[1..] : e.LocalName == label))
                return e;
        }
        throw new InvalidOperationException($"No element {label}.");
    }
}

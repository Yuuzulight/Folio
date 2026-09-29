using System.Globalization;
using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Layout;
using Folio.Style;

namespace Folio.Tests.Layout;

public class BlockLayoutTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Layout", "Block"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void LaysOut(string id)
    {
        var test = AllCases.Value[id];
        var viewport = test.Directives.FirstOrDefault(d => d.StartsWith("viewport ", StringComparison.Ordinal))?.Split(' ');
        var (width, height) = viewport is null ? (800f, 600f) : (float.Parse(viewport[1], CultureInfo.InvariantCulture), float.Parse(viewport[2], CultureInfo.InvariantCulture));

        Assert.Equal(test.Expected, Dump(LayOut(test.Input, width, height)));
    }

    [Fact]
    public void DeepDocumentsLayOutWithoutOverflowingTheStack()
    {
        var document = new DocumentNode();
        ContainerNode parent = document.AppendChild(document.CreateElement(Namespaces.Html, "html"));
        for (var i = 0; i < 20_000; i++)
            parent = parent.AppendChild(document.CreateElement(Namespaces.Html, "div"));
        StyleResolver.Resolve(document, new MediaContext(800, 600));

        var fragment = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document)!, 800, 600);

        Assert.Equal(800, fragment.Children[0].Fragment.Width);
    }

    internal static Fragment LayOut(string html, float width = 800, float height = 600)
    {
        var document = TreeBuilder.Parse(html);
        StyleResolver.Resolve(document, new MediaContext(width, height));
        return LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document)!, width, height);
    }

    // The fragment tree below the initial containing block, border boxes in page coordinates.
    internal static List<string> Dump(Fragment initialContainingBlock)
    {
        var lines = new List<string>();
        foreach (var child in initialContainingBlock.Children)
            DumpFragment(child, 0, 0, 0, lines);
        return lines;
    }

    private static void DumpFragment(ChildFragment placed, float parentX, float parentY, int depth, List<string> lines)
    {
        var (x, y, fragment) = (parentX + placed.X, parentY + placed.Y, placed.Fragment);
        lines.Add($"{new string(' ', depth * 2)}{Label(fragment.Box)} {N(x)},{N(y)} {N(fragment.Width)}x{N(fragment.Height)}");
        foreach (var child in fragment.Children)
            DumpFragment(child, x, y, depth + 1, lines);
    }

    private static string Label(Box? box) => box switch
    {
        { PseudoElement: not PseudoElement.None and var pe } => "::" + pe.ToString().ToLowerInvariant(),
        { Node: Element e } => e.LocalName + (e.GetAttribute("id") is { } id ? "#" + id : ""),
        _ => "(anonymous)",
    };

    private static string N(float value) => Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
}

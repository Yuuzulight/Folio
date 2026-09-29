using System.Text;
using Folio.Dom;
using Folio.Html;

namespace Folio.Tests.Html;

public class TreeBuilderTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Html", "TreeBuilder"));

    public static TheoryData<string> Cases => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Cases))]
    public void BuildsTree(string id)
    {
        var test = AllCases.Value[id];

        Assert.Equal(string.Join("\n", test.Expected), Dump(TreeBuilder.Parse(test.Input)));
    }

    [Fact]
    public void DeepNestingStopsAtTheDepthLimitAndKeepsContent()
    {
        var errors = new List<string>();
        var html = string.Concat(Enumerable.Repeat("<div>", 2000)) + "x";

        var document = TreeBuilder.Parse(html, new ParserLimits(MaxDepth: 50), (code, _) => errors.Add(code));

        var depth = 0;
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            var d = 0;
            for (var n = node; n.Parent is not null; n = n.Parent)
                d++;
            depth = Math.Max(depth, d);
        }
        Assert.True(depth <= 51, $"depth {depth}");
        Assert.Equal("x", document.DocumentElement!.TextContent);
        Assert.Contains("nesting-depth-limit", errors);
    }

    [Fact]
    public void NodeLimitStopsParsingAndKeepsWhatIsDone()
    {
        var errors = new List<string>();
        var html = "<!DOCTYPE html>" + string.Concat(Enumerable.Repeat("<p>x", 1000));

        var document = TreeBuilder.Parse(html, new ParserLimits(MaxNodes: 101), (code, _) => errors.Add(code));

        var nodes = 0;
        for (Node? node = document.FirstChild; node is not null; node = node.NextInTree(document))
            nodes++;
        Assert.Equal(101, nodes);
        Assert.Contains("node-count-limit", errors);
    }

    [Fact]
    public void ReportsTreeConstructionErrors()
    {
        var errors = new List<string>();

        TreeBuilder.Parse("<!DOCTYPE html><b><p></b></p>", parseError: (code, _) => errors.Add(code));

        Assert.Contains("unexpected-token", errors);
    }

    [Fact]
    public void ManyAttributesParseInLinearTime()
    {
        var html = "<div " + string.Join(' ', Enumerable.Range(0, 20_000).Select(i => $"a{i}=1")) + " a0=2>";

        var body = (Element)TreeBuilder.Parse(html).DocumentElement!.LastChild!;
        var div = (Element)body.FirstChild!;

        Assert.Equal(20_000, div.Attributes.Length);
        Assert.Equal("1", div.GetAttribute("a0"));
    }

    [Fact]
    public void ParsesATypicalArtifact()
    {
        const string html = """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width, initial-scale=1.0">
              <title>Quarterly Report</title>
              <style>
                :root { --accent: #4f46e5; }
                body { font-family: system-ui, sans-serif; margin: 0; }
                .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 1rem; }
              </style>
            </head>
            <body>
              <header><h1>Q3 &mdash; Summary</h1><p class="lead">Revenue grew <strong>12%</strong>.</p></header>
              <main class="grid">
                <section class="card"><h2>North</h2><ul><li>Item one<li>Item two</ul></section>
                <section class="card"><h2>South</h2>
                  <table>
                    <thead><tr><th>Month<th>Sales</thead>
                    <tr><td>July<td>1,200
                    <tr><td>August<td>1,350
                  </table>
                </section>
                <svg width="100" height="20" viewBox="0 0 100 20"><rect width="60" height="20" fill="var(--accent)"/></svg>
              </main>
              <script>document.querySelector('.lead').dataset.ready = "1";</script>
            </body>
            </html>
            """;
        var errors = new List<string>();

        var document = TreeBuilder.Parse(html, parseError: (code, _) => errors.Add(code));

        Assert.Empty(errors);
        Assert.Equal(DocumentMode.NoQuirks, document.Mode);
        var body = (Element)document.DocumentElement!.LastChild!;
        Assert.Equal("body", body.LocalName);
        var main = body.Children.OfType<Element>().Single(e => e.LocalName == "main");
        Assert.Equal(["section", "section", "svg"], main.Children.OfType<Element>().Select(e => e.LocalName));
        var table = main.Children.OfType<Element>().ElementAt(1).Children.OfType<Element>().Single(e => e.LocalName == "table");
        Assert.Equal(["thead", "tbody"], table.Children.OfType<Element>().Select(e => e.LocalName));
        Assert.Contains("Q3 \u2014 Summary", body.TextContent);
    }

    internal static string Dump(DocumentNode document)
    {
        var lines = new List<string>
        {
            document.Mode switch
            {
                DocumentMode.Quirks => "#document quirks",
                DocumentMode.LimitedQuirks => "#document limited-quirks",
                _ => "#document",
            },
        };

        var stack = new Stack<(Node Node, int Depth)>();
        PushChildren(stack, document, 1);
        while (stack.TryPop(out var item))
        {
            var (node, depth) = item;
            var indent = new string(' ', depth * 2);
            switch (node)
            {
                case DocumentType doctype:
                    lines.Add(doctype.PublicId.Length + doctype.SystemId.Length == 0
                        ? $"{indent}<!DOCTYPE {doctype.Name}>"
                        : $"{indent}<!DOCTYPE {doctype.Name} \"{doctype.PublicId}\" \"{doctype.SystemId}\">");
                    break;
                case Element element:
                    lines.Add(indent + StartTag(element));
                    PushChildren(stack, element, depth + 1);
                    if (element is TemplateElement template)
                    {
                        // Pushed last, so dumped first: the content fragment, then (rarely) real children.
                        stack.Push((template.Content, depth + 1));
                    }
                    break;
                case DocumentFragment fragment:
                    lines.Add(indent + "#content");
                    PushChildren(stack, fragment, depth + 1);
                    break;
                case Text text:
                    lines.Add($"{indent}\"{CaseFiles.Escape(text.Data)}\"");
                    break;
                case Comment comment:
                    lines.Add($"{indent}<!--{CaseFiles.Escape(comment.Data)}-->");
                    break;
            }
        }
        return string.Join('\n', lines);
    }

    private static void PushChildren(Stack<(Node, int)> stack, ContainerNode parent, int depth)
    {
        for (var child = parent.LastChild; child is not null; child = child.PreviousSibling)
            stack.Push((child, depth));
    }

    private static string StartTag(Element element)
    {
        var prefix = element.Name.Namespace == Namespaces.Svg ? "svg:" : element.Name.Namespace == Namespaces.MathML ? "math:" : "";
        var tag = new StringBuilder("<").Append(prefix).Append(element.LocalName);
        foreach (var attribute in element.Attributes)
        {
            tag.Append(' ').Append(element.OwnerDocument.TextOf(attribute.Name))
                .Append("=\"").Append(CaseFiles.Escape(attribute.Value)).Append('"');
        }
        return tag.Append('>').ToString();
    }
}

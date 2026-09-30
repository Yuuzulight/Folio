using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Layout;
using Folio.Style;

namespace Folio.Tests.Layout;

public class BoxTreeTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Layout", "BoxTree"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void Builds(string id)
    {
        var test = AllCases.Value[id];

        Assert.Equal(test.Expected, Dump(Build(test.Input)));
    }

    [Fact]
    public void RootWithDisplayNoneHasNoBox()
    {
        Assert.Null(Build("<!DOCTYPE html><style>html { display: none }</style>"));
    }

    [Fact]
    public void DeepDocumentsBuildWithoutRecursion()
    {
        var document = new DocumentNode();
        ContainerNode parent = document.AppendChild(document.CreateElement(Namespaces.Html, "html"));
        for (var i = 0; i < 20_000; i++)
            parent = parent.AppendChild(document.CreateElement(Namespaces.Html, "div"));
        parent.AppendChild(document.CreateText("deep"));
        StyleResolver.Resolve(document, new MediaContext(800, 600));

        var box = BoxTreeBuilder.Build(document)!;

        var depth = 0;
        while (box.Children.Count > 0)
        {
            box = box.Children[0];
            depth++;
        }
        Assert.Equal(20_000, depth);
        Assert.Equal("deep", ((BlockContainerBox)box).Inline!.Text);
    }

    private static Box? Build(string html)
    {
        var document = TreeBuilder.Parse(html);
        StyleResolver.Resolve(document, new MediaContext(800, 600));
        return BoxTreeBuilder.Build(document);
    }

    internal static List<string> Dump(Box? root)
    {
        var lines = new List<string>();
        if (root is not null)
            DumpBox(root, 0, lines);
        return lines;
    }

    private static void DumpBox(Box box, int depth, List<string> lines)
    {
        var indent = new string(' ', depth * 2);
        var flags = (box.IsFloat ? " [float]" : "") + (box.IsAbsolutelyPositioned ? " [absolute]" : "");
        lines.Add($"{indent}{Kind(box)} {Label(box)}{flags}");
        if (box is BlockContainerBox { Marker: { } marker })
            lines.Add($"{indent}  marker \"{CaseFiles.Escape(marker.Text)}\"");
        if (box is BlockContainerBox { Inline: { } inline })
        {
            foreach (var item in inline.Items)
            {
                var itemIndent = new string(' ', (depth + 1) * 2);
                switch (item.Kind)
                {
                    case InlineItemKind.Text:
                        lines.Add($"{itemIndent}\"{CaseFiles.Escape(inline.Text.Substring(item.Start, item.Length))}\"");
                        break;
                    case InlineItemKind.OpenBox:
                        lines.Add($"{itemIndent}<{Label(item.Box!)}{(item.Continuation ? "+" : "")}>");
                        break;
                    case InlineItemKind.CloseBox:
                        lines.Add($"{itemIndent}</{Label(item.Box!)}{(item.Continuation ? "+" : "")}>");
                        break;
                    case InlineItemKind.ForcedBreak:
                        lines.Add($"{itemIndent}br");
                        break;
                    case InlineItemKind.BreakOpportunity:
                        lines.Add($"{itemIndent}wbr");
                        break;
                    default:
                        DumpBox(item.Box!, depth + 1, lines);
                        break;
                }
            }
        }
        foreach (var child in box.Children)
            DumpBox(child, depth + 1, lines);
    }

    private static string Kind(Box box) => box switch
    {
        BlockContainerBox { IsAtomicInline: true } and not TablePartBox => "inline-block",
        TablePartBox part => part.Part switch
        {
            TablePart.Table => "table",
            TablePart.RowGroup => "row-group",
            TablePart.HeaderGroup => "header-group",
            TablePart.FooterGroup => "footer-group",
            TablePart.Row => "row",
            TablePart.Cell => "cell",
            TablePart.ColumnGroup => "column-group",
            TablePart.Column => "column",
            _ => "caption",
        },
        BlockContainerBox => "block",
        FlexContainerBox f => f.IsAtomicInline ? "inline-flex" : "flex",
        GridContainerBox g => g.IsAtomicInline ? "inline-grid" : "grid",
        TableWrapperBox => "table-wrapper",
        ReplacedBox r => $"replaced({r.Kind.ToString().ToLowerInvariant()})",
        InlineBox => "inline",
        _ => box.GetType().Name,
    };

    private static string Label(Box box) => box.PseudoElement switch
    {
        PseudoElement.Before => "::before",
        PseudoElement.After => "::after",
        PseudoElement.Marker => "::marker",
        _ => box.Node is ElementNode e ? e.LocalName : "(anonymous)",
    };
}

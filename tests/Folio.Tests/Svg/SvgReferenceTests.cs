using Folio.Dom;
using Folio.Layout;
using Folio.Painting;
using Folio.Svg;
using Folio.Tests.Layout;
using Folio.Typography;

namespace Folio.Tests.Svg;

public class SvgReferenceTests
{
    private static ElementNode Element(DocumentNode document, string name)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is ElementNode { LocalName: var local } element && local == name)
                return element;
        }
        throw new InvalidOperationException(name);
    }

    // A clip path or mask of this many rectangles.
    private static string Rectangles(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"<rect x=\"{i % 20 * 5}\" y=\"{i / 20 * 5}\" width=\"4\" height=\"4\" />"));

    [Fact]
    public void ReferencedContentIsBuiltOncePerElementAndViewport()
    {
        var document = Folio.Html.TreeBuilder.Parse("<!DOCTYPE html><svg><clipPath id=\"c\"><rect width=\"5\" height=\"5\" /></clipPath></svg>");
        var clip = Element(document, "clipPath");
        var layout = new LayoutContext(new FontCollection());
        var builds = 0;
        List<SvgRenderNode> Build()
        {
            builds++;
            return [];
        }

        // Two render trees in one layout: the second finds the content built by the first.
        foreach (var context in new[] { new SvgContext(layout, document), new SvgContext(layout, document) })
        {
            context.Clipping.Add(clip);
            context.ReferencedContent(clip, new(10, 10), Build);
            context.ReferencedContent(clip, new(10, 10), Build);
        }
        // Another viewport is other content.
        var third = new SvgContext(layout, document);
        third.Clipping.Add(clip);
        third.ReferencedContent(clip, new(20, 10), Build);

        Assert.Equal(2, builds);
    }

    [Fact]
    public void ContentBuiltWhileFollowingAnotherReferenceIsNotKept()
    {
        var document = Folio.Html.TreeBuilder.Parse("<!DOCTYPE html><svg><mask id=\"m\" /><clipPath id=\"c\" /></svg>");
        var (mask, clip) = (Element(document, "mask"), Element(document, "clipPath"));
        var layout = new LayoutContext(new FontCollection());
        var context = new SvgContext(layout, document);
        context.Masking.Add(mask);
        context.Clipping.Add(clip);
        var builds = 0;

        context.ReferencedContent(clip, new(10, 10), () => { builds++; return []; });
        context.ReferencedContent(clip, new(10, 10), () => { builds++; return []; });

        Assert.Equal(2, builds);
    }

    [Fact]
    public void ManyBoxesReferencingOneLargeClipPathStayWithinTheBudget()
    {
        // 3,000 boxes each clipped by 200 rectangles would draw 600,000 shapes; past the budget each box clips to the
        // shapes' bounding box instead.
        var html = "<!DOCTYPE html><style>div { width: 100px; height: 20px; background: red; clip-path: url(#c) }</style>" +
            $"<svg width=\"0\" height=\"0\" style=\"position: absolute\"><clipPath id=\"c\">{Rectangles(200)}</clipPath></svg>" +
            string.Concat(Enumerable.Repeat("<div></div>", 3000));

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var shapes = list.Items.Count(i => i.Kind == DisplayItemKind.FillPath);
        Assert.InRange(shapes, 200, SvgContext.MaxReferencedNodes + 200);
        // The last box's region is its clip path's bounding box, a path clip of one rectangle.
        var lastClip = list.Items.Last(i => i.Kind == DisplayItemKind.PushClip && i.Path is not null);
        Assert.Equal(5, lastClip.Path!.Commands.Count);
    }

    [Fact]
    public void ManySvgElementsReferencingOneLargeMaskStayWithinTheBudget()
    {
        var html = $"<!DOCTYPE html><svg width=\"800\" height=\"600\"><mask id=\"m\" maskUnits=\"userSpaceOnUse\">{Rectangles(200).Replace("<rect", "<rect fill=\"white\"")}</mask>" +
            string.Concat(Enumerable.Range(0, 3000).Select(i => $"<rect x=\"{i % 100}\" width=\"10\" height=\"10\" mask=\"url(#m)\" />")) + "</svg>";

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var shapes = list.Items.Count(i => i.Kind == DisplayItemKind.FillPath);
        Assert.InRange(shapes, 3000, 3000 + SvgContext.MaxReferencedNodes + 200 + 3000);
    }
}

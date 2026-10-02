using Folio.Dom;
using Folio.Layout;
using Folio.Painting;
using Folio.Tests.Layout;

namespace Folio.Tests.Painting;

public class PaintOrderWalkerTests
{
    private const string Page = """
        <!DOCTYPE html><style>body { margin: 0 } div { width: 10px; height: 10px }</style>
        <div id=block>x</div>
        <div id=float style="float: left"></div>
        <div id=positioned style="position: relative"></div>
        <div id=negative style="position: absolute; z-index: -1"></div>
        <div id=context style="position: relative; z-index: 2; outline: 1px solid">
          <div id=inner-negative style="position: absolute; z-index: -1"></div>
          <div id=inner-positive style="position: relative; z-index: 1"></div>
        </div>
        <div id=translucent style="opacity: .5"></div>
        """;

    [Fact]
    public void WalksBackToFrontInPaintOrder()
    {
        // CSS 2.2 Appendix E: the context's background, negative z, blocks, floats, inline content, z-index auto and 0 in
        // tree order, positive z, then outlines; a stacking context's own negative and positive z stay inside it.
        Assert.Equal(
        [
            "begin html", "background html",
            "begin negative", "background negative", "end negative",
            "background body", "background block",
            "begin float", "background float", "end float",
            "text",
            "begin positioned", "background positioned", "end positioned",
            "begin translucent", "background translucent", "end translucent",
            "begin context", "background context",
            "begin inner-negative", "background inner-negative", "end inner-negative",
            "begin inner-positive", "background inner-positive", "end inner-positive",
            "end context",
            "outline context",
            "end html",
        ], Walk(frontToBack: false));
    }

    [Fact]
    public void WalksFrontToBackInExactReverse()
    {
        // Hit testing goes the other way, with each context still begun before its content and ended after it.
        var expected = Walk(frontToBack: false).AsEnumerable().Reverse()
            .Select(step => step.StartsWith("begin ") ? "end " + step[6..] : step.StartsWith("end ") ? "begin " + step[4..] : step);

        Assert.Equal(expected, Walk(frontToBack: true));
    }

    private static List<string> Walk(bool frontToBack)
    {
        var recorder = new Recorder();
        PaintOrderWalker.Walk(PaintOrderWalker.StackingTree(BlockLayoutTests.LayOut(Page))!, recorder, frontToBack);
        return recorder.Steps;
    }

    private sealed class Recorder : IPaintVisitor
    {
        public List<string> Steps { get; } = [];

        public bool BeginContext(Context context)
        {
            Steps.Add("begin " + Name(context.Owner!));
            return true;
        }

        public void EndContext(Context context) => Steps.Add("end " + Name(context.Owner!));

        public void VisitBackground(PaintBox box, List<PaintBox> text) => Steps.Add("background " + Name(box));

        public void VisitCollapsedBorders(PaintBox table, List<PaintBox> cells) => Steps.Add("borders " + Name(table));

        public void VisitText(PaintBox text) => Steps.Add(Name(text));

        public void VisitOutline(PaintBox box) => Steps.Add("outline " + Name(box));

        private static string Name(PaintBox box) => box.Fragment.Kind == FragmentKind.Text ? "text"
            : box.Box.Node is ElementNode element ? element.GetAttribute("id") ?? element.LocalName : "anonymous";
    }
}

using Folio.Painting;
using Folio.Tests.Layout;

namespace Folio.Tests.Painting;

public class DisplayListPlayerTests
{
    [Fact]
    public void ClipsAndLayersBecomeSaveRestoreAndLayerPairs()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='opacity: .5; overflow: hidden; height: 10px'><div style='height: 20px; background: red'></div></div>");

        Assert.Equal(["layer 0.5", "save", "clip-rrect", "fill-rrect", "restore", "pop-layer"], calls);
    }

    [Fact]
    public void UniformSolidBordersAreOneRing()
    {
        Assert.Equal(["fill-path EvenOdd"], Replay("<style>body { margin: 0 }</style><div style='border: 2px solid red; height: 10px'></div>"));
    }

    [Fact]
    public void OtherBordersArePaintedSideBySideInClippedRegions()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='border: 2px solid red; border-left: 0; border-bottom-color: blue; height: 10px'></div>");

        // Top, right and bottom; the left side has no width.
        Assert.Equal(3, calls.Count(c => c == "clip-path NonZero"));
        Assert.Equal(3, calls.Count(c => c == "fill-path EvenOdd"));
        Assert.Equal(calls.Count(c => c == "save"), calls.Count(c => c == "restore"));
    }

    [Fact]
    public void DashedAndDottedBordersAreStroked()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='border: 3px dashed red; border-top-style: dotted; height: 10px'></div>");

        Assert.Contains("stroke 3 Round 0,6", calls);
        Assert.Contains("stroke 3 Butt 9,9", calls);
    }

    private static List<string> Replay(string html)
    {
        var canvas = new RecordingCanvas();
        DisplayListPlayer.Replay(DisplayListBuilder.Build(BlockLayoutTests.LayOut("<!DOCTYPE html>" + html)), canvas);
        return canvas.Calls;
    }

    // Logs the calls a replay makes.
    private sealed class RecordingCanvas : ICanvas
    {
        public List<string> Calls { get; } = [];

        public void Save() => Calls.Add("save");
        public void Restore() => Calls.Add("restore");
        public void ClipRoundedRect(in RoundedRect rect) => Calls.Add("clip-rrect");
        public void ClipPath(PathData path, FillRule rule) => Calls.Add($"clip-path {rule}");
        public void FillRoundedRect(in RoundedRect rect, in Paint paint) => Calls.Add("fill-rrect");
        public void FillPath(PathData path, FillRule rule, in Paint paint) => Calls.Add($"fill-path {rule}");
        public void StrokePath(PathData path, in Stroke stroke, in Paint paint) =>
            Calls.Add($"stroke {stroke.Width} {stroke.Cap} {string.Join(",", stroke.Dashes ?? [])}");
        public void PushLayer(in LayerOptions options) => Calls.Add($"layer {options.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        public void PopLayer() => Calls.Add("pop-layer");
    }
}

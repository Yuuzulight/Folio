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
    public void FilterLayersCarryTheirPrimitivesAndTheBackdropClip()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='filter: blur(1px) invert(1); backdrop-filter: blur(2px); width: 20px; height: 10px'></div>");

        Assert.Equal(["layer 1 filters 2 backdrop 1 20x10", "pop-layer"], calls);
    }

    [Fact]
    public void ClipPathsClipToTheirPathOrRoundedRectangle()
    {
        var calls = Replay("<style>body { margin: 0 } div { height: 10px }</style><div style='clip-path: polygon(evenodd, 0 0, 10px 0, 0 10px)'></div><div style='clip-path: circle()'></div>");

        Assert.Equal(["save", "clip-path EvenOdd", "restore", "save", "clip-rrect", "restore"], calls);
    }

    [Fact]
    public void TransformsBecomeSaveTransformRestorePairs()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='transform: translate(2px, 3px); height: 10px; background: red'></div>");

        Assert.Equal(["save", "transform 1,0,0,1,2,3", "fill-rrect", "restore"], calls);
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
        // 3px dashes are 6px long; the 16px-high sides fit two of them with a 4px gap, one at each end.
        Assert.Contains("stroke 3 Butt 6,4", calls);
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
        public void Transform(in System.Numerics.Matrix3x2 m) =>
            Calls.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"transform {m.M11},{m.M12},{m.M21},{m.M22},{m.M31},{m.M32}"));
        public void ClipRoundedRect(in RoundedRect rect) => Calls.Add("clip-rrect");
        public void ClipPath(PathData path, FillRule rule) => Calls.Add($"clip-path {rule}");
        public void FillRoundedRect(in RoundedRect rect, in Paint paint) => Calls.Add("fill-rrect");
        public void FillPath(PathData path, FillRule rule, in Paint paint) => Calls.Add($"fill-path {rule}");
        public void StrokePath(PathData path, in Stroke stroke, in Paint paint) =>
            Calls.Add($"stroke {stroke.Width} {stroke.Cap} {string.Join(",", stroke.Dashes ?? [])}");
        public void DrawGlyphs(Folio.Typography.IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<System.Numerics.Vector2> origins, in Paint paint) =>
            Calls.Add($"glyphs {glyphs.Length}");
        public void DrawImage(Folio.Imaging.IImageHandle image, in RectF destination, ImageSampling sampling) =>
            Calls.Add($"image {image.Width}x{image.Height} {sampling}");
        public void PushLayer(in LayerOptions options) => Calls.Add($"layer {options.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + (options.Filters is { } f ? $" filters {f.Count}" : "") + (options.Backdrop is { } b ? $" backdrop {b.Count} {options.BackdropClip.Rect.Width}x{options.BackdropClip.Rect.Height}" : ""));
        public void PopLayer() => Calls.Add("pop-layer");
    }
}

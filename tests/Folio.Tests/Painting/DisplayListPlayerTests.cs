using Folio.Painting;
using Folio.Tests.Layout;

namespace Folio.Tests.Painting;

public class DisplayListPlayerTests
{
    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(1.5f, 0f)]
    [InlineData(1.25f, 37.6f)]
    public void DamageSnapsOutwardToWholeDevicePixels(float scale, float scrollTop)
    {
        var damage = new RectF(-1, 40.3f, 773.32404f, 263.76404f);
        var snapped = DisplayListPlayer.SnapToDevicePixels(damage, scale, scrollTop);
        Assert.True(snapped.X <= damage.X && snapped.Y <= damage.Y && snapped.Right >= damage.Right && snapped.Bottom >= damage.Bottom);
        foreach (var edge in new[] { snapped.X * scale, (snapped.Y - scrollTop) * scale, snapped.Right * scale, (snapped.Bottom - scrollTop) * scale })
            Assert.True(MathF.Abs(edge - MathF.Round(edge)) < 1e-4f, $"{edge} is not a whole device pixel");
    }

    [Fact]
    public void ClipsAndLayersBecomeSaveRestoreAndLayerPairs()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='opacity: .5; overflow: hidden; height: 10px'><div style='height: 20px; background: red'></div></div>");

        // The layer is bounded by a clip around what it draws.
        Assert.Equal(["save", "clip-rrect", "layer 0.5", "save", "clip-rrect", "fill-rrect", "restore", "pop-layer", "restore"], calls);
    }

    [Fact]
    public void FilterLayersCarryTheirPrimitivesAndTheBackdropClip()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='filter: blur(1px) invert(1); backdrop-filter: blur(2px); width: 20px; height: 10px'></div>");

        Assert.Equal(["save", "clip-rrect", "layer 1 filters 2 backdrop 1 20x10", "pop-layer", "restore"], calls);
    }

    [Fact]
    public void ClipPathsClipToTheirPathOrRoundedRectangle()
    {
        var calls = Replay("<style>body { margin: 0 } div { height: 10px }</style><div style='clip-path: polygon(evenodd, 0 0, 10px 0, 0 10px)'></div><div style='clip-path: circle()'></div>");

        Assert.Equal(["save", "clip-path EvenOdd", "restore", "save", "clip-rrect", "restore"], calls);
    }

    [Fact]
    public void ItemsOutsideTheVisiblePartAreNotDrawn()
    {
        // Three 10px blocks, the middle one clipped and the last one moved; the view shows only y 0 to 8 (bounds take a pixel for antialiasing).
        const string html = "<style>body { margin: 0 } div { height: 10px; background: red }</style><div></div>"
                            + "<div style='overflow: hidden'><div></div></div><div style='transform: translate(0, -15px)'></div>";
        var calls = Replay(html, new RectF(0, 0, 100, 8));

        // The first block is drawn, the clipped one is not but its clip stays balanced, and the transformed one is
        // drawn since its place on the page is not known without the transform.
        Assert.Equal(["fill-rrect", "save", "clip-rrect", "restore", "save", "transform 1,0,0,1,0,-15", "fill-rrect", "restore"], calls);
        Assert.Equal(4, Replay(html).Count(c => c == "fill-rrect")); // without a view, every block and the clipped one inside
    }

    [Fact]
    public void OffscreenLayerDoesNotPopItsVisibleParentClip()
    {
        var view = new RoundedRect(new RectF(0, 0, 40, 40), default);
        var outside = new RoundedRect(new RectF(0, 100, 20, 20), default);
        var list = new DisplayList();
        list.Items.AddRange([
            new(DisplayItemKind.PushClip, view),
            new(DisplayItemKind.PushLayer, Opacity: 0.5f),
            new(DisplayItemKind.PushClip, outside),
            new(DisplayItemKind.PushTransform, Transform: System.Numerics.Matrix3x2.Identity),
            new(DisplayItemKind.Fill, outside),
            new(DisplayItemKind.Pop),
            new(DisplayItemKind.Pop),
            new(DisplayItemKind.Pop),
            new(DisplayItemKind.Fill, view),
            new(DisplayItemKind.Pop),
        ]);
        var canvas = new RecordingCanvas();
        DisplayListPlayer.Replay(list, canvas, view.Rect);
        Assert.Equal(["save", "clip-rrect", "fill-rrect", "restore"], canvas.Calls);
    }

    [Fact]
    public void ALayerMovedIntoTheViewportIsDrawn()
    {
        var calls = Replay("<style>body { margin: 0 }</style><div style='height:100px'></div>" +
            "<div style='transform:translateY(-100px)'><div style='height:20px;opacity:.5;background:red'></div></div>",
            new RectF(0, 0, 40, 40));
        Assert.Contains("layer 0.5", calls);
        Assert.Contains("fill-rrect", calls);
        Assert.Equal(calls.Count(call => call == "save"), calls.Count(call => call == "restore"));
    }

    [Fact]
    public void ATransformedOuterShadowUsesTheTransformedBox()
    {
        var calls = Replay("<style>body { margin: 0 }</style>" +
            "<div style='width:100px;height:100px;transform:translateX(-75px);box-shadow:0 0 16px black'></div>",
            new RectF(30, 30, 20, 20));
        Assert.Contains("clip-path EvenOdd", calls);
        Assert.Contains("fill-rrect", calls);
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

    [Fact]
    public void LayersAreBoundedByWhatTheyDrawAndHowFarTheirFiltersReach()
    {
        // A 100x10 fill at the top left; its antialiased edges add a pixel all round.
        Assert.Equal(new RectF(-1, -1, 102, 12), Bounds("<div style='opacity: .5; width: 100px; height: 10px; background: red'></div>"));
        // A 2px blur reaches three standard deviations further.
        Assert.Equal(new RectF(-7, -7, 114, 24), Bounds("<div style='filter: blur(2px); width: 100px; height: 10px; background: red'></div>"));
        // A drop shadow adds its offset copy.
        Assert.Equal(new RectF(-1, -1, 112, 17), Bounds("<div style='filter: drop-shadow(10px 5px 0 black); width: 100px; height: 10px; background: red'></div>"));
        // Content under a transform inside the layer is mapped out of it.
        Assert.Equal(new RectF(49, -1, 102, 12), Bounds("<div style='opacity: .5'><div style='transform: translateX(50px); width: 100px; height: 10px; background: red'></div></div>"));
    }

    [Fact]
    public void SvgFilterLayersAreBoundedByTheirLastSubregion()
    {
        // The offset moves the box out of what it draws; the filter region (here the bounding box) still holds it.
        Assert.Equal(new RectF(0, 0, 100, 10), Bounds("<svg width='200' height='100' style='display: block'><filter id='f' x='0' y='0' width='1' height='1'><feOffset dx='50' /></filter>" +
            "<rect width='100' height='10' fill='red' filter='url(#f)' /></svg>"));
        var list = new DisplayList();
        list.Items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: [new Filter(FilterKind.Flood)]));
        list.Items.Add(new DisplayItem(DisplayItemKind.Pop));
        Assert.Null(DisplayListPlayer.LayerBounds(list)[0]);
    }

    [Fact]
    public void LayersWithUnknownReachAreNotBounded()
    {
        Assert.Null(Bounds("<div style='perspective: 100px; opacity: .5'><div style='transform: rotateY(30deg); width: 100px; height: 10px; background: red'></div></div>"));
    }

    private static RectF? Bounds(string html)
    {
        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut("<!DOCTYPE html><style>body { margin: 0 }</style>" + html));
        var first = list.Items.FindIndex(i => i.Kind == DisplayItemKind.PushLayer);
        return DisplayListPlayer.LayerBounds(list)[first];
    }

    private static List<string> Replay(string html, RectF? visible = null)
    {
        var canvas = new RecordingCanvas();
        DisplayListPlayer.Replay(DisplayListBuilder.Build(BlockLayoutTests.LayOut("<!DOCTYPE html>" + html)), canvas, visible);
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

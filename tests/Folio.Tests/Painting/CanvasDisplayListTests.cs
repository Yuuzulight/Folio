using Folio.Dom;
using Folio.Painting;

namespace Folio.Tests.Painting;

public sealed class CanvasDisplayListTests
{
    [Fact]
    public void RectangleCommandsArePaintedIntoTheDisplayList()
    {
        using var document = Document.Parse("<html><body><canvas width='40' height='30'></canvas></body></html>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        canvas.CanvasSurface = new TestCanvasSurface([
            new CanvasSurfaceCommand("fillRect", 2, 3, 10, 8, "#f00")
        ]);

        var (displayList, _) = document.Paint(200, 100);
        var fill = Assert.Single(displayList.Items, item => item.Kind == DisplayItemKind.Fill && item.Color.R > 0.9f && item.Color.G < 0.1f);
        Assert.Equal(10, fill.Shape.Rect.Width);
        Assert.Equal(8, fill.Shape.Rect.Height);
    }

    [Fact]
    public void PathCommandsArePaintedIntoTheDisplayList()
    {
        using var document = Document.Parse("<html><body><canvas width='40' height='30'></canvas></body></html>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        canvas.CanvasSurface = new TestCanvasSurface([
            new CanvasSurfaceCommand("beginPath", 0, 0, 0, 0, ""),
            new CanvasSurfaceCommand("moveTo", 2, 3, 0, 0, ""),
            new CanvasSurfaceCommand("lineTo", 12, 3, 0, 0, ""),
            new CanvasSurfaceCommand("lineTo", 12, 11, 0, 0, ""),
            new CanvasSurfaceCommand("closePath", 0, 0, 0, 0, ""),
            new CanvasSurfaceCommand("fill", 0, 0, 0, 0, "#00f")
        ]);

        var (displayList, _) = document.Paint(200, 100);
        Assert.Contains(displayList.Items, item => item.Kind == DisplayItemKind.FillPath && item.Color.B > 0.9f);
    }

    [Fact]
    public void TransformCommandsWrapSubsequentCanvasPaint()
    {
        using var document = Document.Parse("<html><body><canvas width='40' height='30'></canvas></body></html>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        canvas.CanvasSurface = new TestCanvasSurface([
            new CanvasSurfaceCommand("translate", 5, 7, 0, 0, ""),
            new CanvasSurfaceCommand("fillRect", 1, 2, 3, 4, "#0f0")
        ]);

        var (displayList, _) = document.Paint(200, 100);
        var transforms = displayList.Items.Where(item => item.Kind == DisplayItemKind.PushTransform).ToList();
        Assert.Contains(transforms, item => item.Transform.Translation == new System.Numerics.Vector2(5, 7));
    }

    [Fact]
    public void LinearCanvasGradientBecomesDisplayListGradient()
    {
        using var document = Document.Parse("<html><body><canvas width='40' height='30'></canvas></body></html>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        canvas.CanvasSurface = new TestCanvasSurface([
            new CanvasSurfaceCommand("fillRect", 0, 0, 10, 10, "gradient:0,0,10,0|0:red|1:blue")
        ]);

        var (displayList, _) = document.Paint(200, 100);
        Assert.Contains(displayList.Items, item => item.Kind == DisplayItemKind.Fill && item.Gradient is { Stops.Count: 2 });
    }

    private sealed class TestCanvasSurface(IReadOnlyList<CanvasSurfaceCommand> commands) : ICanvasSurface
    {
        public IReadOnlyList<CanvasSurfaceCommand> Commands { get; } = commands;
    }
}

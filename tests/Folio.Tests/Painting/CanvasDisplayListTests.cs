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

    private sealed class TestCanvasSurface(IReadOnlyList<CanvasSurfaceCommand> commands) : ICanvasSurface
    {
        public IReadOnlyList<CanvasSurfaceCommand> Commands { get; } = commands;
    }
}

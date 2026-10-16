using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;

namespace Folio.Tests.Painting;

public sealed class CanvasDisplayListTests
{
    [Fact]
    public void ACanvasPaintsItsBitmapAsAnImageOfItsContentBox()
    {
        using var document = Document.Parse("<html><body style='margin:0'><canvas width='40' height='30'></canvas></body></html>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        canvas.CanvasSurface = new TestCanvasSurface(2, 2);

        var (displayList, _) = document.Paint(200, 100);
        var image = Assert.Single(displayList.Items, item => item.Kind == DisplayItemKind.Image);
        Assert.Equal(new RectF(0, 0, 40, 30), image.Shape.Rect);
    }

    [Fact]
    public void ACanvasWithoutAContextPaintsNothing()
    {
        using var document = Document.Parse("<html><body><canvas width='40' height='30'></canvas></body></html>");

        var (displayList, _) = document.Paint(200, 100);
        Assert.DoesNotContain(displayList.Items, item => item.Kind == DisplayItemKind.Image);
    }

    [Theory]
    [InlineData("<canvas></canvas>", 300, 150)]
    [InlineData("<canvas width='64'></canvas>", 64, 150)]
    [InlineData("<canvas height='20'></canvas>", 300, 20)]
    [InlineData("<canvas width='x' height='-3'></canvas>", 300, 150)]
    [InlineData("<canvas width='10' height='10' style='width:80px'></canvas>", 80, 80)]
    public void ACanvasIsSizedByItsAttributesLikeAReplacedElement(string html, float width, float height)
    {
        using var document = Document.Parse("<html><body style='margin:0'>" + html + "</body></html>");
        var canvas = document.Node.Descendants.OfType<ElementNode>().Single(element => element.LocalName == "canvas");
        document.Paint(800, 600);

        var bounds = document.GetElementBounds(canvas)!.Value;
        Assert.Equal((width, height), (bounds.Width, bounds.Height));
    }

    private sealed class TestCanvasSurface(int width, int height) : ICanvasSurface
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public IImageHandle? Snapshot() => new Pixels(Width, Height);
    }

    private sealed class Pixels(int width, int height) : IImageHandle
    {
        public int Width { get; } = width;

        public int Height { get; } = height;

        public ReadOnlyMemory<byte> Pixels_ => new byte[width * height * 4];

        ReadOnlyMemory<byte> IImageHandle.Pixels => Pixels_;
    }
}

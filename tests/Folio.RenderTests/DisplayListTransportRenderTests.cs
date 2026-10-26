using Folio.Painting;
using Folio.Skia;
using Folio.Typography;
using SkiaSharp;

namespace Folio.RenderTests;

public class DisplayListTransportRenderTests
{
    [Fact]
    public void ReplayingTransportedListMatchesInProcessPixelsAcrossStaticCorpus()
    {
        var root = Path.Combine(RepoPaths.Tests, "conformance");
        var pages = Directory.EnumerateFiles(root, "index.html", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f);
                return rel.StartsWith("S1", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S2", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S3", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S4", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S5", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S6", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        Assert.NotEmpty(pages);

        foreach (var file in pages)
        {
            var html = File.ReadAllText(file);
            using var document = Document.Parse(html, TestRenderer.Options);
            var original = document.Paint(800, 600).List;

            var sender = new DisplayListContext();
            var receiver = new DisplayListContext { FontSource = TestRenderer.Options.Fonts?.Source };

            var bytes = DisplayListCodec.Encode(original, sender);
            var decoded = DisplayListCodec.Decode(bytes, receiver);

            using var originalBitmap = new SKBitmap(new SKImageInfo(800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(originalBitmap))
            {
                canvas.Clear(SKColors.White);
                DisplayListPlayer.Replay(original, new SkiaCanvas(canvas, false));
            }

            using var decodedBitmap = new SKBitmap(new SKImageInfo(800, 600, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(decodedBitmap))
            {
                canvas.Clear(SKColors.White);
                DisplayListPlayer.Replay(decoded, new SkiaCanvas(canvas, false));
            }

            var origPixels = originalBitmap.Bytes;
            var decPixels = decodedBitmap.Bytes;
            Assert.True(origPixels.AsSpan().SequenceEqual(decPixels), $"Pixel mismatch for {Path.GetRelativePath(root, file)}");
        }
    }
}

using Folio.Skia;

namespace Folio.RenderTests;

/// <summary>A page drawn by Folio's own raster backend, to compare with the Skia backend's picture of it.</summary>
public static class SoftwareRender
{
    /// <summary>The page as premultiplied BGRA pixels: by the Skia backend, or by Folio's own software one (text is still shaped by HarfBuzz).</summary>
    internal static (int Width, int Height, byte[] Pixels, IReadOnlyList<Diagnostic> Diagnostics) Pixels(Document document, RenderRequest request, bool software)
    {
        if (!software)
        {
            using var result = HeadlessRenderer.Render(document, request);
            return (result.Width, result.Height, result.Pixels.ToArray(), result.Diagnostics);
        }

        var layoutHeight = request.ViewportHeight ?? Math.Max(1, request.ViewportWidth * 3 / 4);
        var (list, documentHeight) = document.Paint(request.ViewportWidth, layoutHeight, request.DeviceScale, new HarfBuzzShaper(), request.AnimationTime?.TotalSeconds);
        var height = request.ViewportHeight ?? Math.Max(1, (int)Math.Ceiling(documentHeight));
        var (pixelWidth, pixelHeight) = ((int)Math.Ceiling(request.ViewportWidth * request.DeviceScale), (int)Math.Ceiling(height * request.DeviceScale));
        var surface = new Folio.Painting.SoftwareRasterSurface(pixelWidth, pixelHeight);
        var canvas = surface.Canvas;
        canvas.FillRoundedRect(new Folio.Painting.RoundedRect(new Folio.Painting.RectF(0, 0, pixelWidth, pixelHeight), default),
            document.ColorScheme == ColorScheme.Dark ? new Folio.Painting.Paint(new Folio.Painting.Rgba(18 / 255f, 18 / 255f, 18 / 255f, 1)) : new Folio.Painting.Paint(new Folio.Painting.Rgba(1, 1, 1, 1)));
        canvas.Transform(System.Numerics.Matrix3x2.CreateScale(request.DeviceScale));
        Folio.Painting.DisplayListPlayer.Replay(list, canvas, new Folio.Painting.RectF(0, 0, pixelWidth / request.DeviceScale, pixelHeight / request.DeviceScale));
        // Premultiplied RGBA to premultiplied BGRA.
        var pixels = surface.PremultipliedPixels.ToArray();
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return (pixelWidth, pixelHeight, pixels, document.Diagnostics.ToList());
    }

    /// <summary>A test page in an 800x600 viewport, by the software backend.</summary>
    public static PixelBuffer RenderPage(string htmlPath)
    {
        using var document = Document.Parse(File.ReadAllText(htmlPath), TestRenderer.Options);
        var (width, height, pixels, _) = Pixels(document, new RenderRequest(800, 600), software: true);
        return new PixelBuffer(width, height, pixels);
    }
}

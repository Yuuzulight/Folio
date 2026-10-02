using Folio.Painting;
using SkiaSharp;

namespace Folio.Skia;

/// <summary>How text edges are antialiased when rendering.</summary>
public enum TextAntialiasing
{
    /// <summary>Greyscale: right on any background, the default without a window.</summary>
    Greyscale,

    /// <summary>Subpixel (LCD): sharper on opaque backgrounds on a known screen.</summary>
    Subpixel,
}

/// <summary>What to render: the viewport, the scale, how text is antialiased, and the moment of the animations.</summary>
/// <param name="ViewportWidth">The viewport width in CSS pixels.</param>
/// <param name="ViewportHeight">The viewport height in CSS pixels; null renders the whole document, laid out in a viewport three quarters as tall as it is wide.</param>
/// <param name="DeviceScale">Device pixels per CSS pixel.</param>
/// <param name="ResourceWait">How long to wait for allowed loads; nothing loads asynchronously yet, so it has no effect.</param>
/// <param name="AnimationTime">
/// The time since the document loaded at which CSS animations are drawn, for deterministic frames. Null draws the
/// settled document: every animation as if it had no duration and no delay, so one that fills forwards shows its end.
/// </param>
public sealed record RenderRequest(int ViewportWidth, int? ViewportHeight = null, float DeviceScale = 1f, TimeSpan? ResourceWait = null,
                                   TextAntialiasing Text = TextAntialiasing.Greyscale, TimeSpan? AnimationTime = null);

/// <summary>A rendered image: premultiplied BGRA8 pixels, row-major without padding, and the document's diagnostics.</summary>
public sealed class RenderResult : IDisposable
{
    private readonly SKBitmap _bitmap;

    internal RenderResult(SKBitmap bitmap, IReadOnlyList<Diagnostic> diagnostics)
    {
        _bitmap = bitmap;
        Diagnostics = diagnostics;
    }

    public int Width => _bitmap.Width;

    public int Height => _bitmap.Height;

    public ReadOnlySpan<byte> Pixels => _bitmap.GetPixelSpan();

    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    public void SavePng(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        using var data = _bitmap.Encode(SKEncodedImageFormat.Png, 100);
        data.SaveTo(destination);
    }

    public void Dispose() => _bitmap.Dispose();
}

/// <summary>Renders documents to images without a window (docs/architecture.md, headless rendering).</summary>
public static class HeadlessRenderer
{
    /// <summary>The largest image side in device pixels; taller documents are cut off, with a diagnostic.</summary>
    public const int MaxSide = 16384;

    public static RenderResult Render(Document document, RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.ViewportWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.ViewportWidth, MaxSide);
        if (request.ViewportHeight is { } h)
            ArgumentOutOfRangeException.ThrowIfLessThan(h, 1);
        if (!(request.DeviceScale is > 0 and <= 8))
            throw new ArgumentOutOfRangeException(nameof(request), "DeviceScale must be above 0 and at most 8.");

        var layoutHeight = request.ViewportHeight ?? Math.Max(1, request.ViewportWidth * 3 / 4);
        var (list, documentHeight) = document.Paint(request.ViewportWidth, layoutHeight, request.DeviceScale, new HarfBuzzShaper(),
            request.AnimationTime?.TotalSeconds);
        var diagnostics = document.Diagnostics.ToList();

        var height = request.ViewportHeight ?? Math.Max(1, (int)Math.Ceiling(documentHeight));
        var (pixelWidth, pixelHeight) = ((int)Math.Ceiling(request.ViewportWidth * request.DeviceScale), (int)Math.Ceiling(height * request.DeviceScale));
        if (pixelWidth > MaxSide || pixelHeight > MaxSide)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCode.LimitExceeded, Severity.Warning,
                $"The image would be {pixelWidth}x{pixelHeight} device pixels; it was cut to {MaxSide} on each side.", null, null));
            (pixelWidth, pixelHeight) = (Math.Min(pixelWidth, MaxSide), Math.Min(pixelHeight, MaxSide));
        }

        var bitmap = new SKBitmap(new SKImageInfo(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            // The canvas behind the page: white, or near-black when the host asks for the dark scheme.
            canvas.Clear(document.Options.ColorScheme == ColorScheme.Dark ? new SKColor(18, 18, 18) : SKColors.White);
            canvas.Scale(request.DeviceScale);
            DisplayListPlayer.Replay(list, new SkiaCanvas(canvas, request.Text == TextAntialiasing.Subpixel),
                new RectF(0, 0, pixelWidth / request.DeviceScale, pixelHeight / request.DeviceScale));
            canvas.Flush();
        }
        return new RenderResult(bitmap, diagnostics);
    }

    /// <summary>Parses and renders a whole page at a width, as PNG bytes.</summary>
    public static byte[] RenderPng(string html, int width, FolioOptions? options = null)
    {
        using var document = Document.Parse(html, options);
        using var result = Render(document, new RenderRequest(width));
        using var png = new MemoryStream();
        result.SavePng(png);
        return png.ToArray();
    }
}

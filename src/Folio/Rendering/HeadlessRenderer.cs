using System.Numerics;
using Folio.Imaging;
using Folio.Painting;
using Folio.Typography;

namespace Folio.Rendering;

/// <summary>A static document viewport, device scale, and optional CSS-animation time.</summary>
public sealed record RenderRequest(int ViewportWidth, int? ViewportHeight = null, float DeviceScale = 1f,
    TimeSpan? AnimationTime = null);

/// <summary>An owned image of premultiplied RGBA8 pixels. Dispose it to release its pixel storage.</summary>
public sealed class RenderResult : IDisposable
{
    private SoftwareRasterSurface? _surface;

    internal RenderResult(SoftwareRasterSurface surface, IReadOnlyList<Diagnostic> diagnostics)
    {
        _surface = surface;
        Width = surface.Width;
        Height = surface.Height;
        Diagnostics = diagnostics;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public ReadOnlySpan<byte> Pixels => Surface.PremultipliedPixels;

    private SoftwareRasterSurface Surface => _surface ?? throw new ObjectDisposedException(nameof(RenderResult));

    public void SavePng(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Surface.Encode("image/png", 1)!);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _surface, null)?.Dispose();
    }
}

/// <summary>Renders static HTML/CSS with Folio's own shaper, rasterizer and PNG encoder, without native dependencies.</summary>
/// <remarks>Does not execute JavaScript. Use Folio.Scripting's isolated content process for scripted documents.</remarks>
public static class HeadlessRenderer
{
    public const int MaxSide = 16384;
    public const long MaxPixels = ImageLimits.MaxPixels;

    public static RenderResult Render(Document document, RenderRequest request)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.ViewportWidth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.ViewportWidth, MaxSide);
        if (request.ViewportHeight is { } requestedHeight)
            ArgumentOutOfRangeException.ThrowIfLessThan(requestedHeight, 1);
        if (!(request.DeviceScale is > 0 and <= 8))
            throw new ArgumentOutOfRangeException(nameof(request), "DeviceScale must be above zero and at most eight.");

        var layoutHeight = request.ViewportHeight ?? Math.Max(1, request.ViewportWidth * 3 / 4);
        var (list, documentHeight) = document.Paint(request.ViewportWidth, layoutHeight, request.DeviceScale,
            ComplexShaper.Instance, request.AnimationTime?.TotalSeconds);
        var requestedPixelsWide = Math.Ceiling((double)request.ViewportWidth * request.DeviceScale);
        var requestedPixelsHigh = Math.Ceiling((request.ViewportHeight ?? Math.Max(1d, Math.Ceiling(documentHeight))) * request.DeviceScale);
        if (double.IsNaN(requestedPixelsHigh)) throw new InvalidOperationException("Document layout produced an invalid height.");
        var pixelWidth = (int)Math.Min(MaxSide, requestedPixelsWide);
        var pixelHeight = (int)Math.Min(Math.Min(MaxSide, MaxPixels / pixelWidth), requestedPixelsHigh);
        var diagnostics = document.Diagnostics.ToList();
        if (pixelWidth < requestedPixelsWide || pixelHeight < requestedPixelsHigh)
            diagnostics.Add(new Diagnostic(DiagnosticCode.LimitExceeded, Severity.Warning,
                $"The requested image was cut to {pixelWidth}x{pixelHeight} to respect the image dimension/pixel limits.", null, null));

        var surface = new SoftwareRasterSurface(pixelWidth, pixelHeight);
        try
        {
            var background = document.Options.ColorScheme == ColorScheme.Dark
                ? new Rgba(18 / 255f, 18 / 255f, 18 / 255f, 1) : new Rgba(1, 1, 1, 1);
            surface.Canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, pixelWidth, pixelHeight), default), new Paint(background));
            surface.Canvas.Transform(Matrix3x2.CreateScale(request.DeviceScale));
            DisplayListPlayer.Replay(list, surface.Canvas,
                new RectF(0, 0, pixelWidth / request.DeviceScale, pixelHeight / request.DeviceScale));
            return new RenderResult(surface, diagnostics);
        }
        catch { surface.Dispose(); throw; }
    }

    /// <summary>Renders a static page with shipped fonts when unspecified; retains the caller's explicit font source and resource policies.</summary>
    public static byte[] RenderPng(string html, int width, FolioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        using var document = Document.Parse(html, (options ?? new FolioOptions()).WithDefaultFonts());
        using var image = Render(document, new RenderRequest(width));
        using var png = new MemoryStream();
        image.SavePng(png);
        return png.ToArray();
    }
}

using System.Globalization;
using System.Numerics;
using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Dom;

/// <summary>
/// What a canvas element owns on the scripting side: its bitmap, and the context drawing on it. The display list paints
/// the bitmap through <see cref="ICanvasSurface"/>.
/// </summary>
internal sealed class CanvasElementState : ICanvasSurface, IFeatureFallbackSource
{
    private readonly IRasterBackend? _backend;
    private readonly Hosting.CanvasStyleEngine? _styles;
    private IRasterSurface? _surface;
    private IImageHandle? _snapshot;
    private long _snapshotVersion = -1;
    private long _touches;
    private bool _attemptedWebGl;
    private bool _nativeFrame;
    private FeatureFallback? _fallback;

    public CanvasElementState(ElementNode element, IRasterBackend? backend, Hosting.CanvasStyleEngine? styles)
    {
        Element = element;
        _backend = backend;
        _styles = styles;
        element.CanvasSurface = this;
    }

    public ElementNode Element { get; }

    public Canvas2DContext? Context2D { get; private set; }

    public WebGlContextState? WebGlContext { get; set; }

    public FeatureFallback? Fallback => _attemptedWebGl && !Element.OwnerDocument.WebGlAllowed
        ? new(FallbackFeature.WebGl, FallbackReason.HostDenied) : _fallback;

    internal void AttemptWebGl() => _attemptedWebGl = true;
    internal void MarkWebGlDenied() { _attemptedWebGl = true; _fallback = new(FallbackFeature.WebGl, FallbackReason.HostDenied); }
    internal void MarkGraphicsUnavailable()
    {
        if (_attemptedWebGl) _fallback = new(FallbackFeature.WebGl, FallbackReason.GraphicsUnavailable);
    }
    internal void MarkGpuFailure(GpuFailure failure)
    {
        _attemptedWebGl = true;
        _fallback = new(FallbackFeature.WebGl, failure.Kind == GpuFailureKind.BudgetExceeded ? FallbackReason.ResourceLimit : FallbackReason.GraphicsUnavailable);
    }
    internal void MarkContextLost()
    {
        if (_attemptedWebGl && _fallback is not { Reason: FallbackReason.HostDenied or FallbackReason.ResourceLimit })
            _fallback = new(FallbackFeature.WebGl, FallbackReason.ContextLost);
    }
    internal void MarkHostDenied() { if (_attemptedWebGl) _fallback = new(FallbackFeature.WebGl, FallbackReason.HostDenied); }
    internal void ClearFallback()
    {
        if (_attemptedWebGl && _fallback is not { Reason: FallbackReason.GraphicsUnavailable } && (SizeAllowed || _fallback is not { Reason: FallbackReason.ResourceLimit }))
            _fallback = null;
    }
    internal void ProbeGraphics() { _ = Surface(); }
    internal Action? NativeSurfaceInvalidated { get; set; }
    /// <summary>Publishes a successful native readback and clears a prior transient GPU fallback.</summary>
    internal void MarkNativeFrame()
    {
        _nativeFrame = true;
        _attemptedWebGl = true;
        _fallback = null;
    }
    internal void ClearNativeFrame() { _nativeFrame = false; NativeSurfaceInvalidated?.Invoke(); NativeSurfaceInvalidated = null; }
    public int Width => Side("width", 300);

    public int Height => Side("height", 150);

    private int Side(string name, int fallback) =>
        int.TryParse(Element.GetAttribute(name)?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var side) ? side : fallback;

    /// <summary>Whether a bitmap of the element's size fits the image limits (16,384 pixels a side, 64 megapixels).</summary>
    public bool SizeAllowed => ImageLimits.Allows(Width, Height, ImageLimits.MaxPixels);

    /// <summary>The element's bitmap, made on first use; null when there is no backend or the size is not allowed or zero.</summary>
    public IRasterSurface? Surface()
    {
        if (_surface is not null) return _surface;
        if (!SizeAllowed)
        {
            if (_attemptedWebGl && Width > 0 && Height > 0) _fallback = new(FallbackFeature.WebGl, FallbackReason.ResourceLimit);
            return null;
        }
        if (_backend is null)
        {
            MarkGraphicsUnavailable();
            return null;
        }
        try { _surface = _backend.CreateSurface(Width, Height); }
        catch { _surface = null; }
        if (_surface is null) MarkGraphicsUnavailable();
        else if (_fallback is { Reason: FallbackReason.GraphicsUnavailable or FallbackReason.ResourceLimit }) _fallback = null;
        return _surface;
    }

    /// <summary>Marks the bitmap changed by something other than the 2D context.</summary>
    public void Touch() => _touches++;

    /// <summary>The 2D context, made on first use; null when it cannot be (no backend, a size that is not allowed, a context of another kind).</summary>
    public Canvas2DContext? GetContext2D(bool alpha, Func<Rgba> currentColor)
    {
        if (Context2D is not null)
            return Sync();
        if (WebGlContext is not null || Surface() is not { } surface)
            return null;
        _attemptedWebGl = false;
        _fallback = null;
        return Context2D = new Canvas2DContext(surface, alpha, currentColor, _styles);
    }

    /// <summary>The context, on a bitmap of the element's current size (changing width or height starts over).</summary>
    public Canvas2DContext? Sync()
    {
        if (Context2D is null)
            return null;
        if (_surface is null || _surface.Width != Width || _surface.Height != Height)
            Restart();
        return Context2D;
    }

    /// <summary>Setting width or height, even to the same value, empties the bitmap and resets the context.</summary>
    public void Restart()
    {
        ClearNativeFrame();
        _surface?.Dispose();
        _surface = null;
        _snapshot = null;
        _snapshotVersion = -1;
        Context2D?.Reattach(Surface() ?? NullRasterSurface.Instance);
        if (WebGlContext is { Lost: false } && Element.OwnerDocument.WebGlAllowed)
            ProbeGraphics();
    }

    public IImageHandle? Snapshot()
    {
        if (Context2D is null && WebGlContext is null && !_nativeFrame)
            return null;
        if (Context2D is not null) Sync();
        if (_surface is null)
            return null;
        var version = (Context2D?.Version ?? 0) + _touches;
        if (_snapshotVersion != version || _snapshot is null)
        {
            _snapshot = _surface.Snapshot();
            _snapshotVersion = version;
        }
        return _snapshot;
    }
}

/// <summary>The bitmap of a canvas that has none (size zero or beyond the limits): drawing on it does nothing and it reads as empty.</summary>
internal sealed class NullRasterSurface : IRasterSurface, ICanvas
{
    public static readonly NullRasterSurface Instance = new();

    public int Width => 0;

    public int Height => 0;

    public ICanvas Canvas => this;

    public byte[] ReadPixels(int x, int y, int width, int height) => new byte[Math.Max(0, width) * Math.Max(0, height) * 4];

    public void WritePixels(int x, int y, int width, int height, ReadOnlySpan<byte> rgba)
    {
    }

    public IImageHandle Snapshot() => throw new InvalidOperationException("A canvas without a bitmap has no snapshot.");

    public byte[]? Encode(string mimeType, double quality) => null;

    public void Dispose()
    {
    }

    public void Save()
    {
    }

    public void Restore()
    {
    }

    public void Transform(in Matrix3x2 matrix)
    {
    }

    public void ClipRoundedRect(in RoundedRect rect)
    {
    }

    public void ClipPath(PathData path, FillRule rule)
    {
    }

    public void FillRoundedRect(in RoundedRect rect, in Paint paint)
    {
    }

    public void FillPath(PathData path, FillRule rule, in Paint paint)
    {
    }

    public void StrokePath(PathData path, in Stroke stroke, in Paint paint)
    {
    }

    public void DrawGlyphs(Typography.IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint)
    {
    }

    public void DrawImage(IImageHandle image, in RectF destination, ImageSampling sampling)
    {
    }

    public void PushLayer(in LayerOptions options)
    {
    }

    public void PopLayer()
    {
    }
}

namespace Folio.Scripting.Gpu;

internal enum GpuRendererKind
{
    Hardware,
    Warp,
    Unavailable,
}

internal enum GpuFailureKind
{
    Unavailable,
    BudgetExceeded,
    NativeError,
}

internal sealed record GpuFailure(GpuFailureKind Kind, string Message, uint? ApiError = null);

internal sealed record GpuDeviceResult(IGpuDevice? Device, GpuFailure? Failure)
{
    public bool Succeeded => Device is not null;
}

internal interface IGpuDevice : IDisposable
{
    GpuRendererKind RendererKind { get; }
    string RendererIdentity { get; }
    long ReservedBytes { get; }
    long PeakReservedBytes { get; }
    bool IsDisposed { get; }
    GpuSurfaceResult TryCreateSurface(int width, int height);
}

internal sealed record GpuSurfaceResult(IGpuSurface? Surface, GpuFailure? Failure)
{
    public bool Succeeded => Surface is not null;
}

internal interface IGpuSurface : IDisposable
{
    int Width { get; }
    int Height { get; }
    long ReservedBytes { get; }
    bool ClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure);
    bool ClearColorRect(float red, float green, float blue, float alpha, int x, int y, int width, int height, out GpuFailure? failure);
    bool TryReadPixels(Span<byte> destination, out GpuFailure? failure);
    IGpuResourceSet Resources { get; }
}

internal interface IGpuResourceSet
{
    bool TryAllocateTexture(int width, int height, out IGpuResource? resource, out GpuFailure? failure);
    bool TryAllocateBuffer(int bytes, out IGpuResource? resource, out GpuFailure? failure);
    bool TryAllocateRenderbuffer(int width, int height, int bytesPerPixel, out IGpuResource? resource, out GpuFailure? failure);
}

internal interface IGpuResource : IDisposable
{
    long ReservedBytes { get; }
}

internal enum GpuRendererPreference
{
    HardwareThenWarp,
    HardwareOnly,
    WarpOnly,
}

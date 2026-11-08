using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

/// <summary>
/// Folio-owned native GPU backend for Windows using Direct3D 11 and DXGI.
/// Replaces third-party ANGLE runtime with zero external DLL dependencies.
/// </summary>
internal static class FolioGpuBackend
{
    internal static GpuDeviceResult TryCreate(long maxBytes, GpuRendererPreference preference = GpuRendererPreference.HardwareThenWarp)
    {
        if (maxBytes < 0)
            return new(null, new(GpuFailureKind.BudgetExceeded, "The GPU budget is negative."));
        if (!OperatingSystem.IsWindows())
            return new(null, new(GpuFailureKind.Unavailable, "Folio's Direct3D 11 GPU backend is only available on Windows."));

        var attempts = preference switch
        {
            GpuRendererPreference.HardwareOnly => new[] { GpuRendererKind.Hardware },
            GpuRendererPreference.WarpOnly => new[] { GpuRendererKind.Warp },
            _ => new[] { GpuRendererKind.Hardware, GpuRendererKind.Warp },
        };

        string? lastError = null;
        foreach (var kind in attempts)
        {
            try
            {
                var device = FolioGpuDevice.Create(kind, maxBytes);
                if (device is not null)
                    return new(device, null);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException or ExternalException)
            {
                lastError = error.Message;
            }
        }
        return new(null, new(GpuFailureKind.Unavailable, lastError ?? "Direct3D 11 device creation failed."));
    }
}

internal sealed class FolioGpuDevice : IGpuDevice, INativeWebGlFactory
{
    private readonly object _gate = new();
    private readonly IntPtr _device;
    private readonly IntPtr _context;
    private readonly long _maxBytes;
    private long _reserved;
    private long _peakReserved;
    private bool _disposed;
    private readonly HashSet<FolioGpuSurface> _surfaces = [];
    private readonly HashSet<FolioNativeWebGlContext> _contexts = [];

    // Cached delegates
    private readonly FolioD3D11Interop.CreateTexture2DDelegate _createTexture2D;
    private readonly FolioD3D11Interop.CreateRenderTargetViewDelegate _createRenderTargetView;
    private readonly FolioD3D11Interop.CreateBufferDelegate _createBuffer;
    private readonly FolioD3D11Interop.ClearRenderTargetViewDelegate _clearRenderTargetView;
    private readonly FolioD3D11Interop.CopyResourceDelegate _copyResource;
    private readonly FolioD3D11Interop.UpdateSubresourceDelegate _updateSubresource;
    private readonly FolioD3D11Interop.MapDelegate _map;
    private readonly FolioD3D11Interop.UnmapDelegate _unmap;
    private readonly FolioD3D11Interop.FlushDelegate _flush;
    private readonly FolioD3D11Interop.GetDeviceRemovedReasonDelegate _getDeviceRemovedReason;
    private volatile bool _removed;

    public GpuRendererKind RendererKind { get; }
    public string RendererIdentity { get; }
    public long ReservedBytes { get { lock (_gate) return _reserved; } }
    public long PeakReservedBytes { get { lock (_gate) return _peakReserved; } }
    public bool IsDisposed { get { lock (_gate) return _disposed; } }
    internal object Gate => _gate;
    internal IntPtr DevicePtr => _device;
    internal long MaxBytes => _maxBytes;
    internal IntPtr ContextPtr => _context;

    private FolioGpuDevice(IntPtr device, IntPtr context, GpuRendererKind kind, long maxBytes, int featureLevel)
    {
        _device = device;
        _context = context;
        RendererKind = kind;
        _maxBytes = maxBytes;
        RendererIdentity = $"{kind}:Direct3D11-FL0x{featureLevel:X4}";

        _createTexture2D = FolioD3D11Interop.Method<FolioD3D11Interop.CreateTexture2DDelegate>(
            device, FolioD3D11Interop.DeviceSlotCreateTexture2D);
        _createRenderTargetView = FolioD3D11Interop.Method<FolioD3D11Interop.CreateRenderTargetViewDelegate>(
            device, FolioD3D11Interop.DeviceSlotCreateRenderTargetView);
        _createBuffer = FolioD3D11Interop.Method<FolioD3D11Interop.CreateBufferDelegate>(
            device, FolioD3D11Interop.DeviceSlotCreateBuffer);
        _clearRenderTargetView = FolioD3D11Interop.Method<FolioD3D11Interop.ClearRenderTargetViewDelegate>(
            context, FolioD3D11Interop.ContextSlotClearRenderTargetView);
        _copyResource = FolioD3D11Interop.Method<FolioD3D11Interop.CopyResourceDelegate>(
            context, FolioD3D11Interop.ContextSlotCopyResource);
        _updateSubresource = FolioD3D11Interop.Method<FolioD3D11Interop.UpdateSubresourceDelegate>(
            context, FolioD3D11Interop.ContextSlotUpdateSubresource);
        _map = FolioD3D11Interop.Method<FolioD3D11Interop.MapDelegate>(
            context, FolioD3D11Interop.ContextSlotMap);
        _unmap = FolioD3D11Interop.Method<FolioD3D11Interop.UnmapDelegate>(
            context, FolioD3D11Interop.ContextSlotUnmap);
        _flush = FolioD3D11Interop.Method<FolioD3D11Interop.FlushDelegate>(
            context, FolioD3D11Interop.ContextSlotFlush);
        _getDeviceRemovedReason = FolioD3D11Interop.Method<FolioD3D11Interop.GetDeviceRemovedReasonDelegate>(
            device, FolioD3D11Interop.DeviceSlotGetDeviceRemovedReason);
    }

    /// <summary>
    /// True once the OS removed or reset the device (for example after a GPU timeout); a removed device
    /// never works again, so every context on it is lost. Sticky, and cheap enough to ask per call.
    /// </summary>
    /// <summary>Test seam: behave as if the OS removed the device.</summary>
    internal void MarkRemoved() => _removed = true;

    internal bool IsRemoved
    {
        get
        {
            if (_removed) return true;
            lock (_gate) // never ask a released device
            {
                if (!_disposed && _getDeviceRemovedReason(_device) != 0) _removed = true;
                return _removed;
            }
        }
    }

    public static FolioGpuDevice Create(GpuRendererKind kind, long maxBytes)
    {
        var driverType = kind == GpuRendererKind.Warp
            ? FolioD3D11Interop.DriverTypeWarp
            : FolioD3D11Interop.DriverTypeHardware;

        var hr = FolioD3D11Interop.D3D11CreateDevice(
            IntPtr.Zero,
            driverType,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            FolioD3D11Interop.D3D11SdkVersion,
            out var device,
            out var featureLevel,
            out var context);

        if (hr < 0 || device == IntPtr.Zero)
            throw new InvalidOperationException($"Direct3D 11 device creation failed with HRESULT 0x{hr:X8}");

        return new FolioGpuDevice(device, context, kind, maxBytes, featureLevel);
    }

    public GpuSurfaceResult TryCreateSurface(int width, int height)
    {
        lock (_gate)
        {
            if (_disposed)
                return new(null, new(GpuFailureKind.Unavailable, "The GPU device is disposed."));
            if (width <= 0 || height <= 0)
                return new(null, new(GpuFailureKind.NativeError, "The GPU surface dimensions are invalid."));

            long bytes;
            try { bytes = checked((long)width * height * 4); }
            catch (OverflowException) { return new(null, new(GpuFailureKind.BudgetExceeded, "The GPU surface size overflowed.")); }

            if (!TryReserveLocked(bytes, out var reservation))
                return new(null, new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."));

            try
            {
                var surface = new FolioGpuSurface(this, width, height, reservation);
                _surfaces.Add(surface);
                return new(surface, null);
            }
            catch (Exception error)
            {
                reservation.Dispose();
                return new(null, new(GpuFailureKind.NativeError, error.Message));
            }
        }
    }

    public bool TryCreateNativeWebGlContext(WebGlContextOptions options, out FolioNativeWebGlContext? context, out GpuFailure? failure)
    {
        context = null;
        failure = null;
        lock (_gate)
        {
            if (_disposed)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed.");
                return false;
            }
            if (options is null)
            {
                failure = new(GpuFailureKind.NativeError, "The WebGL context options are required.");
                return false;
            }
            if (options.Width < 0 || options.Height < 0 || options.Width > 4096 || options.Height > 4096)
            {
                failure = new(GpuFailureKind.NativeError, "The WebGL surface dimensions are invalid.");
                return false;
            }
            if (options.MaxBytes < 0 || options.MaxShaderSourceBytes <= 0)
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The WebGL budget is invalid.");
                return false;
            }

            long surfaceBytes;
            try { surfaceBytes = checked((long)Math.Max(1, options.Width) * Math.Max(1, options.Height) * 4); }
            catch (OverflowException)
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The WebGL surface size overflowed.");
                return false;
            }

            if (options.MaxBytes > 0 && surfaceBytes > options.MaxBytes)
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The WebGL context budget was exceeded.");
                return false;
            }
            if (!TryReserveLocked(surfaceBytes, out var reservation))
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded.");
                return false;
            }

            try
            {
                context = new FolioNativeWebGlContext(this, options, reservation, surfaceBytes);
                _contexts.Add(context);
                return true;
            }
            catch (Exception error)
            {
                reservation.Dispose();
                failure = new(GpuFailureKind.NativeError, error.Message);
                return false;
            }
        }
    }

    public bool TryCreateNativeWebGlContext(int width, int height, out FolioNativeWebGlContext? context, out GpuFailure? failure)
    {
        var attributes = new WebGlContextAttributes(
            WebGlContextVersion.WebGl2,
            Alpha: true,
            Depth: true,
            Stencil: false,
            Antialias: false,
            PremultipliedAlpha: true,
            PreserveDrawingBuffer: false);
        return TryCreateNativeWebGlContext(new(WebGlContextVersion.WebGl2, width, height, attributes, _maxBytes), out context, out failure);
    }

    bool INativeWebGlFactory.TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure)
    {
        var ok = TryCreateNativeWebGlContext(options, out var typed, out failure);
        context = typed;
        return ok;
    }

    internal bool TryCreateNativeSurface(int width, int height, out IntPtr texture, out IntPtr rtv, out IntPtr staging)
    {
        texture = rtv = staging = IntPtr.Zero;

        var desc = new FolioD3D11Interop.D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FolioD3D11Interop.DxgiFormatR8G8B8A8Unorm,
            SampleDesc = new FolioD3D11Interop.DxgiSampleDesc { Count = 1, Quality = 0 },
            Usage = FolioD3D11Interop.D3D11UsageDefault,
            BindFlags = FolioD3D11Interop.D3D11BindRenderTarget | FolioD3D11Interop.D3D11BindShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0,
        };

        var hr = _createTexture2D(_device, ref desc, IntPtr.Zero, out texture);
        if (hr < 0 || texture == IntPtr.Zero)
            throw new InvalidOperationException($"Direct3D 11 texture creation failed with HRESULT 0x{hr:X8}");

        try
        {
            hr = _createRenderTargetView(_device, texture, IntPtr.Zero, out rtv);
            if (hr < 0 || rtv == IntPtr.Zero)
                throw new InvalidOperationException($"Direct3D 11 render target view creation failed with HRESULT 0x{hr:X8}");

            var stagingDesc = desc;
            stagingDesc.Usage = FolioD3D11Interop.D3D11UsageStaging;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = FolioD3D11Interop.D3D11CpuAccessRead;

            hr = _createTexture2D(_device, ref stagingDesc, IntPtr.Zero, out staging);
            if (hr < 0 || staging == IntPtr.Zero)
                throw new InvalidOperationException($"Direct3D 11 staging texture creation failed with HRESULT 0x{hr:X8}");

            return true;
        }
        catch
        {
            if (staging != IntPtr.Zero) Marshal.Release(staging);
            if (rtv != IntPtr.Zero) Marshal.Release(rtv);
            if (texture != IntPtr.Zero) Marshal.Release(texture);
            texture = rtv = staging = IntPtr.Zero;
            throw;
        }
    }

    internal bool Clear(IntPtr rtv, float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (_disposed)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed.");
                return false;
            }

            var color = new float[] { red, green, blue, alpha };
            _clearRenderTargetView(_context, rtv, ref color[0]);
            return true;
        }
    }

    internal bool ClearRect(IntPtr texture, IntPtr rtv, int surfWidth, int surfHeight, float red, float green, float blue, float alpha, int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (_disposed)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed.");
                return false;
            }

            if (x == 0 && y == 0 && width == surfWidth && height == surfHeight)
                return Clear(rtv, red, green, blue, alpha, out failure);

            var box = new FolioD3D11Interop.D3D11Box
            {
                Left = (uint)x,
                Top = (uint)y,
                Front = 0,
                Right = (uint)(x + width),
                Bottom = (uint)(y + height),
                Back = 1,
            };

            var rByte = (byte)Math.Clamp((int)Math.Round(red * 255f), 0, 255);
            var gByte = (byte)Math.Clamp((int)Math.Round(green * 255f), 0, 255);
            var bByte = (byte)Math.Clamp((int)Math.Round(blue * 255f), 0, 255);
            var aByte = (byte)Math.Clamp((int)Math.Round(alpha * 255f), 0, 255);

            var pixelBytes = new byte[width * height * 4];
            for (var i = 0; i < pixelBytes.Length; i += 4)
            {
                pixelBytes[i] = rByte;
                pixelBytes[i + 1] = gByte;
                pixelBytes[i + 2] = bByte;
                pixelBytes[i + 3] = aByte;
            }

            var handle = GCHandle.Alloc(pixelBytes, GCHandleType.Pinned);
            try
            {
                _updateSubresource(_context, texture, 0, ref box, handle.AddrOfPinnedObject(), (uint)(width * 4), 0);
            }
            finally
            {
                handle.Free();
            }

            return true;
        }
    }

    internal bool Read(IntPtr texture, IntPtr staging, int width, int height, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (_disposed)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed.");
                return false;
            }

            var expectedBytes = width * height * 4;
            if (destination.Length < expectedBytes)
            {
                failure = new(GpuFailureKind.NativeError, "The destination span is smaller than the surface dimensions.");
                return false;
            }

            _copyResource(_context, staging, texture);

            var hr = _map(_context, staging, 0, FolioD3D11Interop.D3D11MapRead, 0, out var mapped);
            if (hr < 0 || mapped.Data == IntPtr.Zero)
            {
                failure = new(GpuFailureKind.NativeError, $"Direct3D 11 staging map failed with HRESULT 0x{hr:X8}");
                return false;
            }

            try
            {
                var rowBytes = width * 4;
                var rowBuffer = new byte[rowBytes];
                for (var row = 0; row < height; row++)
                {
                    var srcPtr = IntPtr.Add(mapped.Data, checked((int)(row * mapped.RowPitch)));
                    var dstRow = destination.Slice(row * rowBytes, rowBytes);
                    Marshal.Copy(srcPtr, rowBuffer, 0, rowBytes);
                    rowBuffer.AsSpan().CopyTo(dstRow);
                }
                return true;
            }
            finally
            {
                _unmap(_context, staging, 0);
            }
        }
    }

    internal void ForgetSurface(FolioGpuSurface surface)
    {
        lock (_gate) _surfaces.Remove(surface);
    }

    internal void ForgetWebGlContext(FolioNativeWebGlContext context)
    {
        lock (_gate) _contexts.Remove(context);
    }

    internal bool TryReserve(long bytes, out IDisposable reservation)
    {
        lock (_gate) return TryReserveLocked(bytes, out reservation);
    }

    private bool TryReserveLocked(long bytes, out IDisposable reservation)
    {
        reservation = NullReservation.Instance;
        if (_disposed) return false;
        if (bytes < 0) return false;
        if (bytes == 0) return true;
        if (_maxBytes >= 0 && _reserved + bytes > _maxBytes) return false;
        _reserved += bytes;
        if (_reserved > _peakReserved) _peakReserved = _reserved;
        reservation = new Reservation(this, bytes);
        return true;
    }

    internal void Release(long bytes)
    {
        lock (_gate) _reserved = Math.Max(0, _reserved - bytes);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var context in _contexts.ToArray())
            {
                try { context.Dispose(); }
                catch { }
            }
            _contexts.Clear();
            foreach (var surface in _surfaces.ToArray())
            {
                try { surface.Dispose(); }
                catch { }
            }
            _surfaces.Clear();
            _disposed = true;

            if (_context != IntPtr.Zero)
            {
                try { _flush(_context); } catch { }
                Marshal.Release(_context);
            }
            if (_device != IntPtr.Zero)
            {
                Marshal.Release(_device);
            }
            _reserved = 0;
        }
    }

    private sealed class Reservation(FolioGpuDevice owner, long bytes) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Release(bytes);
        }
    }

    private sealed class NullReservation : IDisposable
    {
        public static readonly NullReservation Instance = new();
        public void Dispose() { }
    }
}

internal sealed class FolioGpuSurface : IGpuSurface, IGpuResourceSet
{
    private readonly FolioGpuDevice _device;
    private readonly IDisposable _reservation;
    private IntPtr _texture;
    private IntPtr _rtv;
    private IntPtr _staging;
    private int _disposed;

    public int Width { get; }
    public int Height { get; }
    public long ReservedBytes => (long)Width * Height * 4;
    public IGpuResourceSet Resources => this;

    public FolioGpuSurface(FolioGpuDevice device, int width, int height, IDisposable reservation)
    {
        _device = device;
        Width = width;
        Height = height;
        _reservation = reservation;
        try
        {
            device.TryCreateNativeSurface(width, height, out _texture, out _rtv, out _staging);
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    public bool ClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed.");
                return false;
            }
            return _device.Clear(_rtv, red, green, blue, alpha, out failure);
        }
    }

    public bool ClearColorRect(float red, float green, float blue, float alpha, int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed.");
                return false;
            }
            if (x < 0 || y < 0 || width <= 0 || height <= 0 || x > Width - width || y > Height - height)
            {
                failure = new(GpuFailureKind.NativeError, "The clear rectangle is outside the GPU surface.");
                return false;
            }
            return _device.ClearRect(_texture, _rtv, Width, Height, red, green, blue, alpha, x, y, width, height, out failure);
        }
    }

    public bool TryReadPixels(Span<byte> destination, out GpuFailure? failure)
    {
        lock (_device.Gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed.");
                return false;
            }
            return _device.Read(_texture, _staging, Width, Height, destination, out failure);
        }
    }

    public bool TryAllocateTexture(int width, int height, out IGpuResource? resource, out GpuFailure? failure) =>
        TryResourceSize(width, height, 4, out resource, out failure);

    public bool TryAllocateBuffer(int bytes, out IGpuResource? resource, out GpuFailure? failure) =>
        TryResource(bytes, out resource, out failure);

    public bool TryAllocateRenderbuffer(int width, int height, int bytesPerPixel, out IGpuResource? resource, out GpuFailure? failure) =>
        TryResourceSize(width, height, bytesPerPixel, out resource, out failure);

    private bool TryResourceSize(int width, int height, int bytesPerPixel, out IGpuResource? resource, out GpuFailure? failure)
    {
        resource = null; failure = null;
        if (width <= 0 || height <= 0 || bytesPerPixel <= 0)
        {
            failure = new(GpuFailureKind.NativeError, "The GPU resource dimensions are invalid.");
            return false;
        }
        long bytes;
        try { bytes = checked((long)width * height * bytesPerPixel); }
        catch (OverflowException)
        {
            failure = new(GpuFailureKind.BudgetExceeded, "The GPU resource size overflowed.");
            return false;
        }
        return TryResource(bytes, out resource, out failure);
    }

    private bool TryResource(long bytes, out IGpuResource? resource, out GpuFailure? failure)
    {
        resource = null; failure = null;
        lock (_device.Gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || _device.IsDisposed)
            {
                failure = new(GpuFailureKind.Unavailable, "The GPU device or surface is disposed.");
                return false;
            }
            if (bytes <= 0 || bytes > int.MaxValue)
            {
                failure = new(GpuFailureKind.NativeError, "The GPU resource is unavailable or too large.");
                return false;
            }
            if (!_device.TryReserve(bytes, out var reservation))
            {
                failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded.");
                return false;
            }
            resource = new FolioGpuResource(reservation, bytes);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_device.Gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _device.ForgetSurface(this);
            if (_rtv != IntPtr.Zero) { Marshal.Release(_rtv); _rtv = IntPtr.Zero; }
            if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
            if (_texture != IntPtr.Zero) { Marshal.Release(_texture); _texture = IntPtr.Zero; }
            _reservation.Dispose();
        }
    }
}

internal sealed class FolioGpuResource(IDisposable reservation, long bytes) : IGpuResource
{
    private int _disposed;
    public long ReservedBytes => bytes;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        reservation.Dispose();
    }
}

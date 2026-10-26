using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

/// <summary>Small internal ANGLE/EGL backend used by the content process. It intentionally has no CPU fallback.</summary>
internal static class AngleGpuBackend
{
    internal static GpuDeviceResult TryCreate(string nativeLibraryPath, long maxBytes, GpuRendererPreference preference = GpuRendererPreference.HardwareThenWarp)
    {
        if (maxBytes < 0)
            return new(null, new(GpuFailureKind.BudgetExceeded, "The GPU budget is negative."));
        if (!OperatingSystem.IsWindows())
            return new(null, new(GpuFailureKind.Unavailable, "ANGLE's Windows native backend is unavailable on this platform."));
        if (string.IsNullOrWhiteSpace(nativeLibraryPath) || !Path.IsPathFullyQualified(nativeLibraryPath))
            return new(null, new(GpuFailureKind.Unavailable, "ANGLE requires an absolute native library path."));

        var attempts = preference switch
        {
            GpuRendererPreference.HardwareOnly => new[] { GpuRendererKind.Hardware },
            GpuRendererPreference.WarpOnly => new[] { GpuRendererKind.Warp },
            _ => new[] { GpuRendererKind.Hardware, GpuRendererKind.Warp },
        };
        string? lastError = null;
        foreach (var renderer in attempts)
        {
            try
            {
                var device = AngleGpuDevice.Create(nativeLibraryPath, renderer, maxBytes);
                if (device is not null)
                    return new(device, null);
            }
            catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or InvalidOperationException or ExternalException)
            {
                lastError = error.Message;
            }
        }
        return new(null, new(GpuFailureKind.Unavailable, lastError ?? "ANGLE could not create a native GPU device."));
    }
}

internal sealed partial class AngleGpuDevice : IGpuDevice, INativeWebGlFactory
{
    private const uint EglNone = 0x3038;
    private const uint EglPbufferBit = 0x0001;
    private const uint EglOpenGlesApi = 0x30A0;
    private const uint EglOpenGles3Bit = 0x0040;
    private const uint EglPlatformAngle = 0x3202;
    private const uint EglPlatformAngleType = 0x3203;
    private const uint EglPlatformAngleTypeD3D11 = 0x3208;
    private const uint EglPlatformAngleDeviceType = 0x3209;
    private const uint EglPlatformAngleDeviceTypeHardware = 0x320A;
    private const uint EglPlatformAngleDeviceTypeWarp = 0x320B;
    private const uint EglSurfaceType = 0x3033;
    private const uint EglRenderableType = 0x3040;
    private const uint EglRedSize = 0x3024;
    private const uint EglGreenSize = 0x3023;
    private const uint EglBlueSize = 0x3022;
    private const uint EglAlphaSize = 0x3021;
    private const uint EglContextClientVersion = 0x3098;
    private const uint EglWidth = 0x3057;
    private const uint EglHeight = 0x3056;
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlRgba = 0x1908;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlRgba8 = 0x8058;
    private const uint GlFramebuffer = 0x8D40;
    private const uint GlColorAttachment0 = 0x8CE0;
    private const uint GlFramebufferComplete = 0x8CD5;
    private const uint GlArrayBuffer = 0x8892;
    private const uint GlRenderbuffer = 0x8D41;
    private const uint GlRenderer = 0x1F01;
    private const uint GlStaticDraw = 0x88E4;
    private const uint GlScissorTest = 0x0C11;

    private object _gate = new();
    private readonly IntPtr _module;
    private readonly EglApi _egl;
    private readonly GlApi _gl;
    private readonly long _maxBytes;
    private IntPtr _display;
    private IntPtr _context;
    private IntPtr _surface;
    private long _reserved;
    private long _peakReserved;
    private bool _disposed;
    private readonly HashSet<AngleGpuSurface> _surfaces = [];
    private readonly HashSet<AngleNativeWebGlContext> _nativeWebGlContexts = [];
    private SharedEglState? _shared;

    public GpuRendererKind RendererKind { get; }
    public string RendererIdentity { get; private set; } = "";
    public long ReservedBytes { get { lock (_gate) return _reserved; } }
    public long PeakReservedBytes { get { lock (_gate) return _peakReserved; } }
    public bool IsDisposed { get { lock (_gate) return _disposed; } }
    internal object Gate => _gate;

    private AngleGpuDevice(IntPtr module, EglApi egl, GlApi gl, GpuRendererKind kind, long maxBytes)
    {
        _module = module; _egl = egl; _gl = gl; RendererKind = kind; _maxBytes = maxBytes;
    }

    public static AngleGpuDevice? Create(string nativeLibraryPath, GpuRendererKind kind, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(nativeLibraryPath)) throw new ArgumentException("A native ANGLE library path is required.", nameof(nativeLibraryPath));
        var module = NativeLibrary.Load(nativeLibraryPath);
        AngleGpuDevice? device = null;
        try
        {
            var egl = EglApi.Load(module);
            var gl = GlApi.Load(module);
            device = new AngleGpuDevice(module, egl, gl, kind, maxBytes);
            device.Initialize();
            return device;
        }
        catch
        {
            if (device is not null) device.Dispose(); else NativeLibrary.Free(module);
            throw;
        }
    }

    private void Initialize()
    {
        _shared = SharedEglState.Acquire(_egl, RendererKind, _module);
        _gate = _shared.Gate;
        lock (_gate)
        {
            // EGL client API binding is thread-local on Windows; every device may be
            // created on a different thread even when the display is shared.
            if (_egl.BindApi(EglOpenGlesApi) == 0)
                throw new InvalidOperationException("ANGLE could not bind the OpenGL ES API.");
            _display = _shared.Display;
            var configAttributes = new[] { (int)EglSurfaceType, (int)EglPbufferBit, (int)EglRenderableType, (int)EglOpenGles3Bit,
                (int)EglRedSize, 8, (int)EglGreenSize, 8, (int)EglBlueSize, 8, (int)EglAlphaSize, 8, (int)EglNone };
            if (_egl.ChooseConfig(_display, configAttributes, out var config, 1, out var count) == 0 || count == 0 || config == IntPtr.Zero)
                throw new InvalidOperationException("ANGLE could not choose an EGL configuration.");
            var contextAttributes = new[] { (int)EglContextClientVersion, 3, (int)EglNone };
            _context = _egl.CreateContext(_display, config, IntPtr.Zero, contextAttributes);
            if (_context == IntPtr.Zero) throw new InvalidOperationException("ANGLE could not create an OpenGL ES context.");
            var pbufferAttributes = new[] { (int)EglWidth, 1, (int)EglHeight, 1, (int)EglNone };
            _surface = _egl.CreatePbufferSurface(_display, config, pbufferAttributes);
            if (_surface == IntPtr.Zero) throw new InvalidOperationException("ANGLE could not create an offscreen surface.");
            MakeCurrent();
            var reportedRenderer = ReadString(_gl.GetString(GlRenderer));
            if (string.IsNullOrWhiteSpace(reportedRenderer)) throw new InvalidOperationException("ANGLE did not report a renderer identity.");
            RendererIdentity = $"{RendererKind}:{reportedRenderer}";
            ReleaseCurrent();
        }
    }

    private void MakeCurrent()
    {
        if (_egl.MakeCurrent(_display, _surface, _surface, _context) == 0)
            throw new InvalidOperationException("ANGLE could not make the context current.");
    }

    public GpuSurfaceResult TryCreateSurface(int width, int height)
    {
        lock (_gate)
        {
            if (_disposed) return new(null, new(GpuFailureKind.Unavailable, "The GPU device is disposed."));
            if (width <= 0 || height <= 0) return new(null, new(GpuFailureKind.NativeError, "The GPU surface dimensions are invalid."));
            long bytes;
            try { bytes = checked((long)width * height * 4); } catch (OverflowException) { return new(null, new(GpuFailureKind.BudgetExceeded, "The GPU surface size overflowed.")); }
            if (!TryReserveLocked(bytes, out var reservation)) return new(null, new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."));
            try { var surface = new AngleGpuSurface(this, width, height, reservation); _surfaces.Add(surface); return new(surface, null); }
            catch (Exception error) { reservation.Dispose(); return new(null, new(GpuFailureKind.NativeError, error.Message)); }
        }
    }

    public bool TryCreateNativeSurface(int width, int height, out uint texture, out uint framebuffer)
    {
        texture = framebuffer = 0;
        MakeCurrent();
        try
        {
            _gl.GenTextures(1, out texture); _gl.BindTexture(GlTexture2D, texture);
            _gl.TexParameteri(GlTexture2D, 0x2801, 0x2601); _gl.TexParameteri(GlTexture2D, 0x2800, 0x2601);
            _gl.TexImage2D(GlTexture2D, 0, (int)GlRgba8, width, height, 0, GlRgba, GlUnsignedByte, IntPtr.Zero);
            if (!CheckGl()) throw new InvalidOperationException("ANGLE texture allocation failed.");
            _gl.GenFramebuffers(1, out framebuffer); _gl.BindFramebuffer(GlFramebuffer, framebuffer);
            _gl.FramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, texture, 0);
            if (_gl.CheckFramebufferStatus(GlFramebuffer) != GlFramebufferComplete || !CheckGl())
            {
                _gl.DeleteFramebuffers(1, ref framebuffer); _gl.DeleteTextures(1, ref texture); CheckGl();
                texture = framebuffer = 0;
                throw new InvalidOperationException("ANGLE framebuffer creation failed.");
            }
            return true;
        }
        catch
        {
            if (framebuffer != 0) _gl.DeleteFramebuffers(1, ref framebuffer);
            if (texture != 0) _gl.DeleteTextures(1, ref texture);
            CheckGl(); texture = framebuffer = 0; throw;
        }
        finally { ReleaseCurrent(); }
    }

    internal void DeleteSurface(uint texture, uint framebuffer)
    {
        lock (_gate)
        {
            if (_disposed) return;
            MakeCurrent(); try { _gl.DeleteFramebuffers(1, ref framebuffer); _gl.DeleteTextures(1, ref texture); CheckGl(); } finally { ReleaseCurrent(); }
        }
    }

    private void ForgetSurface(AngleGpuSurface surface) { lock (_gate) _surfaces.Remove(surface); }
    internal void RegisterNativeWebGlContext(AngleNativeWebGlContext context) { lock (_gate) _nativeWebGlContexts.Add(context); }
    internal void UnregisterNativeWebGlContext(AngleNativeWebGlContext context) { lock (_gate) _nativeWebGlContexts.Remove(context); }

    internal bool Clear(uint framebuffer, float red, float green, float blue, float alpha, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (_disposed) { failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed."); return false; }
            try { MakeCurrent(); _gl.BindFramebuffer(GlFramebuffer, framebuffer); _gl.ClearColor(red, green, blue, alpha); _gl.Clear(0x4000); return CheckGl(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { ReleaseCurrent(); }
        }
    }

    internal bool ClearRect(uint framebuffer, float red, float green, float blue, float alpha, int x, int y, int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (_disposed) { failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed."); return false; }
            var current = false;
            try { MakeCurrent(); current = true; _gl.BindFramebuffer(GlFramebuffer, framebuffer); _gl.Enable(GlScissorTest); _gl.Scissor(x, y, width, height); _gl.ClearColor(red, green, blue, alpha); _gl.Clear(0x4000); _gl.Disable(GlScissorTest); return CheckGl(out failure); }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { if (current) { try { _gl.Disable(GlScissorTest); } catch { } } ReleaseCurrent(); }
        }
    }

    internal bool Read(uint framebuffer, int width, int height, Span<byte> destination, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (_disposed) { failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed."); return false; }
            if (destination.Length != width * height * 4) { failure = new(GpuFailureKind.NativeError, "The readback buffer size is invalid."); return false; }
            try
            {
                MakeCurrent(); _gl.BindFramebuffer(GlFramebuffer, framebuffer); var bytes = new byte[destination.Length];
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try { _gl.ReadPixels(0, 0, width, height, GlRgba, GlUnsignedByte, handle.AddrOfPinnedObject()); }
                finally { handle.Free(); }
                if (!CheckGl(out failure)) return false;
                for (var row = 0; row < height; row++) bytes.AsSpan((height - row - 1) * width * 4, width * 4).CopyTo(destination[(row * width * 4)..]);
                return true;
            }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { ReleaseCurrent(); }
        }
    }

    public bool TryReserve(long bytes, out IDisposable reservation)
    {
        lock (_gate) { reservation = NullReservation.Instance; return !_disposed && TryReserveLocked(bytes, out reservation); }
    }

    private bool TryReserveLocked(long bytes, out IDisposable reservation)
    {
        reservation = NullReservation.Instance;
        if (bytes < 0 || bytes > _maxBytes - _reserved) return false;
        _reserved += bytes; _peakReserved = Math.Max(_peakReserved, _reserved); reservation = new Reservation(this, bytes); return true;
    }

    private void Release(long bytes) { lock (_gate) _reserved = Math.Max(0, _reserved - bytes); }
    public bool TryCreateResource(NativeResourceKind kind, int width, int height, int bytes, out uint handle, out GpuFailure? failure)
    {
        lock (_gate)
        {
            handle = 0; failure = null;
            if (_disposed) { failure = new(GpuFailureKind.Unavailable, "The GPU device is disposed."); return false; }
            try
            {
                MakeCurrent();
                switch (kind)
                {
                    case NativeResourceKind.Texture:
                        _gl.GenTextures(1, out handle); _gl.BindTexture(GlTexture2D, handle);
                        _gl.TexImage2D(GlTexture2D, 0, (int)GlRgba8, width, height, 0, GlRgba, GlUnsignedByte, IntPtr.Zero); break;
                    case NativeResourceKind.Buffer:
                        _gl.GenBuffers(1, out handle); _gl.BindBuffer(GlArrayBuffer, handle); _gl.BufferData(GlArrayBuffer, (IntPtr)bytes, IntPtr.Zero, GlStaticDraw); break;
                    case NativeResourceKind.Renderbuffer:
                        _gl.GenRenderbuffers(1, out handle); _gl.BindRenderbuffer(GlRenderbuffer, handle); _gl.RenderbufferStorage(GlRenderbuffer, GlRgba8, width, height); break;
                }
                if (!CheckGl(out failure)) { DeleteResourceLocked(kind, ref handle); return false; }
                return true;
            }
            catch (Exception error) { if (handle != 0) DeleteResourceLocked(kind, ref handle); failure = new(GpuFailureKind.NativeError, error.Message); return false; }
            finally { ReleaseCurrent(); }
        }
    }

    internal void DeleteResource(NativeResourceKind kind, uint handle)
    {
        lock (_gate) { if (!_disposed) { MakeCurrent(); try { DeleteResourceLocked(kind, ref handle); CheckGl(); } finally { ReleaseCurrent(); } } }
    }

    private void DeleteResourceLocked(NativeResourceKind kind, ref uint handle)
    {
        if (handle == 0) return;
        switch (kind) { case NativeResourceKind.Texture: _gl.DeleteTextures(1, ref handle); break; case NativeResourceKind.Buffer: _gl.DeleteBuffers(1, ref handle); break; case NativeResourceKind.Renderbuffer: _gl.DeleteRenderbuffers(1, ref handle); break; }
        handle = 0;
    }
    private bool CheckGl() => _gl.GetError() == 0;
    private bool CheckGl(out GpuFailure? failure) { var error = _gl.GetError(); failure = error == 0 ? null : new(GpuFailureKind.NativeError, $"ANGLE OpenGL ES error 0x{error:X4}."); return error == 0; }
    private static string ReadString(IntPtr pointer) => pointer == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(pointer) ?? "";
    private void ReleaseCurrent() { if (_display != IntPtr.Zero) { try { _egl.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } catch { } } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var context in _nativeWebGlContexts.ToArray())
            {
                try { context.Dispose(); }
                catch { /* Continue tearing down the legacy surface and device. */ }
            }
            _nativeWebGlContexts.Clear();
            foreach (var surface in _surfaces.ToArray())
            {
                try { surface.Dispose(); }
                catch { /* Continue tearing down the context and shared display. */ }
            }
            _surfaces.Clear(); _disposed = true;
            ReleaseCurrent();
            try { if (_display != IntPtr.Zero && _context != IntPtr.Zero) _egl.DestroyContext(_display, _context); } catch { }
            try { if (_display != IntPtr.Zero && _surface != IntPtr.Zero) _egl.DestroySurface(_display, _surface); } catch { }
            try { _shared?.Release(); } catch { }
            _shared = null;
            _display = _context = _surface = IntPtr.Zero; _reserved = 0; NativeLibrary.Free(_module);
        }
    }

    private sealed class Reservation(AngleGpuDevice owner, long bytes) : IDisposable { private int _done; public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) owner.Release(bytes); } }
    private sealed class NullReservation : IDisposable { public static readonly NullReservation Instance = new(); public void Dispose() { } }

    private sealed class SharedEglState
    {
        private static readonly object RegistryGate = new();
        private static readonly Dictionary<string, SharedEglState> Registry = new(StringComparer.OrdinalIgnoreCase);
        private readonly EglApi _egl;
        private readonly string _key;
        private int _references;
        public object Gate { get; } = new();
        public IntPtr Display { get; }
        private SharedEglState(EglApi egl, string key, IntPtr display) { _egl = egl; _key = key; Display = display; _references = 1; }
        public static SharedEglState Acquire(EglApi egl, GpuRendererKind kind, IntPtr module)
        {
            var key = $"{module}:{kind}";
            lock (RegistryGate)
            {
                if (Registry.TryGetValue(key, out var existing)) { existing._references++; return existing; }
                var attrs = new[] { (int)EglPlatformAngleType, (int)EglPlatformAngleTypeD3D11, (int)EglPlatformAngleDeviceType, (int)(kind == GpuRendererKind.Warp ? EglPlatformAngleDeviceTypeWarp : EglPlatformAngleDeviceTypeHardware), (int)EglNone };
                var display = egl.GetPlatformDisplay((IntPtr)EglPlatformAngle, IntPtr.Zero, attrs);
                if (display == IntPtr.Zero || egl.Initialize(display, out _, out _) == 0 || egl.BindApi(EglOpenGlesApi) == 0)
                {
                    if (display != IntPtr.Zero) { try { egl.Terminate(display); } catch { } }
                    throw new InvalidOperationException("ANGLE EGL display initialization failed.");
                }
                var state = new SharedEglState(egl, key, display); Registry.Add(key, state); return state;
            }
        }
        public void Release()
        {
            lock (RegistryGate)
            {
                if (--_references != 0) return;
                Registry.Remove(_key);
                try { _egl.Terminate(Display); } catch { }
            }
        }
    }

    private sealed class AngleGpuSurface : IGpuSurface, IGpuResourceSet
    {
        private readonly AngleGpuDevice _device; private readonly IDisposable _reservation; private uint _texture; private uint _framebuffer; private int _disposed;
        public AngleGpuSurface(AngleGpuDevice device, int width, int height, IDisposable reservation) { _device = device; Width = width; Height = height; _reservation = reservation; try { device.TryCreateNativeSurface(width, height, out _texture, out _framebuffer); } catch { reservation.Dispose(); throw; } }
        public int Width { get; } public int Height { get; } public long ReservedBytes => (long)Width * Height * 4; public IGpuResourceSet Resources => this;
        public bool ClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure)
        {
            lock (_device.Gate)
            {
                if (Volatile.Read(ref _disposed) != 0) { failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed."); return false; }
                return _device.Clear(_framebuffer, red, green, blue, alpha, out failure);
            }
        }
        public bool ClearColorRect(float red, float green, float blue, float alpha, int x, int y, int width, int height, out GpuFailure? failure)
        {
            lock (_device.Gate)
            {
                if (Volatile.Read(ref _disposed) != 0) { failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed."); return false; }
                if (x < 0 || y < 0 || width <= 0 || height <= 0 || x > Width - width || y > Height - height) { failure = new(GpuFailureKind.NativeError, "The clear rectangle is outside the GPU surface."); return false; }
                return _device.ClearRect(_framebuffer, red, green, blue, alpha, x, y, width, height, out failure);
            }
        }
        public bool TryReadPixels(Span<byte> destination, out GpuFailure? failure)
        {
            lock (_device.Gate)
            {
                if (Volatile.Read(ref _disposed) != 0) { failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed."); return false; }
                return _device.Read(_framebuffer, Width, Height, destination, out failure);
            }
        }
        public bool TryAllocateTexture(int width, int height, out IGpuResource? resource, out GpuFailure? failure) => TryResourceSize(width, height, 4, out resource, out failure, NativeResourceKind.Texture);
        public bool TryAllocateBuffer(int bytes, out IGpuResource? resource, out GpuFailure? failure) => TryResource(bytes, 0, 0, out resource, out failure, NativeResourceKind.Buffer);
        public bool TryAllocateRenderbuffer(int width, int height, int bytesPerPixel, out IGpuResource? resource, out GpuFailure? failure)
        {
            lock (_device.Gate)
            {
                if (Volatile.Read(ref _disposed) != 0) { resource = null; failure = new(GpuFailureKind.Unavailable, "The GPU surface is disposed."); return false; }
                if (bytesPerPixel != 4) { resource = null; failure = new(GpuFailureKind.NativeError, "The ANGLE clear-path renderbuffer format is RGBA8 only."); return false; }
                return TryResourceSize(width, height, bytesPerPixel, out resource, out failure, NativeResourceKind.Renderbuffer);
            }
        }
        private bool TryResourceSize(int width, int height, int bytesPerPixel, out IGpuResource? resource, out GpuFailure? failure, NativeResourceKind kind)
        {
            try { return TryResource(checked((long)width * height * bytesPerPixel), width, height, out resource, out failure, kind); }
            catch (OverflowException) { resource = null; failure = new(GpuFailureKind.BudgetExceeded, "The GPU resource size overflowed."); return false; }
        }
        private bool TryResource(long bytes, int width, int height, out IGpuResource? resource, out GpuFailure? failure, NativeResourceKind kind)
        {
            resource = null; failure = null;
            lock (_device.Gate)
            {
                if (Volatile.Read(ref _disposed) != 0 || _device.IsDisposed) { failure = new(GpuFailureKind.Unavailable, "The GPU device or surface is disposed."); return false; }
                if (bytes <= 0 || bytes > int.MaxValue || (kind != NativeResourceKind.Buffer && (width <= 0 || height <= 0))) { failure = new(GpuFailureKind.NativeError, "The GPU resource is unavailable or too large."); return false; }
                if (!_device.TryReserve(bytes, out var reservation)) { failure = new(GpuFailureKind.BudgetExceeded, "The GPU budget was exceeded."); return false; }
                if (!_device.TryCreateResource(kind, width, height, (int)bytes, out var handle, out failure)) { reservation.Dispose(); return false; }
                resource = new AngleGpuResource(_device, reservation, kind, handle, bytes); return true;
            }
        }
        public void Dispose()
        {
            lock (_device.Gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _device.ForgetSurface(this);
                try { _device.DeleteSurface(_texture, _framebuffer); }
                catch { /* Native teardown must not strand the reservation. */ }
                finally { _reservation.Dispose(); }
            }
        }
    }

    private sealed class AngleGpuResource(AngleGpuDevice device, IDisposable reservation, NativeResourceKind kind, uint handle, long bytes) : IGpuResource
    {
        private int _disposed; public long ReservedBytes => bytes;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { device.DeleteResource(kind, handle); }
            catch { /* Native deletion failure cannot retain the budget reservation. */ }
            finally { reservation.Dispose(); }
        }
    }

    internal enum NativeResourceKind { Texture, Buffer, Renderbuffer }

    internal sealed class EglApi
    {
        public required EglGetPlatformDisplay GetPlatformDisplay; public required EglInitialize Initialize; public required EglQueryString QueryString;
        public required EglBindApi BindApi; public required EglChooseConfig ChooseConfig;
        public required EglCreateContext CreateContext; public required EglCreatePbufferSurface CreatePbufferSurface;
        public required EglMakeCurrent MakeCurrent; public required EglDestroyContext DestroyContext;
        public required EglDestroySurface DestroySurface; public required EglTerminate Terminate;
        public static EglApi Load(IntPtr m) => new() { GetPlatformDisplay = D<EglGetPlatformDisplay>(m, "EGL_GetPlatformDisplayEXT"), Initialize = D<EglInitialize>(m, "EGL_Initialize"), QueryString = D<EglQueryString>(m, "EGL_QueryString"), BindApi = D<EglBindApi>(m, "EGL_BindAPI"), ChooseConfig = D<EglChooseConfig>(m, "EGL_ChooseConfig"), CreateContext = D<EglCreateContext>(m, "EGL_CreateContext"), CreatePbufferSurface = D<EglCreatePbufferSurface>(m, "EGL_CreatePbufferSurface"), MakeCurrent = D<EglMakeCurrent>(m, "EGL_MakeCurrent"), DestroyContext = D<EglDestroyContext>(m, "EGL_DestroyContext"), DestroySurface = D<EglDestroySurface>(m, "EGL_DestroySurface"), Terminate = D<EglTerminate>(m, "EGL_Terminate") };
    }

    #pragma warning disable CS8618 // Native function pointers are populated atomically by Load.
    internal sealed class GlApi
    {
        [System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
        private GlApi() { }

        public required GlGenTextures GenTextures; public required GlBindTexture BindTexture; public required GlTexParameteri TexParameteri; public required GlTexImage2D TexImage2D; public required GlTexStorage2D TexStorage2D; public required GlTexSubImage2D TexSubImage2D; public required GlCopyTexImage2D CopyTexImage2D; public required GlCopyTexSubImage2D CopyTexSubImage2D; public required GlTexImage3D TexImage3D; public required GlTexStorage3D TexStorage3D; public required GlTexSubImage3D TexSubImage3D; public required GlGenerateMipmap GenerateMipmap; public required GlGenFramebuffers GenFramebuffers; public required GlBindFramebuffer BindFramebuffer; public required GlFramebufferTexture2D FramebufferTexture2D; public required GlFramebufferTextureLayer FramebufferTextureLayer; public required GlFramebufferRenderbuffer FramebufferRenderbuffer; public required GlCheckFramebufferStatus CheckFramebufferStatus; public required GlDeleteFramebuffers DeleteFramebuffers; public required GlDeleteTextures DeleteTextures; public required GlClearColor ClearColor; public required GlClear Clear; public required GlClearBufferfv ClearBufferfv; public required GlClearBufferiv ClearBufferiv; public required GlClearBufferuiv ClearBufferuiv; public required GlClearBufferfi ClearBufferfi; public required GlPixelStorei PixelStorei; public required GlGetError GetError; public required GlGetString GetString; public required GlReadPixels ReadPixels; public required GlGenBuffers GenBuffers; public required GlBindBuffer BindBuffer; public required GlBindBufferBase BindBufferBase; public required GlBindBufferRange BindBufferRange; public required GlGetBufferParameteriv GetBufferParameteriv; public required GlMapBufferRange MapBufferRange; public required GlUnmapBuffer UnmapBuffer; public required GlBufferData BufferData; public required GlDeleteBuffers DeleteBuffers; public required GlGenRenderbuffers GenRenderbuffers; public required GlBindRenderbuffer BindRenderbuffer; public required GlRenderbufferStorage RenderbufferStorage; public required GlRenderbufferStorageMultisample RenderbufferStorageMultisample; public required GlBlitFramebuffer BlitFramebuffer; public required GlDeleteRenderbuffers DeleteRenderbuffers; public required GlEnable Enable; public required GlDisable Disable; public required GlScissor Scissor; public required GlDrawBuffers DrawBuffers; public required GlReadBuffer ReadBuffer; public required GlBlendFuncSeparate BlendFuncSeparate; public required GlBlendEquationSeparate BlendEquationSeparate; public required GlBlendColor BlendColor; public required GlColorMask ColorMask; public required GlDepthFunc DepthFunc; public required GlDepthMask DepthMask; public required GlStencilFuncSeparate StencilFuncSeparate; public required GlStencilOpSeparate StencilOpSeparate; public required GlStencilMaskSeparate StencilMaskSeparate; public required GlCullFace CullFace; public required GlFrontFace FrontFace; public required GlDepthRangef DepthRangef; public required GlLineWidth LineWidth; public required GlPolygonOffset PolygonOffset; public required GlSampleCoverage SampleCoverage; public required GlHint Hint; public required GlActiveTexture ActiveTexture; public required GlFlush Flush; public required GlFinish Finish; public required GlGetIntegerv GetIntegerv; public required GlGetIntegervArray GetIntegervArray; public required GlGetFloatv GetFloatv; public required GlGetFloatvArray GetFloatvArray; public required GlGetBooleanv GetBooleanv; public required GlGetStringi GetStringi; public required GlGetAttachedShaders GetAttachedShaders;
        public required GlCreateShader CreateShader; public required GlShaderSource ShaderSource; public required GlCompileShader CompileShader; public required GlGetShaderiv GetShaderiv; public required GlGetShaderInfoLog GetShaderInfoLog; public required GlGetShaderSource GetShaderSource; public required GlDeleteShader DeleteShader;
        public required GlCreateProgram CreateProgram; public required GlAttachShader AttachShader; public required GlDetachShader DetachShader; public required GlLinkProgram LinkProgram; public required GlValidateProgram ValidateProgram; public required GlGetProgramiv GetProgramiv; public required GlGetProgramInfoLog GetProgramInfoLog; public required GlDeleteProgram DeleteProgram; public required GlUseProgram UseProgram; public required GlGetAttribLocation GetAttribLocation; public required GlGetUniformLocation GetUniformLocation; public required GlBindAttribLocation BindAttribLocation; public required GlGetActiveAttrib GetActiveAttrib; public required GlGetActiveUniform GetActiveUniform; public required GlGetUniformIndices GetUniformIndices; public required GlGetActiveUniformsiv GetActiveUniformsiv; public required GlGetUniformBlockIndex GetUniformBlockIndex; public required GlUniformBlockBinding UniformBlockBinding; public required GlGetShaderPrecisionFormat GetShaderPrecisionFormat; public required GlUniform1fv Uniform1fv; public required GlUniform2fv Uniform2fv; public required GlUniform3fv Uniform3fv; public required GlUniform4fv Uniform4fv; public required GlUniform1iv Uniform1iv; public required GlUniform2iv Uniform2iv; public required GlUniform3iv Uniform3iv; public required GlUniform4iv Uniform4iv; public required GlUniform1uiv Uniform1uiv; public required GlUniform2uiv Uniform2uiv; public required GlUniform3uiv Uniform3uiv; public required GlUniform4uiv Uniform4uiv; public required GlUniformMatrix2fv UniformMatrix2fv; public required GlUniformMatrix3fv UniformMatrix3fv; public required GlUniformMatrix4fv UniformMatrix4fv; public required GlUniformMatrix2x3fv UniformMatrix2x3fv; public required GlUniformMatrix3x2fv UniformMatrix3x2fv; public required GlUniformMatrix2x4fv UniformMatrix2x4fv; public required GlUniformMatrix4x2fv UniformMatrix4x2fv; public required GlUniformMatrix3x4fv UniformMatrix3x4fv; public required GlUniformMatrix4x3fv UniformMatrix4x3fv; public required GlGetUniformfv GetUniformfv; public required GlGetUniformiv GetUniformiv; public required GlGetUniformuiv GetUniformuiv; public required GlBufferSubData BufferSubData; public required GlCopyBufferSubData CopyBufferSubData; public required GlGenVertexArrays GenVertexArrays; public required GlBindVertexArray BindVertexArray; public required GlDeleteVertexArrays DeleteVertexArrays; public required GlEnableVertexAttribArray EnableVertexAttribArray; public required GlDisableVertexAttribArray DisableVertexAttribArray; public required GlVertexAttribPointer VertexAttribPointer; public required GlVertexAttrib4fv VertexAttrib4fv; public required GlVertexAttribI4iv VertexAttribI4iv; public required GlVertexAttribI4uiv VertexAttribI4uiv; public required GlGetVertexAttribiv GetVertexAttribiv; public required GlGetVertexAttribfv GetVertexAttribfv; public required GlGetVertexAttribPointerv GetVertexAttribPointerv; public required GlVertexAttribIPointer VertexAttribIPointer; public required GlVertexAttribDivisor VertexAttribDivisor; public required GlDrawArrays DrawArrays; public required GlDrawElements DrawElements; public required GlDrawArraysInstanced DrawArraysInstanced; public required GlDrawElementsInstanced DrawElementsInstanced; public required GlViewport Viewport; public required GlClearDepthf ClearDepthf; public required GlClearStencil ClearStencil;

        public static GlApi Load(IntPtr module)
        {
            var api = new GlApi();
            foreach (var field in typeof(GlApi).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                var export = field.Name switch
                {
                    "GetIntegervArray" => "glGetIntegerv",
                    "GetFloatvArray" => "glGetFloatv",
                    _ => "gl" + field.Name,
                };
                var pointer = NativeLibrary.GetExport(module, export);
                field.SetValue(api, Marshal.GetDelegateForFunctionPointer(pointer, field.FieldType));
            }
            return api;
        }
    }
    #pragma warning restore CS8618

    private static T D<T>(IntPtr m, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(m, name));
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr EglGetPlatformDisplay(IntPtr platform, IntPtr nativeDisplay, int[] attrs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglInitialize(IntPtr display, out int major, out int minor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr EglQueryString(IntPtr display, int name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglBindApi(uint api);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglChooseConfig(IntPtr display, int[] attrs, out IntPtr config, int size, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr EglCreateContext(IntPtr display, IntPtr config, IntPtr share, int[] attrs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr EglCreatePbufferSurface(IntPtr display, IntPtr config, int[] attrs);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglDestroyContext(IntPtr display, IntPtr context);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglDestroySurface(IntPtr display, IntPtr surface);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint EglTerminate(IntPtr display);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGenTextures(int count, out uint textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindTexture(uint target, uint texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexParameteri(uint target, uint pname, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexStorage2D(uint target, int levels, uint internalFormat, int width, int height);
[UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexSubImage2D(uint target, int level, int xOffset, int yOffset, int width, int height, uint format, uint type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlCopyTexImage2D(uint target, int level, int internalFormat, int x, int y, int width, int height, int border);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlCopyTexSubImage2D(uint target, int level, int xOffset, int yOffset, int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexImage3D(uint target, int level, int internalFormat, int width, int height, int depth, int border, uint format, uint type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexStorage3D(uint target, int levels, uint internalFormat, int width, int height, int depth);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlTexSubImage3D(uint target, int level, int xOffset, int yOffset, int zOffset, int width, int height, int depth, uint format, uint type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGenerateMipmap(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGenFramebuffers(int count, out uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindFramebuffer(uint target, uint framebuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlFramebufferTexture2D(uint target, uint attachment, uint textarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlFramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlFramebufferRenderbuffer(uint target, uint attachment, uint renderbufferTarget, uint renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GlCheckFramebufferStatus(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteFramebuffers(int count, ref uint framebuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteTextures(int count, ref uint textures);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearColor(float red, float green, float blue, float alpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlPixelStorei(uint parameter, int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClear(uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearBufferfv(uint buffer, int drawbuffer, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearBufferiv(uint buffer, int drawbuffer, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearBufferuiv(uint buffer, int drawbuffer, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearBufferfi(uint buffer, int drawbuffer, float depth, int stencil);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GlGetError();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr GlGetString(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlReadPixels(int x, int y, int width, int height, uint format, uint type, IntPtr pixels);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGenBuffers(int count, out uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindBuffer(uint target, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindBufferBase(uint target, uint index, uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindBufferRange(uint target, uint index, uint buffer, IntPtr offset, IntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetBufferParameteriv(uint target, uint pname, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr GlMapBufferRange(uint target, IntPtr offset, IntPtr length, uint access);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate byte GlUnmapBuffer(uint target);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBufferData(uint target, IntPtr size, IntPtr data, uint usage);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteBuffers(int count, ref uint buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGenRenderbuffers(int count, out uint renderbuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindRenderbuffer(uint target, uint renderbuffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlRenderbufferStorage(uint target, uint internalFormat, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlRenderbufferStorageMultisample(uint target, int samples, uint internalFormat, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, uint mask, uint filter);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteRenderbuffers(int count, ref uint renderbuffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlEnable(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDisable(uint capability);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBlendFuncSeparate(uint sourceRgb, uint destinationRgb, uint sourceAlpha, uint destinationAlpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBlendColor(float red, float green, float blue, float alpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBlendEquationSeparate(uint rgb, uint alpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlColorMask(byte red, byte green, byte blue, byte alpha);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDepthFunc(uint function);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDepthMask(byte enabled);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlStencilFuncSeparate(uint face, uint function, int reference, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlStencilOpSeparate(uint face, uint fail, uint depthFail, uint depthPass);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlStencilMaskSeparate(uint face, uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlCullFace(uint face);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlFrontFace(uint mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDepthRangef(float nearValue, float farValue);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlLineWidth(float width);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlPolygonOffset(float factor, float units);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlSampleCoverage(float value, byte invert);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlActiveTexture(uint texture);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlHint(uint target, uint mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlFlush();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlFinish();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetIntegervArray(uint parameter, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetIntegerv(uint parameter, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetFloatvArray(uint parameter, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetFloatv(uint parameter, out float value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetBooleanv(uint parameter, out byte value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate IntPtr GlGetStringi(uint name, uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetAttachedShaders(uint program, int maxCount, out int count, IntPtr shaders);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDrawBuffers(int count, IntPtr buffers);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlReadBuffer(uint buffer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlScissor(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GlCreateShader(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlShaderSource(uint shader, int count, IntPtr strings, IntPtr lengths);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlCompileShader(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetShaderiv(uint shader, uint pname, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetShaderInfoLog(uint shader, int maxLength, out int length, IntPtr infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetShaderSource(uint shader, int maxLength, out int length, IntPtr source);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteShader(uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GlCreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlAttachShader(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDetachShader(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlLinkProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlValidateProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetProgramiv(uint program, uint pname, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetProgramInfoLog(uint program, int maxLength, out int length, IntPtr infoLog);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUseProgram(uint program);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int GlGetAttribLocation(uint program, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate int GlGetUniformLocation(uint program, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindAttribLocation(uint program, uint index, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetActiveAttrib(uint program, uint index, int maxLength, out int length, out int size, out uint type, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetUniformIndices(uint program, int count, IntPtr names, IntPtr indices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetActiveUniformsiv(uint program, int count, IntPtr indices, uint parameter, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint GlGetUniformBlockIndex(uint program, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformBlockBinding(uint program, uint blockIndex, uint binding);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetActiveUniform(uint program, uint index, int maxLength, out int length, out int size, out uint type, IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetShaderPrecisionFormat(uint shaderType, uint precisionType, IntPtr range, IntPtr precision);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform1fv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform2fv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform3fv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform4fv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform1iv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform2iv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform3iv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform4iv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform1uiv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform2uiv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform3uiv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniform4uiv(int location, int count, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix2fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix3fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix4fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix2x3fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix3x2fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix2x4fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix4x2fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix3x4fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlUniformMatrix4x3fv(int location, int count, byte transpose, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetUniformfv(uint program, int location, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetUniformiv(uint program, int location, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetUniformuiv(uint program, int location, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBufferSubData(uint target, IntPtr offset, IntPtr size, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlCopyBufferSubData(uint readTarget, uint writeTarget, IntPtr readOffset, IntPtr writeOffset, IntPtr size);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGenVertexArrays(int count, out uint arrays);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlBindVertexArray(uint array);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDeleteVertexArrays(int count, ref uint arrays);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlEnableVertexAttribArray(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDisableVertexAttribArray(uint index);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlVertexAttrib4fv(uint index, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlVertexAttribI4iv(uint index, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlVertexAttribI4uiv(uint index, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetVertexAttribiv(uint index, uint pname, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetVertexAttribfv(uint index, uint pname, IntPtr values);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlGetVertexAttribPointerv(uint index, uint pname, out IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlVertexAttribPointer(uint index, int size, uint type, byte normalized, int stride, IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlVertexAttribIPointer(uint index, int size, uint type, int stride, IntPtr pointer);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlVertexAttribDivisor(uint index, uint divisor);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDrawArrays(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDrawElements(uint mode, int count, uint type, IntPtr indices);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDrawArraysInstanced(uint mode, int first, int count, int instanceCount);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlDrawElementsInstanced(uint mode, int count, uint type, IntPtr indices, int instanceCount);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlViewport(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearDepthf(float depth);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void GlClearStencil(int value);
}

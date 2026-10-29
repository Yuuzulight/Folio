using System.Runtime.InteropServices;

namespace Folio.Scripting.Gpu;

internal sealed class NativeWebGlInitializationException(GpuFailure failure) : Exception(failure.Message)
{
    internal GpuFailure Failure { get; } = failure;
}

internal sealed partial class AngleGpuDevice
{
    internal EglApi Egl => _egl;
    internal GlApi Gl => _gl;
    internal object DeviceGate => _gate;
    internal IntPtr Display => _display;
    internal bool DeviceDisposed => _disposed;
    public bool TryCreateNativeWebGlContext(WebGlContextOptions options, out AngleNativeWebGlContext? context, out GpuFailure? failure)
    {
        lock (_gate)
        {
            context = null; failure = null;
            if (_disposed) { failure = new(GpuFailureKind.Unavailable, "The document GPU device is disposed."); return false; }
            try { context = AngleNativeWebGlContext.Create(this, options); return true; }
            catch (NativeWebGlInitializationException error) { failure = error.Failure; return false; }
            catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
        }
    }

    bool INativeWebGlFactory.TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure)
    {
        if (TryCreateNativeWebGlContext(options, out var native, out failure)) { context = native; return true; }
        context = null; return false;
    }
}

/// <summary>
/// Per-canvas ANGLE context foundation. The scripting adapter will add WebGL object
/// commands on top of this class; this type owns only EGL state, drawing-buffer
/// attachments, and the document device's aggregate reservations.
/// </summary>
internal sealed partial class AngleNativeWebGlContext : IDisposable, INativeWebGlContext
{
    private const uint EglNone = 0x3038;
    private const uint EglPbufferBit = 0x0001;
    private const uint EglOpenGlesApi = 0x30A0;
    private const uint EglOpenGles2Bit = 0x0004;
    private const uint EglOpenGles3Bit = 0x0040;
    private const uint EglSurfaceType = 0x3033;
    private const uint EglRenderableType = 0x3040;
    private const uint EglRedSize = 0x3024;
    private const uint EglGreenSize = 0x3023;
    private const uint EglBlueSize = 0x3022;
    private const uint EglAlphaSize = 0x3021;
    private const uint EglDepthSize = 0x3025;
    private const uint EglStencilSize = 0x3026;
    private const uint EglSamples = 0x3031;
    private const uint EglContextClientVersion = 0x3098;
    private const uint EglContextWebGlCompatibility = 0x33AC;
    private const uint EglRobustResourceInitialization = 0x3453;
    private const uint EglWidth = 0x3057;
    private const uint EglHeight = 0x3056;
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlRgba = 0x1908;
    private const uint GlRgb = 0x1907;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlRgba8 = 0x8058;
    private const uint GlRgb8 = 0x8051;
    private const uint GlFramebuffer = 0x8D40;
    private const uint GlColorAttachment0 = 0x8CE0;
    private const uint GlDepthAttachment = 0x8D00;
    private const uint GlStencilAttachment = 0x8D20;
    private const uint GlDepthStencilAttachment = 0x821A;
    private const uint GlDepthComponent16 = 0x81A5;
    private const uint GlDepth24Stencil8 = 0x88F0;
    private const uint GlStencilIndex8 = 0x8D48;
    private const uint GlRenderbuffer = 0x8D41;
    private const uint GlFramebufferComplete = 0x8CD5;
    private const uint GlDrawFramebuffer = 0x8CA9;
    private const uint GlReadFramebuffer = 0x8CA8;
    private const uint GlDepthBufferBit = 0x0100;
    private const uint GlStencilBufferBit = 0x0400;
    private const uint GlNearest = 0x2600;

    private readonly AngleGpuDevice _device;
    private readonly AngleGpuDevice.EglApi _egl;
    private readonly AngleGpuDevice.GlApi _gl;
    private readonly object _gate;
    private IDisposable? _colorReservation;
    private IDisposable? _depthReservation;
    private IDisposable? _stencilReservation;
    private IntPtr _display;
    private IntPtr _context;
    private IntPtr _surface;
    private uint _texture;
    private uint _framebuffer;
    private uint _depthRenderbuffer;
    private uint _stencilRenderbuffer;
    private uint _multisampleFramebuffer;
    private uint _multisampleColorRenderbuffer;
    private uint _multisampleDepthRenderbuffer;
    private int _width;
    private int _height;
    private int _disposed;
    private readonly HashSet<uint> _shaders = [];
    private readonly HashSet<uint> _programs = [];
    private readonly HashSet<uint> _buffers = [];
    private readonly HashSet<uint> _textures = [];
    private readonly HashSet<uint> _framebuffers = [];
    private readonly HashSet<uint> _renderbuffers = [];
    private readonly Dictionary<uint, IDisposable> _renderbufferReservations = [];
    private readonly Dictionary<uint, long> _renderbufferSizes = [];
    // ANGLE/WARP rejects optional float renderbuffer formats even though it
    // supports the corresponding float textures. Keep the WebGL renderbuffer
    // identity while attaching a texture-backed storage object for those
    // formats.
    private readonly Dictionary<uint, uint> _emulatedFloatRenderbuffers = [];
    private uint? _boundRenderbuffer;
    private readonly Dictionary<uint, IDisposable> _textureReservations = [];
    private readonly Dictionary<uint, long> _textureSizes = [];
    private readonly Dictionary<uint, Dictionary<uint, int>> _textureParameters = [];
    private readonly Dictionary<uint, uint> _textureTargets = [];
    private readonly Dictionary<(uint Texture, uint Target, int Level), (int Width, int Height, int Depth, int InternalFormat)> _textureLevels = [];
    private readonly Dictionary<(uint Texture, uint Target, int Level), long> _textureLevelSizes = [];
    private uint? _boundTexture2D;
    private uint? _boundTexture3D;
    private uint? _boundTexture2DArray;
    private uint? _boundTextureCube;
    private readonly Dictionary<uint, IDisposable> _bufferReservations = [];
    private readonly Dictionary<uint, long> _bufferSizes = [];
    private readonly HashSet<uint> _vertexArrays = [];
    private readonly HashSet<uint> _transformFeedbacks = [];
    // ELEMENT_ARRAY_BUFFER is VAO state in GLES/WebGL. The default VAO is represented by key 0.
    private readonly Dictionary<uint, uint?> _vaoElementBuffers = [];
    private uint? _boundCopyReadBuffer;
    private uint? _boundCopyWriteBuffer;
    private uint? _boundUniformBuffer;
    private int _packAlignment = 4;
    private bool _unpackFlipY;
    private int _packRowLength;
    private int _packSkipPixels;
    private int _packSkipRows;
    private readonly float[] _vertexAttribConstants = new float[33 * 4];

    public WebGlContextOptions Options { get; }
    public WebGlContextAttributes Attributes => Options.RequestedAttributes with { Antialias = _multisampleFramebuffer != 0 };
    public int Width { get { lock (_gate) return _width; } }
    public int Height { get { lock (_gate) return _height; } }
    public int DrawingBufferWidth => Width;
    public int DrawingBufferHeight => Height;
    public bool IsLost => Volatile.Read(ref _disposed) != 0;
    public long ReservedBytes => _device.ReservedBytes;
    public long PeakReservedBytes => _device.PeakReservedBytes;
    public uint Texture { get { lock (_gate) return _texture; } }
    public uint Framebuffer { get { lock (_gate) return _framebuffer; } }

    private AngleNativeWebGlContext(AngleGpuDevice device, WebGlContextOptions options)
    {
        _device = device;
        _egl = device.Egl;
        _gl = device.Gl;
        _gate = device.DeviceGate;
        Options = options;
        for (var i = 0; i < 33; i++) _vertexAttribConstants[(i * 4) + 3] = 1f;
        if (options.Width < 0 || options.Height < 0) throw new ArgumentOutOfRangeException(nameof(options));
        lock (_gate)
        {
            try
            {
                if (device.DeviceDisposed) throw new InvalidOperationException("The document GPU device is disposed.");
                if (_egl.BindApi(EglOpenGlesApi) == 0) throw new InvalidOperationException("ANGLE could not bind OpenGL ES.");
                _display = device.Display;
                var extensions = Marshal.PtrToStringAnsi(_egl.QueryString(_display, 0x3055)) ?? string.Empty;
                var extensionSet = extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!extensionSet.Contains("EGL_ANGLE_create_context_webgl_compatibility", StringComparer.Ordinal) ||
                    !extensionSet.Contains("EGL_ANGLE_robust_resource_initialization", StringComparer.Ordinal))
                    throw new InvalidOperationException($"ANGLE lacks required EGL extensions (WebGL compatibility={extensionSet.Contains("EGL_ANGLE_create_context_webgl_compatibility", StringComparer.Ordinal)}, robust resource initialization={extensionSet.Contains("EGL_ANGLE_robust_resource_initialization", StringComparer.Ordinal)}).");
                var configAttrs = BuildConfigAttributes(options);
                if (_egl.ChooseConfig(_display, configAttrs, out var config, 1, out var count) == 0 || count == 0 || config == IntPtr.Zero)
                    throw new InvalidOperationException("ANGLE could not choose a WebGL configuration.");
                var clientVersion = options.Version == WebGlContextVersion.WebGl2 ? 3 : 2;
                var contextAttrs = new[] { (int)EglContextClientVersion, clientVersion, (int)EglContextWebGlCompatibility, 1, (int)EglRobustResourceInitialization, 1, (int)EglNone };
                _context = _egl.CreateContext(_display, config, IntPtr.Zero, contextAttrs);
                if (_context == IntPtr.Zero) throw new InvalidOperationException("ANGLE could not create the WebGL context.");
                var pbufferAttrs = new[] { (int)EglWidth, 1, (int)EglHeight, 1, (int)EglNone };
                _surface = _egl.CreatePbufferSurface(_display, config, pbufferAttrs);
                if (_surface == IntPtr.Zero) throw new InvalidOperationException("ANGLE could not create the WebGL pbuffer.");
                _width = options.Width;
                _height = options.Height;
                if (!TryAllocateAttachmentsLocked(Math.Max(1, _width), Math.Max(1, _height), out var failure))
                    throw new NativeWebGlInitializationException(failure ?? new(GpuFailureKind.NativeError, "ANGLE could not allocate the WebGL drawing buffer."));
                ReleaseCurrentLocked();
            }
            catch
            {
                ReleaseCurrentLocked();
                try { DeleteAttachmentsLocked(_texture, _framebuffer, _depthRenderbuffer, _stencilRenderbuffer, _multisampleFramebuffer, _multisampleColorRenderbuffer, _multisampleDepthRenderbuffer); } catch { }
                _colorReservation?.Dispose(); _depthReservation?.Dispose(); _stencilReservation?.Dispose();
                try { if (_context != IntPtr.Zero) _egl.DestroyContext(_display, _context); } catch { }
                try { if (_surface != IntPtr.Zero) _egl.DestroySurface(_display, _surface); } catch { }
                throw;
            }
        }
    }

    internal static AngleNativeWebGlContext Create(AngleGpuDevice device, WebGlContextOptions options) =>
        new(device, options);

    private static int[] BuildConfigAttributes(WebGlContextOptions options)
    {
        var attributes = new List<int>
        {
            (int)EglSurfaceType, (int)EglPbufferBit,
            (int)EglRenderableType, options.Version == WebGlContextVersion.WebGl2 ? (int)EglOpenGles3Bit : (int)EglOpenGles2Bit,
            (int)EglRedSize, 8, (int)EglGreenSize, 8, (int)EglBlueSize, 8,
            (int)EglAlphaSize, options.RequestedAttributes.Alpha ? 8 : 0,
            (int)EglDepthSize, options.RequestedAttributes.Depth ? 24 : 0,
            (int)EglStencilSize, options.RequestedAttributes.Stencil ? 8 : 0,
            (int)EglSamples, options.RequestedAttributes.Antialias ? 4 : 0,
            (int)EglNone,
        };
        return attributes.ToArray();
    }

    private bool TryAllocateAttachmentsLocked(int width, int height, out GpuFailure? failure)
    {
        failure = null;
        IDisposable? colorReservation = null;
        IDisposable? depthReservation = null;
        IDisposable? stencilReservation = null;
        uint texture = 0, framebuffer = 0, depth = 0, stencil = 0, multisampleFramebuffer = 0, multisampleColor = 0, multisampleDepth = 0;
        try
        {
            var samples = Options.RequestedAttributes.Antialias ? 4 : 1;
            var colorBytes = checked((long)width * height * 4 * (samples + (Options.RequestedAttributes.Antialias ? 1 : 0)));
            if (!_device.TryReserve(colorBytes, out colorReservation)) { failure = new(GpuFailureKind.BudgetExceeded, "The document GPU budget was exceeded by the drawing buffer."); return false; }
            if (Options.RequestedAttributes.Depth && !_device.TryReserve(checked((long)width * height * 4 * samples), out depthReservation)) { failure = new(GpuFailureKind.BudgetExceeded, "The document GPU budget was exceeded by the depth buffer."); return false; }
            if (Options.RequestedAttributes.Stencil && !_device.TryReserve(checked((long)width * height * samples), out stencilReservation)) { failure = new(GpuFailureKind.BudgetExceeded, "The document GPU budget was exceeded by the stencil buffer."); return false; }
            MakeCurrentLocked();
            _gl.GenTextures(1, out texture);
            _gl.BindTexture(GlTexture2D, texture);
            _gl.TexParameteri(GlTexture2D, 0x2801, 0x2601);
            _gl.TexParameteri(GlTexture2D, 0x2800, 0x2601);
            var colorFormat = Options.Version == WebGlContextVersion.WebGl2
                ? (Options.RequestedAttributes.Alpha ? GlRgba8 : GlRgb8)
                : (Options.RequestedAttributes.Alpha ? GlRgba : GlRgb);
            var colorUploadFormat = Options.RequestedAttributes.Alpha ? GlRgba : GlRgb;
            _gl.TexImage2D(GlTexture2D, 0, (int)colorFormat, width, height, 0, colorUploadFormat, GlUnsignedByte, IntPtr.Zero);
            _gl.GenFramebuffers(1, out framebuffer);
            _gl.BindFramebuffer(GlFramebuffer, framebuffer);
            _gl.FramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, texture, 0);
            if (!Options.RequestedAttributes.Antialias && (Options.RequestedAttributes.Depth || Options.RequestedAttributes.Stencil))
            {
                _gl.GenRenderbuffers(1, out depth);
                _gl.BindRenderbuffer(GlRenderbuffer, depth);
                var format = Options.RequestedAttributes.Depth && Options.RequestedAttributes.Stencil ? GlDepth24Stencil8 : Options.RequestedAttributes.Depth ? GlDepthComponent16 : GlStencilIndex8;
                _gl.RenderbufferStorage(GlRenderbuffer, format, width, height);
                if (Options.RequestedAttributes.Depth && Options.RequestedAttributes.Stencil)
                    _gl.FramebufferRenderbuffer(GlFramebuffer, GlDepthStencilAttachment, GlRenderbuffer, depth);
                else if (Options.RequestedAttributes.Depth)
                    _gl.FramebufferRenderbuffer(GlFramebuffer, GlDepthAttachment, GlRenderbuffer, depth);
                else
                    _gl.FramebufferRenderbuffer(GlFramebuffer, GlStencilAttachment, GlRenderbuffer, depth);
            }
            if (_gl.CheckFramebufferStatus(GlFramebuffer) != GlFramebufferComplete || _gl.GetError() != 0)
                throw new InvalidOperationException("ANGLE WebGL drawing buffer is incomplete.");
            if (Options.RequestedAttributes.Antialias)
            {
                _gl.GenFramebuffers(1, out multisampleFramebuffer);
                _gl.BindFramebuffer(GlFramebuffer, multisampleFramebuffer);
                _gl.GenRenderbuffers(1, out multisampleColor);
                _gl.BindRenderbuffer(GlRenderbuffer, multisampleColor);
                _gl.RenderbufferStorageMultisample(GlRenderbuffer, samples, Options.RequestedAttributes.Alpha ? GlRgba8 : GlRgb8, width, height);
                _gl.FramebufferRenderbuffer(GlFramebuffer, GlColorAttachment0, GlRenderbuffer, multisampleColor);
                if (Options.RequestedAttributes.Depth || Options.RequestedAttributes.Stencil)
                {
                    _gl.GenRenderbuffers(1, out multisampleDepth);
                    _gl.BindRenderbuffer(GlRenderbuffer, multisampleDepth);
                    var format = Options.RequestedAttributes.Depth && Options.RequestedAttributes.Stencil ? GlDepth24Stencil8 : Options.RequestedAttributes.Depth ? GlDepthComponent16 : GlStencilIndex8;
                    _gl.RenderbufferStorageMultisample(GlRenderbuffer, samples, format, width, height);
                    _gl.FramebufferRenderbuffer(GlFramebuffer, Options.RequestedAttributes.Depth && Options.RequestedAttributes.Stencil ? GlDepthStencilAttachment : Options.RequestedAttributes.Depth ? GlDepthAttachment : GlStencilAttachment, GlRenderbuffer, multisampleDepth);
                }
                if (_gl.CheckFramebufferStatus(GlFramebuffer) != GlFramebufferComplete || _gl.GetError() != 0) throw new InvalidOperationException("ANGLE multisample framebuffer is incomplete.");
                _gl.BindFramebuffer(GlFramebuffer, framebuffer);
            }
            _texture = texture; _framebuffer = framebuffer; _depthRenderbuffer = depth; _stencilRenderbuffer = stencil;
            _multisampleFramebuffer = multisampleFramebuffer; _multisampleColorRenderbuffer = multisampleColor; _multisampleDepthRenderbuffer = multisampleDepth;
            _gl.Viewport(0, 0, width, height);
            _colorReservation = colorReservation;
            _depthReservation = depthReservation;
            _stencilReservation = stencilReservation;
            _device.RegisterNativeWebGlContext(this);
            colorReservation = depthReservation = stencilReservation = null;
            return true;
        }
        catch (OverflowException) { failure = new(GpuFailureKind.BudgetExceeded, "The WebGL drawing buffer size overflowed."); return false; }
        catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
        finally
        {
            if (framebuffer != 0 && framebuffer != _framebuffer) _gl.DeleteFramebuffers(1, ref framebuffer);
            if (texture != 0 && texture != _texture) _gl.DeleteTextures(1, ref texture);
            if (depth != 0 && depth != _depthRenderbuffer) _gl.DeleteRenderbuffers(1, ref depth);
            if (stencil != 0 && stencil != _stencilRenderbuffer) _gl.DeleteRenderbuffers(1, ref stencil);
            if (multisampleFramebuffer != 0 && multisampleFramebuffer != _multisampleFramebuffer) _gl.DeleteFramebuffers(1, ref multisampleFramebuffer);
            if (multisampleColor != 0 && multisampleColor != _multisampleColorRenderbuffer) _gl.DeleteRenderbuffers(1, ref multisampleColor);
            if (multisampleDepth != 0 && multisampleDepth != _multisampleDepthRenderbuffer) _gl.DeleteRenderbuffers(1, ref multisampleDepth);
            colorReservation?.Dispose(); depthReservation?.Dispose(); stencilReservation?.Dispose();
            ReleaseCurrentLocked();
        }
    }

    public bool TryResize(int width, int height, out GpuFailure? failure)
    {
        lock (_gate)
        {
            failure = null;
            if (Volatile.Read(ref _disposed) != 0) { failure = new(GpuFailureKind.Unavailable, "The WebGL context is disposed."); return false; }
            if (width < 0 || height < 0) { failure = new(GpuFailureKind.NativeError, "The WebGL drawing buffer dimensions are invalid."); return false; }
            var oldTexture = _texture; var oldFramebuffer = _framebuffer; var oldDepth = _depthRenderbuffer; var oldStencil = _stencilRenderbuffer;
            var oldMsFramebuffer = _multisampleFramebuffer; var oldMsColor = _multisampleColorRenderbuffer; var oldMsDepth = _multisampleDepthRenderbuffer;
            var oldColor = _colorReservation; var oldDepthReservation = _depthReservation; var oldStencilReservation = _stencilReservation;
            _texture = _framebuffer = _depthRenderbuffer = _stencilRenderbuffer = 0;
            _multisampleFramebuffer = _multisampleColorRenderbuffer = _multisampleDepthRenderbuffer = 0;
            _depthReservation = _stencilReservation = null;
            if (!TryAllocateAttachmentsLocked(Math.Max(1, width), Math.Max(1, height), out failure))
            {
                _texture = oldTexture; _framebuffer = oldFramebuffer; _depthRenderbuffer = oldDepth; _stencilRenderbuffer = oldStencil;
                _multisampleFramebuffer = oldMsFramebuffer; _multisampleColorRenderbuffer = oldMsColor; _multisampleDepthRenderbuffer = oldMsDepth;
                _depthReservation = oldDepthReservation; _stencilReservation = oldStencilReservation;
                return false;
            }
            GpuFailure? deleteFailure = null;
            try { DeleteAttachmentsLocked(oldTexture, oldFramebuffer, oldDepth, oldStencil, oldMsFramebuffer, oldMsColor, oldMsDepth); }
            catch (Exception error) { deleteFailure = new(GpuFailureKind.NativeError, error.Message); }
            finally { oldColor?.Dispose(); oldDepthReservation?.Dispose(); oldStencilReservation?.Dispose(); }
            _width = width; _height = height;
            failure = deleteFailure;
            return deleteFailure is null;
        }
    }

    private void DeleteAttachmentsLocked(uint texture, uint framebuffer, uint depth, uint stencil, uint multisampleFramebuffer = 0, uint multisampleColor = 0, uint multisampleDepth = 0)
    {
        try { MakeCurrentLocked(); if (framebuffer != 0) _gl.DeleteFramebuffers(1, ref framebuffer); if (multisampleFramebuffer != 0) _gl.DeleteFramebuffers(1, ref multisampleFramebuffer); if (texture != 0) _gl.DeleteTextures(1, ref texture); if (depth != 0) _gl.DeleteRenderbuffers(1, ref depth); if (stencil != 0) _gl.DeleteRenderbuffers(1, ref stencil); if (multisampleColor != 0) _gl.DeleteRenderbuffers(1, ref multisampleColor); if (multisampleDepth != 0) _gl.DeleteRenderbuffers(1, ref multisampleDepth); } finally { ReleaseCurrentLocked(); }
    }

    private void MakeCurrentLocked() { if (_egl.MakeCurrent(_display, _surface, _surface, _context) == 0) throw new InvalidOperationException("ANGLE could not make the WebGL context current."); }
    private bool ResolveMultisampleLocked(out GpuFailure? failure)
    {
        failure = null;
        if (_multisampleFramebuffer == 0) return true;
        try
        {
            _gl.BindFramebuffer(GlReadFramebuffer, _multisampleFramebuffer);
            _gl.BindFramebuffer(GlDrawFramebuffer, _framebuffer);
            var mask = GlColorBufferBit;
            _gl.BlitFramebuffer(0, 0, _width, _height, 0, 0, _width, _height, mask, GlNearest);
            if (!CheckError(out failure)) return false;
            _gl.BindFramebuffer(GlFramebuffer, _framebuffer);
            return true;
        }
        catch (Exception error) { failure = new(GpuFailureKind.NativeError, error.Message); return false; }
    }
    private void ReleaseCurrentLocked() { if (_display != IntPtr.Zero) { try { _egl.MakeCurrent(_display, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); } catch { } } }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                MakeCurrentLocked();
                foreach (var program in _programs.ToArray()) { try { _gl.DeleteProgram(program); } catch { } }
                foreach (var shader in _shaders.ToArray()) { try { _gl.DeleteShader(shader); } catch { } }
                foreach (var buffer in _buffers.ToArray()) { try { var value = buffer; _gl.DeleteBuffers(1, ref value); } catch { } if (_bufferReservations.Remove(buffer, out var reservation)) reservation.Dispose(); }
                foreach (var texture in _textures.ToArray()) { try { var value = texture; _gl.DeleteTextures(1, ref value); } catch { } if (_textureReservations.Remove(texture, out var reservation)) reservation.Dispose(); }
                foreach (var framebuffer in _framebuffers.ToArray()) { try { var value = framebuffer; _gl.DeleteFramebuffers(1, ref value); } catch { } }
                foreach (var renderbuffer in _renderbuffers.ToArray()) { try { var value = renderbuffer; _gl.DeleteRenderbuffers(1, ref value); } catch { } }
                foreach (var vertexArray in _vertexArrays.ToArray()) { try { var value = vertexArray; _gl.DeleteVertexArrays(1, ref value); } catch { } }
                foreach (var feedback in _transformFeedbacks.ToArray()) { try { var value = feedback; _gl.DeleteTransformFeedbacks(1, ref value); } catch { } }
                _programs.Clear(); _shaders.Clear();
                foreach (var reservation in _renderbufferReservations.Values) { try { reservation.Dispose(); } catch { } }
                _buffers.Clear(); _textures.Clear(); _framebuffers.Clear(); _renderbuffers.Clear(); _textureSizes.Clear(); _textureParameters.Clear(); _textureTargets.Clear(); _textureLevels.Clear(); _textureLevelSizes.Clear(); _renderbufferReservations.Clear(); _renderbufferSizes.Clear(); _emulatedFloatRenderbuffers.Clear(); _vertexArrays.Clear(); _transformFeedbacks.Clear();
            }
            catch { }
            finally { ReleaseCurrentLocked(); }
            try { DeleteAttachmentsLocked(_texture, _framebuffer, _depthRenderbuffer, _stencilRenderbuffer, _multisampleFramebuffer, _multisampleColorRenderbuffer, _multisampleDepthRenderbuffer); } catch { }
            _texture = _framebuffer = _depthRenderbuffer = _stencilRenderbuffer = 0;
            _multisampleFramebuffer = _multisampleColorRenderbuffer = _multisampleDepthRenderbuffer = 0;
            _colorReservation?.Dispose(); _depthReservation?.Dispose(); _stencilReservation?.Dispose();
            _colorReservation = null; _depthReservation = _stencilReservation = null;
            try { if (_context != IntPtr.Zero) _egl.DestroyContext(_display, _context); } catch { }
            try { if (_surface != IntPtr.Zero) _egl.DestroySurface(_display, _surface); } catch { }
            _context = _surface = IntPtr.Zero;
            _device.UnregisterNativeWebGlContext(this);
        }
    }
}

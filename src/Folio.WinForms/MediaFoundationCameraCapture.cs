using System.Runtime.InteropServices;

namespace Folio.WinForms;

internal sealed class MediaFoundationCameraCapture : ICaptureDevice
{
    private readonly IMfCameraApi _api;
    internal MediaFoundationCameraCapture() : this(new MfCameraApi()) { }
    internal MediaFoundationCameraCapture(IMfCameraApi api) => _api = api;
    public void Open() => _api.Open();
    public void Pump(CancellationToken cancellation, Action<MediaCaptureVideoFrame> video, Action<MediaAudioFrame> audio)
    {
        while (!cancellation.IsCancellationRequested)
        {
            var frame = _api.Read(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (frame is { } value) video(value);
        }
    }
    public void Dispose() => _api.Dispose();

    // scanline0 is the displayed top row, exactly as IMF2DBuffer::Lock2D specifies.
    internal static MediaCaptureVideoFrame CopyRgb32(IntPtr scanline0, int width, int height, int stride)
    {
        ValidateSize(width, height);
        if (scanline0 == IntPtr.Zero || Math.Abs((long)stride) < (long)width * 4 || Math.Abs((long)stride) * height > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid RGB32 stride.");
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
            Marshal.Copy(IntPtr.Add(scanline0, checked(y * stride)), pixels, y * width * 4, width * 4);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            pixels[i + 3] = 255; // RGB32 is BGRX, not premultiplied BGRA.
        }
        return new MediaCaptureVideoFrame(width, height, pixels);
    }
    internal static void ValidateSize(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 8 * 1024 * 1024)
            throw new InvalidDataException("Camera dimensions exceed the capture budget.");
    }
}

internal interface IMfCameraApi : IDisposable
{
    void Open();
    MediaCaptureVideoFrame? Read(CancellationToken cancellation);
}

// ABI slots include inherited methods, as defined in Windows SDK mfobjects.h/mfidl.h/
// mfreadwrite.h. Raw interface pointers avoid RCW finalizers releasing on an arbitrary apartment.
// Every acquired pointer is released by the owning MTA worker; only the callback retains a
// sample reference across its bounded one-slot handoff. No native call is made by constructors.
internal sealed class MfCameraApi : IMfCameraApi
{
    private const uint VideoStream = 0xfffffffc;
    private IntPtr _source, _reader;
    private bool _com, _mf, _disposed, _readerFailed;
    private int _width, _height, _stride;
    private readonly CameraCallback _callback = new();
    private static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    private static readonly Guid VideoSource = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    private static readonly Guid MediaSource = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");
    private static readonly Guid AsyncCallback = new("1e3dbeac-bb43-4c35-b507-cd644464c965");
    private static readonly Guid VideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public void Open()
    {
        Check(CoInitializeEx(IntPtr.Zero, 0)); // COINIT_MULTITHREADED; S_FALSE also needs CoUninitialize.
        _com = true;
        Check(MFStartup(0x20070, 0));
        _mf = true;
        IntPtr attributes = IntPtr.Zero, devices = IntPtr.Zero, type = IntPtr.Zero;
        uint count = 0;
        try
        {
            Check(MFCreateAttributes(out attributes, 3));
            SetGuid(attributes, SourceType, VideoSource);
            Check(MFEnumDeviceSources(attributes, out devices, out count));
            if (count == 0) throw new IOException("No Windows camera is available.");
            var activate = Marshal.ReadIntPtr(devices);
            Check(Method<Activate>(activate, 33)(activate, MediaSource, out _source));
            // Reuse the store; source-type attributes are ignored by Source Reader.
            Check(Method<SetUnknown>(attributes, 27)(attributes, AsyncCallback, _callback));
            Check(Method<SetUInt32>(attributes, 21)(attributes, VideoProcessing, 1));
            Check(MFCreateSourceReaderFromMediaSource(_source, attributes, out _reader));
            Check(Method<SelectStream>(_reader, 4)(_reader, 0xfffffffe, false)); // ALL_STREAMS
            Check(Method<SelectStream>(_reader, 4)(_reader, VideoStream, true));
            Check(MFCreateMediaType(out type));
            SetGuid(type, MajorType, Video);
            SetGuid(type, Subtype, Rgb32);
            Check(Method<SetType>(_reader, 7)(_reader, VideoStream, IntPtr.Zero, type));
            ReadFormat();
        }
        finally
        {
            Release(type);
            if (devices != IntPtr.Zero)
            {
                for (uint i = 0; i < count; i++) Release(Marshal.ReadIntPtr(devices, checked((int)i * IntPtr.Size)));
                Marshal.FreeCoTaskMem(devices);
            }
            Release(attributes);
        }
    }

    private void ReadFormat()
    {
        Check(Method<GetCurrentTypeNative>(_reader, 6)(_reader, VideoStream, out var type));
        try
        {
            Check(Method<GetGuid>(type, 10)(type, Subtype, out var subtype));
            if (subtype != Rgb32) throw new InvalidDataException("Camera output is no longer RGB32.");
            Check(Method<GetUInt64>(type, 8)(type, FrameSize, out var size));
            _width = checked((int)(size >> 32)); _height = checked((int)(size & uint.MaxValue));
            MediaFoundationCameraCapture.ValidateSize(_width, _height);
            // A buffer exposing IMF2DBuffer supplies the actual stride. For a plain contiguous
            // buffer require the negotiated stride instead of guessing DIB orientation.
            _stride = Method<GetUInt32>(type, 7)(type, DefaultStride, out var stride) >= 0 ? unchecked((int)stride) : 0;
        }
        finally { Release(type); }
    }

    public MediaCaptureVideoFrame? Read(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        // Async ReadSample MUST receive NULL for all four output parameters.
        Check(Method<ReadAsync>(_reader, 9)(_reader, VideoStream, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        var result = _callback.Take(cancellation);
        try
        {
            _readerFailed = result.Status < 0 || (result.Flags & 1) != 0;
            Check(result.Status);
            if ((result.Flags & 3) != 0) throw new IOException("Camera removed or capture stream ended.");
            if ((result.Flags & 0x30) != 0) ReadFormat();
            if (result.Sample == IntPtr.Zero) return null; // stream tick, not end-of-stream
            cancellation.ThrowIfCancellationRequested();
            Check(Method<OutPointer>(result.Sample, 41)(result.Sample, out var buffer));
            try { return CopyBuffer(buffer); }
            finally { Release(buffer); }
        }
        finally { Release(result.Sample); }
    }

    private MediaCaptureVideoFrame CopyBuffer(IntPtr buffer)
    {
        var iid = new Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"); // IMF2DBuffer
        if (Marshal.QueryInterface(buffer, in iid, out var surface) >= 0)
        {
            try
            {
                Check(Method<Lock2D>(surface, 3)(surface, out var top, out var pitch));
                try { return MediaFoundationCameraCapture.CopyRgb32(top, _width, _height, pitch); }
                finally { Check(Method<NoArgs>(surface, 4)(surface)); }
            }
            finally { Release(surface); }
        }
        Check(Method<LockBuffer>(buffer, 3)(buffer, out var data, out var maximum, out var length));
        try
        {
            var required = Math.Abs((long)_stride) * _height;
            if (_stride == 0 || required > length || required > maximum || required > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid contiguous camera buffer.");
            var top = _stride < 0 ? IntPtr.Add(data, checked((_height - 1) * -_stride)) : data;
            return MediaFoundationCameraCapture.CopyRgb32(top, _width, _height, _stride);
        }
        finally { Check(Method<NoArgs>(buffer, 4)(buffer)); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Disable the callback before Flush/Shutdown: late native callbacks drop their borrowed
        // sample, never touch a disposed event and never deliver a frame after cancellation.
        _callback.Dispose();
        try
        {
            if (_reader != IntPtr.Zero && !_readerFailed) Method<StreamOnly>(_reader, 10)(_reader, VideoStream);
        }
        finally
        {
            try { if (_source != IntPtr.Zero) Method<NoArgs>(_source, 12)(_source); }
            finally
            {
                Release(_reader); _reader = IntPtr.Zero;
                Release(_source); _source = IntPtr.Zero;
                try { if (_mf) { MFShutdown(); _mf = false; } }
                finally { if (_com) { CoUninitialize(); _com = false; } }
            }
        }
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static void SetGuid(IntPtr p, Guid key, Guid value) => Check(Method<SetGuidValue>(p, 24)(p, key, value));
    private static void Release(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgs(IntPtr p);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Activate(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetGuidValue(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUnknown(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetUInt32(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt32(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetUInt64(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetGuid(IntPtr p, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SelectStream(IntPtr p, uint stream, [MarshalAs(UnmanagedType.Bool)] bool select);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetType(IntPtr p, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetCurrentTypeNative(IntPtr p, uint stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReadAsync(IntPtr p, uint stream, uint flags, IntPtr actual, IntPtr streamFlags, IntPtr time, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int StreamOnly(IntPtr p, uint stream);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int OutPointer(IntPtr p, out IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Lock2D(IntPtr p, out IntPtr top, out int stride);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int LockBuffer(IntPtr p, out IntPtr data, out uint maximum, out uint length);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("mfplat.dll")] private static extern int MFStartup(int version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateAttributes(out IntPtr attributes, uint count);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mf.dll")] private static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr devices, out uint count);
    [DllImport("mfreadwrite.dll")] private static extern int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, out IntPtr reader);

    internal readonly record struct ReadResult(int Status, uint Flags, IntPtr Sample);

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class CameraCallback : IWindowsCaptureSourceReaderCallback, IDisposable
    {
        private readonly object _gate = new();
        private readonly AutoResetEvent _ready = new(false);
        private ReadResult? _pending;
        private bool _closed;
        public int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample)
        {
            lock (_gate)
            {
                if (_closed) return 0;
                if (sample != IntPtr.Zero) Marshal.AddRef(sample);
                if (_pending is { } old) Release(old.Sample);
                _pending = new ReadResult(status, flags, sample);
                _ready.Set();
            }
            return 0;
        }
        public int OnFlush(uint stream) => 0;
        public int OnEvent(uint stream, IntPtr mediaEvent) => 0;
        internal ReadResult Take(CancellationToken cancellation)
        {
            var signal = WaitHandle.WaitAny([cancellation.WaitHandle, _ready], 5000);
            cancellation.ThrowIfCancellationRequested();
            if (signal == WaitHandle.WaitTimeout) throw new IOException("Camera stopped returning samples.");
            lock (_gate)
            {
                var result = _pending ?? throw new IOException("Camera callback was closed.");
                _pending = null;
                return result;
            }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
                if (_pending is { } result) Release(result.Sample);
                _pending = null;
                _ready.Dispose();
            }
        }
    }
}

// An explicit public COM interface lets the CCW expose IMFSourceReaderCallback while keeping
// the implementation internal. This is an ABI surface, not a host capture control interface.
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
[ComVisible(true), Guid("deec8d99-fa1d-4d82-84c2-2c8969944867"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsCaptureSourceReaderCallback
{
    [PreserveSig] int OnReadSample(int status, uint stream, uint flags, long timestamp, IntPtr sample);
    [PreserveSig] int OnFlush(uint stream);
    [PreserveSig] int OnEvent(uint stream, IntPtr mediaEvent);
}

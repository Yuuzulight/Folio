using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Folio.Hosting;
using Folio.Imaging;

namespace Folio.WinForms;

/// <summary>Decodes bounded audio and timestamped video frames through Windows Media Foundation.</summary>
/// <remarks>Video decoding is bounded by the caller's byte budget and returns a presentation-ordered frame sequence; playback pacing remains owned by the document clock.</remarks>
public sealed class MediaFoundationAudioDecoder : IMediaDecoder
{
    private const uint FirstAudioStream = 0xFFFFFFFD, FirstVideoStream = 0xFFFFFFFC;
    private const uint ErrorFlag = 1, EndOfStream = 2, CurrentTypeChanged = 0x20;
    private const int MaxOutputBytes = 64 * 1024 * 1024;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid VideoType = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatFormat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmFormat = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid Rgb32Format = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid SampleRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid BitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid FrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid VideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid DurationAttribute = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    public bool CanDecode(string? contentType, bool video)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var mime = (contentType ?? "").Trim().ToLowerInvariant().Split(';', 2)[0];
        return video
            ? mime is "video/mp4" or "video/webm" or "video/ogg"
            : mime is "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/mpeg" or "audio/mp3" or "audio/ogg";
    }

    public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
    {
        if (!OperatingSystem.IsWindows() || data.IsEmpty || maxOutputBytes <= 0 || data.Length > maxOutputBytes)
            return null;
        var path = Path.Combine(Path.GetTempPath(), $"folio-media-{Guid.NewGuid():N}.bin");
        var initialized = false;
        var started = false;
        IMFSourceReader? reader = null;
        IMFAttributes? attributes = null;
        IMFMediaType? output = null;
        try
        {
            var com = CoInitializeEx(IntPtr.Zero, 0);
            // A UI caller can already own an STA; use that apartment without uninitializing it.
            if (com != unchecked((int)0x80010106)) Check(com);
            initialized = com >= 0;
            Check(MFStartup(0x00020070, 0));
            started = true;
            File.WriteAllBytes(path, data.ToArray());
            if (video)
            {
                Check(MFCreateAttributes(out attributes, 1));
                Check(attributes.SetUINT32(VideoProcessing, 1));
            }
            Check(MFCreateSourceReaderFromURL(path, attributes, out reader));
            Check(MFCreateMediaType(out output));
            Check(output.SetGUID(MajorType, video ? VideoType : AudioType));
            Check(output.SetGUID(Subtype, video ? Rgb32Format : FloatFormat));
            var stream = video ? FirstVideoStream : FirstAudioStream;
            // Avoid buffering unused streams while decoding an audio/video container.
            Check(reader.SetStreamSelection(0xFFFFFFFE, false));
            Check(reader.SetStreamSelection(stream, true));
            var result = reader.SetCurrentMediaType(stream, IntPtr.Zero, output);
            if (result < 0 && !video)
            {
                Check(output.SetGUID(Subtype, PcmFormat));
                Check(output.SetUINT32(BitsPerSample, 16));
                result = reader.SetCurrentMediaType(stream, IntPtr.Zero, output);
            }
            Check(result);
            var timeFormat = Guid.Empty;
            var position = new PropVariant { Type = 20, Unsigned = 0 };
            Check(reader.SetCurrentPosition(ref timeFormat, ref position));
            Check(reader.GetCurrentMediaType(stream, out var current));
            try { return video ? ReadVideo(reader, current, maxOutputBytes) : ReadAudio(reader, current, maxOutputBytes); }
            finally { Marshal.FinalReleaseComObject(current); }
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or OverflowException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (output is not null) Marshal.FinalReleaseComObject(output);
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
            if (attributes is not null) Marshal.FinalReleaseComObject(attributes);
            if (started) MFShutdown();
            if (initialized) CoUninitialize();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static MediaDecodeResult? ReadAudio(IMFSourceReader reader, IMFMediaType type, int budget)
    {
        Check(type.GetUINT32(SampleRate, out var rate));
        Check(type.GetUINT32(Channels, out var channels));
        Check(type.GetUINT32(BitsPerSample, out var bits));
        Check(type.GetGUID(Subtype, out var subtype));
        var floating = subtype == FloatFormat && bits == 32;
        if (rate == 0 || rate > int.MaxValue || channels is 0 or > 32 || (!floating && (subtype != PcmFormat || bits != 16)))
            return null;
        var limit = Math.Min(budget, MaxOutputBytes) / sizeof(float);
        var samples = new List<float>(Math.Min(limit, 96000));
        var clock = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 1_000_000 && clock.Elapsed < TimeSpan.FromSeconds(10); iteration++)
        {
            Check(reader.ReadSample(FirstAudioStream, 0, out _, out var flags, out _, out var sample));
            try
            {
                if ((flags & (ErrorFlag | CurrentTypeChanged)) != 0) return null;
                if (sample is not null)
                {
                    Check(sample.ConvertToContiguousBuffer(out var buffer));
                    try
                    {
                        Check(buffer.Lock(out var pointer, out _, out var length));
                        try
                        {
                            var sampleBytes = floating ? sizeof(float) : sizeof(short);
                            if (length < 0 || length % (sampleBytes * channels) != 0) return null;
                            var count = length / sampleBytes;
                            // A bounded decoder must reject, not report a truncated stream as complete.
                            if (count > limit - samples.Count) return null;
                            if (floating)
                            {
                                var chunk = new float[count];
                                Marshal.Copy(pointer, chunk, 0, count);
                                samples.AddRange(chunk);
                            }
                            else
                            {
                                var chunk = new short[count];
                                Marshal.Copy(pointer, chunk, 0, count);
                                foreach (var value in chunk) samples.Add(value / 32768f);
                            }
                        }
                        finally { Check(buffer.Unlock()); }
                    }
                    finally { Marshal.FinalReleaseComObject(buffer); }
                }
                if ((flags & EndOfStream) != 0)
                    return samples.Count == 0 ? null : new MediaDecodeResult(samples.Count / ((double)rate * channels), null,
                        new MediaAudioFrame((int)rate, (int)channels, samples.ToArray()));
            }
            finally { if (sample is not null) Marshal.FinalReleaseComObject(sample); }
        }
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static MediaDecodeResult? ReadVideo(IMFSourceReader reader, IMFMediaType type, int budget)
    {
        Check(type.GetUINT64(FrameSize, out var size));
        var width = checked((int)(size >> 32));
        var height = checked((int)(size & uint.MaxValue));
        var bytes = (long)width * height * 4;
        if (width <= 0 || height <= 0 || bytes > Math.Min(budget, MaxOutputBytes)) return null;
        var stride = type.GetUINT32(DefaultStride, out var storedStride) >= 0 ? unchecked((int)storedStride) : checked(width * 4);
        if (Math.Abs((long)stride) < (long)width * 4) return null;
        var frameLimit = Math.Min(240L, Math.Min(budget, MaxOutputBytes) / bytes);
        if (frameLimit <= 0) return null;
        var frames = new List<MediaVideoFrame>((int)frameLimit);
        double duration = 0;
        if (reader.GetPresentationAttribute(0xFFFFFFFF, DurationAttribute, out var presentation) >= 0)
        {
            try { if (presentation.Type == 21) duration = presentation.Unsigned / 10_000_000d; }
            finally { PropVariantClear(ref presentation); }
        }
        var clock = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 10000 && clock.Elapsed < TimeSpan.FromSeconds(10); iteration++)
        {
            Check(reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample));
            try
            {
                if ((flags & (ErrorFlag | CurrentTypeChanged)) != 0) return null;
                if (sample is not null)
                {
                    Check(sample.ConvertToContiguousBuffer(out var buffer));
                    try
                    {
                        Check(buffer.Lock(out var pointer, out _, out var length));
                        try
                        {
                            if (length < Math.Abs((long)stride) * (height - 1) + (long)width * 4) return null;
                            var pixels = new byte[(int)bytes];
                            var row = new byte[checked(width * 4)];
                            for (var y = 0; y < height; y++)
                            {
                                var offset = stride < 0 ? checked((height - 1 - y) * -stride) : checked(y * stride);
                                Marshal.Copy(IntPtr.Add(pointer, offset), row, 0, row.Length);
                                for (var x = 0; x < row.Length; x += 4)
                                {
                                    var target = y * row.Length + x;
                                    pixels[target] = row[x + 2]; pixels[target + 1] = row[x + 1];
                                    pixels[target + 2] = row[x]; pixels[target + 3] = 255;
                                }
                            }
                            var image = new NativeImage(width, height, pixels);
                            frames.Add(new MediaVideoFrame(Math.Max(0, timestamp) / 10_000_000d, image));
                            if (frames.Count >= frameLimit)
                                return new MediaDecodeResult(duration, frames[0].Frame, null) { VideoFrames = frames };
                        }
                        finally { Check(buffer.Unlock()); }
                    }
                    finally { Marshal.FinalReleaseComObject(buffer); }
                }
                if ((flags & EndOfStream) != 0)
                    return frames.Count == 0 ? null : new MediaDecodeResult(duration, frames[0].Frame, null) { VideoFrames = frames };
            }
            finally { if (sample is not null) Marshal.FinalReleaseComObject(sample); }
        }
        return frames.Count == 0 ? null : new MediaDecodeResult(duration, frames[0].Frame, null) { VideoFrames = frames };
    }

    private static void Check(int result) { if (result < 0) throw new COMException($"Media Foundation failed (0x{result:X8}).", result); }
    private sealed record NativeImage(int Width, int Height, byte[] Data) : IImageHandle
    {
        public ReadOnlyMemory<byte> Pixels => Data;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public ulong Unsigned;
    }

    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType mediaType);
        [PreserveSig] int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
        [PreserveSig] int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
        [PreserveSig] int SetCurrentPosition(ref Guid format, ref PropVariant position);
        [PreserveSig] int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint flags, out long timestamp, [MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid guidService, ref Guid riid, out IntPtr service);
        [PreserveSig] int GetPresentationAttribute(uint streamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid attribute, out PropVariant value);
    }

    [ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFAttributes
    {
        [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int type);
        [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IMFMediaType other, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
        [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out double value);
        [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint length);
        [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, uint size, out uint actual);
        [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint size);
        [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr buffer, uint size, out uint actual);
        [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
        [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, double value);
        [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, Guid value);
        [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int type);
        [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IMFMediaType other, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
        [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out double value);
        [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint length);
        [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, uint size, out uint actual);
        [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint size);
        [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr buffer, uint size, out uint actual);
        [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
        [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, double value);
        [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, Guid value);
        [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        [PreserveSig] int GetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        [PreserveSig] int GetItemType([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int type);
        [PreserveSig] int CompareItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int Compare(IMFMediaType other, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint value);
        [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value);
        [PreserveSig] int GetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out double value);
        [PreserveSig] int GetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out Guid value);
        [PreserveSig] int GetStringLength([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint length);
        [PreserveSig] int GetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value, uint size, out uint actual);
        [PreserveSig] int GetAllocatedString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr value, out uint length);
        [PreserveSig] int GetBlobSize([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out uint size);
        [PreserveSig] int GetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr buffer, uint size, out uint actual);
        [PreserveSig] int GetAllocatedBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out IntPtr buffer, out uint size);
        [PreserveSig] int GetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
        [PreserveSig] int SetItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        [PreserveSig] int DeleteItem([In, MarshalAs(UnmanagedType.LPStruct)] Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, uint value);
        [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
        [PreserveSig] int SetDouble([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, double value);
        [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, Guid value);
        [PreserveSig] int SetString([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr buffer, uint size);
        [PreserveSig] int SetUnknown([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
        [PreserveSig] int CopyAllItems(IntPtr destination);
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int currentLength);
        [PreserveSig] int SetCurrentLength(int currentLength);
        [PreserveSig] int GetMaxLength(out int maxLength);
    }

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes? attributes, out IMFSourceReader sourceReader);
    [DllImport("mfplat.dll")]
    private static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);
    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();
}

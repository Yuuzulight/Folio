using System.Runtime.InteropServices;
using Folio.Hosting;
using Folio.Imaging;

namespace Folio.WinForms;

/// <summary>Decodes bounded audio and first-frame video samples through Media Foundation.</summary>
/// <remarks>
/// Video decoding returns the first frame as Folio's RGBA image handle; playback remains bounded by the
/// media resource limit and the host can use another decoder for streaming or unsupported formats.
/// </remarks>
public sealed class MediaFoundationAudioDecoder : IMediaDecoder
{
    private const uint FirstAudioStream = 0xFFFFFFFD;
    private const uint FirstVideoStream = 0xFFFFFFFC;
    private const uint SourceReaderfEndOfStream = 0x00000001;
    private const int MfVersion = 0x00020070;
    private const int MfStartupLite = 1;
    private const int MaxSampleBytes = 64 * 1024 * 1024;

    private static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfAudioFormatPcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfVideoFormatArgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e7");
    private static readonly Guid MfMtFrameSize = new("1652C33D-D6B2-4012-B834-72030849A37D");
    private static int _startupCount;

    public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
    {
        if (data.IsEmpty || maxOutputBytes <= 0 || !OperatingSystem.IsWindows())
            return null;

        var outputLimit = Math.Min(Math.Min(maxOutputBytes, MaxSampleBytes), 64 * 1024 * 1024);
        var path = Path.Combine(Path.GetTempPath(), $"folio-media-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, data.ToArray());
        try
        {
            return video ? DecodeVideoFile(path, outputLimit) : DecodeAudioFile(path, outputLimit);
        }
        catch (COMException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static MediaDecodeResult? DecodeVideoFile(string path, int outputLimit)
    {
        Startup();
        IMFSourceReader? reader = null;
        IMFMediaType? mediaType = null;
        try
        {
            HResult.ThrowIfFailed(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader));
            HResult.ThrowIfFailed(MFCreateMediaType(out mediaType));
            HResult.ThrowIfFailed(mediaType.SetGUID(MfMtMajorType, new Guid("73646976-0000-0010-8000-00AA00389B71")));
            HResult.ThrowIfFailed(mediaType.SetGUID(MfMtSubtype, MfVideoFormatArgb32));
            HResult.ThrowIfFailed(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, mediaType));
            HResult.ThrowIfFailed(reader.GetCurrentMediaType(FirstVideoStream, out var currentType));
            try
            {
                HResult.ThrowIfFailed(currentType.GetUINT64(MfMtFrameSize, out var packedSize));
                var width = checked((int)(packedSize >> 32));
                var height = checked((int)(packedSize & uint.MaxValue));
                var byteCount = checked(width * height * 4);
                if (width <= 0 || height <= 0 || byteCount > outputLimit || byteCount > int.MaxValue)
                    return null;

                HResult.ThrowIfFailed(reader.ReadSample(FirstVideoStream, 0, out _, out var flags, out var timestamp, out var sample));
                if (sample is null)
                    return null;
                try
                {
                    HResult.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer));
                    try
                    {
                        HResult.ThrowIfFailed(buffer.Lock(out var pointer, out _, out var length));
                        try
                        {
                            if (length < byteCount)
                                return null;
                            var bgra = new byte[byteCount];
                            Marshal.Copy(pointer, bgra, 0, bgra.Length);
                            var rgba = new byte[bgra.Length];
                            for (var i = 0; i < bgra.Length; i += 4)
                            {
                                rgba[i] = bgra[i + 2];
                                rgba[i + 1] = bgra[i + 1];
                                rgba[i + 2] = bgra[i];
                                rgba[i + 3] = bgra[i + 3];
                            }
                            return new MediaDecodeResult(Math.Max(0.1, timestamp / 10_000_000d), new NativeImage(width, height, rgba), null);
                        }
                        finally { buffer.Unlock(); }
                    }
                    finally { Marshal.FinalReleaseComObject(buffer); }
                }
                finally { Marshal.FinalReleaseComObject(sample); }
            }
            finally { Marshal.FinalReleaseComObject(currentType); }
        }
        finally
        {
            if (mediaType is not null) Marshal.FinalReleaseComObject(mediaType);
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
            Shutdown();
        }
    }

    private static MediaDecodeResult? DecodeAudioFile(string path, int outputLimit)
    {
        Startup();
        IMFSourceReader? reader = null;
        IMFMediaType? mediaType = null;
        var samples = new List<float>(Math.Min(outputLimit / sizeof(float), 48000 * 2));
        try
        {
            HResult.ThrowIfFailed(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader));
            HResult.ThrowIfFailed(MFCreateMediaType(out mediaType));
            HResult.ThrowIfFailed(mediaType.SetGUID(MfMtMajorType, MfMediaTypeAudio));
            HResult.ThrowIfFailed(mediaType.SetGUID(MfMtSubtype, MfAudioFormatFloat));
            var result = reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, mediaType);
            var floatOutput = result >= 0;
            if (result < 0)
            {
                HResult.ThrowIfFailed(mediaType.SetGUID(MfMtSubtype, MfAudioFormatPcm));
                HResult.ThrowIfFailed(reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, mediaType));
            }

            var sampleRate = 48000;
            var channels = 2;
            long lastTimestamp = 0;
            while (samples.Count * sizeof(float) < outputLimit)
            {
                HResult.ThrowIfFailed(reader.ReadSample(FirstAudioStream, 0, out _, out var flags, out var timestamp, out var sample));
                lastTimestamp = timestamp;
                if (sample is not null)
                {
                    HResult.ThrowIfFailed(sample.ConvertToContiguousBuffer(out var buffer));
                    try
                    {
                        HResult.ThrowIfFailed(buffer.Lock(out var pointer, out _, out var length));
                        try
                        {
                            var count = floatOutput
                                ? Math.Min(length / sizeof(float), (outputLimit / sizeof(float)) - samples.Count)
                                : Math.Min(length / sizeof(short), (outputLimit / sizeof(float)) - samples.Count);
                            if (count > 0)
                            {
                                var raw = new byte[count * (floatOutput ? sizeof(float) : sizeof(short))];
                                Marshal.Copy(pointer, raw, 0, raw.Length);
                                if (floatOutput)
                                    samples.AddRange(MemoryMarshal.Cast<byte, float>(raw.AsSpan()).ToArray());
                                else
                                {
                                    foreach (var value in MemoryMarshal.Cast<byte, short>(raw.AsSpan()))
                                        samples.Add(value / (float)short.MaxValue);
                                }
                            }
                        }
                        finally { buffer.Unlock(); }
                    }
                    finally { Marshal.FinalReleaseComObject(buffer); }
                    Marshal.FinalReleaseComObject(sample);
                }
                if ((flags & SourceReaderfEndOfStream) != 0)
                    break;
            }

            if (samples.Count == 0)
                return null;
            return new MediaDecodeResult(Math.Max(lastTimestamp / 10_000_000d, samples.Count / (double)(sampleRate * channels)), null, new MediaAudioFrame(sampleRate, channels, samples.ToArray()));
        }
        finally
        {
            if (mediaType is not null) Marshal.FinalReleaseComObject(mediaType);
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
            Shutdown();
        }
    }

    private static void Startup()
    {
        if (Interlocked.Increment(ref _startupCount) == 1)
            HResult.ThrowIfFailed(MFStartup(MfVersion, MfStartupLite));
    }

    private static void Shutdown()
    {
        if (Interlocked.Decrement(ref _startupCount) == 0)
            MFShutdown();
    }

    private static class HResult
    {
        public static void ThrowIfFailed(int result)
        {
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
        }
    }

    private sealed record NativeImage(int Width, int Height, byte[] Data) : IImageHandle
    {
        ReadOnlyMemory<byte> IImageHandle.Pixels => Data;
    }

    [ComImport, Guid("70AE66F2-C809-4E4F-8915-BDCB406B7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        int SetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
        int GetNativeMediaType(uint streamIndex, uint typeIndex, out IMFMediaType mediaType);
        int GetCurrentMediaType(uint streamIndex, out IMFMediaType mediaType);
        int SetCurrentMediaType(uint streamIndex, IntPtr reserved, IMFMediaType mediaType);
        int SetCurrentPosition(ref Guid format, long position);
        int ReadSample(uint streamIndex, uint controlFlags, out uint actualStreamIndex, out uint flags, out long timestamp, [MarshalAs(UnmanagedType.Interface)] out IMFSample? sample);
        int Flush(uint streamIndex);
        int GetServiceForStream(uint streamIndex, ref Guid guidService, ref Guid riid, out IntPtr service);
        int GetPresentationAttribute(uint streamIndex, ref Guid attribute, IntPtr value);
    }

    [ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        int GetItem(ref Guid key, IntPtr value);
        int GetItemType(ref Guid key, out int type);
        int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        int Compare(IMFMediaType other, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        int GetUINT32(ref Guid key, out uint value);
        int GetUINT64(ref Guid key, out ulong value);
        int GetDouble(ref Guid key, out double value);
        int GetGUID(ref Guid key, out Guid value);
        int GetStringLength(ref Guid key, out uint length);
        int GetString(ref Guid key, IntPtr value, uint size, out uint actual);
        int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        int GetBlobSize(ref Guid key, out uint size);
        int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint actual);
        int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        int GetUnknown(ref Guid key, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
        int SetItem(ref Guid key, IntPtr value);
        int DeleteItem(ref Guid key);
        int DeleteAllItems();
        int SetUINT32(ref Guid key, uint value);
        int SetUINT64(ref Guid key, ulong value);
        int SetDouble(ref Guid key, double value);
        int SetGUID(ref Guid key, Guid value);
        int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    }

    [ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        int GetItem(ref Guid key, IntPtr value);
        int GetItemType(ref Guid key, out int type);
        int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
        int Compare(IMFMediaType other, uint matchType, [MarshalAs(UnmanagedType.Bool)] out bool result);
        int GetUINT32(ref Guid key, out uint value);
        int GetUINT64(ref Guid key, out ulong value);
        int GetDouble(ref Guid key, out double value);
        int GetGUID(ref Guid key, out Guid value);
        int GetStringLength(ref Guid key, out uint length);
        int GetString(ref Guid key, IntPtr value, uint size, out uint actual);
        int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
        int GetBlobSize(ref Guid key, out uint size);
        int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint actual);
        int GetAllocatedBlob(ref Guid key, out IntPtr buffer, out uint size);
        int GetUnknown(ref Guid key, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object value);
        int SetItem(ref Guid key, IntPtr value);
        int DeleteItem(ref Guid key);
        int DeleteAllItems();
        int SetUINT32(ref Guid key, uint value);
        int SetUINT64(ref Guid key, ulong value);
        int SetDouble(ref Guid key, double value);
        int SetGUID(ref Guid key, Guid value);
        int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        int GetSampleFlags(out uint flags);
        int SetSampleFlags(uint flags);
        int GetSampleTime(out long time);
        int SetSampleTime(long time);
        int GetSampleDuration(out long duration);
        int SetSampleDuration(long duration);
        int GetBufferCount(out uint count);
        int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        int Unlock();
        int GetCurrentLength(out int currentLength);
        int SetCurrentLength(int currentLength);
        int GetMaxLength(out int maxLength);
    }

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader sourceReader);
    [DllImport("mfplat.dll")]
    private static extern int MFCreateMediaType(out IMFMediaType mediaType);
    [DllImport("mfplat.dll")]
    private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")]
    private static extern int MFShutdown();
}

using System.Buffers.Binary;
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
    private const uint AnyStream = 0xFFFFFFFE;
    private const int InvalidStreamNumber = unchecked((int)0xC00D36B3);
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

    // MIME-only hints cannot certify codecs for a particular source. Avoid claiming optional
    // WebM/Ogg extensions; actual source/type negotiation remains authoritative in Decode.
    public bool CanDecode(string? contentType, bool video)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var mime = (contentType ?? "").Trim().ToLowerInvariant().Split(';', 2)[0];
        return video
            ? mime is "video/mp4"
            : mime is "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/mpeg" or "audio/mp3";
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
        try
        {
            var com = CoInitializeEx(IntPtr.Zero, 0);
            // A UI caller can already own an STA; use that apartment without uninitializing it.
            if (com != unchecked((int)0x80010106)) Check(com);
            initialized = com >= 0;
            Check(MFStartup(0x00020070, 0));
            started = true;
            var source = data.ToArray();
            var presentation = ReadMp4Presentation(source, video);
            File.WriteAllBytes(path, source);
            if (video)
            {
                Check(MFCreateAttributes(out attributes, 1));
                Check(attributes.SetUINT32(VideoProcessing, 1));
            }
            Check(MFCreateSourceReaderFromURL(path, attributes, out reader));
            // Discover actual indices so ANY_STREAM results can be attributed to their owner.
            uint? audioStream = null, videoStream = null;
            var enumerated = false;
            for (uint index = 0; index < 128; index++)
            {
                var status = reader.GetNativeMediaType(index, 0, out var native);
                if (status == InvalidStreamNumber) { enumerated = true; break; }
                Check(status);
                try
                {
                    Check(native.GetGUID(MajorType, out var major));
                    if (major == AudioType) audioStream ??= index;
                    if (major == VideoType) videoStream ??= index;
                }
                finally { Marshal.FinalReleaseComObject(native); }
            }
            if (!enumerated || (video ? videoStream is null : audioStream is null)) return null;
            if (!video) videoStream = null;
            Check(reader.SetStreamSelection(AnyStream, false));
            if (videoStream is uint vi) ConfigureStream(reader, vi, true);
            if (audioStream is uint ai) ConfigureStream(reader, ai, false);
            var timeFormat = Guid.Empty;
            var position = new PropVariant { Type = 20, Unsigned = 0 };
            Check(reader.SetCurrentPosition(ref timeFormat, ref position));
            return ReadStreams(reader, videoStream, audioStream, Math.Min(maxOutputBytes, MaxOutputBytes), presentation);
        }
        catch (Exception error) when (error is COMException or IOException or InvalidDataException or UnauthorizedAccessException or OverflowException or DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (reader is not null) Marshal.FinalReleaseComObject(reader);
            if (attributes is not null) Marshal.FinalReleaseComObject(attributes);
            if (started) MFShutdown();
            if (initialized) CoUninitialize();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ConfigureStream(IMFSourceReader reader, uint stream, bool video)
    {
        Check(reader.SetStreamSelection(stream, true));
        Check(MFCreateMediaType(out var output));
        try
        {
            Check(output.SetGUID(MajorType, video ? VideoType : AudioType));
            Check(output.SetGUID(Subtype, video ? Rgb32Format : FloatFormat));
            if (!video)
            {
                Check(reader.GetNativeMediaType(stream, 0, out var native));
                try
                {
                    Check(native.GetUINT32(Channels, out var channels));
                    // Never return a format that the output adapter cannot accept. Ask MF to
                    // negotiate/downmix to stereo; unsupported conversions fail the entire decode.
                    Check(output.SetUINT32(Channels, channels == 1 ? 1u : 2u));
                }
                finally { Marshal.FinalReleaseComObject(native); }
            }
            var status = reader.SetCurrentMediaType(stream, IntPtr.Zero, output);
            if (status < 0 && !video)
            {
                Check(output.SetGUID(Subtype, PcmFormat));
                Check(output.SetUINT32(BitsPerSample, 16));
                status = reader.SetCurrentMediaType(stream, IntPtr.Zero, output);
            }
            Check(status);
        }
        finally { Marshal.FinalReleaseComObject(output); }
    }

    [SupportedOSPlatform("windows")]
    private static MediaDecodeResult? ReadStreams(IMFSourceReader reader, uint? videoStream, uint? audioStream, int budget, Mp4Presentation? presentation)
    {
        int width = 0, height = 0, stride = 0, frameBytes = 0, rate = 0, channels = 0, sampleBytes = 0;
        if (videoStream is uint vi)
        {
            Check(reader.GetCurrentMediaType(vi, out var type));
            try
            {
                Check(type.GetUINT64(FrameSize, out var size));
                width = checked((int)(size >> 32)); height = checked((int)(size & uint.MaxValue));
                var bytes = checked((long)width * height * 4);
                if (width <= 0 || height <= 0 || bytes > budget) return null;
                frameBytes = checked((int)bytes);
                stride = type.GetUINT32(DefaultStride, out var stored) >= 0 ? unchecked((int)stored) : checked(width * 4);
                if (Math.Abs((long)stride) < (long)width * 4) return null;
            }
            finally { Marshal.FinalReleaseComObject(type); }
        }
        if (audioStream is uint ai)
        {
            Check(reader.GetCurrentMediaType(ai, out var type));
            try
            {
                Check(type.GetUINT32(SampleRate, out var r)); Check(type.GetUINT32(Channels, out var c));
                Check(type.GetUINT32(BitsPerSample, out var bits)); Check(type.GetGUID(Subtype, out var subtype));
                if (r is 0 or > 384000 || c is 0 or > 2) return null;
                sampleBytes = subtype == FloatFormat && bits == 32 ? 4 : subtype == PcmFormat && bits == 16 ? 2 : 0;
                if (sampleBytes == 0) return null;
                rate = (int)r; channels = (int)c;
            }
            finally { Marshal.FinalReleaseComObject(type); }
        }
        var frames = new List<MediaVideoFrame>();
        var chunks = new List<AudioChunk>();
        var videoEnded = videoStream is null; var audioEnded = audioStream is null;
        var retainedBytes = 0;
        long? origin = videoStream is null ? 0 : null;
        double duration = 0, videoEnd = 0;
        if (reader.GetPresentationAttribute(0xFFFFFFFF, DurationAttribute, out var durationValue) >= 0)
        {
            try { if (durationValue.Type == 21) duration = durationValue.Unsigned / 10_000_000d; }
            finally { PropVariantClear(ref durationValue); }
        }
        if (presentation is not null) duration = presentation.Duration / 10_000_000d;
        var clock = Stopwatch.StartNew();
        for (var iteration = 0; iteration < 100_000 && clock.Elapsed < TimeSpan.FromSeconds(10); iteration++)
        {
            var readStatus = reader.ReadSample(AnyStream, 0, out var actual, out var flags, out var timestamp, out var sample);
            try
            {
                Check(readStatus);
                // A new stream or a format change invalidates our fixed format/budget assumptions.
                if ((flags & (ErrorFlag | CurrentTypeChanged | 0x4 | 0x10 | 0x200)) != 0) return null;
                var isVideo = actual == videoStream;
                if ((!isVideo && actual != audioStream) || (isVideo ? videoEnded : audioEnded)) return null;
                if (sample is not null)
                {
                    var edit = isVideo ? presentation?.Video : presentation?.Audio;
                    timestamp = checked(timestamp + (edit?.Offset ?? 0));
                    // Consume edited-out video through EOS, but do not expose it as presentation frames.
                    var editedOut = isVideo && edit is not null && (timestamp < edit.Start || timestamp >= edit.End);
                    if (!editedOut)
                    {
                        if (isVideo && (frames.Count >= 240 || frameBytes > budget - retainedBytes)) return null;
                        Check(sample.ConvertToContiguousBuffer(out var buffer));
                        try
                        {
                            Check(buffer.Lock(out var pointer, out _, out var length));
                            try
                            {
                                if (isVideo)
                                {
                                    if (length < Math.Abs((long)stride) * (height - 1) + (long)width * 4) return null;
                                    var pixels = new byte[frameBytes]; var row = new byte[checked(width * 4)];
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
                                    origin ??= timestamp;
                                    var time = checked(timestamp - origin.Value) / 10_000_000d;
                                    if (time < 0 || frames.Count > 0 && time < frames[^1].Time) return null;
                                    frames.Add(new MediaVideoFrame(time, new NativeImage(width, height, pixels)));
                                    var sampleDuration = sample.GetSampleDuration(out var ticks) >= 0 && ticks > 0 ? ticks / 10_000_000d : 0;
                                    videoEnd = Math.Max(videoEnd, time + sampleDuration);
                                    retainedBytes += frameBytes;
                                }
                                else
                                {
                                    if (length < 0 || length % (sampleBytes * channels) != 0) return null;
                                    var count = length / sampleBytes;
                                    if (count > (budget - retainedBytes) / sizeof(float)) return null;
                                    var values = new float[count];
                                    if (sampleBytes == 4) Marshal.Copy(pointer, values, 0, count);
                                    else
                                    {
                                        var pcm = new short[count]; Marshal.Copy(pointer, pcm, 0, count);
                                        for (var i = 0; i < count; i++) values[i] = pcm[i] / 32768f;
                                    }
                                    // Charge raw decoded packets until normalization; rejecting a tight
                                    // budget conservatively is preferable to retaining unbounded preroll.
                                    chunks.Add(new AudioChunk(timestamp, values));
                                    retainedBytes += count * sizeof(float);
                                }
                            }
                            finally { Check(buffer.Unlock()); }
                        }
                        finally { Marshal.FinalReleaseComObject(buffer); }
                    }
                }
                if ((flags & EndOfStream) != 0)
                {
                    if (isVideo) videoEnded = true; else audioEnded = true;
                    // Stop asking ANY_STREAM for an ended stream while draining its sibling.
                    Check(reader.SetStreamSelection(actual, false));
                }
                if (videoEnded && audioEnded)
                {
                    if (clock.Elapsed >= TimeSpan.FromSeconds(10) || videoStream is not null && frames.Count == 0) return null;
                    MediaAudioFrame? audio = null;
                    if (audioStream is not null)
                    {
                        var values = BuildAudioTimeline(chunks, origin ?? 0, rate, channels, budget - frames.Count * frameBytes,
                            presentation?.Audio?.Start, presentation?.Audio?.End);
                        if (values is null || values.Length == 0) return null;
                        if (clock.Elapsed >= TimeSpan.FromSeconds(10)) return null;
                        audio = new MediaAudioFrame(rate, channels, values);
                        duration = Math.Max(duration - (origin ?? 0) / 10_000_000d, values.Length / ((double)rate * channels));
                    }
                    else duration -= (origin ?? 0) / 10_000_000d;
                    return new MediaDecodeResult(Math.Max(duration, videoEnd), frames.Count == 0 ? null : frames[0].Frame, audio)
                        { VideoFrames = videoStream is null ? null : frames };
                }
            }
            finally { if (sample is not null) Marshal.FinalReleaseComObject(sample); }
        }
        return null; // Only EOS from every selected stream constitutes success, never a limit/watchdog.
    }

    internal sealed record AudioChunk(long Timestamp, float[] Samples);

    // Positions are on the movie presentation timeline, not the MP4 track's media timeline.
    internal static float[]? BuildAudioTimeline(IReadOnlyList<AudioChunk> chunks, long origin, int rate, int channels, int budget,
        long? presentationStart = null, long? presentationEnd = null)
    {
        if (rate <= 0 || channels is < 1 or > 2 || budget < 0) return null;
        var limit = budget / sizeof(float) / channels;
        long Position(long time) => checked((long)Math.Round(checked(time - origin) * (rate / 10_000_000d)));
        var first = Math.Max(0, presentationStart is long begin ? Position(begin) : 0);
        var last = presentationEnd is long finish ? Position(finish) : long.MaxValue;
        long end = 0, previous = long.MinValue;
        foreach (var chunk in chunks)
        {
            if (chunk.Samples.Length % channels != 0 || chunk.Timestamp < previous) return null;
            previous = chunk.Timestamp;
            if (chunk.Samples.Length == 0) continue;
            var start = Position(chunk.Timestamp);
            var stop = Math.Min(last, checked(start + chunk.Samples.Length / channels));
            if (stop <= Math.Max(first, start)) continue;
            end = Math.Max(end, stop);
            if (end > limit) return null;
        }
        var result = new float[checked((int)end * channels)];
        long written = first;
        foreach (var chunk in chunks)
        {
            if (chunk.Samples.Length == 0) continue;
            var start = Position(chunk.Timestamp);
            var target = Math.Max(written, start);
            var stop = Math.Min(last, checked(start + chunk.Samples.Length / channels));
            if (stop <= target) continue;
            Array.Copy(chunk.Samples, checked((int)(target - start) * channels), result,
                checked((int)target * channels), checked((int)(stop - target) * channels));
            written = stop;
        }
        return result;
    }

    internal sealed record Mp4Edit(long Offset, long Start, long End);
    internal sealed record Mp4Presentation(long Duration, Mp4Edit? Audio, Mp4Edit? Video);
    private readonly record struct Mp4Box(int Offset, int Payload, int End, uint Type);

    // MF's MP4 source exposes track media timestamps on this Windows implementation, ignoring
    // the initial empty edit. Parse a bounded, unambiguous subset of ISO BMFF edit lists. Hide
    // those edits from MF in our private copy so another OS version cannot apply them twice.
    // Repeated edits/dwell/rate changes and ambiguous same-kind tracks fail closed.
    internal static Mp4Presentation? ReadMp4Presentation(byte[] source, bool video)
    {
        if (source.Length < 8) return null;
        var firstBox = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(4));
        if (firstBox is not (0x66747970 or 0x66726565 or 0x77696465 or 0x6d6f6f76 or 0x6d646174)) return null;
        var top = Mp4Boxes(source, 0, source.Length);
        var moov = RequiredMp4Box(top, 0x6d6f6f76); // moov
        var movie = Mp4Boxes(source, moov.Payload, moov.End);
        var mvhd = RequiredMp4Box(movie, 0x6d766864);
        var (movieScale, movieDuration) = Mp4Clock(source, mvhd);
        Mp4Edit? audioEdit = null, videoEdit = null;
        var audioCount = 0; var videoCount = 0; var trackCount = 0;
        foreach (var track in movie.Where(box => box.Type == 0x7472616b))
        {
            if (++trackCount > 128) throw new InvalidDataException("Too many MP4 tracks.");
            var children = Mp4Boxes(source, track.Payload, track.End);
            var mdia = RequiredMp4Box(children, 0x6d646961);
            var media = Mp4Boxes(source, mdia.Payload, mdia.End);
            var handler = RequiredMp4Box(media, 0x68646c72);
            if (handler.End - handler.Payload < 12) throw new InvalidDataException("Truncated MP4 handler.");
            var kind = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(handler.Payload + 8));
            if (kind != 0x736f756e && kind != 0x76696465) continue; // soun / vide
            if (kind == 0x736f756e && ++audioCount > 1 || kind == 0x76696465 && ++videoCount > 1)
                throw new InvalidDataException("Ambiguous MP4 track selection.");
            var edts = children.Where(box => box.Type == 0x65647473).ToArray();
            if (edts.Length == 0) continue;
            if (edts.Length != 1) throw new InvalidDataException("Duplicate MP4 edit container.");
            var mdhd = RequiredMp4Box(media, 0x6d646864);
            var (mediaScale, _) = Mp4Clock(source, mdhd);
            var edits = Mp4Boxes(source, edts[0].Payload, edts[0].End);
            var elst = RequiredMp4Box(edits, 0x656c7374);
            var bytes = source.AsSpan(elst.Payload, elst.End - elst.Payload);
            if (bytes.Length < 8 || bytes[0] > 1) throw new InvalidDataException("Invalid MP4 edit list.");
            var count = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
            var size = bytes[0] == 1 ? 20 : 12;
            if (count is < 1 or > 2 || bytes.Length != 8 + count * size)
                throw new InvalidDataException("Unsupported MP4 edit sequence.");
            long leading = 0; Mp4Edit? edit = null;
            for (var i = 0; i < count; i++)
            {
                var entry = bytes.Slice(8 + i * size, size);
                var length = size == 20 ? BinaryPrimitives.ReadUInt64BigEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry);
                var time = size == 20 ? BinaryPrimitives.ReadInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
                if (BinaryPrimitives.ReadUInt32BigEndian(entry[^4..]) != 0x10000 || length == 0)
                    throw new InvalidDataException("Unsupported MP4 edit rate/duration.");
                if (time == -1 && i == 0 && count == 2) leading = Mp4Ticks(length, movieScale);
                else if (time >= 0 && i == count - 1)
                    edit = new Mp4Edit(checked(leading - Mp4Ticks((ulong)time, mediaScale)), leading,
                        checked(leading + Mp4Ticks(length, movieScale)));
                else throw new InvalidDataException("Unsupported MP4 media edit.");
            }
            if (edit is null) throw new InvalidDataException("MP4 edit has no media segment.");
            if (kind == 0x736f756e) audioEdit = edit; else videoEdit = edit;
            "free"u8.CopyTo(source.AsSpan(edts[0].Offset + 4, 4));
        }
        if (video ? videoCount != 1 : audioCount != 1) throw new InvalidDataException("MP4 requested track is missing.");
        return new Mp4Presentation(Mp4Ticks(movieDuration, movieScale), audioEdit, videoEdit);
    }

    private static List<Mp4Box> Mp4Boxes(byte[] source, int start, int end)
    {
        var boxes = new List<Mp4Box>();
        while (start < end)
        {
            if (end - start < 8 || boxes.Count >= 4096) throw new InvalidDataException("Invalid MP4 box boundary/count.");
            var size = (ulong)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(start));
            var kind = BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(start + 4));
            var header = 8;
            if (size == 1)
            {
                if (end - start < 16) throw new InvalidDataException("Truncated extended MP4 box.");
                size = BinaryPrimitives.ReadUInt64BigEndian(source.AsSpan(start + 8)); header = 16;
            }
            if (size == 0) size = (ulong)(end - start);
            if (size < (ulong)header || size > (ulong)(end - start)) throw new InvalidDataException("Invalid MP4 box size.");
            var stop = checked(start + (int)size);
            boxes.Add(new Mp4Box(start, start + header, stop, kind)); start = stop;
        }
        return boxes;
    }

    private static Mp4Box RequiredMp4Box(List<Mp4Box> boxes, uint type)
    {
        var found = boxes.Where(box => box.Type == type).Take(2).ToArray();
        return found.Length == 1 ? found[0] : throw new InvalidDataException("Missing/duplicate MP4 metadata box.");
    }

    private static (uint Scale, ulong Duration) Mp4Clock(byte[] source, Mp4Box box)
    {
        var bytes = source.AsSpan(box.Payload, box.End - box.Payload);
        if (bytes.Length < 20 || bytes[0] > 1) throw new InvalidDataException("Invalid MP4 clock.");
        var offset = bytes[0] == 1 ? 20 : 12;
        if (bytes.Length < offset + (bytes[0] == 1 ? 12 : 8)) throw new InvalidDataException("Truncated MP4 clock.");
        var scale = BinaryPrimitives.ReadUInt32BigEndian(bytes[offset..]);
        if (scale == 0) throw new InvalidDataException("Invalid MP4 timescale.");
        return (scale, bytes[0] == 1 ? BinaryPrimitives.ReadUInt64BigEndian(bytes[(offset + 4)..]) : BinaryPrimitives.ReadUInt32BigEndian(bytes[(offset + 4)..]));
    }

    private static long Mp4Ticks(ulong units, uint scale) => checked((long)decimal.Round(units * 10_000_000m / scale));

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

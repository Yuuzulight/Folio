using System.Buffers.Binary;
using Folio.Imaging;

namespace Folio.Scripting.Rtc.Vp8;

/// <summary>
/// An <see cref="IMediaDecoder"/> for VP8 video in an IVF container (<c>video/x-ivf</c>, <c>video/ivf</c> or <c>video/vp8</c>),
/// the container the libvpx test vectors use. It decodes the whole bounded resource and returns every shown frame as RGBA.
/// The same <see cref="Vp8Decoder"/> serves live WebRTC video; WebM and MP4 demuxing are not implemented, so a host that wants
/// those still supplies its own decoder.
/// </summary>
public sealed class Vp8MediaDecoder : IMediaDecoder
{
    private const int MaxFrames = 100_000;
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    public bool CanDecode(string? contentType, bool video)
    {
        if (!video || contentType is null) return false;
        var mime = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return mime is "video/x-ivf" or "video/ivf" or "video/vp8";
    }

    public MediaDecodeResult? Decode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes)
    {
        if (!CanDecode(contentType, video) || maxOutputBytes <= 0) return null;
        var span = data.Span;
        if (span.Length < 32 || span[0] != 'D' || span[1] != 'K' || span[2] != 'I' || span[3] != 'F') return null;
        var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(span[6..]);
        if (headerLength < 32 || headerLength > span.Length || span[8] != 'V' || span[9] != 'P' || span[10] != '8' || span[11] != '0') return null;
        var rate = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
        var scale = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
        if (rate == 0) rate = 30;
        if (scale == 0) scale = 1;

        using var decoder = new Vp8Decoder(Math.Min(Vp8Decoder.DefaultMaxPixels, maxOutputBytes / 4));
        var frames = new List<MediaVideoFrame>();
        long retained = 0;
        int offset = headerLength;
        var duration = 0.0;
        while (offset + 12 <= span.Length && frames.Count < MaxFrames)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);
            var pts = BinaryPrimitives.ReadUInt64LittleEndian(span[(offset + 4)..]);
            offset += 12;
            if (size > MaxFrameBytes || size > span.Length - offset) break; // truncated: keep what decoded
            var status = decoder.Decode(span.Slice(offset, (int)size));
            offset += (int)size;
            if (status != Vp8Status.Ok) { if (frames.Count == 0) return null; break; }
            if (!decoder.FrameShown) continue;
            retained += (long)decoder.Width * decoder.Height * 4;
            if (retained > maxOutputBytes) return null; // the caller's output budget is a hard limit
            var rgba = decoder.ToRgba();
            if (rgba is null) return null;
            var time = (double)pts * scale / rate;
            frames.Add(new MediaVideoFrame(time, new DecodedImage(decoder.Width, decoder.Height, rgba)));
            duration = Math.Max(duration, time + (double)scale / rate);
        }
        if (frames.Count == 0) return null;
        return new MediaDecodeResult(duration, frames[0].Frame, null) { VideoFrames = frames };
    }
}

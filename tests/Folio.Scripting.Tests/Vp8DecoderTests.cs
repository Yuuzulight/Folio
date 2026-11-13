using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio.Scripting.Rtc;
using Folio.Scripting.Rtc.Vp8;

namespace Folio.Scripting.Tests;

/// <summary>
/// The VP8 decoder against the libvpx conformance vectors (vp80-00-comprehensive-001 .. 018, from
/// https://storage.googleapis.com/downloads.webmproject.org/test_data/libvpx/ together with their per-frame .md5 files,
/// which are the MD5 of each decoded I420 frame). They are the vectors the WebM project publishes for RFC 6386 decoders.
/// </summary>
public sealed class Vp8DecoderTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Vp8Vectors");

    public static IEnumerable<object[]> Vectors() =>
        Enumerable.Range(1, 18).Select(i => new object[] { i.ToString("D3") });

    /// <summary>Splits an IVF file into its frames.</summary>
    internal static List<byte[]> IvfFrames(byte[] file, out int width, out int height)
    {
        width = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12));
        height = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14));
        var frames = new List<byte[]>();
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(6));
        while (offset + 12 <= file.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset));
            offset += 12;
            frames.Add(file.AsSpan(offset, size).ToArray());
            offset += size;
        }
        return frames;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void DecodesTheLibvpxConformanceVectorBitExactly(string id)
    {
        var name = $"vp80-00-comprehensive-{id}";
        var frames = IvfFrames(File.ReadAllBytes(Path.Combine(Dir, name + ".ivf")), out _, out _);
        var expected = File.ReadAllLines(Path.Combine(Dir, name + ".ivf.md5")).Where(l => l.Length > 32).Select(l => l[..32]).ToArray();
        using var decoder = new Vp8Decoder(maxPixels: 4096 * 4096);
        var shown = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            Assert.Equal(Vp8Status.Ok, decoder.Decode(frames[i]));
            if (!decoder.FrameShown) continue;
            var hash = Convert.ToHexString(MD5.HashData(decoder.ToI420()!)).ToLowerInvariant();
            Assert.True(shown < expected.Length, $"{name}: more shown frames than md5 lines");
            Assert.True(expected[shown] == hash, $"{name}: frame {shown} (stream frame {i}) differs");
            shown++;
        }
        Assert.Equal(expected.Length, shown);
    }
}

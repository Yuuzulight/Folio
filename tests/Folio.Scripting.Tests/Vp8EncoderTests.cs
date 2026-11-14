using Folio.Scripting.Rtc.Vp8;

namespace Folio.Scripting.Tests;

public sealed class Vp8EncoderTests
{
    [Fact]
    public void BoolEncoderDecoderRoundTripsRandomBits()
    {
        var rng = new Random(42);
        for (var trial = 0; trial < 10; trial++)
        {
            var count = 500;
            var bits = new int[count];
            var probs = new int[count];
            for (var i = 0; i < count; i++)
            {
                bits[i] = rng.Next(2);
                probs[i] = rng.Next(1, 255);
            }

            var encoder = new BoolEncoder();
            for (var i = 0; i < count; i++)
                encoder.Put(bits[i], probs[i]);
            encoder.Finish();

            var data = encoder.ToArray();
            var decoder = new BoolDecoder(data, 0, data.Length);
            for (var i = 0; i < count; i++)
            {
                var decoded = decoder.Get(probs[i]);
                Assert.Equal(bits[i], decoded);
            }
        }
    }

    [Fact]
    public void KeyFrameEncodesAndDecodesWithHighPsnr()
    {
        const int w = 320;
        const int h = 240;
        var rgba = new byte[w * h * 4];

        for (var py = 0; py < h; py++)
        {
            for (var px = 0; px < w; px++)
            {
                var idx = (py * w + px) * 4;
                rgba[idx] = (byte)((px * 255) / w);          // R gradient
                rgba[idx + 1] = (byte)((py * 255) / h);      // G gradient
                rgba[idx + 2] = (byte)(px < w / 2 ? 200 : 50); // B step
                rgba[idx + 3] = 255;
            }
        }

        var encoder = new Vp8Encoder(w, h, qIndex: 4);
        var frame = encoder.Encode(rgba, w, h);
        Assert.NotNull(frame);

        using var decoder = new Vp8Decoder();
        var status = decoder.Decode(frame);
        Assert.Equal(Vp8Status.Ok, status);

        var decoded = decoder.ToRgba();
        Assert.NotNull(decoded);
        Assert.Equal(rgba.Length, decoded.Length);
        var psnr = Psnr(rgba, decoded);
        Assert.True(psnr >= 30.0, $"Expected PSNR >= 30.0 dB, got {psnr:F2} dB");
    }

    /// <summary>Peak signal-to-noise ratio across the RGB channels of two equal-sized RGBA images.</summary>
    internal static double Psnr(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        double mse = 0;
        for (var i = 0; i < expected.Length; i += 4)
        {
            var dr = expected[i] - actual[i];
            var dg = expected[i + 1] - actual[i + 1];
            var db = expected[i + 2] - actual[i + 2];
            mse += dr * dr + dg * dg + db * db;
        }
        mse /= expected.Length / 4 * 3;
        return 10.0 * Math.Log10(255.0 * 255.0 / (mse == 0 ? 1e-10 : mse));
    }

    internal static byte[] Pattern(int w, int h, int variant)
    {
        var rgba = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                rgba[i] = (byte)(variant == 0 ? x * 255 / w : 255 - x * 255 / w);
                rgba[i + 1] = (byte)(variant == 0 ? y * 255 / h : 40);
                rgba[i + 2] = (byte)(variant == 0 ? 60 : y * 255 / h);
                rgba[i + 3] = 255;
            }
        return rgba;
    }

    [Fact]
    public void EveryFrameIsAKeyFrameAndTracksItsInput()
    {
        const int w = 160;
        const int h = 120;
        var first = Pattern(w, h, 0);
        var second = Pattern(w, h, 1);
        Assert.True(Psnr(first, second) < 15, "test images must differ clearly");

        var encoder = new Vp8Encoder(w, h);
        using var decoder = new Vp8Decoder();

        var frame1 = encoder.Encode(first, w, h)!;
        Assert.True(Vp8Decoder.IsKeyFrame(frame1));
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frame1));
        Assert.True(Psnr(first, decoder.ToRgba()!) >= 30);

        var frame2 = encoder.Encode(second, w, h)!;
        Assert.True(Vp8Decoder.IsKeyFrame(frame2));
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frame2));
        var decoded2 = decoder.ToRgba()!;
        var psnr2 = Psnr(second, decoded2);
        Assert.True(psnr2 >= 30, $"second frame must match the second image, PSNR {psnr2:F2} dB");
        Assert.True(Psnr(first, decoded2) < 20, "second decoded frame must not still show the first image");
    }

    [Theory]
    [InlineData(17, 15)]
    [InlineData(1, 1)]
    [InlineData(641, 361)]
    [InlineData(1920, 1080)]
    [InlineData(10000, 1)]
    [InlineData(1, 10000)]
    public void OddAndOversizedInputsEncodeWithinTheCap(int w, int h)
    {
        var rgba = new byte[w * h * 4];
        new Random(7).NextBytes(rgba);
        var encoder = new Vp8Encoder(Math.Min(w, 640), Math.Min(h, 360));
        var frame = encoder.Encode(rgba, w, h);
        Assert.NotNull(frame);
        Assert.True(Vp8Decoder.TryReadKeyFrameSize(frame, out var fw, out var fh));
        Assert.InRange(fw, 16, Vp8Encoder.MaxWidth);
        Assert.InRange(fh, 16, Vp8Encoder.MaxHeight);
        using var decoder = new Vp8Decoder();
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frame));
    }

    [Fact]
    public void SizeCheckDoesNotOverflowForHugeDimensions()
    {
        var encoder = new Vp8Encoder(160, 120);
        // 40000 x 40000 x 4 overflows int; the check must reject the short buffer rather than wrap into accepting it
        Assert.Null(encoder.Encode(new byte[16], 40000, 40000));
        Assert.Null(encoder.Encode(new byte[16], int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void FuzzAndMalformedInputsDoNotThrow()
    {
        var encoder = new Vp8Encoder(160, 120);

        Assert.Null(encoder.Encode([], 160, 120));
        Assert.Null(encoder.Encode(new byte[100], 160, 120));
        Assert.Null(encoder.Encode(new byte[160 * 120 * 4], 0, 120));
        Assert.Null(encoder.Encode(new byte[160 * 120 * 4], 160, -1));

        var rng = new Random(1234);
        var noise = new byte[160 * 120 * 4];
        rng.NextBytes(noise);

        var frame = encoder.Encode(noise, 160, 120);
        Assert.NotNull(frame);
        using var decoder = new Vp8Decoder();
        Assert.Equal(Vp8Status.Ok, decoder.Decode(frame));
        // noise is not smooth, but it must still decode to a real picture of the right size and resemble the input
        var decoded = decoder.ToRgba()!;
        Assert.Equal(noise.Length, decoded.Length);
        Assert.True(Psnr(noise, decoded) > 8, "noise frame decoded to something unrelated to the input");
    }
}

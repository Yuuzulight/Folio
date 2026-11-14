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
        var frame = encoder.Encode(rgba, w, h, forceKeyFrame: true);
        Assert.NotNull(frame);

        using var decoder = new Vp8Decoder();
        var status = decoder.Decode(frame);
        Assert.Equal(Vp8Status.Ok, status);

        var decoded = decoder.ToRgba();
        Assert.NotNull(decoded);
        Assert.Equal(rgba.Length, decoded.Length);

        // Compute Peak Signal-to-Noise Ratio (PSNR) across RGB channels
        double mse = 0;
        var totalSamples = w * h * 3;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var dr = rgba[i] - decoded[i];
            var dg = rgba[i + 1] - decoded[i + 1];
            var db = rgba[i + 2] - decoded[i + 2];
            mse += dr * dr + dg * dg + db * db;
        }
        mse /= totalSamples;
        var psnr = 10.0 * Math.Log10((255.0 * 255.0) / (mse == 0 ? 1e-10 : mse));
        Assert.True(psnr >= 30.0, $"Expected PSNR >= 30.0 dB, got {psnr:F2} dB (MSE = {mse:F2})");
    }

    [Fact]
    public void InterFrameDecodesAgainstReferenceFrame()
    {
        const int w = 160;
        const int h = 120;
        var rgba = new byte[w * h * 4];
        Array.Fill<byte>(rgba, 100);

        var encoder = new Vp8Encoder(w, h, qIndex: 4);
        var keyFrame = encoder.Encode(rgba, w, h, forceKeyFrame: true);
        Assert.NotNull(keyFrame);
        Assert.True(Vp8Decoder.IsKeyFrame(keyFrame));

        using var decoder = new Vp8Decoder();
        var keyStatus = decoder.Decode(keyFrame);
        Assert.Equal(Vp8Status.Ok, keyStatus);

        var interFrame = encoder.Encode(rgba, w, h, forceKeyFrame: false);
        Assert.NotNull(interFrame);
        Assert.False(Vp8Decoder.IsKeyFrame(interFrame));

        var interStatus = decoder.Decode(interFrame);
        Assert.Equal(Vp8Status.Ok, interStatus);
        Assert.True(decoder.HasFrame);

        var decoded = decoder.ToRgba();
        Assert.NotNull(decoded);
        Assert.Equal(rgba.Length, decoded.Length);
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

        var frame = encoder.Encode(noise, 160, 120, forceKeyFrame: true);
        Assert.NotNull(frame);
        using var decoder = new Vp8Decoder();
        var status = decoder.Decode(frame);
        Assert.Equal(Vp8Status.Ok, status);
    }
}

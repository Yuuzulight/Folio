using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;

namespace Folio.Painting.Software;

/// <summary>
/// A Gaussian blur of interleaved 8-bit channels, treating what lies outside as transparent. Small deviations use the
/// kernel itself; larger ones three box blurs, as the SVG filter specification describes
/// (https://www.w3.org/TR/filter-effects-1/#feGaussianBlurElement).
/// </summary>
internal static class GaussianBlur
{
    private const float BoxThreshold = 2.0f;
    private static readonly ConcurrentDictionary<(int SigmaBits, int Radius), float[]> Kernels = new();
    private static readonly object KernelGate = new();

    public static byte[] Apply(byte[] source, int width, int height, int channels, float sigmaX, float sigmaY, int activeChannelMask = -1)
    {
        var current = source;
        if (sigmaX > 0)
            current = Pass(current, width, height, channels, sigmaX, horizontal: true, activeChannelMask);
        if (sigmaY > 0)
            current = Pass(current, width, height, channels, sigmaY, horizontal: false, activeChannelMask);
        return ReferenceEquals(current, source) ? (byte[])source.Clone() : current;
    }

    private static byte[] Pass(byte[] source, int width, int height, int channels, float sigma, bool horizontal, int activeChannelMask)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var stride = horizontal ? channels : width * channels; // distance between samples along a line
        var lineStride = horizontal ? width * channels : channels; // distance between lines
        var output = new byte[source.Length];
        var pool = ArrayPool<float>.Shared;
        var radius = sigma < BoxThreshold ? Math.Max(1, (int)MathF.Ceiling(sigma * 3)) : 0;
        var kernel = radius == 0 ? [] : GetKernel(sigma, radius);
        void ProcessRange(int first, int last)
        {
            var line = pool.Rent(length);
            var result = pool.Rent(length);
            var scratchA = pool.Rent(length);
            var scratchB = pool.Rent(length);
            try
            {
                for (var l = first; l < last; l++)
                {
                    for (var c = 0; c < channels; c++)
                    {
                        if ((activeChannelMask & (1 << c)) == 0)
                            continue;
                        var start = l * lineStride + c;
                        var any = false;
                        for (var i = 0; i < length; i++)
                        {
                            var v = source[start + i * stride];
                            line[i] = v;
                            any |= v != 0;
                        }
                        if (!any)
                            continue;
                        if (sigma < BoxThreshold)
                            Kernel(line, result, length, kernel, radius);
                        else
                            Boxes(line, result, scratchA, scratchB, length, sigma);
                        for (var i = 0; i < length; i++)
                            output[start + i * stride] = (byte)Math.Clamp((int)(result[i] + 0.5f), 0, 255);
                    }
                }
            }
            finally
            {
                pool.Return(line);
                pool.Return(result);
                pool.Return(scratchA);
                pool.Return(scratchB);
            }
        }
        if (lines < 64)
            ProcessRange(0, lines);
        else
            Parallel.ForEach(Partitioner.Create(0, lines), range => ProcessRange(range.Item1, range.Item2));
        return output;
    }

    private static float[] GetKernel(float sigma, int radius)
    {
        var key = (BitConverter.SingleToInt32Bits(sigma), radius);
        if (Kernels.TryGetValue(key, out var kernel))
            return kernel;
        lock (KernelGate)
        {
            if (Kernels.TryGetValue(key, out kernel))
                return kernel;
            if (Kernels.Count >= 64)
                Kernels.Clear();
            kernel = KernelWeights(sigma, radius);
            Kernels[key] = kernel;
            return kernel;
        }
    }

    private static float[] KernelWeights(float sigma, int radius)
    {
        var kernel = new float[2 * radius + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            sum += kernel[i + radius];
        }
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;
        return kernel;
    }

    private static void Kernel(float[] input, float[] output, int length, float[] kernel, int radius)
    {
        // The interior of the separable pass has no transparent edge checks.  Process it a
        // vector at a time; this is the hot path for the small-radius blur used by shadows
        // and CSS filter: blur().  Keep the scalar loop at both ends where the transparent
        // outside-of-image rule matters.
        var width = Vector<float>.Count;
        var interiorEnd = length - radius;
        var vectorEnd = interiorEnd - (interiorEnd - radius) % width;
        for (var x = radius; x < vectorEnd; x += width)
        {
            var total = Vector<float>.Zero;
            for (var k = -radius; k <= radius; k++)
                total += new Vector<float>(input, x + k) * kernel[k + radius];
            total.CopyTo(output, x);
        }

        for (var x = 0; x < radius; x++)
        {
            var total = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var j = x + k;
                if (j >= 0 && j < length)
                    total += input[j] * kernel[k + radius];
            }
            output[x] = total;
        }
        for (var x = vectorEnd; x < length; x++)
        {
            var total = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var j = x + k;
                if (j >= 0 && j < length)
                    total += input[j] * kernel[k + radius];
            }
            output[x] = total;
        }
    }

    private static void Boxes(float[] input, float[] output, float[] scratchA, float[] scratchB, int length, float sigma)
    {
        var d = (int)MathF.Floor(sigma * 3 * MathF.Sqrt(2 * MathF.PI) / 4 + 0.5f);
        var a = scratchA;
        var b = scratchB;
        input.CopyTo(a, 0);
        if (d % 2 == 1)
        {
            Box(a, b, length, d, d / 2);
            Box(b, a, length, d, d / 2);
            Box(a, output, length, d, d / 2);
        }
        else
        {
            // Two boxes offset to either side of the pixel, then one one wider, centred.
            Box(a, b, length, d, d / 2);
            Box(b, a, length, d, d / 2 - 1);
            Box(a, output, length, d + 1, d / 2);
        }
    }

    // A box of the given size whose first pixel is `before` pixels before the one being computed.
    private static void Box(float[] input, float[] output, int length, int size, int before)
    {
        var window = 0f;
        var scale = 1f / size;
        for (var i = -before; i < size - before; i++)
        {
            if (i >= 0 && i < length)
                window += input[i];
        }
        for (var x = 0; x < length; x++)
        {
            output[x] = window * scale;
            var leaving = x - before;
            var entering = x - before + size;
            if (leaving >= 0 && leaving < length)
                window -= input[leaving];
            if (entering >= 0 && entering < length)
                window += input[entering];
        }
    }
}

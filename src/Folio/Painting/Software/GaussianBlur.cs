using System.Buffers;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Folio.Painting.Software;

/// <summary>
/// A Gaussian blur of interleaved 8-bit channels, treating what lies outside as transparent. Small deviations use the
/// kernel itself; larger ones three box blurs, as the SVG filter specification describes
/// (https://www.w3.org/TR/filter-effects-1/#feGaussianBlurElement).
/// </summary>
internal static class GaussianBlur
{
    private const float BoxThreshold = 2.0f;
    private const long ParallelMinimumSamples = 1 << 18;
    private static readonly ConcurrentDictionary<(int SigmaBits, int Radius), float[]> Kernels = new();
    private static readonly object KernelGate = new();

    public static byte[] Apply(byte[] source, int width, int height, int channels, float sigmaX, float sigmaY, int activeChannelMask = -1)
    {
        if (!(sigmaX > 0 || sigmaY > 0)) return (byte[])source.Clone();
        var output = new byte[source.Length];
        if (sigmaX > 0 && sigmaY > 0)
        {
            // The horizontal result is private scratch, not a retained image. Reuse it instead of creating a
            // large-object-heap array for every animated shadow. Clear skipped channels and empty lines exactly
            // as a newly allocated buffer would, and return it even if processing throws.
            var scratch = ArrayPool<byte>.Shared.Rent(source.Length);
            try
            {
                scratch.AsSpan(0, source.Length).Clear();
                Pass(source, scratch, width, height, channels, sigmaX, horizontal: true, activeChannelMask);
                Pass(scratch, output, width, height, channels, sigmaY, horizontal: false, activeChannelMask);
            }
            finally { ArrayPool<byte>.Shared.Return(scratch, clearArray: true); }
        }
        else
            Pass(source, output, width, height, channels, sigmaX > 0 ? sigmaX : sigmaY, horizontal: sigmaX > 0, activeChannelMask);
        return output;
    }

    private static void Pass(byte[] source, byte[] output, int width, int height, int channels, float sigma, bool horizontal, int activeChannelMask)
    {
        var allChannels = (1 << channels) - 1;
        if (sigma >= BoxThreshold && (activeChannelMask & allChannels) == allChannels)
        {
            if (!horizontal)
            {
                VerticalBoxes(source, output, width, height, channels, sigma);
                return;
            }
            // A horizontal pass is the vertical one on the transposed image. Per line the running sum is one long
            // dependency chain; across many lines at once it vectorizes. Same operations per line, same result.
            var pixels = width * height;
            var transposed = ArrayPool<byte>.Shared.Rent(pixels * channels);
            var blurred = ArrayPool<byte>.Shared.Rent(pixels * channels);
            try
            {
                Transpose(source, transposed, width, height, channels);
                VerticalBoxes(transposed, blurred, height, width, channels, sigma);
                Transpose(blurred, output, height, width, channels);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(transposed);
                ArrayPool<byte>.Shared.Return(blurred);
            }
            return;
        }
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var stride = horizontal ? channels : width * channels; // distance between samples along a line
        var lineStride = horizontal ? width * channels : channels; // distance between lines
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
        // Lines are independent, so the split never changes the result. Below ~256K samples (a canvas shadow, a small
        // filter region) handing ranges to the thread pool costs more than the blur.
        if (lines < 64 || (long)lines * length * channels < ParallelMinimumSamples)
            ProcessRange(0, lines);
        else
            Parallel.ForEach(Partitioner.Create(0, lines), range => ProcessRange(range.Item1, range.Item2));
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

    // The three boxes for a deviation, as (size, pixels before the one computed); the same in both directions.
    private static (int Size, int Before)[] BoxPlan(float sigma)
    {
        var d = BoxSize(sigma);
        return d % 2 == 1 ? [(d, d / 2), (d, d / 2), (d, d / 2)] : [(d, d / 2), (d, d / 2 - 1), (d + 1, d / 2)];
    }

    private static int BoxSize(float sigma) => (int)MathF.Floor(sigma * 3 * MathF.Sqrt(2 * MathF.PI) / 4 + 0.5f);

    /// <summary>
    /// The vertical box pass done a row at a time: every column's running sum advances together, so the rows are
    /// read in order and the sums are vector operations, instead of gathering each column with a stride. Each column
    /// sees exactly the additions and subtractions, in the same order, as the per-line pass, so the result is
    /// bit-identical to it (all-zero columns, which that pass skips, stay zero here too).
    /// </summary>
    private static void VerticalBoxes(byte[] source, byte[] output, int width, int height, int channels, float sigma)
    {
        var rowLength = width * channels;
        var count = rowLength * height;
        var pool = ArrayPool<float>.Shared;
        var a = pool.Rent(count);
        var b = pool.Rent(count);
        try
        {
            ExpandSamples(source.AsSpan(0, count), a.AsSpan(0, count));
            var plan = BoxPlan(sigma);
            void ProcessColumns(int first, int last)
            {
                var window = pool.Rent(last - first);
                try
                {
                    VerticalBox(a, b, rowLength, height, plan[0].Size, plan[0].Before, first, last, window);
                    VerticalBox(b, a, rowLength, height, plan[1].Size, plan[1].Before, first, last, window);
                    VerticalBox(a, b, rowLength, height, plan[2].Size, plan[2].Before, first, last, window);
                }
                finally { pool.Return(window); }
            }
            if (rowLength < 64 || (long)count < ParallelMinimumSamples)
                ProcessColumns(0, rowLength);
            else
                Parallel.ForEach(Partitioner.Create(0, rowLength, Math.Max(64, rowLength / Environment.ProcessorCount)), range => ProcessColumns(range.Item1, range.Item2));
            RoundSamples(b.AsSpan(0, count), output.AsSpan(0, count));
        }
        finally
        {
            pool.Return(a);
            pool.Return(b);
        }
    }

    private static void ExpandSamples(ReadOnlySpan<byte> source, Span<float> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var groups = MemoryMarshal.Cast<byte, Vector<byte>>(source);
            var output = MemoryMarshal.Cast<float, Vector<float>>(destination);
            foreach (var group in groups)
            {
                Vector.Widen(group, out Vector<ushort> lower, out Vector<ushort> upper);
                Vector.Widen(lower, out Vector<uint> first, out Vector<uint> second);
                Vector.Widen(upper, out Vector<uint> third, out Vector<uint> fourth);
                output[i++] = Vector.ConvertToSingle(first);
                output[i++] = Vector.ConvertToSingle(second);
                output[i++] = Vector.ConvertToSingle(third);
                output[i++] = Vector.ConvertToSingle(fourth);
            }
            i *= Vector<float>.Count;
        }
        for (; i < source.Length; i++) destination[i] = source[i];
    }

    private static void RoundSamples(ReadOnlySpan<float> source, Span<byte> destination)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var input = MemoryMarshal.Cast<float, Vector<float>>(source);
            var groups = MemoryMarshal.Cast<byte, Vector<byte>>(destination);
            for (var group = 0; group < groups.Length; group++)
            {
                var lower = Vector.Narrow(Round(input[i++]), Round(input[i++]));
                var upper = Vector.Narrow(Round(input[i++]), Round(input[i++]));
                groups[group] = Vector.Narrow(lower, upper);
            }
            i *= Vector<float>.Count;
        }
        for (; i < source.Length; i++)
            destination[i] = (byte)Math.Clamp((int)(source[i] + 0.5f), 0, 255);

        static Vector<uint> Round(Vector<float> values) => Vector.ConvertToUInt32(
            Vector.Min(Vector.Max(values + new Vector<float>(0.5f), Vector<float>.Zero), new Vector<float>(255)));
    }

    // Pixel (x, y) of a width x height image to (y, x) of a height x width one, channels kept together.
    private static void Transpose(byte[] source, byte[] destination, int width, int height, int channels)
    {
        if (channels == 1)
        {
            TransposePixels<byte>(source, destination, width, height);
            return;
        }
        if (channels == 4)
        {
            TransposePixels<uint>(MemoryMarshal.Cast<byte, uint>(source.AsSpan()),
                MemoryMarshal.Cast<byte, uint>(destination.AsSpan()), width, height);
            return;
        }
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var from = (y * width + x) * channels;
                var to = (x * height + y) * channels;
                for (var c = 0; c < channels; c++)
                    destination[to + c] = source[from + c];
            }
    }

    private static void TransposePixels<T>(ReadOnlySpan<T> source, Span<T> destination, int width, int height)
        where T : unmanaged
    {
        // Keep the destination rows of one tile hot while walking adjacent source rows.
        const int tile = 32;
        for (var top = 0; top < height; top += tile)
        {
            var bottom = Math.Min(top + tile, height);
            for (var left = 0; left < width; left += tile)
            {
                var right = Math.Min(left + tile, width);
                for (var y = top; y < bottom; y++)
                {
                    var input = source.Slice(y * width + left, right - left);
                    var output = left * height + y;
                    for (var x = 0; x < input.Length; x++, output += height)
                        destination[output] = input[x];
                }
            }
        }
    }

    private static void VerticalBox(float[] input, float[] output, int rowLength, int rows, int size, int before, int first, int last, float[] windowBuffer)
    {
        var columns = last - first;
        var window = windowBuffer.AsSpan(0, columns);
        window.Clear();
        var scale = 1f / size;
        for (var i = -before; i < size - before; i++)
            if (i >= 0 && i < rows)
                Add(window, input.AsSpan(i * rowLength + first, columns));
        for (var y = 0; y < rows; y++)
        {
            Scaled(window, scale, output.AsSpan(y * rowLength + first, columns));
            var leaving = y - before;
            var entering = y - before + size;
            if (leaving >= 0 && leaving < rows)
                Subtract(window, input.AsSpan(leaving * rowLength + first, columns));
            if (entering >= 0 && entering < rows)
                Add(window, input.AsSpan(entering * rowLength + first, columns));
        }
    }

    private static void Add(Span<float> sum, ReadOnlySpan<float> values)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(sum);
        var others = MemoryMarshal.Cast<float, Vector<float>>(values);
        for (var i = 0; i < vectors.Length; i++) vectors[i] += others[i];
        for (var i = vectors.Length * Vector<float>.Count; i < sum.Length; i++) sum[i] += values[i];
    }

    private static void Subtract(Span<float> sum, ReadOnlySpan<float> values)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(sum);
        var others = MemoryMarshal.Cast<float, Vector<float>>(values);
        for (var i = 0; i < vectors.Length; i++) vectors[i] -= others[i];
        for (var i = vectors.Length * Vector<float>.Count; i < sum.Length; i++) sum[i] -= values[i];
    }

    private static void Scaled(ReadOnlySpan<float> sum, float scale, Span<float> output)
    {
        var vectors = MemoryMarshal.Cast<float, Vector<float>>(sum);
        var outputs = MemoryMarshal.Cast<float, Vector<float>>(output);
        for (var i = 0; i < vectors.Length; i++) outputs[i] = vectors[i] * scale;
        for (var i = vectors.Length * Vector<float>.Count; i < sum.Length; i++) output[i] = sum[i] * scale;
    }

    private static void Boxes(float[] input, float[] output, float[] scratchA, float[] scratchB, int length, float sigma)
    {
        var d = BoxSize(sigma);
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
        // Where both the leaving and the entering pixel lie inside the line no edge checks are needed; the
        // operations, and their order, are the same as at the edges.
        var interiorStart = Math.Clamp(before, 0, length);
        var interiorEnd = Math.Clamp(length + before - size, interiorStart, length);
        for (var x = 0; x < interiorStart; x++)
            Step(x);
        for (var x = interiorStart; x < interiorEnd; x++)
        {
            output[x] = window * scale;
            window -= input[x - before];
            window += input[x - before + size];
        }
        for (var x = interiorEnd; x < length; x++)
            Step(x);

        void Step(int x)
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

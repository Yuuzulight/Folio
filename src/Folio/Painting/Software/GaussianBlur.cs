namespace Folio.Painting.Software;

/// <summary>
/// A Gaussian blur of interleaved 8-bit channels, treating what lies outside as transparent. Small deviations use the
/// kernel itself; larger ones three box blurs, as the SVG filter specification describes
/// (https://www.w3.org/TR/filter-effects-1/#feGaussianBlurElement).
/// </summary>
internal static class GaussianBlur
{
    private const float BoxThreshold = 2.0f;

    public static byte[] Apply(byte[] source, int width, int height, int channels, float sigmaX, float sigmaY)
    {
        var current = source;
        if (sigmaX > 0)
            current = Pass(current, width, height, channels, sigmaX, horizontal: true);
        if (sigmaY > 0)
            current = Pass(current, width, height, channels, sigmaY, horizontal: false);
        return ReferenceEquals(current, source) ? (byte[])source.Clone() : current;
    }

    private static byte[] Pass(byte[] source, int width, int height, int channels, float sigma, bool horizontal)
    {
        var length = horizontal ? width : height;
        var lines = horizontal ? height : width;
        var stride = horizontal ? channels : width * channels; // distance between samples along a line
        var lineStride = horizontal ? width * channels : channels; // distance between lines
        var output = new byte[source.Length];
        var line = new float[length];
        var result = new float[length];

        for (var l = 0; l < lines; l++)
        {
            for (var c = 0; c < channels; c++)
            {
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
                    Kernel(line, result, sigma);
                else
                    Boxes(line, result, sigma);
                for (var i = 0; i < length; i++)
                    output[start + i * stride] = (byte)Math.Clamp((int)(result[i] + 0.5f), 0, 255);
            }
        }
        return output;
    }

    private static void Kernel(float[] input, float[] output, float sigma)
    {
        var radius = Math.Max(1, (int)MathF.Ceiling(sigma * 3));
        var kernel = new float[2 * radius + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            kernel[i + radius] = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            sum += kernel[i + radius];
        }
        for (var i = 0; i < kernel.Length; i++)
            kernel[i] /= sum;
        var length = input.Length;
        for (var x = 0; x < length; x++)
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

    private static void Boxes(float[] input, float[] output, float sigma)
    {
        var d = (int)MathF.Floor(sigma * 3 * MathF.Sqrt(2 * MathF.PI) / 4 + 0.5f);
        var length = input.Length;
        var a = (float[])input.Clone();
        var b = new float[length];
        if (d % 2 == 1)
        {
            Box(a, b, d, d / 2);
            Box(b, a, d, d / 2);
            Box(a, output, d, d / 2);
        }
        else
        {
            // Two boxes offset to either side of the pixel, then one one wider, centred.
            Box(a, b, d, d / 2);
            Box(b, a, d, d / 2 - 1);
            Box(a, output, d + 1, d / 2);
        }
    }

    // A box of the given size whose first pixel is `before` pixels before the one being computed.
    private static void Box(float[] input, float[] output, int size, int before)
    {
        var length = input.Length;
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

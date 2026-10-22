namespace Folio.Imaging;

/// <summary>
/// The ALPH chunk of an extended WebP file (https://developers.google.com/speed/webp/docs/riff_container#alpha): one byte
/// of header, then the alpha plane, raw or compressed as a headerless lossless stream (the alpha values are its green
/// channel), and optionally stored as differences from a prediction (horizontal, vertical or gradient).
/// </summary>
internal static class WebpAlpha
{
    public static byte[]? Decode(ReadOnlySpan<byte> data, int width, int height, long maxPixels)
    {
        if (data.Length < 1 || !ImageLimits.Allows(width, height, maxPixels))
            return null;
        var compression = data[0] & 3;
        var filter = (data[0] >> 2) & 3;
        var count = width * height;
        byte[] alpha;
        if (compression == 0)
        {
            if (data.Length - 1 < count)
                return null;
            alpha = data.Slice(1, count).ToArray();
        }
        else if (compression == 1)
        {
            if (WebpLossless.DecodeHeaderless(data[1..], width, height, maxPixels) is not { } argb)
                return null;
            alpha = new byte[count];
            for (var i = 0; i < count; i++)
                alpha[i] = (byte)(argb[i] >> 8);
        }
        else
            return null;

        if (filter != 0)
            Unfilter(alpha, width, height, filter);
        return alpha;
    }

    // Each value is stored minus a prediction from its already decoded neighbours: the left one (1), the one above (2) or
    // left + above - above-left clamped (3). The first row is predicted from the left, the first column from above, and
    // the very first value from zero.
    private static void Unfilter(byte[] alpha, int width, int height, int filter)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                int prediction;
                if (x == 0 && y == 0)
                    prediction = 0;
                else if (y == 0)
                    prediction = alpha[i - 1];
                else if (x == 0)
                    prediction = alpha[i - width];
                else
                {
                    int left = alpha[i - 1], above = alpha[i - width], aboveLeft = alpha[i - width - 1];
                    prediction = filter switch { 1 => left, 2 => above, _ => Math.Clamp(left + above - aboveLeft, 0, 255) };
                }
                alpha[i] = (byte)(alpha[i] + prediction);
            }
        }
    }
}

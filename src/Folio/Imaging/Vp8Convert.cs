namespace Folio.Imaging;

/// <summary>
/// Y, U and V planes to straight-alpha RGBA8 (ITU-R BT.601, limited range, in fixed point). The chroma planes have half
/// the resolution; they are brought to full size by a triangle filter over the four nearest chroma samples (weights 9, 3,
/// 3 and 1), or, when not smoothing, by repeating each sample.
/// </summary>
internal static class Vp8Convert
{
    private static int MultHi(int v, int coefficient) => (v * coefficient) >> 8;

    private static byte Clip(int v) => (v & ~((256 << 6) - 1)) == 0 ? (byte)(v >> 6) : v < 0 ? (byte)0 : (byte)255;

    private static void Write(byte[] output, int offset, int y, int u, int v, byte alpha)
    {
        output[offset] = Clip(MultHi(y, 19077) + MultHi(v, 26149) - 14234);
        output[offset + 1] = Clip(MultHi(y, 19077) - MultHi(u, 6419) - MultHi(v, 13320) + 8708);
        output[offset + 2] = Clip(MultHi(y, 19077) + MultHi(u, 33050) - 17685);
        output[offset + 3] = alpha;
    }

    public static byte[] ToRgba(Vp8Decoder.Frame frame, byte[]? alpha, bool smooth)
    {
        var width = frame.Width;
        var height = frame.Height;
        var output = new byte[checked(width * height * 4)];
        if (!smooth)
        {
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var uv = (y >> 1) * frame.UvStride + (x >> 1);
                    Write(output, (y * width + x) * 4, frame.Y[y * frame.YStride + x], frame.U[uv], frame.V[uv], alpha?[y * width + x] ?? 255);
                }
            }
            return output;
        }

        // The first row has only its own chroma row; rows 2k-1 and 2k lie between chroma rows k-1 and k; for an even
        // height the last row again has only its own.
        LinePair(frame, alpha, output, 0, -1, 0, 0);
        for (var k = 1; 2 * k - 1 < height; k++)
        {
            if (2 * k < height)
                LinePair(frame, alpha, output, 2 * k - 1, 2 * k, k - 1, k);
            else
                LinePair(frame, alpha, output, 2 * k - 1, -1, k - 1, k - 1);
        }
        return output;
    }

    // Two output rows (the second optional) from two chroma rows: the top row is nearer the top chroma row. The chroma
    // samples are packed as u | v << 16 so that both are filtered by the same arithmetic.
    private static void LinePair(Vp8Decoder.Frame frame, byte[]? alpha, byte[] output, int topRow, int bottomRow, int topChroma, int currentChroma)
    {
        var width = frame.Width;
        var lastPair = (width - 1) >> 1;

        uint Load(int row, int x) => (uint)(frame.U[row * frame.UvStride + x] | frame.V[row * frame.UvStride + x] << 16);

        void Put(int row, int x, uint uv) =>
            Write(output, (row * width + x) * 4, frame.Y[row * frame.YStride + x], (int)(uv & 0xFF), (int)(uv >> 16), alpha?[row * width + x] ?? 255);

        var topLeft = Load(topChroma, 0);
        var left = Load(currentChroma, 0);
        Put(topRow, 0, (3 * topLeft + left + 0x00020002u) >> 2);
        if (bottomRow >= 0)
            Put(bottomRow, 0, (3 * left + topLeft + 0x00020002u) >> 2);

        for (var x = 1; x <= lastPair; x++)
        {
            var top = Load(topChroma, x);
            var current = Load(currentChroma, x);
            var average = topLeft + top + left + current + 0x00080008u;
            var diagonal12 = (average + 2 * (top + left)) >> 3;
            var diagonal03 = (average + 2 * (topLeft + current)) >> 3;
            Put(topRow, 2 * x - 1, (diagonal12 + topLeft) >> 1);
            Put(topRow, 2 * x, (diagonal03 + top) >> 1);
            if (bottomRow >= 0)
            {
                Put(bottomRow, 2 * x - 1, (diagonal03 + left) >> 1);
                Put(bottomRow, 2 * x, (diagonal12 + current) >> 1);
            }
            topLeft = top;
            left = current;
        }

        if ((width & 1) == 0)
        {
            Put(topRow, width - 1, (3 * topLeft + left + 0x00020002u) >> 2);
            if (bottomRow >= 0)
                Put(bottomRow, width - 1, (3 * left + topLeft + 0x00020002u) >> 2);
        }
    }
}

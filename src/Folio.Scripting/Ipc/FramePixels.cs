using System.Runtime.Intrinsics;

namespace Folio.Scripting.Ipc;

internal static class FramePixels
{
    private static readonly Vector128<byte> SwapBytes0And2 = Vector128.Create((byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    /// <summary>
    /// RGBA to BGRA in place: swaps bytes 0 and 2 of every four. Sixteen bytes (four pixels) per vector step, then a scalar loop for
    /// the rest; <paramref name="vectors"/> false takes the scalar loop for all of it (for checking one path against the other).
    /// </summary>
    internal static void SwapRedAndBlue(byte[] pixels, bool vectors = true)
    {
        var i = 0;
        if (vectors && Vector128.IsHardwareAccelerated)
        {
            for (; i <= pixels.Length - 16; i += 16)
            {
                var block = Vector128.LoadUnsafe(ref pixels[i]);
                Vector128.StoreUnsafe(Vector128.Shuffle(block, SwapBytes0And2), ref pixels[i]);
            }
        }
        for (; i + 2 < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}

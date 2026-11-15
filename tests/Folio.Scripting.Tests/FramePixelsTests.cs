using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class FramePixelsTests
{
    private static byte[] Sample(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++) bytes[i] = (byte)(i * 7 + 3);
        return bytes;
    }

    private static byte[] Expected(byte[] source)
    {
        var copy = (byte[])source.Clone();
        for (var i = 0; i + 2 < copy.Length; i += 4) (copy[i], copy[i + 2]) = (copy[i + 2], copy[i]);
        return copy;
    }

    // Lengths on both sides of the 16-byte vector step: a whole number of steps, a whole number of pixels that is not, and
    // a ragged tail that is not a whole pixel.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(32)]
    [InlineData(36)]
    [InlineData(1280 * 4)]
    [InlineData(1280 * 4 + 12)]
    [InlineData(19)]
    [InlineData(18)]
    public void VectorAndScalarPathsSwapRedAndBlueIdentically(int length)
    {
        var source = Sample(length);
        var vector = (byte[])source.Clone();
        var scalar = (byte[])source.Clone();
        FramePixels.SwapRedAndBlue(vector);
        FramePixels.SwapRedAndBlue(scalar, vectors: false);
        Assert.Equal(Expected(source), scalar);
        Assert.Equal(scalar, vector);
    }
}

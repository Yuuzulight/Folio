using Folio.Painting.Software;

namespace Folio.Tests.Painting;

public class CompositorSpanTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 17)]
    [InlineData(3, 129)]
    public void EveryAlphaAndDestinationByteMatchesScalarSourceOver(int offset, int pixels)
    {
        var actual = new byte[offset + pixels * 4 + 7];
        for (var alpha = 0; alpha <= 255; alpha++)
        {
            var r = (byte)(alpha / 2);
            var g = (byte)alpha;
            var b = (byte)(alpha / 3);
            for (var destination = 0; destination <= 255; destination++)
            {
                Array.Fill(actual, (byte)destination);
                var expected = (byte[])actual.Clone();
                for (var i = offset; i < offset + pixels * 4; i += 4)
                {
                    var inverse = 255 - alpha;
                    expected[i] = (byte)(r + (destination * inverse + 127) / 255);
                    expected[i + 1] = (byte)(g + (destination * inverse + 127) / 255);
                    expected[i + 2] = (byte)(b + (destination * inverse + 127) / 255);
                    expected[i + 3] = (byte)(alpha + (destination * inverse + 127) / 255);
                }
                Compositor.BlendConstantSpan(actual.AsSpan(offset, pixels * 4), r, g, b, (byte)alpha);
                Assert.Equal(expected, actual);
            }
        }
    }
}

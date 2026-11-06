using Folio.Painting.Software;

namespace Folio.Tests.Painting;

public class GaussianScratchTests
{
    [Theory]
    [InlineData(1, 1, 1.2f)]
    [InlineData(4, 15, 1.2f)]
    [InlineData(4, 8, 4f)]
    [InlineData(4, 0, 4f)]
    public void PooledIntermediateMatchesTwoIndependentPasses(int channels, int mask, float sigma)
    {
        var random = new Random(512);
        foreach (var size in new[] { (67, 41), (31, 17), (67, 41) })
        {
            var source = new byte[size.Item1 * size.Item2 * channels];
            random.NextBytes(source);
            Array.Clear(source, 0, source.Length / 4);
            var original = (byte[])source.Clone();
            var first = GaussianBlur.Apply(source, size.Item1, size.Item2, channels, sigma, 0, mask);
            var expected = GaussianBlur.Apply(first, size.Item1, size.Item2, channels, 0, sigma, mask);
            var actual = GaussianBlur.Apply(source, size.Item1, size.Item2, channels, sigma, sigma, mask);
            Assert.Equal(source.Length, actual.Length);
            Assert.Equal(expected, actual);
            Assert.Equal(original, source);
            // Dirty the shared pool before another mask/size, checking that skipped channels and blank lines stay zero.
            _ = GaussianBlur.Apply(source, size.Item1, size.Item2, channels, sigma, sigma);
        }
    }
}

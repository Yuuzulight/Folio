using Folio.Painting.Software;

namespace Folio.Tests.Painting;

// A blur of lines and columns that equal their neighbour once must give exactly the bytes of blurring every one.
public class GaussianBlurDeduplicationTests
{
    // The mask a big rounded box makes: flat inside and outside, antialiased at its edge and corners.
    private static byte[] RoundedBox(int width, int height, int margin, int radius, bool noisy)
    {
        var data = new byte[width * height];
        var random = new Random(3);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                double Distance(int a, int lo, int hi) => a < lo ? lo - a : a > hi ? a - hi : 0;
                var (dx, dy) = (Distance(x, margin + radius, width - margin - radius - 1), Distance(y, margin + radius, height - margin - radius - 1));
                var inside = x >= margin && x < width - margin && y >= margin && y < height - margin;
                var d = Math.Sqrt(dx * dx + dy * dy);
                var value = !inside ? 0 : d <= radius - 1 ? 255 : d >= radius ? 0 : (int)((radius - d) * 255);
                data[y * width + x] = (byte)(noisy && random.Next(40) == 0 ? random.Next(256) : value);
            }
        return data;
    }

    public static IEnumerable<object[]> Cases() =>
        from size in new[] { (400, 300), (700, 520), (301, 257) } from sigma in new[] { 2f, 3.3f, 8f, 16f } from noisy in new[] { false, true } select new object[] { size.Item1, size.Item2, sigma, noisy };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BlurringEachDistinctLineAndColumnOnceGivesTheSameBytes(int width, int height, float sigma, bool noisy)
    {
        var mask = RoundedBox(width, height, 20, 12, noisy);
        byte[] fast, slow;
        GaussianBlur.Deduplicate = false;
        try { slow = GaussianBlur.Apply(mask, width, height, 1, sigma, sigma); }
        finally { GaussianBlur.Deduplicate = true; }
        fast = GaussianBlur.Apply(mask, width, height, 1, sigma, sigma);
        Assert.True(slow.AsSpan().SequenceEqual(fast), $"{width}x{height} sigma {sigma} noisy {noisy}: the bytes differ");
    }

    [Fact]
    public void ManyChannelsAndUnequalDeviationsAgreeToo()
    {
        var random = new Random(11);
        var (width, height) = (260, 300);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var inside = x is > 30 and < 220 && y is > 40 and < 250;
                for (var c = 0; c < 4; c++)
                    rgba[(y * width + x) * 4 + c] = (byte)(inside ? 200 + c * 10 : 0);
            }
        rgba[(100 * width + 100) * 4] = (byte)random.Next(256);
        GaussianBlur.Deduplicate = false;
        byte[] slow;
        try { slow = GaussianBlur.Apply(rgba, width, height, 4, 5f, 9f); }
        finally { GaussianBlur.Deduplicate = true; }
        Assert.True(slow.AsSpan().SequenceEqual(GaussianBlur.Apply(rgba, width, height, 4, 5f, 9f)));
    }
}

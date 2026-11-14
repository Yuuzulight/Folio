using Folio.Painting.Software;

namespace Folio.Tests.Painting;

// A blur of lines and columns that equal their neighbour once must give exactly the bytes of blurring every one. The switch and the
// counter it reads are per thread, so these tests do not disturb each other when xUnit runs them in parallel.
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

    // The shortcut's bytes and how many passes it took the shortcut, against the plain blur's bytes.
    private static (byte[] Plain, byte[] Fast, int Fired) Both(byte[] source, int width, int height, int channels, float sigmaX, float sigmaY)
    {
        byte[] plain;
        GaussianBlur.Deduplicate = false;
        try
        {
            var before = GaussianBlur.DeduplicatedPasses;
            plain = GaussianBlur.Apply(source, width, height, channels, sigmaX, sigmaY);
            Assert.Equal(before, GaussianBlur.DeduplicatedPasses); // switched off means never
        }
        finally { GaussianBlur.Deduplicate = true; }
        var fired = GaussianBlur.DeduplicatedPasses;
        var fast = GaussianBlur.Apply(source, width, height, channels, sigmaX, sigmaY);
        return (plain, fast, GaussianBlur.DeduplicatedPasses - fired);
    }

    public static IEnumerable<object[]> Cases() =>
        from size in new[] { (400, 300), (700, 520), (301, 257) } from sigma in new[] { 2f, 3.3f, 8f, 16f } from noisy in new[] { false, true } select new object[] { size.Item1, size.Item2, sigma, noisy };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BlurringEachDistinctLineAndColumnOnceGivesTheSameBytes(int width, int height, float sigma, bool noisy)
    {
        var (slow, fast, fired) = Both(RoundedBox(width, height, 20, 12, noisy), width, height, 1, sigma, sigma);
        Assert.True(slow.AsSpan().SequenceEqual(fast), $"{width}x{height} sigma {sigma} noisy {noisy}: the bytes differ");
        if (noisy)
            Assert.Equal(0, fired); // no line or column repeats, so there is nothing to share
    }

    [Theory]
    [InlineData(700, 520, 4f)]
    [InlineData(400, 300, 8f)]
    public void ACleanLargeShadowTakesTheShortcutAndKeepsTheBytes(int width, int height, float sigma)
    {
        var (slow, fast, fired) = Both(RoundedBox(width, height, 20, 12, false), width, height, 1, sigma, sigma);
        Assert.True(fired > 0, "a flat-middled shadow must be blurred by distinct line");
        Assert.True(slow.AsSpan().SequenceEqual(fast));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0f, 5f)]
    [InlineData(5f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(1.9f, 1.9f)]
    public void NoDeviationOrOneBelowTheBoxThresholdGivesTheSameBytes(float sigmaX, float sigmaY)
    {
        var (slow, fast, fired) = Both(RoundedBox(400, 300, 20, 12, false), 400, 300, 1, sigmaX, sigmaY);
        Assert.True(slow.AsSpan().SequenceEqual(fast));
        // Below the threshold the kernel is used, not boxes, so the shortcut has nothing to do; with 0 on one axis the other still may.
        if (Math.Max(sigmaX, sigmaY) < 2f)
            Assert.Equal(0, fired);
    }

    [Fact]
    public void ADeviationMuchLargerThanTheImageGivesTheSameBytes()
    {
        var (slow, fast, _) = Both(RoundedBox(100, 100, 10, 8, false), 100, 100, 1, 300f, 300f);
        Assert.True(slow.AsSpan().SequenceEqual(fast));
    }

    [Theory]
    [InlineData(1, 6000)]
    [InlineData(6000, 1)]
    [InlineData(2, 3000)]
    [InlineData(3000, 2)]
    public void ASingleRowOrColumnGivesTheSameBytes(int width, int height)
    {
        var random = new Random(5);
        var data = new byte[width * height];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(i / 500 % 2 == 0 ? 200 : random.Next(256));
        var (slow, fast, _) = Both(data, width, height, 1, 3f, 3f);
        Assert.True(slow.AsSpan().SequenceEqual(fast));
    }

    [Fact]
    public void AnImageBelowTheSampleThresholdNeverTakesTheShortcut()
    {
        // 60 x 60 = 3600 samples, under 1 << 12: all one value, so everything repeats, but finding that would cost more than it saves.
        var flat = new byte[60 * 60];
        Array.Fill(flat, (byte)180);
        var (slow, fast, fired) = Both(flat, 60, 60, 1, 4f, 4f);
        Assert.Equal(0, fired);
        Assert.True(slow.AsSpan().SequenceEqual(fast));
    }

    [Fact]
    public void ARgbaImageOfRepeatedColumnsAgreesAndSharesItsLines()
    {
        // Vertical stripes: every line is the same, and within a line columns repeat in runs of 20.
        var (width, height) = (240, 80);
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                for (var c = 0; c < 4; c++)
                    rgba[(y * width + x) * 4 + c] = (byte)(x / 20 % 2 == 0 ? 255 - c * 20 : 10 + c);
        var (slow, fast, fired) = Both(rgba, width, height, 4, 4f, 4f);
        Assert.True(slow.AsSpan().SequenceEqual(fast));
        Assert.True(fired > 0, "identical lines are shared");
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
        var (slow, fast, _) = Both(rgba, width, height, 4, 5f, 9f);
        Assert.True(slow.AsSpan().SequenceEqual(fast));
    }
}

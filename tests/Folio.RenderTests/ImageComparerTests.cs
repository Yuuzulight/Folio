namespace Folio.RenderTests;

public class ImageComparerTests
{
    internal static PixelBuffer Solid(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var image = new PixelBuffer(width, height);
        for (var i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = b;
            image.Pixels[i + 1] = g;
            image.Pixels[i + 2] = r;
            image.Pixels[i + 3] = a;
        }
        return image;
    }

    private static bool IsRed(Span<byte> pixel) => pixel.SequenceEqual(new byte[] { 0, 0, 255, 255 });

    [Fact]
    public void IdenticalImagesMatchExactly()
    {
        var result = ImageComparer.Compare(Solid(10, 10, 10, 20, 30), Solid(10, 10, 10, 20, 30), Tolerance.Exact);

        Assert.True(result.Passed);
        Assert.Equal(0, result.DifferingPixels);
        Assert.Equal(0, result.DifferingRatio);
    }

    [Fact]
    public void ExactToleranceFailsOnOneLevelDifference()
    {
        var actual = Solid(10, 10, 10, 20, 30);
        actual.Pixel(3, 4)[1] = 21;

        var result = ImageComparer.Compare(Solid(10, 10, 10, 20, 30), actual, Tolerance.Exact);

        Assert.False(result.Passed);
        Assert.Equal(1, result.DifferingPixels);
    }

    [Theory]
    [InlineData(8, true)]   // at the threshold: not a differing pixel
    [InlineData(9, false)]  // above it
    public void ChannelThresholdIsInclusive(int delta, bool passes)
    {
        var actual = Solid(4, 4, 100, 100, 100);
        actual.Pixel(0, 0)[2] = (byte)(100 + delta);

        var result = ImageComparer.Compare(Solid(4, 4, 100, 100, 100), actual, new Tolerance(8, 0));

        Assert.Equal(passes, result.Passed);
    }

    [Fact]
    public void AlphaDifferencesCount()
    {
        var result = ImageComparer.Compare(Solid(2, 2, 0, 0, 0, 255), Solid(2, 2, 0, 0, 0, 0), Tolerance.Default);

        Assert.Equal(4, result.DifferingPixels);
        Assert.False(result.Passed);
    }

    [Theory]
    [InlineData(5, true)]   // 5 of 1000 = 0.5%, the default limit
    [InlineData(6, false)]
    public void DefaultToleranceAllowsHalfAPercentOfPixels(int changed, bool passes)
    {
        var actual = Solid(40, 25, 200, 200, 200);
        for (var x = 0; x < changed; x++)
            actual.Pixel(x, 0)[0] = 0;

        var result = ImageComparer.Compare(Solid(40, 25, 200, 200, 200), actual, Tolerance.Default);

        Assert.Equal(changed, result.DifferingPixels);
        Assert.Equal(changed / 1000.0, result.DifferingRatio);
        Assert.Equal(passes, result.Passed);
    }

    [Fact]
    public void DifferentSizesFailEvenWithinTheRatio()
    {
        // One extra row out of 1001 is under 0.5%, but a size change is always a failure.
        var result = ImageComparer.Compare(Solid(1000, 1, 0, 0, 0), Solid(1000, 2, 0, 0, 0), new Tolerance(8, 0.5));

        Assert.False(result.Passed);
        Assert.Equal(1000, result.DifferingPixels);
        Assert.Equal((1000, 2), (result.Diff.Width, result.Diff.Height));
        Assert.True(IsRed(result.Diff.Pixel(0, 1)));
        Assert.False(IsRed(result.Diff.Pixel(0, 0)));
    }

    [Fact]
    public void DiffHighlightsDifferingPixelsAndFadesTheRest()
    {
        var actual = Solid(3, 3, 0, 0, 0);
        actual.Pixel(1, 1)[0] = 255;

        var diff = ImageComparer.Compare(Solid(3, 3, 0, 0, 0), actual, Tolerance.Exact).Diff;

        Assert.True(IsRed(diff.Pixel(1, 1)));
        var faded = diff.Pixel(0, 0);
        Assert.Equal(255, faded[3]);
        Assert.Equal(faded[0], faded[1]);
        Assert.Equal(faded[1], faded[2]);
        Assert.InRange(faded[0], 180, 200); // black lightened to a mid-light grey
    }

    [Fact]
    public void TransparentPixelsFadeToWhite()
    {
        var diff = ImageComparer.Compare(Solid(1, 1, 0, 0, 0, 0), Solid(1, 1, 0, 0, 0, 0), Tolerance.Exact).Diff;

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, diff.Pixels);
    }

    [Fact]
    public void EmptyImagesMatch()
    {
        Assert.True(ImageComparer.Compare(new PixelBuffer(0, 0), new PixelBuffer(0, 0), Tolerance.Exact).Passed);
    }

    [Fact]
    public void OutputImagesRoundTripThroughPngAndClear()
    {
        var expected = Solid(5, 4, 10, 20, 30);
        var actual = Solid(5, 4, 10, 20, 30);
        actual.Pixel(2, 2)[0] = 250;
        var diff = ImageComparer.Compare(expected, actual, Tolerance.Exact).Diff;
        var dir = Directory.CreateTempSubdirectory("folio-").FullName;
        var basePath = Path.Combine(dir, "area", "name");
        try
        {
            RenderOutput.Write(basePath, actual, expected, diff);

            Assert.Equal(expected.Pixels, PixelBuffer.LoadPng(basePath + ".expected.png").Pixels);
            Assert.Equal(actual.Pixels, PixelBuffer.LoadPng(basePath + ".actual.png").Pixels);
            Assert.Equal(diff.Pixels, PixelBuffer.LoadPng(basePath + ".diff.png").Pixels);

            RenderOutput.Clear(basePath);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dir, "area")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

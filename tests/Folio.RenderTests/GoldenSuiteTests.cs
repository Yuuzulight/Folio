using static Folio.RenderTests.ImageComparerTests;

namespace Folio.RenderTests;

public sealed class GoldenSuiteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("folio-goldens-").FullName;
    private readonly GoldenSuite _suite;

    public GoldenSuiteTests()
    {
        _suite = new GoldenSuite(Path.Combine(_dir, "goldens"), Path.Combine(_dir, "output"));
        AddPage("area/page");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void AddPage(string test)
    {
        var path = Path.Combine(_suite.Root, test + ".html");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<!DOCTYPE html>");
    }

    private void WriteManifest(string json) => File.WriteAllText(Path.Combine(_suite.Root, "area", "manifest.json"), json);

    private string Output(string test, string suffix) => Path.Combine(_suite.Output, test + suffix);

    private static PixelBuffer WithChangedPixels(int count, byte delta)
    {
        var image = Solid(40, 25, 100, 100, 100); // 1000 pixels
        for (var x = 0; x < count; x++)
            image.Pixel(x, 0)[0] = (byte)(100 + delta);
        return image;
    }

    [Fact]
    public void DiscoversPagesAsAreaSlashName()
    {
        AddPage("other/nested/deep");

        Assert.Equal(["area/page", "other/nested/deep"], _suite.Discover());
    }

    [Fact]
    public void MissingGoldenFailsAndLeavesTheActualImageToApprove()
    {
        var failure = _suite.Check("area/page", Solid(4, 4, 1, 2, 3));

        Assert.Contains("no golden", failure);
        Assert.True(File.Exists(Output("area/page", ".actual.png")));
    }

    [Fact]
    public void ApprovedImageBecomesTheGolden()
    {
        var image = Solid(4, 4, 1, 2, 3);
        _suite.Check("area/page", image);

        Assert.Equal(["area/page"], _suite.Approve("area/page"));

        Assert.Equal(image.Pixels, PixelBuffer.LoadPng(Path.Combine(_suite.Root, "area/page.png")).Pixels);
        Assert.False(File.Exists(Output("area/page", ".actual.png")));
        Assert.Null(_suite.Check("area/page", image));
    }

    [Fact]
    public void ApprovingAnAreaApprovesEveryActualImageInIt()
    {
        AddPage("area/second");
        _suite.Check("area/page", Solid(2, 2, 0, 0, 0));
        _suite.Check("area/second", Solid(2, 2, 0, 0, 0));

        Assert.Equal(["area/page", "area/second"], _suite.Approve("area"));
        Assert.Empty(_suite.Approve("area")); // nothing left to approve
    }

    [Fact]
    public void ApproveIgnoresImagesWithoutAPage()
    {
        RenderOutput.Write(Path.Combine(_suite.Output, "area", "gone"), Solid(2, 2, 0, 0, 0));

        Assert.Empty(_suite.Approve("area"));
        Assert.Empty(_suite.Approve(""));
    }

    [Fact]
    public void IdenticalImagePassesWithoutOutput()
    {
        _suite.Check("area/page", WithChangedPixels(0, 0));
        _suite.Approve("area/page");

        Assert.Null(_suite.Check("area/page", WithChangedPixels(0, 0)));
        Assert.False(Directory.EnumerateFiles(_suite.Output, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void ChangeWithinTolerancePassesButLeavesImagesForReview()
    {
        _suite.Check("area/page", WithChangedPixels(0, 0));
        _suite.Approve("area/page");

        Assert.Null(_suite.Check("area/page", WithChangedPixels(5, 50)));

        Assert.True(File.Exists(Output("area/page", ".actual.png")));
        Assert.True(File.Exists(Output("area/page", ".expected.png")));
        Assert.True(File.Exists(Output("area/page", ".diff.png")));
    }

    [Fact]
    public void ChangeBeyondToleranceFails()
    {
        _suite.Check("area/page", WithChangedPixels(0, 0));
        _suite.Approve("area/page");

        var failure = _suite.Check("area/page", WithChangedPixels(6, 50));

        Assert.Contains("6 pixels", failure);
        Assert.True(File.Exists(Output("area/page", ".diff.png")));
    }

    [Fact]
    public void ToleranceDefaultsWithoutManifest()
    {
        Assert.Equal(Tolerance.Default, _suite.ToleranceFor("area/page"));
    }

    [Fact]
    public void ManifestDefaultAndPerTestOverrideApply()
    {
        AddPage("area/second");
        WriteManifest("""
            {
              "default": { "channelThreshold": 2, "maxDifferingRatio": 0.001 },
              "tests": { "page": { "channelThreshold": 16, "maxDifferingRatio": 0.01, "reason": "emoji glyph edges" } }
            }
            """);

        Assert.Equal(new Tolerance(16, 0.01), _suite.ToleranceFor("area/page"));
        Assert.Equal(new Tolerance(2, 0.001), _suite.ToleranceFor("area/second"));
    }

    [Fact]
    public void OverrideWithoutReasonIsRejected()
    {
        WriteManifest("""{ "tests": { "page": { "channelThreshold": 16, "maxDifferingRatio": 0.01 } } }""");

        Assert.Throws<InvalidDataException>(() => _suite.ToleranceFor("area/page"));
    }

    [Fact]
    public void OutOfRangeToleranceIsRejected()
    {
        WriteManifest("""{ "default": { "channelThreshold": 8, "maxDifferingRatio": 5 } }""");

        Assert.Throws<InvalidDataException>(() => _suite.ToleranceFor("area/page"));
    }
}

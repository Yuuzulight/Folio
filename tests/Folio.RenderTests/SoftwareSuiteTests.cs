namespace Folio.RenderTests;

/// <summary>The golden pages and the reftests drawn by Folio's own raster backend, against the same goldens and tolerances.</summary>
public class SoftwareSuiteTests(ITestOutputHelper output)
{
    // The software backend's picture of each golden page against the Skia backend's picture of it, within the page's tolerance:
    // a golden can lag behind the code, so the pictures are compared with each other as well as with the golden.
    [Fact]
    public void GoldenPagesOnTheSoftwareBackend()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The text limits are for Skia on Windows.");
        var suite = new GoldenSuite(GoldenSuite.Repo.Root, Path.Combine(RenderOutput.Root, "goldens-software"));
        var report = new System.Text.StringBuilder();
        var failures = new List<string>();
        var total = 0;
        foreach (var test in GoldenSuite.Repo.Discover())
        {
            total++;
            var page = Path.Combine(suite.Root, test + ".html");
            var skiaImage = TestRenderer.Render(page);
            var softwareImage = SoftwareRender.RenderPage(page);
            var vsSkia = ImageComparer.Compare(skiaImage, softwareImage, suite.ToleranceFor(test));
            var vsGolden = suite.Check(test, softwareImage);
            report.AppendLine($"{test}: vs Skia {(vsSkia.Passed ? "pass" : "FAIL")} ({vsSkia.DifferingPixels} px, {vsSkia.DifferingRatio:P2}), vs golden {(vsGolden is null ? "pass" : "FAIL - " + vsGolden)}");
            if (!vsSkia.Passed)
                failures.Add($"{test}: {vsSkia.DifferingPixels} pixels ({vsSkia.DifferingRatio:P2}) differ from the Skia backend's picture");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "software-golden-report.txt"), report.ToString());
        output.WriteLine($"{total - failures.Count}/{total} golden pages match the Skia backend's picture on the software backend");
        Assert.True(failures.Count == 0, $"{failures.Count} of {total} golden pages differ on the software backend from the Skia backend's picture:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void ReftestsOnTheSoftwareBackend()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "The text limits are for Skia on Windows.");
        var report = new System.Text.StringBuilder();
        var failures = new List<string>();
        var total = 0;
        foreach (var page in Directory.EnumerateFiles(ReftestRunner.Root, "*.html", SearchOption.AllDirectories).Where(f => !f.EndsWith("-ref.html")).Order())
        {
            var name = Path.ChangeExtension(Path.GetRelativePath(ReftestRunner.Root, page), null).Replace('\\', '/');
            var reference = Path.Combine(ReftestRunner.Root, name + "-ref.html");
            if (!File.Exists(reference))
                continue;
            total++;
            var skia = ReftestRunner.Run(name).Passed;
            var (expected, actual) = (SoftwareRender.RenderPage(reference), SoftwareRender.RenderPage(page));
            var result = ImageComparer.Compare(expected, actual, Tolerance.Exact);
            if (!result.Passed)
                RenderOutput.Write(Path.Combine(RenderOutput.Root, "reftests-software", name), actual, expected, result.Diff);
            report.AppendLine($"{name}: skia {(skia ? "pass" : "FAIL")}, software {(result.Passed ? "pass" : "FAIL (" + result.DifferingPixels + " pixels)")}");
            if (skia && !result.Passed)
                failures.Add(name);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "software-reftest-report.txt"), report.ToString());
        output.WriteLine($"{total - failures.Count}/{total} reftests pass on the software backend");
        Assert.True(failures.Count == 0, $"{failures.Count} of {total} reftests that pass on the Skia backend differ on the software one:\n" + string.Join("\n", failures));
    }
}

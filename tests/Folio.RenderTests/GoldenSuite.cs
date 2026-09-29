using System.Text.Json;

namespace Folio.RenderTests;

/// <summary>
/// Golden-image tests (docs/study/19-testing.md): <c>&lt;area&gt;/&lt;name&gt;.html</c> is rendered and compared with the
/// reviewed <c>&lt;area&gt;/&lt;name&gt;.png</c> under the tolerance from <c>&lt;area&gt;/manifest.json</c>.
/// Goldens change only through <see cref="Approve"/>.
/// </summary>
/// <remarks>
/// manifest.json is optional:
/// <c>{ "default": { "channelThreshold": 8, "maxDifferingRatio": 0.005 },
///      "tests": { "name": { "channelThreshold": 16, "maxDifferingRatio": 0.01, "reason": "why" } } }</c>.
/// A per-test override must give its reason.
/// </remarks>
public sealed class GoldenSuite(string root, string output)
{
    public static GoldenSuite Repo { get; } =
        new(Path.Combine(RepoPaths.Tests, "goldens"), Path.Combine(RenderOutput.Root, "goldens"));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Root => root;
    public string Output => output;

    /// <summary>Test names (<c>area/name</c>) of every golden page.</summary>
    public IEnumerable<string> Discover() =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.html", SearchOption.AllDirectories).Select(TestName).Order()
            : [];

    /// <returns>Null when the test passes, otherwise why it failed.</returns>
    public string? Run(string test) => Check(test, TestRenderer.Render(Path.Combine(root, test + ".html")));

    /// <summary>
    /// Compares a rendered image with the golden. Writes the actual image when there is no golden yet, and
    /// actual, expected and diff images whenever any pixel changed, even within tolerance.
    /// </summary>
    /// <returns>Null when the test passes, otherwise why it failed.</returns>
    public string? Check(string test, PixelBuffer actual)
    {
        var outputBase = Path.Combine(output, test);
        RenderOutput.Clear(outputBase);

        var goldenPath = Path.Combine(root, test + ".png");
        if (!File.Exists(goldenPath))
        {
            RenderOutput.Write(outputBase, actual);
            return $"{test}: no golden image yet. Review {outputBase}.actual.png, then approve it.";
        }

        var expected = PixelBuffer.LoadPng(goldenPath);
        var result = ImageComparer.Compare(expected, actual, ToleranceFor(test));
        if (result.DifferingPixels > 0)
            RenderOutput.Write(outputBase, actual, expected, result.Diff);

        return result.Passed
            ? null
            : $"{test}: {result.DifferingPixels} pixels ({result.DifferingRatio:P2}) differ, " +
              $"{actual.Width}x{actual.Height} vs golden {expected.Width}x{expected.Height}. Images in {outputBase}.*.png";
    }

    public Tolerance ToleranceFor(string test)
    {
        var manifestPath = Path.Combine(root, Path.GetDirectoryName(test) ?? "", "manifest.json");
        if (!File.Exists(manifestPath))
            return Tolerance.Default;

        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException($"{manifestPath}: empty manifest.");

        if (manifest.Tests?.TryGetValue(Path.GetFileName(test), out var entry) == true)
        {
            if (string.IsNullOrWhiteSpace(entry.Reason))
                throw new InvalidDataException($"{manifestPath}: the tolerance override for '{test}' needs a reason.");
            return Checked(new Tolerance(entry.ChannelThreshold, entry.MaxDifferingRatio), manifestPath);
        }
        return manifest.Default is { } fallback ? Checked(fallback, manifestPath) : Tolerance.Default;
    }

    /// <summary>
    /// Copies the actual images of a test (<c>area/name</c>) or a whole area over their goldens and clears
    /// that output. Only pages that exist under the golden root are approved.
    /// </summary>
    /// <returns>The approved test names.</returns>
    public IReadOnlyList<string> Approve(string target)
    {
        target = target.Replace('\\', '/').Trim('/');
        var single = Path.Combine(output, target + ".actual.png");
        var area = Path.Combine(output, target);
        string[] actuals = File.Exists(single) ? [single]
            : target.Length > 0 && Directory.Exists(area) ? Directory.GetFiles(area, "*.actual.png", SearchOption.AllDirectories)
            : [];

        var approved = new List<string>();
        foreach (var actual in actuals.Order())
        {
            var test = Path.GetRelativePath(output, actual)[..^".actual.png".Length].Replace('\\', '/');
            if (!File.Exists(Path.Combine(root, test + ".html")))
                continue;

            File.Copy(actual, Path.Combine(root, test + ".png"), overwrite: true);
            RenderOutput.Clear(Path.Combine(output, test));
            approved.Add(test);
        }
        return approved;
    }

    private string TestName(string htmlPath) =>
        Path.GetRelativePath(root, htmlPath)[..^".html".Length].Replace('\\', '/');

    private static Tolerance Checked(Tolerance tolerance, string manifestPath) =>
        tolerance.ChannelThreshold is >= 0 and <= 255 && tolerance.MaxDifferingRatio is >= 0 and <= 1
            ? tolerance
            : throw new InvalidDataException($"{manifestPath}: tolerance {tolerance} is out of range.");

    private sealed record Manifest(Tolerance? Default, Dictionary<string, Override>? Tests);

    private sealed record Override(int ChannelThreshold, double MaxDifferingRatio, string? Reason);
}

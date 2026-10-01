using System.Globalization;
using System.Text;
using System.Text.Json;
using Folio.Skia;

namespace Folio.RenderTests;

/// <summary>One artifact of a conformance corpus: <c>&lt;root&gt;/&lt;category&gt;/&lt;name&gt;/index.html</c>.</summary>
/// <param name="Corpus">"" for the public corpus in tests/conformance, "private" for the local one.</param>
public sealed record ConformanceArtifact(string Corpus, string Root, string Category, string Name)
{
    public string Folder => Path.Combine(Root, Category, Name);

    public string Id => (Corpus.Length > 0 ? Corpus + "/" : "") + Category + "/" + Name;

    public override string ToString() => Id;
}

public enum ConformanceOutcome
{
    /// <summary>Matches reference.png.</summary>
    Pass,

    /// <summary>Matches only reference-secondary.png: passes, flagged for human review.</summary>
    PassSecondary,
    Mismatch,
    NoReference,
    Crash,
    Hang,
    LimitHit,
}

public sealed record ConformanceResult(ConformanceArtifact Artifact, ConformanceOutcome Outcome, string? Detail)
{
    public bool Passed => Outcome is ConformanceOutcome.Pass or ConformanceOutcome.PassSecondary;

    /// <summary>Crashes, hangs and limit hits fail M1 wherever they happen, whatever the category.</summary>
    public bool Broke => Outcome is ConformanceOutcome.Crash or ConformanceOutcome.Hang or ConformanceOutcome.LimitHit;
}

/// <summary>
/// The conformance suite (docs/study/19-testing.md): renders each artifact at its manifest viewport and compares it
/// with the reference images captured by tools/Folio.RefCapture, under <see cref="Tolerance.Conformance"/>. The public corpus is tests/conformance; the private
/// one (FOLIO_PRIVATE_CORPUS or tests/conformance-private) joins when present, reported separately, with its output
/// images kept inside it.
/// </summary>
/// <remarks>
/// Each artifact folder has <c>index.html</c> and <c>manifest.json</c>:
/// <c>{ "prompt": "...", "features": ["tables"], "viewport": 800,
///      "tolerance": { "channelThreshold": 16, "maxDifferingRatio": 0.01, "reason": "why" } }</c>
/// (tolerance optional, and only with a reason). References are laid out in a viewport as tall as the reference image,
/// so both sides resolve <c>vh</c> the same way; without one, it still renders (full height) for the crash, hang and limit check.
/// </remarks>
public static class Conformance
{
    public static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The families every artifact is drawn with, here and in tools/Folio.RefCapture: the bundled text font, the emoji
    /// and script fonts for characters it lacks, then the box font for anything left, so both sides draw the same glyphs
    /// and neither falls back to system fonts.
    /// </summary>
    public const string FontFamilies = "\"Source Sans 3\", \"Noto Color Emoji\", \"Noto Sans JP\", \"Noto Sans SC\", \"Noto Sans Arabic\", \"Noto Sans Hebrew\", \"Folio Box\"";

    /// <summary>The test renderer's bundled fonts, with a user stylesheet whose !important beats the artifact's own families.</summary>
    public static FolioOptions Options { get; } = new()
    {
        Fonts = TestRenderer.Options.Fonts,
        UserStyleSheet = $"*, *::before, *::after {{ font-family: {FontFamilies} !important; }}",
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string PublicRoot { get; } = Path.Combine(RepoPaths.Tests, "conformance");

    public static string PrivateRoot { get; } =
        Environment.GetEnvironmentVariable("FOLIO_PRIVATE_CORPUS") is { Length: > 0 } folder ? folder : Path.Combine(RepoPaths.Tests, "conformance-private");

    public static IEnumerable<ConformanceArtifact> Discover() =>
        Discover("", PublicRoot).Concat(Discover("private", PrivateRoot));

    private static IEnumerable<ConformanceArtifact> Discover(string corpus, string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "index.html", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Split(Path.DirectorySeparatorChar))
                .Where(parts => parts.Length == 2)
                .Select(parts => new ConformanceArtifact(corpus, root, parts[0], parts[1]))
                .OrderBy(a => a.Id, StringComparer.Ordinal)
            : [];

    public static Manifest ManifestOf(ConformanceArtifact artifact)
    {
        var path = Path.Combine(artifact.Folder, "manifest.json");
        var manifest = File.Exists(path)
            ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), JsonOptions) ?? new Manifest()
            : new Manifest();
        if (manifest.Tolerance is { } t && (string.IsNullOrWhiteSpace(t.Reason) || t.ChannelThreshold is < 0 or > 255 || t.MaxDifferingRatio is < 0 or > 1))
            throw new InvalidDataException($"{path}: a tolerance override needs a reason and values in range.");
        return manifest;
    }

    public static ConformanceResult Run(ConformanceArtifact artifact)
    {
        var manifest = ManifestOf(artifact);
        var reference = LoadIfExists(Path.Combine(artifact.Folder, "reference.png"));
        var secondary = LoadIfExists(Path.Combine(artifact.Folder, "reference-secondary.png"));
        var html = File.ReadAllText(Path.Combine(artifact.Folder, "index.html"));
        var request = new RenderRequest(manifest.Viewport, reference?.Height);

        // ponytail: a hung render keeps its thread spinning until the process exits; the content process (M4) is
        // where work can really be stopped.
        var render = Task.Run(() =>
        {
            using var document = Document.Parse(html, Options);
            using var result = HeadlessRenderer.Render(document, request);
            return (Image: new PixelBuffer(result.Width, result.Height, result.Pixels.ToArray()), result.Diagnostics);
        });
        try
        {
            if (!render.Wait(HangTimeout))
                return new(artifact, ConformanceOutcome.Hang, $"no image after {HangTimeout.TotalSeconds} s");
        }
        catch (AggregateException e)
        {
            var inner = e.InnerException!;
            return new(artifact, ConformanceOutcome.Crash, $"{inner.GetType().Name}: {inner.Message.Split('\n')[0]}");
        }

        var (actual, diagnostics) = render.Result;
        var outputBase = OutputBase(artifact);
        RenderOutput.Clear(outputBase);
        if (diagnostics.FirstOrDefault(d => d.Code == DiagnosticCode.LimitExceeded) is { } limit)
        {
            RenderOutput.Write(outputBase, actual);
            return new(artifact, ConformanceOutcome.LimitHit, limit.Message);
        }
        if (reference is null)
            return new(artifact, ConformanceOutcome.NoReference, null);

        var tolerance = manifest.Tolerance is { } t ? Tolerance.Conformance with { ChannelThreshold = t.ChannelThreshold, MaxDifferingRatio = t.MaxDifferingRatio } : Tolerance.Conformance;
        var primary = ImageComparer.Compare(reference, actual, tolerance);
        if (primary.Passed)
            return new(artifact, ConformanceOutcome.Pass, null);

        RenderOutput.Write(outputBase, actual, reference, primary.Diff);
        if (secondary is not null && ImageComparer.Compare(secondary, actual, tolerance).Passed)
            return new(artifact, ConformanceOutcome.PassSecondary, "matches only the secondary reference; review");
        return new(artifact, ConformanceOutcome.Mismatch, string.Create(CultureInfo.InvariantCulture,
            $"{primary.DifferingRatio * 100:F1}% of pixels differ ({actual.Width}x{actual.Height} vs {reference.Width}x{reference.Height}); {Diagnose(reference, actual, primary.Diff)}"));
    }

    /// <summary>
    /// Where the differences are, to tell drift from local errors: the first differing row, the bounding box of the
    /// differing pixels, and whether below that row the page is mostly the reference moved by a whole number of pixels
    /// (found by matching the rows' and columns' ink profiles), or the differences are scattered.
    /// </summary>
    public static string Diagnose(PixelBuffer expected, PixelBuffer actual, PixelBuffer diff)
    {
        var (left, top, right, bottom) = (int.MaxValue, int.MaxValue, -1, -1);
        for (var y = 0; y < diff.Height; y++)
        {
            for (var x = 0; x < diff.Width; x++)
            {
                if (diff.Pixel(x, y) is [0, 0, 255, 255])
                    (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
            }
        }
        if (bottom < 0)
            return "no differing pixels";
        var where = string.Create(CultureInfo.InvariantCulture, $"from row {top}, within x {left}-{right}, y {top}-{bottom}");
        var dy = BestShift(Profile(expected, top, bottom, rows: true), Profile(actual, top, bottom, rows: true));
        var dx = BestShift(Profile(expected, top, bottom, rows: false), Profile(actual, top, bottom, rows: false));
        return dy != 0 ? $"{where}; mostly shifted {(dy > 0 ? "down" : "up")} {Math.Abs(dy)} px"
            : dx != 0 ? $"{where}; mostly shifted {(dx > 0 ? "right" : "left")} {Math.Abs(dx)} px"
            : $"{where}; scattered";
    }

    // Ink per row (or per column) over rows top to bottom: the sum of how far each pixel is from white.
    private static double[] Profile(PixelBuffer image, int top, int bottom, bool rows)
    {
        var profile = new double[rows ? bottom - top + 1 : image.Width];
        for (var y = top; y <= Math.Min(bottom, image.Height - 1); y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var p = image.Pixel(x, y);
                profile[rows ? y - top : x] += 765 - p[0] - p[1] - p[2] + 3 * (255 - p[3]);
            }
        }
        return profile;
    }

    // The shift of `actual` against `expected` (positive: actual is later) that lines their profiles up, if it halves
    // the mismatch of no shift at all; else 0.
    private static int BestShift(double[] expected, double[] actual)
    {
        double Mismatch(int shift)
        {
            var (sum, count) = (0.0, 0);
            for (var i = Math.Max(0, -shift); i < expected.Length && i + shift < actual.Length; i++)
                (sum, count) = (sum + Math.Abs(expected[i] - actual[i + shift]), count + 1);
            return count < expected.Length / 2 ? double.MaxValue : sum / count;
        }
        var (best, bestMismatch) = (0, Mismatch(0));
        for (var shift = -32; shift <= 32; shift++)
        {
            if (shift != 0 && Mismatch(shift) is var m && m < bestMismatch)
                (best, bestMismatch) = (shift, m);
        }
        return best != 0 && bestMismatch < Mismatch(0) / 2 ? best : 0;
    }

    /// <summary>Where the actual, expected and diff images go: tests/render-output, or inside the private corpus.</summary>
    public static string OutputBase(ConformanceArtifact artifact) => artifact.Corpus.Length == 0
        ? Path.Combine(RenderOutput.Root, "conformance", artifact.Category, artifact.Name)
        : Path.Combine(artifact.Root, "render-output", artifact.Category, artifact.Name);

    /// <summary>Pass rates per category (docs/study/19-testing.md: known gaps count as failures), then every artifact that did not pass.</summary>
    public static string Report(IReadOnlyList<ConformanceResult> results)
    {
        var text = new StringBuilder()
            .AppendLine("| Category | Artifacts | Pass | Pass (secondary, review) | Mismatch | No reference | Crash | Hang | Limit hit | Pass rate |")
            .AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|");
        foreach (var group in results.GroupBy(r => (r.Artifact.Corpus, r.Artifact.Category)).OrderBy(g => g.Key))
        {
            int Count(ConformanceOutcome o) => group.Count(r => r.Outcome == o);
            var withReference = group.Count() - Count(ConformanceOutcome.NoReference);
            var passed = group.Count(r => r.Passed);
            var rate = withReference == 0 ? "n/a" : string.Create(CultureInfo.InvariantCulture, $"{100.0 * passed / withReference:F1}% of {withReference}");
            var name = (group.Key.Corpus.Length > 0 ? group.Key.Corpus + "/" : "") + group.Key.Category;
            text.AppendLine($"| {name} | {group.Count()} | {Count(ConformanceOutcome.Pass)} | {Count(ConformanceOutcome.PassSecondary)} | " +
                $"{Count(ConformanceOutcome.Mismatch)} | {Count(ConformanceOutcome.NoReference)} | {Count(ConformanceOutcome.Crash)} | " +
                $"{Count(ConformanceOutcome.Hang)} | {Count(ConformanceOutcome.LimitHit)} | {rate} |");
        }

        var notPassed = results.Where(r => !r.Passed).ToList();
        if (notPassed.Count > 0)
        {
            text.AppendLine().AppendLine("| Artifact | Outcome | Detail |").AppendLine("|---|---|---|");
            foreach (var r in notPassed)
                text.AppendLine($"| {r.Artifact.Id} | {r.Outcome} | {r.Detail} |");
        }
        return text.ToString();
    }

    private static PixelBuffer? LoadIfExists(string path) => File.Exists(path) ? PixelBuffer.LoadPng(path) : null;

    public sealed record Manifest(string? Prompt = null, string[]? Features = null, int Viewport = 800, ToleranceOverride? Tolerance = null);

    public sealed record ToleranceOverride(int ChannelThreshold, double MaxDifferingRatio, string? Reason);
}

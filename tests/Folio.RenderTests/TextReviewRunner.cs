using System;
using System.IO;
using Folio;
using Folio.RenderTests;
using Folio.Skia;

namespace Folio.RenderTests;

public static class TextReviewRunner
{
    public static void Run()
    {
        var html = @"<!DOCTYPE html><html><body style='margin: 20px; font-family: ""Source Sans 3"", sans-serif; font-size: 16px; line-height: 1.5; color: #111; background: #fff;'>
<h1>The quick brown fox jumps over the lazy dog</h1>
<p style='font-size: 14px;'>Folio builds the engine itself: everything that decides what a page looks like and how it behaves.</p>
<p style='font-size: 18px; font-weight: bold;'>Bold text and numbers: 0123456789, $123.45, 100% and 150% scale.</p>
<p style='font-size: 12px; color: #555;'>Small metadata caption text for side-by-side review across backends.</p>
</body></html>";

        var outDir = Path.Combine(RenderOutput.Root, "text-review");
        Directory.CreateDirectory(outDir);

        foreach (var scale in new[] { 1.0f, 1.5f })
        {
            using var docSkia = Document.Parse(html, TestRenderer.Options);
            using var docSoft = Document.Parse(html, TestRenderer.Options);

            var req = new RenderRequest(800, 400, scale);
            var (w1, h1, skiaPixels, _) = SoftwareRender.Pixels(docSkia, req, software: false);
            var (w2, h2, softPixels, _) = SoftwareRender.Pixels(docSoft, req, software: true);

            var bufSkia = new PixelBuffer(w1, h1, skiaPixels);
            var bufSoft = new PixelBuffer(w2, h2, softPixels);

            var result = ImageComparer.Compare(bufSkia, bufSoft, new Tolerance(48, 0.02, 1));
            var prefix = Path.Combine(outDir, $"text-review-{(int)(scale * 100)}pct");
            bufSoft.SavePng(prefix + "-software.png");
            bufSkia.SavePng(prefix + "-skia.png");
            result.Diff?.SavePng(prefix + "-diff.png");

            Console.WriteLine($"Scale {scale * 100}%: {w1}x{h1}, diff {result.DifferingPixels} px ({result.DifferingRatio:P2}), Passed: {result.Passed}");
            Xunit.Assert.True(result.Passed, $"Scale {scale * 100}% text diff is too large: {result.DifferingPixels} px ({result.DifferingRatio:P2})");
        }
    }
}

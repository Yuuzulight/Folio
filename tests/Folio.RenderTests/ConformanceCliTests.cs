using System.Diagnostics;
using System.Text.Json;
using Folio.Skia;

namespace Folio.RenderTests;

public sealed class ConformanceCliTests
{
    [Theory]
    [InlineData(false, false, 1, "ScriptFailed")]
    [InlineData(false, true, 0, "ExpectedFail")]
    [InlineData(true, false, 0, "NoReference")]
    [InlineData(true, true, 1, "UnexpectedPass")]
    public async Task ReportsUnexpectedInteractionFailuresThroughTheActualCommand(
        bool assertionPasses, bool expectedFailure, int expectedExitCode, string outcome)
    {
        var cancellation = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"folio-conformance-cli-{Guid.NewGuid():N}");
        var artifact = Path.Combine(root, "checks", "interaction");
        Directory.CreateDirectory(artifact);
        try
        {
            const string html = "<p id='message'>original</p>";
            await File.WriteAllTextAsync(Path.Combine(artifact, "index.html"), html, cancellation);
            if (assertionPasses && expectedFailure)
            {
                // Only this synthetic CLI fixture needs a matching reference:
                // prove a stale expected-failure mark requires correction.
                using var document = Document.Parse(html, Conformance.Options);
                using var rendered = HeadlessRenderer.Render(document, new RenderRequest(200, 600));
                new PixelBuffer(rendered.Width, rendered.Height, rendered.Pixels.ToArray())
                    .SavePng(Path.Combine(artifact, "reference.png"));
            }
            await File.WriteAllTextAsync(Path.Combine(artifact, "manifest.json"), JsonSerializer.Serialize(new
            {
                viewport = 200,
                interaction = new[] { $"text #message {(assertionPasses ? "original" : "changed")}" },
                expectedToFail = expectedFailure ? "Explicitly expected interaction failure for the harness regression." : null
            }), cancellation);
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("conformance");
            start.ArgumentList.Add("private/checks/interaction");
            // Discovery occurs in the child. Never change the test runner's
            // environment or shared corpus while other fixtures are running.
            start.Environment["FOLIO_PRIVATE_CORPUS"] = root;
            start.Environment.Remove("GITHUB_STEP_SUMMARY");
            using var process = Process.Start(start)!;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellation);
            var errorTask = process.StandardError.ReadToEndAsync(cancellation);
            try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(30), cancellation); }
            catch (Exception failure) when (failure is TimeoutException or OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                // Reap even when the test token is already cancelled.
                process.WaitForExit(5000);
                throw;
            }
            var output = await outputTask;
            var error = await errorTask;
            Assert.True(process.ExitCode == expectedExitCode, $"Expected exit {expectedExitCode}, got {process.ExitCode}.\n{output}\n{error}");
            Assert.Contains($"| private/checks/interaction | {outcome} |", output);
            if (!assertionPasses) Assert.Contains("text #message changed", output);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

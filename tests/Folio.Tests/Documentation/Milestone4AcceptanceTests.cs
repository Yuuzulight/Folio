namespace Folio.Tests.Documentation;

public sealed class Milestone4AcceptanceTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void M4DocumentationDescribesTheContentProcessAndSafetyBoundary()
    {
        var document = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "milestone-4-scripting.md"));
        Assert.Contains("content-process protocol", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ContentProcessSupervisor", document, StringComparison.Ordinal);
        Assert.Contains("heartbeat", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Milestone 4 is complete", document, StringComparison.Ordinal);
        Assert.Contains("Artifact-scoped persistent storage", document, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M4HasExecutableProcessAndScriptingSuites()
    {
        var scripting = Path.Combine(RepositoryRoot, "tests", "Folio.Scripting.Tests");
        Assert.True(File.Exists(Path.Combine(scripting, "ContentProcessClientCleanupTests.cs")));
        Assert.True(File.Exists(Path.Combine(scripting, "CapabilityIpcTests.cs")));
        Assert.True(File.Exists(Path.Combine(scripting, "ScriptEngineLimitsAndInterruptTests.cs")));
        Assert.True(File.Exists(Path.Combine(scripting, "HtmlEventLoopTests.cs")));
        Assert.True(File.Exists(Path.Combine(scripting, "MutationObserverTests.cs")));
    }

    [Fact]
    public void M4RecoveryGatesHaveExecutableCorpusAndImplementationEvidence()
    {
        var d1 = Path.Combine(RepositoryRoot, "tests", "conformance", "D1");
        var artifacts = Directory.EnumerateFiles(d1, "index.html", SearchOption.AllDirectories).ToArray();
        Assert.True(artifacts.Length >= 10, $"D1 corpus has only {artifacts.Length} artifacts.");
        Assert.All(artifacts, artifact =>
        {
            var directory = Path.GetDirectoryName(artifact)!;
            Assert.True(File.Exists(Path.Combine(directory, "reference.png")), artifact);
            Assert.True(File.Exists(Path.Combine(directory, "manifest.json")), artifact);
        });

        var root = Path.Combine(RepositoryRoot, "src");
        Assert.True(File.Exists(Path.Combine(root, "Folio.Scripting", "Dom", "WhatwgUrl.cs")));
        Assert.True(File.Exists(Path.Combine(root, "Folio", "Hosting", "IArtifactStorage.cs")));
        var dom = File.ReadAllText(Path.Combine(root, "Folio.Scripting", "Dom", "DomNodeTemplates.cs"));
        Assert.Contains("elementsFromPoint", dom, StringComparison.Ordinal);
        Assert.Contains("scrollIntoView", dom, StringComparison.Ordinal);
        Assert.Contains("setCustomValidity", dom, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tests", "Folio.Benchmarks", "ScriptedProcessBenchmark.cs")));
    }
}

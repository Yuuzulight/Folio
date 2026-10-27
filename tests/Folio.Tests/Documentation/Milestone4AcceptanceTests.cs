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
}

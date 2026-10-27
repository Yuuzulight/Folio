namespace Folio.Tests.Documentation;

public sealed class Milestone3AcceptanceTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void M3AuditNamesTheInteractionAndIncrementalAcceptanceEvidence()
    {
        var roadmap = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "roadmap.md"));
        Assert.Contains("Milestone 3 is complete", roadmap, StringComparison.Ordinal);
        Assert.Contains("accessibility tree", roadmap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("incremental-equivalence", roadmap, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("three-iteration", roadmap, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M3HasExecutableInteractionAndEquivalenceSuites()
    {
        var interaction = Path.Combine(RepositoryRoot, "tests", "Folio.Tests", "Interaction");
        Assert.True(Directory.EnumerateFiles(interaction, "*.cs").Any(), interaction);
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tests", "Folio.RenderTests", "IncrementalEquivalenceTests.cs")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tests", "Folio.Tests", "Interaction", "AccessibilityTreeTests.cs")));
    }
}

namespace Folio.Tests.Documentation;

public sealed class Milestone0DocumentationTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void EverySubsystemStudyIsCheckedInAndHasDecisionSections()
    {
        var study = Path.Combine(RepositoryRoot, "docs", "study");
        var expected = Enumerable.Range(1, 19)
            .Select(number => Directory.EnumerateFiles(study, $"{number:00}-*.md").SingleOrDefault())
            .ToArray();

        Assert.DoesNotContain(expected, path => path is null);
        foreach (var path in expected!)
        {
            var text = File.ReadAllText(path!);
            Assert.Contains("##", text, StringComparison.Ordinal);
            Assert.True(ContainsAny(text, "Decision", "decision"), $"Study lacks a decision section: {path}");
            Assert.True(ContainsAny(text, "Spec", "spec", "WHATWG", "OpenType", "W3C"), $"Study lacks a specification reference: {path}");
        }
    }

    [Fact]
    public void ArchitectureRoadmapAndDependencyPlanCoverTheM0AcceptanceSurface()
    {
        var docs = Path.Combine(RepositoryRoot, "docs");
        var architecture = File.ReadAllText(Path.Combine(docs, "architecture.md"));
        var roadmap = File.ReadAllText(Path.Combine(docs, "roadmap.md"));
        var dependencies = File.ReadAllText(Path.Combine(docs, "dependencies.md"));

        foreach (var term in new[] { "pipeline", "thread", "error", "public API" })
            Assert.Contains(term, architecture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## 0. Study and architecture", roadmap, StringComparison.Ordinal);
        Assert.Contains("## 1. Static rendering", roadmap, StringComparison.Ordinal);
        Assert.Contains("Required Folio-owned replacement track", dependencies, StringComparison.Ordinal);
    }

    private static bool ContainsAny(string text, params string[] values) => values.Any(text.Contains);
}

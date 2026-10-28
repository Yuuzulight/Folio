namespace Folio.Tests.Documentation;

public sealed class Milestone5AcceptanceTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void M5DocumentationSeparatesCompatibilityFromNativeReplacement()
    {
        var document = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "milestone-5-scripting.md"));
        Assert.Contains("dependency-backed compatibility baseline passes", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native Folio-engine run now matches", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mandatory Folio-owned", document, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("60/60", document, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D2")]
    [InlineData("D3")]
    [InlineData("D4")]
    [InlineData("D5")]
    [InlineData("D6")]
    [InlineData("D7")]
    public void EveryScriptedCategoryHasTenReviewedArtifacts(string category)
    {
        var directory = Path.Combine(RepositoryRoot, "tests", "conformance", category);
        var artifacts = Directory.EnumerateFiles(directory, "index.html", SearchOption.AllDirectories).ToArray();
        Assert.Equal(10, artifacts.Length);
        Assert.All(artifacts, artifact => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(artifact)!, "reference.png")), artifact));
    }
}

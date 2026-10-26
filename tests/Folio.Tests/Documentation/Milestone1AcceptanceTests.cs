namespace Folio.Tests.Documentation;

public sealed class Milestone1AcceptanceTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void CIWorkflowDeclaresBothHostsAndStaticVerificationStages()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));
        Assert.Contains("windows", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ubuntu", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Folio.RenderTests", workflow, StringComparison.Ordinal);
        Assert.Contains("Folio.Fuzz", workflow, StringComparison.Ordinal);
        Assert.Contains("Conformance report", workflow, StringComparison.Ordinal);
        Assert.Contains("Upload image test output", workflow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("S1")]
    [InlineData("S3")]
    public void StaticConformanceCategoriesContainManifestAndReferencePairs(string category)
    {
        var directory = Path.Combine(RepositoryRoot, "tests", "conformance", category);
        var manifests = Directory.EnumerateFiles(directory, "manifest.json", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(manifests);
        foreach (var manifest in manifests)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
            var referencePath = Path.Combine(Path.GetDirectoryName(manifest)!, "reference.png");
            Assert.True(File.Exists(referencePath), $"Missing reference for {manifest}");
        }
    }

    [Fact]
    public void StaticIntegrationAndSafetyTestProjectsArePresent()
    {
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tests", "Folio.RenderTests", "Folio.RenderTests.csproj")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tests", "Folio.WinForms.Tests", "Folio.WinForms.Tests.csproj")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "tests", "Folio.Fuzz", "Folio.Fuzz.csproj")));
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, "src", "Folio", "Resources", "IResourceLoader.cs")));
    }
}

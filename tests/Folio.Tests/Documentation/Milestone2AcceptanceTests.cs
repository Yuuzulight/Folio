namespace Folio.Tests.Documentation;

public sealed class Milestone2AcceptanceTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Theory]
    [InlineData("S1")]
    [InlineData("S2")]
    [InlineData("S3")]
    [InlineData("S4")]
    [InlineData("S5")]
    [InlineData("S6")]
    public void EveryStaticCategoryHasAReferenceCorpus(string category)
    {
        var directory = Path.Combine(RepositoryRoot, "tests", "conformance", category);
        var artifacts = Directory.EnumerateFiles(directory, "index.html", SearchOption.AllDirectories).ToArray();
        Assert.Equal(12, artifacts.Length);
        Assert.All(artifacts, artifact => Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(artifact)!, "reference.png")), artifact));
    }

    [Fact]
    public void M2FuzzTargetsCoverSvgAndFonts()
    {
        var targets = File.ReadAllText(Path.Combine(RepositoryRoot, "tests", "Folio.Fuzz", "Targets.cs"));
        Assert.Contains("new(\"svg\"", targets, StringComparison.Ordinal);
        Assert.Contains("new(\"font\"", targets, StringComparison.Ordinal);
        Assert.Contains("new(\"png\"", targets, StringComparison.Ordinal);
        Assert.Contains("new(\"jpeg\"", targets, StringComparison.Ordinal);
    }
}

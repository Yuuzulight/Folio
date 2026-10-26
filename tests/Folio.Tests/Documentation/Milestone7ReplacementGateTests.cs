namespace Folio.Tests.Documentation;

public sealed class Milestone7ReplacementGateTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void ReplacementGateIsExplicitlyMandatory()
    {
        var gate = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "replacement-gate.md"));
        Assert.Contains("hard, non-optional release gate", gate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Folio is complete only when every replacement", gate, StringComparison.Ordinal);
        Assert.Contains("superseded production dependency has been removed", gate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplacementGateNamesEveryRequiredTrackAndCurrentBlockers()
    {
        var gate = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "replacement-gate.md"));
        foreach (var track in new[] { "Text shaping", "Rasterisation", "Image codecs", "JavaScript", "WebAssembly" })
            Assert.Contains(track, gate, StringComparison.Ordinal);
        Assert.Contains("gate remains open", gate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Jint remains the production JavaScript engine", gate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Wasmtime remains part of the native WebAssembly path", gate, StringComparison.OrdinalIgnoreCase);
    }
}

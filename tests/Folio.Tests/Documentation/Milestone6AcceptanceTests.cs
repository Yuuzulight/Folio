namespace Folio.Tests.Documentation;

public sealed class Milestone6AcceptanceTests
{
    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

    [Fact]
    public void M6AuditCannotBeMarkedCompleteWhileOriginalGatesRemainOpen()
    {
        var audit = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "milestone-6-audit.md"));
        Assert.Contains("Milestone 6 is **not complete", audit, StringComparison.Ordinal);
        Assert.Contains("complete WebGL 2/integer/volume conformance", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continuous device-level media pacing", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("native-engine worker/Wasm execution", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("real permitted WebRTC capture/transport", audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isolated service-worker offline reopening", audit, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void M6AuditRecordsCurrentNativeConformanceEvidence()
    {
        var audit = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "milestone-6-audit.md"));
        Assert.Contains("Native Folio D8 conformance passes 6/6", audit, StringComparison.Ordinal);
        Assert.Contains("D9 5/5", audit, StringComparison.Ordinal);
        Assert.Contains("D10 4/4", audit, StringComparison.Ordinal);
        Assert.Contains("D11 4/4", audit, StringComparison.Ordinal);
        Assert.Contains("D12 4/4", audit, StringComparison.Ordinal);
        Assert.Contains("D13 4/4", audit, StringComparison.Ordinal);
        Assert.Contains("D14 3/3", audit, StringComparison.Ordinal);
    }
}

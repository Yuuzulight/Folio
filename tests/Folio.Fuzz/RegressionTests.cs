using Folio.RenderTests;

namespace Folio.Fuzz;

/// <summary>Every seed, and every input the fuzzer once failed on (kept in Regressions/&lt;target&gt;/), runs cleanly.</summary>
public class RegressionTests
{
    public static TheoryData<string> Names => new(Targets.All.Select(t => t.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public void SeedsAndPastFailuresRunCleanly(string name)
    {
        var target = Targets.Find(name);
        var folder = Path.Combine(RepoPaths.Tests, "Folio.Fuzz", "Regressions", name);
        var regressions = Directory.Exists(folder) ? Directory.GetFiles(folder).Select(File.ReadAllBytes) : [];
        foreach (var input in target.Seeds().Concat(regressions))
            target.Run(input);
    }
}

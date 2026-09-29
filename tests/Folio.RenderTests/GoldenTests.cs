namespace Folio.RenderTests;

public class GoldenTests
{
    // A theory without rows fails in some runners, so an empty suite yields one row that skips.
    private const string None = "(none)";

    public static TheoryData<string> Goldens =>
        GoldenSuite.Repo.Discover().ToList() is { Count: > 0 } tests ? new(tests) : new(None);

    [Theory]
    [MemberData(nameof(Goldens))]
    public void MatchesItsGolden(string test)
    {
        Assert.SkipWhen(test == None, "There are no golden tests yet.");

        Assert.Null(GoldenSuite.Repo.Run(test));
    }
}

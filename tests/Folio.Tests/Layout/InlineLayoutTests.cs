using System.Globalization;

namespace Folio.Tests.Layout;

public class InlineLayoutTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Layout", "Inline"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void LaysOut(string id)
    {
        var test = AllCases.Value[id];
        var viewport = test.Directives.FirstOrDefault(d => d.StartsWith("viewport ", StringComparison.Ordinal))?.Split(' ');
        var (width, height) = viewport is null ? (800f, 600f) : (float.Parse(viewport[1], CultureInfo.InvariantCulture), float.Parse(viewport[2], CultureInfo.InvariantCulture));

        Assert.Equal(test.Expected, BlockLayoutTests.Dump(BlockLayoutTests.LayOut(test.Input, width, height)));
    }
}

using Folio.Css;
using Folio.Style;

namespace Folio.Tests.Style;

public class ComputedValueTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> AllCases = new(() => CaseFiles.Load("Style", "Values"));

    public static TheoryData<string> Ids => new(AllCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(Ids))]
    public void Computes(string id)
    {
        var test = AllCases.Value[id];
        var parentDeclarations = test.Directives.FirstOrDefault(d => d.StartsWith("parent ", StringComparison.Ordinal))?["parent ".Length..];
        var parent = parentDeclarations is null ? ComputedStyle.Initial : Compute(parentDeclarations, ComputedStyle.Initial);

        var style = Compute(test.Input, parent);

        var actual = test.Expected.Select(line =>
        {
            var name = line[..line.IndexOf(':')];
            return $"{name}: {Properties.Find(name)!.Describe(style)}";
        });
        Assert.Equal(test.Expected, actual);
    }

    [Fact]
    public void EveryInitialValueComputesToTheInitialStyle()
    {
        foreach (var property in Properties.All)
        {
            var style = StyleBuilder.Compute(
                new Dictionary<PropertyId, CssValue> { [property.Id] = property.Initial },
                new ComputeContext(ComputedStyle.Initial, 16, 800, 600));

            Assert.True(property.Describe(ComputedStyle.Initial) == property.Describe(style),
                $"{property.Name}: initial style has {property.Describe(ComputedStyle.Initial)}, its initial value computes to {property.Describe(style)}");
        }
    }

    [Fact]
    public void UnchangedGroupsAreShared()
    {
        var parent = Compute("font-size: 20px; margin: 4px", ComputedStyle.Initial);

        var child = Compute("color: red", parent);

        Assert.Same(parent.Font, child.Font);
        Assert.Same(ComputedStyle.Initial.Spacing, child.Spacing);
        Assert.Same(ComputedStyle.Initial.Box, child.Box);
        Assert.NotSame(parent.Inherited, child.Inherited);
    }

    [Fact]
    public void CalcKeepsNonLinearExpressionsForLayout()
    {
        var style = Compute("width: min(50%, 300px)", ComputedStyle.Initial);

        Assert.Equal(200, style.Size.Width.Length.Resolve(400));
        Assert.Equal(300, style.Size.Width.Length.Resolve(1000));
    }

    [Fact]
    public void EveryPropertyHasAUniqueName()
    {
        Assert.Equal(Properties.All.Count, Properties.All.Select(p => p.Name).Distinct().Count());
        Assert.All(Properties.All, p => Assert.Same(p, Properties.Find(p.Name)));
    }

    private static ComputedStyle Compute(string declarations, ComputedStyle parent)
    {
        var (source, block) = CssParser.ParseBlockContents(declarations);
        var cascaded = new Dictionary<PropertyId, CssValue>();
        foreach (var declaration in block.Declarations)
        {
            if (Properties.Parse(source, declaration) is { } values)
            {
                foreach (var (id, value) in values)
                    cascaded[id] = value;
            }
        }
        return StyleBuilder.Compute(cascaded, new ComputeContext(parent, 16, 800, 600));
    }
}

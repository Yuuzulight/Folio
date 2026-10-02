using Folio.Css;

namespace Folio.Tests.Css;

public class PropertyDamageTests
{
    [Fact]
    public void EveryPropertyHasExactlyOneDamageTag()
    {
        var wrong = Enum.GetValues<PropertyId>().Where(id => PropertyDamage.TagCount(id) != 1).ToList();

        Assert.Empty(wrong);
    }

    [Fact]
    public void TagsSayWhatAChangeInvalidates()
    {
        Assert.Equal(Damage.Paint, PropertyDamage.Of(PropertyId.Color));
        Assert.Equal(Damage.Paint, PropertyDamage.Of(PropertyId.BackgroundColor));
        Assert.Equal(Damage.Layout, PropertyDamage.Of(PropertyId.Width));
        Assert.Equal(Damage.Boxes, PropertyDamage.Of(PropertyId.Display));
    }
}

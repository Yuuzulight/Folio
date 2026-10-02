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

    [Fact]
    public void ColorChangeReturnsPaintWithLayoutCountUnchanged()
    {
        using var document = Document.Parse("<!DOCTYPE html><div style='color: red'>hello</div>");
        document.Paint(800, 600);
        var count = document.LayoutCount;

        var div = document.QuerySelector("div")!;
        div.SetAttribute("style", "color: blue");

        var damage = document.Update();
        Assert.Equal(Damage.Paint, damage);
        Assert.Equal(count, document.LayoutCount);

        using var fresh = Document.Parse("<!DOCTYPE html><div style='color: blue'>hello</div>");
        var (full, _) = fresh.Paint(800, 600);
        Assert.Equal(full.Items, document.DisplayList!.Items);
    }

    [Fact]
    public void WidthChangeReturnsLayout()
    {
        using var document = Document.Parse("<!DOCTYPE html><div style='width: 100px'>hello</div>");
        document.Paint(800, 600);
        var count = document.LayoutCount;

        var div = document.QuerySelector("div")!;
        div.SetAttribute("style", "width: 200px");

        var damage = document.Update();
        Assert.Equal(Damage.Layout, damage);
        Assert.Equal(count + 1, document.LayoutCount);

        using var fresh = Document.Parse("<!DOCTYPE html><div style='width: 200px'>hello</div>");
        var (full, _) = fresh.Paint(800, 600);
        Assert.Equal(full.Items, document.DisplayList!.Items);
    }

    [Fact]
    public void DisplayChangeReturnsBoxes()
    {
        using var document = Document.Parse("<!DOCTYPE html><div style='display: block'>hello</div>");
        document.Paint(800, 600);
        var count = document.LayoutCount;

        var div = document.QuerySelector("div")!;
        div.SetAttribute("style", "display: inline");

        var damage = document.Update();
        Assert.Equal(Damage.Boxes, damage);
        Assert.Equal(count + 1, document.LayoutCount);

        using var fresh = Document.Parse("<!DOCTYPE html><div style='display: inline'>hello</div>");
        var (full, _) = fresh.Paint(800, 600);
        Assert.Equal(full.Items, document.DisplayList!.Items);
    }

    [Fact]
    public void NodeInsertionReturnsBoxes()
    {
        using var document = Document.Parse("<!DOCTYPE html><div>hello</div>");
        document.Paint(800, 600);
        var count = document.LayoutCount;

        var div = document.QuerySelector("div")!;
        var span = document.CreateElement("span");
        div.AppendChild(span);

        var damage = document.Update();
        Assert.Equal(Damage.Boxes, damage);
        Assert.Equal(count + 1, document.LayoutCount);
    }
}

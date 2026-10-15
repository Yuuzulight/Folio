using Folio.Css;
using Folio.Painting;

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

    // The inside disclosure triangle of <summary> is a marker box in an inline run, not a child of a block's Marker slot:
    // a colour change has to reach it, or the update draws the old colour and a fresh render draws the new one.
    [Fact]
    public void ColorChangeOnASummaryRecoloursItsDisclosureTriangle()
    {
        const string Html = "<!DOCTYPE html><details open><summary{0}>Question</summary><p>Answer</p></details>";
        using var document = Document.Parse(string.Format(Html, ""));
        document.Paint(800, 600);
        document.QuerySelector("summary")!.SetAttribute("style", "color: red");

        Assert.Equal(Damage.Paint, document.Update());

        using var fresh = Document.Parse(string.Format(Html, " style='color: red'"));
        var (full, _) = fresh.Paint(800, 600);
        var triangle = (DisplayItem item) => item.Kind == DisplayItemKind.FillPath;
        Assert.Equal(full.Items.Where(triangle).Select(i => i.Color), document.DisplayList!.Items.Where(triangle).Select(i => i.Color));
    }

    // An inline SVG's paints are resolved when its render tree is built in layout, so a colour change on the SVG, inside
    // it or above it has to build that tree again.
    [Theory]
    [InlineData("span")]
    [InlineData("circle")]
    public void ColorChangeAroundAnInlineSvgRecoloursItsCurrentColorStroke(string target)
    {
        const string Html = "<!DOCTYPE html><span{0}><svg width='24' height='24' viewBox='0 0 24 24'><circle{1} cx='12' cy='12' r='5' fill='none' stroke='currentColor'/></svg></span>";
        using var document = Document.Parse(string.Format(Html, "", ""));
        document.Paint(800, 600);
        document.QuerySelector(target)!.SetAttribute("style", "color: red");
        document.Update();

        using var fresh = Document.Parse(string.Format(Html, target == "span" ? " style='color: red'" : "", target == "circle" ? " style='color: red'" : ""));
        var (full, _) = fresh.Paint(800, 600);
        var strokes = (DisplayItem item) => item.Kind == DisplayItemKind.StrokePath;
        Assert.Equal(full.Items.Where(strokes).Select(i => i.Color), document.DisplayList!.Items.Where(strokes).Select(i => i.Color));
    }
}


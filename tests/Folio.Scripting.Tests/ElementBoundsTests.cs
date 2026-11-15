using Folio.Hosting;
using Folio.Scripting.Dom;
using Folio.Scripting.Native;
using Xunit;

namespace Folio.Scripting.Tests;

// getBoundingClientRect on a laid-out page: a page that reads every link's rectangle (a page snapshot does) stays inside the script
// memory budget, and the rectangles follow the layout.
public class ElementBoundsTests
{
    private static (Document Document, NativeJavaScriptEngine Engine) Page(int links)
    {
        var html = "<!doctype html><body style='margin:0'>" + string.Concat(Enumerable.Range(0, links).Select(i => $"<div style='height:20px'><a href=\"#c{i}\">comment {i}</a></div>")) + "</body>";
        var document = Document.Parse(html);
        document.Paint(1280, 720, 1, Folio.Typography.ComplexShaper.Instance, 0);
        var engine = new NativeJavaScriptEngine();
        DomBindings.Install(engine, document);
        return (document, engine);
    }

    [Fact]
    public void ReadingEveryLinksRectangleOfALargePageStaysWithinTheScriptMemoryBudget()
    {
        var (_, engine) = Page(300);
        using var _ = engine;

        var measured = engine.Evaluate("(function(){var c=document.querySelectorAll('a[href]');var n=0;for(var i=0;i<c.length;i++){var r=c[i].getBoundingClientRect();if(r.width>0&&r.height>0)n++;}return n;})()");

        Assert.Equal(300, measured.AsNumber());
    }

    [Fact]
    public void RectanglesFollowTheLayoutRowByRow()
    {
        var (_, engine) = Page(20);
        using var _ = engine;

        Assert.Equal(0, engine.Evaluate("document.querySelectorAll('div')[0].getBoundingClientRect().top").AsNumber());
        Assert.Equal(20 * 7, engine.Evaluate("document.querySelectorAll('div')[7].getBoundingClientRect().top").AsNumber());
        Assert.Equal(20 * 19, engine.Evaluate("document.querySelectorAll('div')[19].getBoundingClientRect().top").AsNumber());
    }

    [Fact]
    public void RectanglesAreReadAgainAfterThePageIsLaidOutAgain()
    {
        var (document, engine) = Page(5);
        using var _ = engine;
        Assert.Equal(40, engine.Evaluate("document.querySelectorAll('div')[2].getBoundingClientRect().top").AsNumber());

        engine.Evaluate("document.querySelectorAll('div')[0].style.height = '100px'; 0");
        document.Paint(1280, 720, 1, Folio.Typography.ComplexShaper.Instance, 0);

        Assert.Equal(100 + 20, engine.Evaluate("document.querySelectorAll('div')[2].getBoundingClientRect().top").AsNumber());
    }
}

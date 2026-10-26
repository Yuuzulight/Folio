using Folio.Scripting.Dom;

namespace Folio.Scripting.Tests;

/// <summary><c>ResizeObserver</c> (#464) and <c>IntersectionObserver</c> (#465): what they report at each rendering opportunity.</summary>
public class ObserverTests
{
    // ---- ResizeObserver ----

    [Fact]
    public void AResizeObserverReportsTheInitialSizeAtTheFirstFrame()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:50px;padding:10px;border:2px solid'></div>");
        page.Run("new ResizeObserver(function (entries, observer) { entries.forEach(function (e) { var c = e.contentRect; log.push([e.target.id, c.x, c.y, c.width, c.height, e.contentBoxSize[0].inlineSize, e.contentBoxSize[0].blockSize, e.borderBoxSize[0].inlineSize, e.borderBoxSize[0].blockSize].join(':')); }); log.push(String(observer instanceof ResizeObserver)); }).observe(el('a'));");
        Assert.Equal("", page.Log());
        page.Frame();
        Assert.Equal("a:10:10:100:50:100:50:124:74 true", page.Log());
        page.Frame();
        Assert.Equal("a:10:10:100:50:100:50:124:74 true", page.Log());
    }

    [Fact]
    public void ASizeChangeIsReportedAtTheNextFrameAndNothingElseIs()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:50px'></div>");
        page.Run("new ResizeObserver(function (entries) { log.push(entries[0].contentRect.width + 'x' + entries[0].contentRect.height); }).observe(el('a'));");
        page.Frame();
        page.Run("el('a').style.width = '200px';");
        Assert.Equal("100x50", page.Log());
        page.Frame();
        Assert.Equal("100x50 200x50", page.Log());
        page.Run("el('a').style.color = 'red';");
        page.Frame();
        Assert.Equal("100x50 200x50", page.Log());
    }

    [Fact]
    public void TheBorderBoxOptionWatchesThePaddingToo()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:50px'></div>");
        page.Run("new ResizeObserver(function (entries) { log.push('b' + entries.length); }).observe(el('a'), { box: 'border-box' });");
        page.Frame();
        page.Run("el('a').style.padding = '5px';");
        page.Frame();
        Assert.Equal("b1 b1", page.Log());
        page.Run("var plain = new ResizeObserver(function () { log.push('c'); }); plain.observe(el('a')); el('a').style.padding = '9px'; el('a').style.boxSizing = 'border-box';");
        page.Frame();
        // The border box shrank back to 100 wide when the box became border-box; the content-box observer reports its first size.
        Assert.Equal("b1 b1 b1 c", page.Log());
    }

    [Fact]
    public void AnElementThatLosesItsBoxIsReportedAsZeroAndUnobservingStopsReports()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:100px;height:50px'></div><div id=b style='width:10px;height:10px'></div>");
        page.Run("var ro = new ResizeObserver(function (entries) { entries.forEach(function (e) { log.push(e.target.id + e.contentRect.width); }); }); ro.observe(el('a')); ro.observe(el('b'));");
        page.Frame();
        Assert.Equal("a100 b10", page.Log());
        page.Run("el('a').style.display = 'none'; ro.unobserve(el('b')); el('b').style.width = '30px';");
        page.Frame();
        Assert.Equal("a100 b10 a0", page.Log());
        page.Run("ro.disconnect(); el('a').style.display = 'block';");
        page.Frame();
        Assert.Equal("a100 b10 a0", page.Log());
    }

    [Fact]
    public void ObserversAreNotifiedInTheOrderTheyWereCreatedWithinOneFrame()
    {
        using var page = new CssomViewTests.Page("<div id=a style='width:10px;height:10px'></div>");
        page.Run("new ResizeObserver(function () { log.push('first'); }).observe(el('a')); new ResizeObserver(function () { log.push('second'); }).observe(el('a'));");
        page.Frame();
        Assert.Equal("first second", page.Log());
    }

    [Fact]
    public void ADeeperResizeCausedByACallbackIsDeliveredInTheSameFrame()
    {
        using var page = new CssomViewTests.Page("<div id=outer style='width:100px;height:100px'><div id=inner style='width:10px;height:10px'></div></div>");
        page.Run("new ResizeObserver(function () { log.push('outer'); el('inner').style.width = '40px'; }).observe(el('outer'));"
            + "new ResizeObserver(function (entries) { log.push('inner:' + entries[0].contentRect.width); }).observe(el('inner'));");
        page.Frame();
        // Both are first observations, so they are delivered together; the width the outer callback set shows next, deeper, in the same frame.
        Assert.Equal("outer inner:10 inner:40", page.Log());
    }

    [Fact]
    public void AResizeThatCannotBeDeliveredInTheFrameRaisesAnErrorEventAndIsDeliveredNextFrame()
    {
        using var page = new CssomViewTests.Page("<div id=outer style='width:100px;height:100px'><div id=inner style='width:10px;height:10px'></div></div>");
        page.Run("window.addEventListener('error', function (e) { log.push('error:' + e.message); });"
            + "new ResizeObserver(function (entries) { log.push('outer:' + entries[0].contentRect.width); }).observe(el('outer'));"
            + "var grown = false; new ResizeObserver(function () { if (!grown) { grown = true; el('outer').style.width = '150px'; } }).observe(el('inner'));");
        page.Frame();
        Assert.Equal("outer:100 error:ResizeObserver loop completed with undelivered notifications.", page.Log());
        page.Frame();
        Assert.EndsWith("outer:150", page.Log());
    }

    [Fact]
    public void ResizeObserverRejectsWhatIsNotAnElementOrACallback()
    {
        using var page = new CssomViewTests.Page("<div id=a></div>");
        Assert.Equal("TypeError", page.Run("try { new ResizeObserver(); } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new ResizeObserver(1); } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new ResizeObserver(function () {}).observe({}); } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new ResizeObserver(function () {}).observe(el('a'), { box: 'margin-box' }); } catch (e) { e.name }"));
        Assert.Equal("[object ResizeObserver]", page.Run("Object.prototype.toString.call(new ResizeObserver(function () {}))"));
    }

    // ---- IntersectionObserver ----

    private const string Spacer = "<div style='height:2000px;position:absolute;top:0;width:1px'></div>";

    [Fact]
    public void AnIntersectionObserverReportsTheInitialStateThenEachCrossing()
    {
        using var page = new CssomViewTests.Page(Spacer + "<div id=t style='position:absolute;top:700px;left:0;width:100px;height:50px'></div>");
        page.Run("new IntersectionObserver(function (entries) { entries.forEach(function (e) { log.push(e.isIntersecting + '/' + e.intersectionRatio + '/' + e.boundingClientRect.top); }); }).observe(el('t'));");
        page.Frame();
        Assert.Equal("false/0/700", page.Log());
        page.Frame();
        Assert.Equal("false/0/700", page.Log());
        page.Run("window.scrollTo(0, 400);");
        page.Frame();
        Assert.Equal("false/0/700 true/1/300", page.Log());
        page.Run("window.scrollTo(0, 0);");
        page.Frame();
        Assert.Equal("false/0/700 true/1/300 false/0/700", page.Log());
    }

    [Fact]
    public void ThresholdsReportEachRatioThatIsCrossed()
    {
        using var page = new CssomViewTests.Page(Spacer + "<div id=t style='position:absolute;top:550px;left:0;width:100px;height:100px'></div>");
        page.Run("new IntersectionObserver(function (entries) { entries.forEach(function (e) { log.push(e.intersectionRatio.toFixed(2)); }); }, { threshold: [0, 0.5, 1] }).observe(el('t'));");
        page.Frame();
        Assert.Equal("0.50", page.Log());
        page.Run("window.scrollTo(0, 30);");
        page.Frame();
        Assert.Equal("0.50", page.Log()); // 0.8 is still between the thresholds 0.5 and 1
        page.Run("window.scrollTo(0, 60);");
        page.Frame();
        Assert.Equal("0.50 1.00", page.Log());
        page.Run("window.scrollTo(0, 70);");
        page.Frame();
        Assert.Equal("0.50 1.00", page.Log());
    }

    [Fact]
    public void TheRootMarginGrowsTheRootThatTheTargetIsMeasuredAgainst()
    {
        using var page = new CssomViewTests.Page(Spacer + "<div id=t style='position:absolute;top:650px;left:0;width:100px;height:50px'></div>");
        page.Run("var plain = new IntersectionObserver(function (es) { log.push('plain:' + es[0].isIntersecting); }); plain.observe(el('t'));"
            + "var margin = new IntersectionObserver(function (es) { log.push('margin:' + es[0].isIntersecting + ':' + es[0].rootBounds.height); }, { rootMargin: '100px 0px' }); margin.observe(el('t'));");
        page.Frame();
        Assert.Equal("plain:false margin:true:800", page.Log());
    }

    [Fact]
    public void AScrollContainerClipsWhatIsInsideIt()
    {
        using var page = new CssomViewTests.Page("<div id=s style='overflow:auto;width:100px;height:100px'><div style='height:150px'></div><div id=t style='height:40px;width:40px'></div><div style='height:400px'></div></div>");
        page.Run("new IntersectionObserver(function (es) { es.forEach(function (e) { log.push(String(e.isIntersecting)); }); }).observe(el('t'));");
        page.Frame();
        Assert.Equal("false", page.Log());
        page.Run("el('s').scrollTop = 100;");
        page.Frame();
        Assert.Equal("false true", page.Log());
    }

    [Fact]
    public void AnExplicitRootMeasuresTheTargetAgainstThatElement()
    {
        using var page = new CssomViewTests.Page("<div id=s style='overflow:auto;width:100px;height:100px'><div style='height:150px'></div><div id=t style='height:40px;width:40px'></div><div style='height:400px'></div></div>");
        page.Run("var io = new IntersectionObserver(function (es) { es.forEach(function (e) { log.push(e.isIntersecting + '/' + e.rootBounds.width); }); }, { root: el('s') }); io.observe(el('t'));");
        page.Frame();
        Assert.Equal("false/100", page.Log());
        page.Run("el('s').scrollTop = 80;");
        page.Frame();
        Assert.Equal("false/100 true/100", page.Log());
        Assert.Equal("s", page.Run("io.root.id"));
    }

    [Fact]
    public void AClippingAncestorCutsTheIntersectionAndTheRatio()
    {
        using var page = new CssomViewTests.Page("<div style='overflow:hidden;width:100px;height:50px'><div id=t style='height:100px;width:100px'></div></div>");
        page.Run("new IntersectionObserver(function (es) { log.push(es[0].intersectionRatio + ':' + es[0].intersectionRect.height); }).observe(el('t'));");
        page.Frame();
        Assert.Equal("0.5:50", page.Log());
    }

    [Fact]
    public void AnElementWithNoBoxDoesNotIntersect()
    {
        using var page = new CssomViewTests.Page("<div id=t style='display:none;width:10px;height:10px'></div>");
        page.Run("new IntersectionObserver(function (es) { log.push(String(es[0].isIntersecting)); }).observe(el('t'));");
        page.Frame();
        Assert.Equal("false", page.Log());
        page.Run("el('t').style.display = 'block';");
        page.Frame();
        Assert.Equal("false true", page.Log());
    }

    [Fact]
    public void ATargetThatOnlyTouchesTheRootEdgeStillIntersects()
    {
        using var page = new CssomViewTests.Page(Spacer + "<div id=t style='position:absolute;top:600px;left:0;width:100px;height:50px'></div>");
        page.Run("new IntersectionObserver(function (es) { log.push(es[0].isIntersecting + '/' + es[0].intersectionRatio); }).observe(el('t'));");
        page.Frame();
        Assert.Equal("true/0", page.Log());
    }

    [Fact]
    public void UnobservingAndTakingRecordsBehaveLikeTheirNames()
    {
        using var page = new CssomViewTests.Page("<div id=t style='width:10px;height:10px'></div>");
        page.Run("var io = new IntersectionObserver(function () { log.push('called'); }); io.observe(el('t')); io.unobserve(el('t'));");
        page.Frame();
        Assert.Equal("", page.Log());
        page.Run("io.observe(el('t')); var taken = io.takeRecords();");
        Assert.Equal("0", page.Run("String(taken.length)"));
        page.Frame();
        Assert.Equal("called", page.Log());
        page.Run("io.disconnect(); el('t').style.display = 'none';");
        page.Frame();
        Assert.Equal("called", page.Log());
    }

    [Fact]
    public void TheConstructorValidatesItsOptions()
    {
        using var page = new CssomViewTests.Page("<div id=a></div>");
        Assert.Equal("RangeError", page.Run("try { new IntersectionObserver(function () {}, { threshold: 2 }); } catch (e) { e.name }"));
        Assert.Equal("SyntaxError", page.Run("try { new IntersectionObserver(function () {}, { rootMargin: '5' }); } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new IntersectionObserver(function () {}, { root: {} }); } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { new IntersectionObserver(1); } catch (e) { e.name }"));
        Assert.Equal("10px 5% 10px 5%", page.Run("new IntersectionObserver(function () {}, { rootMargin: '10px 5%' }).rootMargin"));
        Assert.Equal("0,0.25,1", page.Run("new IntersectionObserver(function () {}, { threshold: [1, 0, 0.25] }).thresholds.join()"));
        Assert.Equal("0px 0px 0px 0px", page.Run("new IntersectionObserver(function () {}).rootMargin"));
        Assert.Equal("0", page.Run("new IntersectionObserver(function () {}).thresholds.join()"));
    }
}

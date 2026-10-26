using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Typography;

namespace Folio.Scripting.Tests;

/// <summary>CSSOM View (#461, #462, #463): element geometry, scrolling, <c>elementFromPoint</c>, <c>matchMedia</c> and their events.</summary>
public class CssomViewTests
{
    private static string FontsFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "fonts")))
                return Path.Combine(dir.FullName, "tests", "fonts");
        }
        throw new DirectoryNotFoundException("tests/fonts");
    }

    internal sealed class Page : IDisposable
    {
        public Page(string body, int width = 800, int height = 600, ColorScheme scheme = ColorScheme.Light)
        {
            Document = Document.Parse("<!DOCTYPE html><style>body { margin: 0 }</style><body>" + body, new FolioOptions
            {
                Scripting = ScriptingPolicy.ContentProcess,
                ColorScheme = scheme,
                Fonts = new FontSettings { Source = new FontFolderSource(FontsFolder()), GenericFamilies = new[] { "serif", "sans-serif", "monospace", "system-ui" }.ToDictionary(g => g, _ => (IReadOnlyList<string>)["Folio Box"], StringComparer.OrdinalIgnoreCase) },
            });
            Engine = new JintScriptEngine();
            Loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, Document, null, Loop);
            Paint(width, height);
            Engine.Evaluate("var log = []; function el(id) { return document.getElementById(id); } function r(id) { var b = el(id).getBoundingClientRect(); return [b.left, b.top, b.width, b.height].map(function (n) { return Math.round(n * 100) / 100; }).join(','); }");
        }

        public Document Document { get; }
        public JintScriptEngine Engine { get; }
        public HtmlEventLoop Loop { get; }

        public void Paint(int width, int height, double? time = null) => Document.Paint(width, height, 1, new Folio.Skia.HarfBuzzShaper(), time);

        public string Run(string script) => Engine.Evaluate(script).ToString() ?? "";

        public void Frame() => Loop.UpdateTheRendering();

        public string Log() => Run("log.join(' ')");

        public void Dispose()
        {
            Loop.Dispose();
            Engine.Dispose();
        }
    }

    // ---- geometry ----

    [Fact]
    public void ABlockBoxReportsItsBorderBoxAndItsPaddingBox()
    {
        using var page = new Page("<div id=a style='width:100px;height:40px;margin:10px 0 0 20px;padding:5px;border:2px solid'></div>");
        Assert.Equal("20,10,114,54", page.Run("r('a')"));
        Assert.Equal("114,54,110,50,2,2", page.Run("var a = el('a'); [a.offsetWidth, a.offsetHeight, a.clientWidth, a.clientHeight, a.clientLeft, a.clientTop].join()"));
        Assert.Equal("20,10", page.Run("a.offsetLeft + ',' + a.offsetTop"));
        Assert.Equal("BODY", page.Run("a.offsetParent.tagName"));
        Assert.Equal("1", page.Run("a.getClientRects().length.toString()"));
    }

    [Fact]
    public void AnInlineElementSplitOverLinesHasARectanglePerLine()
    {
        using var page = new Page("<p id=p style='width:60px;margin:0;font-size:16px'><span id=s>aaa bbb ccc</span></p>");
        var count = int.Parse(page.Run("el('s').getClientRects().length.toString()"));
        Assert.True(count >= 2, $"the span is on {count} lines");
        Assert.Equal("true", page.Run("var rects = Array.from(el('s').getClientRects()); String(rects.every(function (x, i) { return i === 0 || x.top > rects[i - 1].top; }))"));
        Assert.Equal("true", page.Run("var box = el('s').getBoundingClientRect(); String(box.height > rects[0].height && box.top === rects[0].top)"));
    }

    [Fact]
    public void PositionedBoxesAreMeasuredFromTheirOffsetParent()
    {
        using var page = new Page("<div id=p style='position:relative;left:20px;top:30px;border:3px solid;width:100px;height:100px'><div id=c style='position:absolute;left:5px;top:6px;width:10px;height:10px'></div></div><div id=free style='position:absolute;left:30px;top:40px;width:5px;height:5px'></div>");
        Assert.Equal("p,5,6", page.Run("var c = el('c'); c.offsetParent.id + ',' + c.offsetLeft + ',' + c.offsetTop"));
        Assert.Equal("28,39", page.Run("var b = c.getBoundingClientRect(); b.left + ',' + b.top")); // 20 + 3 + 5 and 30 + 3 + 6
        Assert.Equal("BODY,20,30", page.Run("var p = el('p'); p.offsetParent.tagName + ',' + p.offsetLeft + ',' + p.offsetTop"));
        Assert.Equal("BODY,30,40", page.Run("var f = el('free'); f.offsetParent.tagName + ',' + f.offsetLeft + ',' + f.offsetTop"));
        Assert.Equal("null,0,0", page.Run("document.body.offsetParent + ',' + document.body.offsetLeft + ',' + document.body.offsetTop"));
    }

    [Fact]
    public void TransformsMoveTheBoundingRectangleButNotTheOffsets()
    {
        using var page = new Page("<div id=a style='margin:100px 0 0 100px;width:20px;height:10px;transform:rotate(90deg)'></div><div id=b style='margin-left:300px;width:20px;height:10px;transform:translate(50px,25px)'></div><div id=c style='margin-left:400px;width:20px;height:10px;transform:scale(2)'></div>");
        // Turned a quarter about its centre (110, 105): 10 wide, 20 high.
        Assert.Equal("105,95,10,20", page.Run("r('a')"));
        Assert.Equal("20,10,100,100", page.Run("var a = el('a'); [a.offsetWidth, a.offsetHeight, a.offsetLeft, a.offsetTop].join()"));
        Assert.Equal("350", page.Run("el('b').getBoundingClientRect().left.toString()"));
        Assert.Equal("300", page.Run("el('b').offsetLeft.toString()"));
        Assert.Equal("40,20", page.Run("var c = el('c').getBoundingClientRect(); c.width + ',' + c.height"));
    }

    [Fact]
    public void ATableCellsOffsetParentIsItsTable()
    {
        using var page = new Page("<table id=t style='border-collapse:collapse;margin:10px'><tr><td id=td style='width:30px;height:20px;padding:0'></td></tr></table>");
        Assert.Equal("TABLE", page.Run("el('td').offsetParent.tagName"));
        Assert.Equal("30,20", page.Run("var td = el('td'); td.offsetWidth + ',' + td.offsetHeight"));
        Assert.Equal("true", page.Run("String(td.getBoundingClientRect().left >= 10)"));
    }

    [Fact]
    public void GeometryFollowsAScrollContainer()
    {
        using var page = new Page("<div id=box style='overflow:auto;width:100px;height:50px;margin-left:30px'><div style='height:100px'></div><div id=in style='height:20px'></div><div style='height:200px'></div></div>");
        Assert.Equal("30,100,100,20", page.Run("var i = el('in').getBoundingClientRect(); [i.left, i.top, i.width, i.height].join()"));
        page.Run("el('box').scrollTop = 60;");
        Assert.Equal("40", page.Run("el('in').getBoundingClientRect().top.toString()")); // 100 - 60
        Assert.Equal("100", page.Run("el('in').offsetTop.toString()")); // layout position: not scrolled
    }

    [Fact]
    public void AnElementWithNoBoxHasNoGeometry()
    {
        using var page = new Page("<div id=gone style='display:none;width:50px;height:50px'><span id=inside>x</span></div>");
        Assert.Equal("0,0,0,0", page.Run("r('gone')"));
        Assert.Equal("0,0", page.Run("var g = el('gone'); g.offsetWidth + ',' + g.clientHeight"));
        Assert.Equal("0", page.Run("g.getClientRects().length.toString()"));
        Assert.Equal("null", page.Run("String(g.offsetParent)"));
        Assert.Equal("0", page.Run("el('inside').getClientRects().length.toString()"));
        Assert.Equal("0,0,0,0", page.Run("var d = document.createElement('div'); var b = d.getBoundingClientRect(); [b.x, b.y, b.width, b.height].join()"));
    }

    [Fact]
    public void ThePageScrollsAndTheRectanglesFollowIt()
    {
        using var page = new Page("<div style='height:1000px'></div><div id=late style='height:50px'></div><div style='height:1000px'></div>");
        Assert.Equal("0,1000", page.Run("r('late').split(',').slice(0, 2).join()"));
        page.Run("window.scrollTo(0, 900);");
        Assert.Equal("900,900,0", page.Run("scrollY + ',' + pageYOffset + ',' + scrollX"));
        Assert.Equal("100", page.Run("el('late').getBoundingClientRect().top.toString()"));
        Assert.Equal("1000", page.Run("el('late').offsetTop.toString()")); // the layout position: not scrolled
        // What is under a point of the viewport is what is scrolled there.
        Assert.Equal("late", page.Run("document.elementFromPoint(10, 120).id"));
        Assert.Equal("1", page.Run("document.elementsFromPoint(10, 120).filter(function (e) { return e.id === 'late'; }).length.toString()"));
        page.Run("window.scrollBy(0, 50);");
        Assert.Equal("950,50", page.Run("scrollY + ',' + el('late').getBoundingClientRect().top"));
        page.Run("scrollTo({top: 1e6});");
        Assert.Equal("1450", page.Run("scrollY.toString()")); // clamped: 2050 - 600
    }

    // ---- elementFromPoint ----

    [Fact]
    public void ElementFromPointFindsTheTopmostElementAndElementsFromPointListsThemAll()
    {
        using var page = new Page("<div id=back style='position:absolute;left:0;top:0;width:100px;height:100px;z-index:1'></div><div id=front style='position:absolute;left:50px;top:50px;width:100px;height:100px;z-index:2'></div><div id=ghost style='position:absolute;left:0;top:200px;width:50px;height:50px;pointer-events:none'></div><div id=hidden style='position:absolute;left:0;top:300px;width:50px;height:50px;visibility:hidden'></div>");
        Assert.Equal("front", page.Run("document.elementFromPoint(75, 75).id"));
        Assert.Equal("back", page.Run("document.elementFromPoint(10, 10).id"));
        Assert.Equal("front,back,HTML", page.Run("document.elementsFromPoint(75, 75).map(function (e) { return e.id || e.tagName; }).join()")); // the body has no height: nothing of it is there
        Assert.Equal("HTML", page.Run("document.elementFromPoint(500, 500).tagName"));
        Assert.Equal("HTML", page.Run("document.elementFromPoint(10, 220).tagName")); // pointer-events: none
        Assert.Equal("HTML", page.Run("document.elementFromPoint(10, 320).tagName")); // visibility: hidden
        Assert.Equal("null", page.Run("String(document.elementFromPoint(-1, 10))"));
        Assert.Equal("null", page.Run("String(document.elementFromPoint(10, 601))"));
        Assert.Equal("0", page.Run("document.elementsFromPoint(900, 10).length.toString()"));
        Assert.Equal("TypeError", page.Run("try { document.elementFromPoint(1); 'no' } catch (e) { e.name }"));
    }

    // ---- scrolling ----

    [Fact]
    public void ScrollPositionsAreClampedToTheScrollRange()
    {
        using var page = new Page("<div id=c style='overflow:auto;width:50px;height:50px'><div style='width:120px;height:200px'></div></div>");
        Assert.Equal("200,50,120,50", page.Run("var c = el('c'); [c.scrollHeight, c.clientHeight, c.scrollWidth, c.clientWidth].join()"));
        Assert.Equal("150,70", page.Run("c.scrollTop = 1e6; c.scrollLeft = 70; c.scrollTop + ',' + c.scrollLeft"));
        Assert.Equal("0,0", page.Run("c.scrollTop = -5; c.scrollLeft = -1; c.scrollTop + ',' + c.scrollLeft"));
        Assert.Equal("10,20", page.Run("c.scrollTo(20, 10); c.scrollTop + ',' + c.scrollLeft"));
        Assert.Equal("15,20", page.Run("c.scrollTo({top: 15}); c.scrollTop + ',' + c.scrollLeft")); // an absent coordinate stays
        Assert.Equal("25,30", page.Run("c.scrollBy(10, 10); c.scrollTop + ',' + c.scrollLeft"));
        Assert.Equal("30,30", page.Run("c.scrollBy({top: 5}); c.scrollTop + ',' + c.scrollLeft"));
        Assert.Equal("0,0", page.Run("c.scroll(0, 0); c.scrollTop + ',' + c.scrollLeft"));
        Assert.Equal("TypeError", page.Run("try { c.scrollTo(5); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void ASmoothScrollReachesItsTargetOverTheFramesThatFollow()
    {
        using var page = new Page("<div id=c style='overflow:auto;width:50px;height:50px'><div style='height:400px'></div></div>");
        page.Paint(800, 600, 0);
        page.Run("el('c').scrollTo({top: 100, behavior: 'smooth'});");
        Assert.Equal("0", page.Run("el('c').scrollTop.toString()"));
        page.Paint(800, 600, 0.1);
        var midway = double.Parse(page.Run("el('c').scrollTop.toString()"), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(midway, 1, 99);
        page.Paint(800, 600, 0.5);
        Assert.Equal("100", page.Run("el('c').scrollTop.toString()"));
    }

    [Fact]
    public void ScrollIntoViewScrollsEveryScrollerAroundTheTarget()
    {
        using var page = new Page("<div style='height:300px'></div><div id=outer style='overflow:auto;width:100px;height:100px'><div style='height:200px'></div><div id=inner style='overflow:auto;width:80px;height:50px'><div style='height:150px'></div><div id=target style='height:20px'></div><div style='height:100px'></div></div><div style='height:400px'></div></div>");
        page.Run("el('target').scrollIntoView();");
        Assert.True(double.Parse(page.Run("el('inner').scrollTop.toString()"), System.Globalization.CultureInfo.InvariantCulture) > 0, "the inner scroller scrolled");
        Assert.True(double.Parse(page.Run("el('outer').scrollTop.toString()"), System.Globalization.CultureInfo.InvariantCulture) > 0, "the outer scroller scrolled");
        Assert.Equal("true", page.Run("var t = el('target').getBoundingClientRect(); String(t.top >= 0 && t.bottom <= 600 && t.top >= el('outer').getBoundingClientRect().top)"));
    }

    [Fact]
    public void ScrollEventsFireOncePerFramePerScroller()
    {
        using var page = new Page("<div id=c style='overflow:auto;width:50px;height:50px'><div style='height:400px'></div></div><div style='height:2000px'></div>");
        page.Run("el('c').addEventListener('scroll', function (e) { log.push('c:' + e.bubbles); }); document.addEventListener('scroll', function () { log.push('doc'); }); window.addEventListener('scroll', function () { log.push('window'); });");
        page.Run("el('c').scrollTop = 10; el('c').scrollTop = 20; el('c').scrollTop = 30;");
        Assert.Equal("", page.Log()); // not yet: the event waits for the next frame
        page.Frame();
        Assert.Equal("c:false", page.Log());
        page.Run("log.length = 0;");
        page.Frame();
        Assert.Equal("", page.Log()); // nothing changed since
        page.Run("el('c').scrollTop = 30;"); // no change: no event
        page.Frame();
        Assert.Equal("", page.Log());
        page.Run("window.scrollTo(0, 100); el('c').scrollTop = 0;");
        page.Frame();
        Assert.Equal("3", page.Run("log.length.toString()"));
        Assert.Contains("c:false", page.Log());
        Assert.Contains("doc", page.Log());
        Assert.Contains("window", page.Log()); // the viewport's scroll event is the document's, and bubbles to the window
    }

    [Fact]
    public void ScrollingByTheUserFiresTheSameEvents()
    {
        using var page = new Page("<div id=c style='overflow:auto;width:100px;height:100px'><div style='height:500px'></div></div>");
        page.Run("el('c').addEventListener('scroll', function () { log.push('c'); });");
        page.Document.Input.HandleWheel(new WheelEvent(new System.Numerics.Vector2(10, 10), new System.Numerics.Vector2(0, 40)));
        page.Frame();
        Assert.Equal("c", page.Log());
        Assert.True(double.Parse(page.Run("el('c').scrollTop.toString()"), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    // ---- the window and matchMedia ----

    [Fact]
    public void TheWindowReportsTheViewport()
    {
        using var page = new Page("<p>x</p>", 640, 480);
        Assert.Equal("640,480,1", page.Run("innerWidth + ',' + innerHeight + ',' + devicePixelRatio"));
        Assert.Equal("640,480", page.Run("document.documentElement.clientWidth + ',' + document.documentElement.clientHeight"));
        Assert.Equal("true", page.Run("String(document.scrollingElement === document.documentElement)"));
        page.Paint(1000, 700);
        Assert.Equal("1000,700", page.Run("innerWidth + ',' + innerHeight"));
        Assert.Equal("5", page.Run("innerWidth = 5; innerWidth.toString()")); // a script may replace the attribute
    }

    [Fact]
    public void MatchMediaAnswersForTheViewportOrientationAndPreferences()
    {
        using var page = new Page("<p>x</p>", 800, 600);
        Assert.Equal("true,false,true,false", page.Run("[matchMedia('(min-width: 500px)').matches, matchMedia('(max-width: 500px)').matches, matchMedia('(min-width: 500px) and (max-width: 900px)').matches, matchMedia('(width: 700px)').matches].join()"));
        Assert.Equal("true,false,false,true", page.Run("[matchMedia('(orientation: landscape)').matches, matchMedia('(orientation: portrait)').matches, matchMedia('print').matches, matchMedia('screen').matches].join()"));
        Assert.Equal("false,true,false", page.Run("[matchMedia('(prefers-color-scheme: dark)').matches, matchMedia('(prefers-color-scheme: light)').matches, matchMedia('(prefers-reduced-motion: reduce)').matches].join()"));
        Assert.Equal("(min-width: 500px)", page.Run("matchMedia('(min-width: 500px)').media"));
        Assert.Equal("TypeError", page.Run("try { matchMedia(); 'no' } catch (e) { e.name }"));
        using var dark = new Page("<p>x</p>", 800, 600, ColorScheme.Dark);
        Assert.Equal("true,false", dark.Run("[matchMedia('(prefers-color-scheme: dark)').matches, matchMedia('(prefers-color-scheme: light)').matches].join()"));
    }

    [Fact]
    public void ResizingAcrossABreakpointFiresOneChangeAndOneResizeInTheNextFrame()
    {
        using var page = new Page("<p>x</p>", 800, 600);
        page.Run("var wide = matchMedia('(min-width: 600px)'); var narrow = matchMedia('(max-width: 400px)'); window.addEventListener('resize', function () { log.push('resize'); }); wide.addEventListener('change', function (e) { log.push('wide:' + e.matches + ':' + e.media); }); narrow.onchange = function (e) { log.push('narrow:' + e.matches); };");
        page.Frame();
        Assert.Equal("", page.Log()); // nothing has changed
        page.Paint(500, 600);
        Assert.Equal("false", page.Run("String(wide.matches)")); // matches is live; the event waits for the frame
        page.Frame();
        Assert.Equal("resize wide:false:(min-width: 600px)", page.Log());
        page.Run("log.length = 0;");
        page.Frame();
        Assert.Equal("", page.Log()); // once only
        page.Paint(300, 600);
        page.Frame();
        Assert.Equal("resize narrow:true", page.Log()); // a resize that crosses no breakpoint of `wide`, one of `narrow`
        page.Run("log.length = 0;");
        page.Paint(300, 400);
        page.Frame();
        Assert.Equal("resize", page.Log()); // resize alone
    }

    [Fact]
    public void SwitchingColorSchemeAtRuntimeFiresChangeEventForPrefersColorSchemeList()
    {
        using var page = new Page("<p>x</p>", 800, 600);
        page.Run("var dark = matchMedia('(prefers-color-scheme: dark)'); var light = matchMedia('(prefers-color-scheme: light)'); dark.addEventListener('change', function (e) { log.push('dark:' + e.matches); }); light.onchange = function (e) { log.push('light:' + e.matches); };");
        page.Frame();
        Assert.Equal("", page.Log());

        // Switch color scheme to dark at runtime
        page.Document.ColorScheme = ColorScheme.Dark;
        Assert.Equal("true", page.Run("String(dark.matches)"));
        Assert.Equal("false", page.Run("String(light.matches)"));

        // Change event fires at next frame
        page.Frame();
        Assert.Contains("dark:true", page.Log());
        Assert.Contains("light:false", page.Log());

        page.Run("log.length = 0;");
        page.Frame();
        Assert.Equal("", page.Log()); // once only

        // Switch back to light
        page.Document.ColorScheme = ColorScheme.Light;
        page.Frame();
        Assert.Contains("dark:false", page.Log());
        Assert.Contains("light:true", page.Log());
    }

    [Fact]
    public void SwitchingReducedMotionAtRuntimeFiresChangeEventForPrefersReducedMotionList()
    {
        using var page = new Page("<p>x</p>", 800, 600);
        page.Run("var motion = matchMedia('(prefers-reduced-motion: reduce)'); motion.addEventListener('change', function (e) { log.push('motion:' + e.matches); });");
        page.Frame();
        Assert.Equal("", page.Log());

        // Switch reduced motion to true at runtime
        page.Document.ReducedMotion = true;
        Assert.Equal("true", page.Run("String(motion.matches)"));

        page.Frame();
        Assert.Equal("motion:true", page.Log());

        page.Run("log.length = 0;");
        page.Document.ReducedMotion = false;
        page.Frame();
        Assert.Equal("motion:false", page.Log());
    }

    [Fact]
    public void TheLegacyListenerApiWorks()
    {
        using var page = new Page("<p>x</p>", 800, 600);
        page.Run("var list = matchMedia('(min-width: 600px)'); function onChange(e) { log.push('legacy:' + e.matches); } list.addListener(onChange);");
        page.Paint(500, 600);
        page.Frame();
        Assert.Equal("legacy:false", page.Log());
        page.Run("log.length = 0; list.removeListener(onChange);");
        page.Paint(800, 600);
        page.Frame();
        Assert.Equal("", page.Log());
        Assert.Equal("true", page.Run("String(list instanceof EventTarget && Object.prototype.toString.call(list) === '[object MediaQueryList]')"));
    }
}

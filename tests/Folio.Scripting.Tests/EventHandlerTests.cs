using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>Tests for #457: event handler content attributes and the on* IDL attributes.</summary>
public class EventHandlerTests
{
    private const string Page =
        "<!DOCTYPE html><html><body><div id='outer'><a id='link' href='#x'>x</a><button id='btn' onclick=\"log.push('attr:' + this.id + ':' + event.type)\">b</button></div></body></html>";

    private sealed class Session : IDisposable
    {
        public required JintScriptEngine Engine { get; init; }
        public required HtmlEventLoop Loop { get; init; }
        public List<Exception> Uncaught { get; } = [];

        public string Run(string script) => Engine.Evaluate(script).AsString() ?? "";

        public void Dispose()
        {
            Loop.Dispose();
            Engine.Dispose();
        }
    }

    private static Session Create(string html = Page)
    {
        var clock = new VirtualEventLoopClock();
        var engine = new JintScriptEngine(new ScriptEngineOptions { Clock = () => clock.NowUtc });
        var loop = new HtmlEventLoop(engine, clock);
        ScriptGlobalsInstaller.Install(loop, engine);
        DomBindings.Install(engine, TreeBuilder.Parse(html), eventLoop: loop);
        engine.Evaluate("globalThis.log = [];");
        var session = new Session { Engine = engine, Loop = loop };
        loop.UncaughtException += session.Uncaught.Add;
        return session;
    }

    [Fact]
    public void ClickAttributeFiresWithTheElementAsThisAndTheEvent()
    {
        using var s = Create();

        Assert.Equal("attr:btn:click", s.Run(@"
            document.getElementById('btn').dispatchEvent(new Event('click'));
            log.join()"));
    }

    [Fact]
    public void ReturnFalseCancelsAndReturnTrueDoesNot()
    {
        using var s = Create("<body><a id='a' onclick='return false'>x</a><a id='b' onclick='return true'>y</a><a id='c' onclick='return 0'>z</a></body>");

        Assert.Equal("false,true,true", s.Run(@"
            const click = id => document.getElementById(id).dispatchEvent(new Event('click', { cancelable: true }));
            [click('a'), click('b'), click('c')].join()"));
    }

    [Fact]
    public void BodyLoadAttributeFiresOnTheWindowLoad()
    {
        var diagnostics = new List<Diagnostic>();
        var options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess };
        var doc = TreeBuilder.Parse("<body onload=\"log.push('body-load:' + (this === window) + ':' + document.readyState)\"><script>window.log = [];</script></body>",
            new ParserLimits(ScriptingEnabled: true));
        using var engine = new JintScriptEngine();
        using var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);
        DomBindings.Install(engine, doc, eventLoop: loop);
        using var runner = new ScriptRunner(doc, engine, loop, options, diagnostics);

        runner.Run();

        Assert.Equal("body-load:true:complete", engine.Evaluate("log.join()").AsString());
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void HandlersRunInRegistrationPositionWithListeners()
    {
        using var s = Create("<body><div id='d' onclick=\"log.push('attr')\"></div></body>");

        Assert.Equal("attr,after", s.Run(@"
            const d = document.getElementById('d');
            d.addEventListener('click', () => log.push('after'));
            d.dispatchEvent(new Event('click'));
            log.join()"));
    }

    [Fact]
    public void ReplacingTheHandlerKeepsItsPosition()
    {
        using var s = Create("<body><div id='d'></div></body>");

        Assert.Equal("first,second,third", s.Run(@"
            const d = document.getElementById('d');
            d.onclick = () => log.push('old');
            d.addEventListener('click', () => log.push('second'));
            d.onclick = () => log.push('first');
            d.addEventListener('click', () => log.push('third'));
            d.dispatchEvent(new Event('click'));
            log.join()"));
    }

    [Fact]
    public void ChangingTheAttributeReplacesTheHandlerInPlace()
    {
        using var s = Create("<body><div id='d' onclick=\"log.push('one')\"></div></body>");

        Assert.Equal("two,after", s.Run(@"
            const d = document.getElementById('d');
            d.addEventListener('click', () => log.push('after'));
            d.setAttribute('onclick', ""log.push('two')"");
            d.dispatchEvent(new Event('click'));
            log.join()"));
    }

    [Fact]
    public void SettingNullOrRemovingTheAttributeRemovesTheHandler()
    {
        using var s = Create("<body><div id='d' onclick=\"log.push('attr')\"></div><div id='e'></div></body>");

        Assert.Equal("null,,null", s.Run(@"
            const d = document.getElementById('d');
            const e = document.getElementById('e');
            e.onclick = () => log.push('idl');
            e.onclick = null;
            d.removeAttribute('onclick');
            d.dispatchEvent(new Event('click'));
            e.dispatchEvent(new Event('click'));
            [String(d.onclick), log.join(), String(e.onclick)].join()"));
    }

    [Fact]
    public void NonCallableValuesBecomeNull()
    {
        using var s = Create("<body><div id='d'></div></body>");

        Assert.Equal("null,null,function", s.Run(@"
            const d = document.getElementById('d');
            d.onclick = 5;
            const a = String(d.onclick);
            d.onclick = {};
            const b = String(d.onclick);
            d.onclick = function() {};
            [a, b, typeof d.onclick].join()"));
    }

    [Fact]
    public void TheIdlGetterReturnsTheCompiledAttributeFunction()
    {
        using var s = Create();

        Assert.Equal("function,1", s.Run(@"
            const b = document.getElementById('btn');
            [typeof b.onclick, b.onclick.length].join()"));
    }

    [Fact]
    public void AttributeHandlersSeeTheElementAndTheDocumentInScope()
    {
        using var s = Create("<body><div id='d' onclick=\"log.push(id + ':' + getElementById('d').tagName)\"></div></body>");

        Assert.Equal("d:DIV", s.Run(@"
            document.getElementById('d').dispatchEvent(new Event('click'));
            log.join()"));
    }

    [Fact]
    public void ASyntaxErrorIsReportedOnceAndOtherHandlersStillWork()
    {
        using var s = Create("<body><div id='bad' onclick='if ('></div><div id='good' onclick=\"log.push('ok')\"></div></body>");
        s.Run(@"
            window.addEventListener('error', e => { errors = (globalThis.errors || []); errors.push(e.message); globalThis.errors = errors; });");

        s.Run(@"
            const bad = document.getElementById('bad');
            bad.dispatchEvent(new Event('click'));
            bad.dispatchEvent(new Event('click'));
            document.getElementById('good').dispatchEvent(new Event('click'));");

        Assert.Equal("1,ok", s.Run("[errors.length, log.join()].join()"));
        Assert.Single(s.Uncaught);
    }

    [Fact]
    public void WindowOnerrorReceivesTheClassicArgumentsAndTrueCancels()
    {
        using var s = Create("<body></body>");

        Assert.Equal("boom|a.js|3|7|true,true", s.Run(@"
            window.onerror = function(message, source, line, column, error) {
                log.push([message, source, line, column, error instanceof Error].join('|'));
                return true;
            };
            const event = new ErrorEvent('error', { message: 'boom', filename: 'a.js', lineno: 3, colno: 7, error: new Error('x'), cancelable: true });
            const notCanceled = window.dispatchEvent(event);
            [log.join(), event.defaultPrevented].join()"));
    }

    [Fact]
    public void WindowAndDocumentHandlersWork()
    {
        using var s = Create("<body></body>");

        Assert.Equal("window,document", s.Run(@"
            window.onresize = () => log.push('window');
            document.onkeydown = () => log.push('document');
            window.dispatchEvent(new Event('resize'));
            document.dispatchEvent(new Event('keydown'));
            log.join()"));
    }

    [Fact]
    public void BodyForwardsWindowHandlersAndShowsThemThroughTheWindow()
    {
        using var s = Create("<body onresize=\"log.push('body')\"></body>");

        Assert.Equal("body,true", s.Run(@"
            window.dispatchEvent(new Event('resize'));
            [log.join(), window.onresize === document.body.onresize].join()"));
    }

    [Fact]
    public void ElementsInsertedLaterGetTheirAttributeHandlers()
    {
        using var s = Create("<body></body>");

        Assert.Equal("inserted", s.Run(@"
            const holder = document.createElement('div');
            holder.innerHTML = ""<p id='p' onclick=\""log.push('inserted')\"">x</p>"";
            document.body.appendChild(holder);
            document.getElementById('p').dispatchEvent(new Event('click'));
            log.join()"));
    }

    [Fact]
    public void AnIdlHandlerIsNotReplacedWhenItsElementIsInsertedLater()
    {
        using var s = Create("<body></body>");

        Assert.Equal("idl", s.Run(@"
            const p = document.createElement('p');
            p.setAttribute('onclick', ""log.push('attr')"");
            p.onclick = () => log.push('idl');
            document.body.appendChild(p);
            p.dispatchEvent(new Event('click'));
            log.join()"));
    }
}

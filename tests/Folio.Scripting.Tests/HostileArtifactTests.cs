using Folio;
using Folio.Dom;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class HostileArtifactTests
{
    [Fact]
    public void TopLevelInfiniteLoopEndsInScriptLimit()
    {
        var limits = new ScriptLimits
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxStatements = 2000
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        var ex = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("while (true) {}"));
        Assert.True(ex.LimitName is "Timeout" or "Statements");
    }

    [Fact]
    public void TimerInfiniteLoopEndsInScriptLimitWithoutHangingHost()
    {
        var limits = new ScriptLimits
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxStatements = 2000
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        using var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);

        Exception? captured = null;
        loop.UncaughtException += ex => captured = ex;

        engine.Evaluate("setTimeout(() => { while (true) {} }, 0);");
        loop.RunOneTurn();

        Assert.NotNull(captured);
        Assert.IsType<ScriptLimitExceededException>(captured);
    }

    [Fact]
    public void AnimationFrameInfiniteLoopEndsInScriptLimitWithoutHangingHost()
    {
        var limits = new ScriptLimits
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxStatements = 2000
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        using var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);

        Exception? captured = null;
        loop.UncaughtException += ex => captured = ex;

        engine.Evaluate("requestAnimationFrame(() => { while (true) {} });");
        loop.UpdateTheRendering();

        Assert.NotNull(captured);
        Assert.IsType<ScriptLimitExceededException>(captured);
    }

    [Fact]
    public void EventHandlerInfiniteLoopEndsInScriptLimitWithoutHangingHost()
    {
        var limits = new ScriptLimits
        {
            // Long enough that the host's own setup, which runs under the same limit, is never what runs out on a busy machine.
            Timeout = TimeSpan.FromMilliseconds(400),
            MaxStatements = 2000
        };
        var doc = Document.Parse("<button id='btn'>Click</button>");
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        using var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);
        DomBindings.Install(engine, doc, eventLoop: loop);

        engine.Evaluate("document.getElementById('btn').addEventListener('click', () => { while (true) {} });");
        var ex = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("document.getElementById('btn').click();"));
        Assert.True(ex.LimitName is "Timeout" or "Statements");
    }

    [Fact]
    public void UnboundedRecursionEndsInRecursionLimit()
    {
        var limits = new ScriptLimits
        {
            MaxRecursionDepth = 50
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        var ex = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("function recurse() { recurse(); } recurse();"));
        Assert.Equal("Recursion", ex.LimitName);
    }

    [Fact]
    public void StringDoublingMemoryBombTerminatesWithinLimit()
    {
        var limits = new ScriptLimits
        {
            MemoryLimitBytes = 2 * 1024 * 1024 // 2 MB
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        var ex = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("let s = 'hostile payload string'; while (true) { s += s; }"));
        Assert.Equal("Memory", ex.LimitName);
    }

    [Fact]
    public void ArrayGrowthMemoryBombTerminatesWithinLimit()
    {
        var limits = new ScriptLimits
        {
            MemoryLimitBytes = 2 * 1024 * 1024 // 2 MB
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        var ex = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("const a = []; while (true) { a.push(new Array(50000).fill('bomb')); }"));
        Assert.Equal("Memory", ex.LimitName);
    }

    [Fact]
    public void DomNodeGrowthMemoryBombTerminatesWithinLimit()
    {
        var limits = new ScriptLimits
        {
            MemoryLimitBytes = 4 * 1024 * 1024 // 4 MB
        };
        var doc = Document.Parse("<div id='root'></div>");
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        using var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);
        DomBindings.Install(engine, doc, eventLoop: loop);

        var ex = Assert.Throws<ScriptLimitExceededException>(() =>
        {
            engine.Evaluate(@"
                const root = document.getElementById('root');
                while (true) {
                    const el = document.createElement('div');
                    el.textContent = 'hostile node expansion';
                    root.appendChild(el);
                }
            ");
        });
        Assert.True(ex.LimitName is "Memory" or "Timeout" or "Statements");
    }

    [Fact]
    public void FloodsOfTimersDoNotExhaustOrHangHost()
    {
        var limits = new ScriptLimits
        {
            MaxStatements = 5000
        };
        using var engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits });
        using var loop = new HtmlEventLoop(engine);
        ScriptGlobalsInstaller.Install(loop, engine);

        engine.Evaluate(@"
            globalThis.count = 0;
            for (let i = 0; i < 50; i++) setTimeout(() => { globalThis.count++; }, 0);
        ");
        var turns = loop.SpinUntilIdle(maxTurns: 100);
        Assert.True(turns <= 100);
        Assert.Equal(50, (int)engine.Evaluate("globalThis.count").AsNumber());
    }

    [Fact]
    public void FloodsOfConsoleMessagesDoNotExhaustOrHangHost()
    {
        var doc = Document.Parse("<p>test</p>");
        var messages = new List<Diagnostic>();
        doc.Node.DiagnosticSink = d => { if (d.Code == DiagnosticCode.ScriptConsole) messages.Add(d); };
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, doc);

        engine.Evaluate("for (let i = 0; i < 1500; i++) console.log('hostile log line ' + i);");
        Assert.Equal(1001, messages.Count);
        Assert.Contains("dropped", messages[^1].Message);
    }

    [Fact]
    public void HostileScriptCannotAccessDotNetTypesOrReflection()
    {
        using var engine = new JintScriptEngine();
        Assert.Equal("undefined", engine.Evaluate("typeof System").AsString());
        Assert.Equal("undefined", engine.Evaluate("typeof importNamespace").AsString());
        Assert.Equal("undefined", engine.Evaluate("typeof clr").AsString());
        Assert.Equal("undefined", engine.Evaluate("new (({}).constructor.constructor)('return typeof System')()").AsString());
        Assert.Equal("undefined", engine.Evaluate("typeof (typeof System !== 'undefined' ? System.IO : undefined)").AsString());
        Assert.Equal("undefined", engine.Evaluate("typeof (typeof System !== 'undefined' ? System.Diagnostics.Process : undefined)").AsString());
    }

    [Fact]
    public void LoadsOfHttpUrlsAndLocalPathsAreBlockedByPolicy()
    {
        var policy = new ContentResourcePolicy();
        Assert.False(policy.IsAllowed(new Uri("http://attacker.example.com/malware.js")));
        Assert.False(policy.IsAllowed(new Uri("https://attacker.example.com/malware.js")));
        Assert.False(policy.IsAllowed(new Uri("file:///C:/Windows/System32/drivers/etc/hosts")));
        Assert.True(policy.IsAllowed(new Uri("data:text/javascript,console.log(1)")));
    }
}

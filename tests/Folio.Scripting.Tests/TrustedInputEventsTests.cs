using System.Numerics;
using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Xunit;

namespace Folio.Scripting.Tests;

/// <summary>Tests for #458: trusted input events, event interfaces, pointer capture, and cancellable default actions.</summary>
public class TrustedInputEventsTests
{
    private sealed class TestHarness : IDisposable
    {
        public Document Doc { get; }
        public JintScriptEngine Engine { get; }
        public HtmlEventLoop Loop { get; }

        public TestHarness(string html, FolioOptions? options = null)
        {
            var opts = new FolioOptions
            {
                BaseUri = options?.BaseUri,
                ColorScheme = options?.ColorScheme ?? ColorScheme.Light,
                ReducedMotion = options?.ReducedMotion ?? false,
                UserStyleSheet = options?.UserStyleSheet,
                Fonts = options?.Fonts ?? Typography.FontSettings.Default,
                Limits = options?.Limits ?? ResourceLimits.Default,
                ResourceLoader = options?.ResourceLoader,
                CollectDiagnostics = options?.CollectDiagnostics ?? true,
                Scripting = ScriptingPolicy.ContentProcess
            };
            Doc = Document.Parse(html, opts);
            Doc.Paint(800, 600);

            var clock = new VirtualEventLoopClock();
            Engine = new JintScriptEngine(new ScriptEngineOptions { Clock = () => clock.NowUtc });
            Loop = new HtmlEventLoop(Engine, clock);
            ScriptGlobalsInstaller.Install(Loop, Engine);
            DomBindings.Install(Engine, Doc.Node, eventLoop: Loop);
        }

        public string Run(string script) => Engine.Evaluate(script).AsString() ?? "";

        public void Dispose()
        {
            Loop.Dispose();
            Engine.Dispose();
            Doc.Dispose();
        }
    }

    [Fact]
    public void EventConstructorsAndConstantsExist()
    {
        using var h = new TestHarness("<!DOCTYPE html><html><body></body></html>");
        var res = h.Run("""
            [
                typeof UIEvent === 'function',
                typeof MouseEvent === 'function',
                typeof PointerEvent === 'function',
                typeof WheelEvent === 'function',
                typeof KeyboardEvent === 'function',
                typeof InputEvent === 'function',
                typeof FocusEvent === 'function',
                typeof CompositionEvent === 'function',
                WheelEvent.DOM_DELTA_PIXEL === 0,
                WheelEvent.DOM_DELTA_LINE === 1,
                WheelEvent.DOM_DELTA_PAGE === 2,
                KeyboardEvent.DOM_KEY_LOCATION_STANDARD === 0,
                KeyboardEvent.DOM_KEY_LOCATION_LEFT === 1,
                KeyboardEvent.DOM_KEY_LOCATION_RIGHT === 2,
                KeyboardEvent.DOM_KEY_LOCATION_NUMPAD === 3
            ].join(',')
        """);
        Assert.Equal("true,true,true,true,true,true,true,true,true,true,true,true,true,true,true", res);
    }

    [Fact]
    public void ScriptCreatedEventsHaveIsTrustedFalse()
    {
        using var h = new TestHarness("<!DOCTYPE html><html><body></body></html>");
        var res = h.Run("""
            [
                new Event('test').isTrusted === false,
                new MouseEvent('click').isTrusted === false,
                new PointerEvent('pointerdown').isTrusted === false,
                new KeyboardEvent('keydown').isTrusted === false
            ].join(',')
        """);
        Assert.Equal("true,true,true,true", res);
    }

    [Fact]
    public void HostDispatchedInputEventsHaveIsTrustedTrue()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <button id='btn' style='display:block;width:100px;height:50px'>Click</button>
            </body></html>
        """);

        h.Run("""
            window.log = [];
            const btn = document.getElementById('btn');
            btn.addEventListener('pointerdown', e => window.log.push('pointerdown:' + e.isTrusted));
            btn.addEventListener('mousedown', e => window.log.push('mousedown:' + e.isTrusted));
            btn.addEventListener('click', e => window.log.push('click:' + e.isTrusted));
        """);

        h.Doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        h.Doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));

        var log = h.Run("window.log.join(',')");
        Assert.Equal("pointerdown:true,mousedown:true,click:true", log);
    }

    [Fact]
    public void PointerCaptureApiFiresGotAndLostCapture()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <div id='box' style='display:block;width:100px;height:50px'>Box</div>
            </body></html>
        """);

        var res = h.Run("""
            window.log = [];
            const box = document.getElementById('box');
            box.addEventListener('gotpointercapture', e => window.log.push('got:' + e.pointerId + ':' + e.isTrusted));
            box.addEventListener('lostpointercapture', e => window.log.push('lost:' + e.pointerId + ':' + e.isTrusted));

            box.setPointerCapture(1);
            const has1 = box.hasPointerCapture(1);
            box.releasePointerCapture(1);
            const has2 = box.hasPointerCapture(1);
            window.log.push('has1:' + has1);
            window.log.push('has2:' + has2);
            window.log.join(',');
        """);
        Assert.Equal("got:1:true,lost:1:true,has1:true,has2:false", res);
    }

    [Fact]
    public void PreventDefaultOnMousedownPreventsFocusChange()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <input id='inp1' value='first' style='display:block;width:100px;height:30px' />
            <input id='inp2' value='second' style='display:block;width:100px;height:30px' />
            </body></html>
        """);

        // Focus inp1 first
        var inp1 = h.Doc.GetElementById("inp1")!;
        h.Doc.Input.Focus(inp1);
        Assert.Equal(inp1, h.Doc.Input.FocusedElement);

        // Add mousedown listener with preventDefault on inp2
        h.Run("""
            document.getElementById('inp2').addEventListener('mousedown', e => {
                e.preventDefault();
            });
        """);

        // Click on inp2: bounds (0, 30) to (100, 60)
        h.Doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 45), PointerButton.Primary, PointerButtons.Primary));

        // Focus should still be inp1 because mousedown was prevented!
        Assert.Equal(inp1, h.Doc.Input.FocusedElement);
    }

    [Fact]
    public void PreventDefaultOnClickPreventsLinkNavigation()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html>
            <style>
              body { margin: 0; }
              a { display: block; width: 100px; height: 50px; }
            </style>
            <body>
            <a id='link' href='https://example.com/target'>Link</a>
            </body></html>
        """, new FolioOptions { BaseUri = new Uri("https://example.com/") });

        LinkActivatedEventArgs? activated = null;
        h.Doc.Input.LinkActivated += (_, e) => activated = e;

        h.Run("""
            window.clickFired = false;
            document.getElementById('link').addEventListener('click', e => {
                window.clickFired = true;
                e.preventDefault();
            });
        """);

        h.Doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        h.Doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));

        Assert.Equal("true", h.Run("String(window.clickFired)"));
        Assert.Null(activated);
    }

    [Fact]
    public void PreventDefaultOnClickPreventsCheckboxToggle()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <input id='cb' type='checkbox' style='display:block;width:50px;height:50px' />
            </body></html>
        """);

        var cb = h.Doc.GetElementById("cb")!;
        Assert.False(cb.Node.IsChecked);

        h.Run("""
            document.getElementById('cb').addEventListener('click', e => {
                e.preventDefault();
            });
        """);

        h.Doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        h.Doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));

        // Should still be unchecked!
        Assert.False(cb.Node.IsChecked);
    }

    [Fact]
    public void PreventDefaultOnKeydownPreventsDefaultActions()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <input id='txt' style='display:block;width:100px;height:30px' />
            </body></html>
        """);

        var txt = h.Doc.GetElementById("txt")!;
        h.Doc.Input.Focus(txt);

        h.Run("""
            document.getElementById('txt').addEventListener('keydown', e => {
                if (e.key === 'Backspace') e.preventDefault();
            });
        """);

        // Type text first
        h.Doc.Input.HandleTextInput(new TextInputEvent("abc"));
        Assert.Equal("abc", txt.Node.Value);

        // Press Backspace with preventDefault
        h.Doc.Input.HandleKeyDown(new KeyEvent("Backspace"));
        // Value should still be "abc"
        Assert.Equal("abc", txt.Node.Value);
    }

    [Fact]
    public void PreventDefaultOnBeforeInputPreventsCharacterInsertion()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <input id='txt' style='display:block;width:100px;height:30px' />
            </body></html>
        """);

        var txt = h.Doc.GetElementById("txt")!;
        h.Doc.Input.Focus(txt);

        h.Run("""
            window.events = [];
            document.getElementById('txt').addEventListener('beforeinput', e => {
                window.events.push('beforeinput:' + e.data + ':' + e.isTrusted);
                if (e.data === 'x') e.preventDefault();
            });
            document.getElementById('txt').addEventListener('input', e => {
                window.events.push('input:' + e.data + ':' + e.isTrusted);
            });
        """);

        // Type 'a' (allowed)
        h.Doc.Input.HandleTextInput(new TextInputEvent("a"));
        Assert.Equal("a", txt.Node.Value);

        // Type 'x' (prevented)
        h.Doc.Input.HandleTextInput(new TextInputEvent("x"));
        Assert.Equal("a", txt.Node.Value);

        var events = h.Run("window.events.join(',')");
        Assert.Equal("beforeinput:a:true,input:a:true,beforeinput:x:true", events);
    }

    [Fact]
    public void FocusEventSequenceDispatchesCorrectEvents()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <input id='a' style='display:block;width:100px;height:30px' />
            <input id='b' style='display:block;width:100px;height:30px' />
            </body></html>
        """);

        h.Run("""
            window.log = [];
            const a = document.getElementById('a');
            const b = document.getElementById('b');
            a.addEventListener('focusout', e => window.log.push('a-focusout:' + (e.relatedTarget === b)));
            a.addEventListener('blur', e => window.log.push('a-blur:' + (e.relatedTarget === b)));
            b.addEventListener('focusin', e => window.log.push('b-focusin:' + (e.relatedTarget === a)));
            b.addEventListener('focus', e => window.log.push('b-focus:' + (e.relatedTarget === a)));
        """);

        var a = h.Doc.GetElementById("a")!;
        var b = h.Doc.GetElementById("b")!;

        h.Doc.Input.Focus(a);
        h.Run("window.log = [];");

        // Focus b now
        h.Doc.Input.Focus(b);

        var log = h.Run("window.log.join(',')");
        Assert.Equal("a-focusout:true,b-focusin:true,a-blur:true,b-focus:true", log);
    }

    [Fact]
    public void PreventDefaultOnWheelCancelsScrolling()
    {
        using var h = new TestHarness("""
            <!DOCTYPE html><html><body>
            <div id='scroller' style='display:block;width:200px;height:100px;overflow:scroll'>
              <div style='height:1000px'>Long Content</div>
            </div>
            </body></html>
        """);

        var scroller = h.Doc.GetElementById("scroller")!;
        Assert.Equal(0, h.Doc.GetScrollTop(scroller.Node));

        // Normal wheel event scrolls
        h.Doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 100)));
        Assert.True(h.Doc.GetScrollTop(scroller.Node) > 0);

        h.Doc.ScrollTo(scroller, 0, 0);
        Assert.Equal(0, h.Doc.GetScrollTop(scroller.Node));

        // Add non-passive wheel listener with preventDefault on scroller
        h.Run("""
            document.getElementById('scroller').addEventListener('wheel', e => {
                e.preventDefault();
            }, { passive: false });
        """);

        h.Doc.Input.HandleWheel(new WheelEvent(new Vector2(50, 50), new Vector2(0, 100)));
        // Should not have scrolled!
        Assert.Equal(0, h.Doc.GetScrollTop(scroller.Node));
    }
}

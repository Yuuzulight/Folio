using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>Binding tests for #455: EventTarget, Event, CustomEvent, ErrorEvent, dispatch, AbortController and AbortSignal.</summary>
public class DomEventsTests
{
    private const string Page =
        "<!DOCTYPE html><html><body><div id='outer'><div id='mid'><button id='btn'>x</button></div></div></body></html>";

    private sealed class Session : IDisposable
    {
        public required JintScriptEngine Engine { get; init; }
        public required HtmlEventLoop Loop { get; init; }
        public required VirtualEventLoopClock Clock { get; init; }
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
        var session = new Session { Engine = engine, Loop = loop, Clock = clock };
        loop.UncaughtException += session.Uncaught.Add;
        return session;
    }

    [Fact]
    public void NodesWindowAndPlainTargetsAreEventTargets()
    {
        using var s = Create();
        Assert.Equal("true,true,true,true,true,true", s.Run(@"
            [document instanceof EventTarget,
             document.getElementById('btn') instanceof EventTarget,
             document.getElementById('btn') instanceof Node,
             typeof window.addEventListener === 'function',
             new EventTarget() instanceof EventTarget,
             new Event('x') instanceof Event].join()"));
    }

    [Fact]
    public void CaptureRunsBeforeBubbleAndTargetSeesBothInRegistrationOrder()
    {
        using var s = Create();
        Assert.Equal("w-c,d-c,outer-c,mid-c,btn-capture,btn-1,btn-2,mid-b,outer-b,d-b,w-b", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            const add = (t, name, capture) => t.addEventListener('go', () => log.push(name), capture);
            add(window, 'w-c', true); add(window, 'w-b', false);
            add(document, 'd-c', true); add(document, 'd-b', false);
            add($('outer'), 'outer-c', true); add($('outer'), 'outer-b', false);
            add($('mid'), 'mid-c', true); add($('mid'), 'mid-b', false);
            add($('btn'), 'btn-1', false); add($('btn'), 'btn-capture', true); add($('btn'), 'btn-2', false);
            $('btn').dispatchEvent(new Event('go', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void NonBubblingEventsSkipBubbleListenersButNotTheTarget()
    {
        using var s = Create();
        Assert.Equal("outer-c,btn", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            $('outer').addEventListener('go', () => log.push('outer-b'));
            $('outer').addEventListener('go', () => log.push('outer-c'), true);
            $('btn').addEventListener('go', () => log.push('btn'));
            $('btn').dispatchEvent(new Event('go'));
            log.join()"));
    }

    [Fact]
    public void EventPhaseCurrentTargetTargetAndComposedPath()
    {
        using var s = Create();
        Assert.Equal("c:1:outer:btn,t:2:btn:btn,b:3:outer:btn,after:0,null,0", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            const note = (tag, e) => log.push(tag + ':' + e.eventPhase + ':' + e.currentTarget.id + ':' + e.target.id);
            $('outer').addEventListener('go', e => note('c', e), true);
            $('btn').addEventListener('go', e => note('t', e));
            $('outer').addEventListener('go', e => note('b', e));
            const ev = new Event('go', { bubbles: true });
            $('btn').dispatchEvent(ev);
            log.push('after:' + ev.eventPhase + ',' + ev.currentTarget + ',' + ev.composedPath().length);
            log.join()"));
    }

    [Fact]
    public void ComposedPathEndsAtTheWindowForConnectedNodes()
    {
        using var s = Create();
        Assert.Equal("btn,mid,outer,BODY,HTML,#document,window", s.Run(@"
            let path;
            document.getElementById('btn').addEventListener('go', e => { path = e.composedPath(); });
            document.getElementById('btn').dispatchEvent(new Event('go', { bubbles: true }));
            path.map(n => n === window ? 'window' : n.id || n.nodeName).join()"));
    }

    [Fact]
    public void StopPropagationStopsLaterTargetsButNotTheSameTarget()
    {
        using var s = Create();
        Assert.Equal("btn-1,btn-2", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            $('btn').addEventListener('go', e => { log.push('btn-1'); e.stopPropagation(); });
            $('btn').addEventListener('go', () => log.push('btn-2'));
            $('mid').addEventListener('go', () => log.push('mid'));
            $('btn').dispatchEvent(new Event('go', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void StopImmediatePropagationStopsTheSameTargetToo()
    {
        using var s = Create();
        Assert.Equal("btn-1", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            $('btn').addEventListener('go', e => { log.push('btn-1'); e.stopImmediatePropagation(); });
            $('btn').addEventListener('go', () => log.push('btn-2'));
            $('mid').addEventListener('go', () => log.push('mid'));
            $('btn').dispatchEvent(new Event('go', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void StopPropagationInCapturePhaseKeepsTheTargetFromRunning()
    {
        using var s = Create();
        Assert.Equal("outer-c", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            $('outer').addEventListener('go', e => { log.push('outer-c'); e.stopPropagation(); }, true);
            $('btn').addEventListener('go', () => log.push('btn'));
            $('btn').dispatchEvent(new Event('go', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void CancelBubbleIsALegacyAliasOfStopPropagation()
    {
        using var s = Create();
        Assert.Equal("btn|true", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            let flag;
            $('btn').addEventListener('go', e => { e.cancelBubble = true; flag = e.cancelBubble; log.push('btn'); });
            $('mid').addEventListener('go', () => log.push('mid'));
            $('btn').dispatchEvent(new Event('go', { bubbles: true }));
            log.join() + '|' + flag"));
    }

    [Fact]
    public void PreventDefaultHonoursCancelableAndDispatchReturnsTheResult()
    {
        using var s = Create();
        Assert.Equal("true,false,true,true,false|true,false", s.Run(@"
            const btn = document.getElementById('btn');
            btn.addEventListener('c', e => e.preventDefault());
            btn.addEventListener('n', e => e.preventDefault());
            const c = new Event('c', { cancelable: true });
            const n = new Event('n');
            const rc = btn.dispatchEvent(c);
            const rn = btn.dispatchEvent(n);
            [rn, rc, !n.defaultPrevented, c.defaultPrevented, n.defaultPrevented].join() + '|' +
              [c.returnValue === false, n.returnValue === false].join()"));
    }

    [Fact]
    public void ReturnValueFalseCancelsACancelableEvent()
    {
        using var s = Create();
        Assert.Equal("true,false", s.Run(@"
            const btn = document.getElementById('btn');
            btn.addEventListener('go', e => { e.returnValue = false; });
            const ev = new Event('go', { cancelable: true });
            btn.dispatchEvent(ev);
            [ev.defaultPrevented, ev.returnValue].join()"));
    }

    [Fact]
    public void PassiveListenersIgnorePreventDefault()
    {
        using var s = Create();
        Assert.Equal("false,true", s.Run(@"
            const btn = document.getElementById('btn');
            btn.addEventListener('go', e => e.preventDefault(), { passive: true });
            const a = new Event('go', { cancelable: true });
            btn.dispatchEvent(a);
            const b = new Event('stop', { cancelable: true });
            btn.addEventListener('stop', e => e.preventDefault(), { passive: false });
            btn.dispatchEvent(b);
            [a.defaultPrevented, b.defaultPrevented].join()"));
    }

    [Fact]
    public void ScrollBlockingEventsOnTheWindowAndDocumentArePassiveByDefault()
    {
        using var s = Create();
        Assert.Equal("false,false,true", s.Run(@"
            window.addEventListener('wheel', e => e.preventDefault());
            document.addEventListener('touchstart', e => e.preventDefault());
            document.getElementById('btn').addEventListener('wheel', e => e.preventDefault());
            const w = new Event('wheel', { cancelable: true });
            window.dispatchEvent(w);
            const d = new Event('touchstart', { cancelable: true });
            document.dispatchEvent(d);
            const b = new Event('wheel', { cancelable: true });
            document.getElementById('btn').dispatchEvent(b);
            [w.defaultPrevented, d.defaultPrevented, b.defaultPrevented].join()"));
    }

    [Fact]
    public void OnceRunsAtMostOnce()
    {
        using var s = Create();
        Assert.Equal("1", s.Run(@"
            let count = 0;
            const btn = document.getElementById('btn');
            btn.addEventListener('go', () => count++, { once: true });
            btn.dispatchEvent(new Event('go'));
            btn.dispatchEvent(new Event('go'));
            String(count)"));
    }

    [Fact]
    public void DuplicateRegistrationsAreIgnoredButCaptureDistinguishesThem()
    {
        using var s = Create();
        Assert.Equal("2,3", s.Run(@"
            let count = 0;
            const btn = document.getElementById('btn');
            const fn = () => count++;
            btn.addEventListener('go', fn);
            btn.addEventListener('go', fn);
            btn.addEventListener('go', fn, false);
            btn.addEventListener('go', fn, { passive: true });
            btn.dispatchEvent(new Event('go'));
            const first = count + 1;
            btn.addEventListener('go', fn, true);
            btn.dispatchEvent(new Event('go'));
            first + ',' + count"));
    }

    [Fact]
    public void RemoveEventListenerMatchesTypeCallbackAndCapture()
    {
        using var s = Create();
        Assert.Equal("1,1,0", s.Run(@"
            let count = 0;
            const btn = document.getElementById('btn');
            const fn = () => count++;
            btn.addEventListener('go', fn);
            btn.removeEventListener('go', fn, true);          // wrong capture: nothing removed
            btn.dispatchEvent(new Event('go'));
            const afterWrongCapture = count;
            btn.removeEventListener('other', fn);              // wrong type: nothing removed
            btn.dispatchEvent(new Event('go'));
            const afterWrongType = count - 1;
            btn.removeEventListener('go', fn, { capture: false });
            btn.dispatchEvent(new Event('go'));
            afterWrongCapture + ',' + afterWrongType + ',' + (count - 2)"));
    }

    [Fact]
    public void ListenerAddedDuringDispatchDoesNotRunOnTheSameTarget()
    {
        using var s = Create();
        Assert.Equal("first|first,first,added", s.Run(@"
            const log = [];
            const btn = document.getElementById('btn');
            btn.addEventListener('go', () => { log.push('first'); btn.addEventListener('go', () => log.push('added')); });
            btn.dispatchEvent(new Event('go'));
            const firstDispatch = log.join();
            btn.dispatchEvent(new Event('go'));
            firstDispatch + '|' + log.join()"));
    }

    [Fact]
    public void ListenerAddedDuringDispatchRunsOnLaterTargetsInThePath()
    {
        using var s = Create();
        Assert.Equal("btn,outer", s.Run(@"
            const log = [];
            const $ = id => document.getElementById(id);
            $('btn').addEventListener('go', () => { log.push('btn'); $('outer').addEventListener('go', () => log.push('outer')); });
            $('btn').dispatchEvent(new Event('go', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void ListenerRemovedDuringDispatchDoesNotRun()
    {
        using var s = Create();
        Assert.Equal("a", s.Run(@"
            const log = [];
            const btn = document.getElementById('btn');
            const b = () => log.push('b');
            btn.addEventListener('go', () => { log.push('a'); btn.removeEventListener('go', b); });
            btn.addEventListener('go', b);
            btn.dispatchEvent(new Event('go'));
            log.join()"));
    }

    [Fact]
    public void AbortSignalOptionRemovesTheListener()
    {
        using var s = Create();
        Assert.Equal("1,0,true", s.Run(@"
            let count = 0;
            const btn = document.getElementById('btn');
            const controller = new AbortController();
            btn.addEventListener('go', () => count++, { signal: controller.signal });
            btn.dispatchEvent(new Event('go'));
            const before = count;
            controller.abort();
            btn.dispatchEvent(new Event('go'));
            before + ',' + (count - before) + ',' + controller.signal.aborted"));
    }

    [Fact]
    public void AnAlreadyAbortedSignalRegistersNothing()
    {
        using var s = Create();
        Assert.Equal("0", s.Run(@"
            let count = 0;
            const btn = document.getElementById('btn');
            btn.addEventListener('go', () => count++, { signal: AbortSignal.abort() });
            btn.dispatchEvent(new Event('go'));
            String(count)"));
    }

    [Fact]
    public void DelegationOnAnAncestorSeesEventsFromChildrenAddedLater()
    {
        using var s = Create();
        Assert.Equal("late-1,late-2", s.Run(@"
            const log = [];
            const mid = document.getElementById('mid');
            mid.addEventListener('click', e => { if (e.target.matches('.item')) log.push(e.target.id); });
            for (const n of [1, 2]) {
                const item = document.createElement('span');
                item.className = 'item';
                item.id = 'late-' + n;
                mid.appendChild(item);
            }
            for (const n of [1, 2]) document.getElementById('late-' + n).dispatchEvent(new Event('click', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void AThrowingListenerDoesNotStopTheNextOneAndIsReported()
    {
        using var s = Create();
        Assert.Equal("first,third|boom", s.Run(@"
            const log = [];
            const btn = document.getElementById('btn');
            const errors = [];
            window.addEventListener('error', e => errors.push(e.message));
            btn.addEventListener('go', () => log.push('first'));
            btn.addEventListener('go', () => { throw new Error('boom'); });
            btn.addEventListener('go', () => log.push('third'));
            btn.dispatchEvent(new Event('go'));
            log.join() + '|' + errors.join()"));
        var reported = Assert.Single(s.Uncaught);
        Assert.Equal("boom", reported.Message);
    }

    [Fact]
    public void WindowErrorEventIsAnErrorEventCarryingTheThrownValue()
    {
        using var s = Create();
        Assert.Equal("true,true,error,oops", s.Run(@"
            let seen;
            window.addEventListener('error', e => { seen = e; });
            document.getElementById('btn').addEventListener('go', () => { throw new RangeError('oops'); });
            document.getElementById('btn').dispatchEvent(new Event('go'));
            [seen instanceof ErrorEvent, seen.error instanceof RangeError, seen.type, seen.message].join()"));
    }

    [Fact]
    public void AThrowingWindowErrorListenerDoesNotRecurse()
    {
        using var s = Create();
        Assert.Equal("1", s.Run(@"
            let calls = 0;
            window.addEventListener('error', () => { calls++; throw new Error('again'); });
            document.getElementById('btn').addEventListener('go', () => { throw new Error('first'); });
            document.getElementById('btn').dispatchEvent(new Event('go'));
            String(calls)"));
        Assert.Equal(2, s.Uncaught.Count);
    }

    [Fact]
    public void HandleEventObjectsAreSupported()
    {
        using var s = Create();
        Assert.Equal("handled:go,true", s.Run(@"
            const log = [];
            const listener = { handleEvent(e) { log.push('handled:' + e.type); log.push(this === listener); } };
            const btn = document.getElementById('btn');
            btn.addEventListener('go', listener);
            btn.dispatchEvent(new Event('go'));
            log.join()"));
    }

    [Fact]
    public void ListenersRunWithThisAsTheCurrentTarget()
    {
        using var s = Create();
        Assert.Equal("true", s.Run(@"
            let ok;
            const outer = document.getElementById('outer');
            outer.addEventListener('go', function(e) { ok = this === outer && e.currentTarget === outer; });
            document.getElementById('btn').dispatchEvent(new Event('go', { bubbles: true }));
            String(ok)"));
    }

    [Fact]
    public void NullCallbackIsIgnoredAndBadArgumentsThrow()
    {
        using var s = Create();
        Assert.Equal("ok,TypeError,TypeError,TypeError", s.Run(@"
            const btn = document.getElementById('btn');
            const probe = f => { try { f(); return 'ok'; } catch (e) { return e.name; } };
            [probe(() => btn.addEventListener('go', null)),
             probe(() => btn.addEventListener('go', 42)),
             probe(() => btn.addEventListener('go')),
             probe(() => btn.dispatchEvent({}))].join()"));
    }

    [Fact]
    public void ReDispatchingAnEventInFlightThrowsInvalidStateError()
    {
        using var s = Create();
        Assert.Equal("InvalidStateError", s.Run(@"
            let name;
            const btn = document.getElementById('btn');
            btn.addEventListener('go', e => { try { btn.dispatchEvent(e); } catch (x) { name = x.name; } });
            btn.dispatchEvent(new Event('go'));
            name"));
    }

    [Fact]
    public void AnEventCanBeDispatchedAgainOnceFinished()
    {
        using var s = Create();
        Assert.Equal("2", s.Run(@"
            let count = 0;
            const btn = document.getElementById('btn');
            btn.addEventListener('go', () => count++);
            const ev = new Event('go');
            btn.dispatchEvent(ev);
            btn.dispatchEvent(ev);
            String(count)"));
    }

    [Fact]
    public void EventConstructorReadsTheInitDictionary()
    {
        using var s = Create();
        Assert.Equal("x,true,true,true,false,0,false,true,0", s.Run(@"
            const e = new Event('x', { bubbles: true, cancelable: true, composed: true });
            const plain = new Event('y');
            [e.type, e.bubbles, e.cancelable, e.composed, plain.bubbles, e.eventPhase, e.isTrusted,
             e.target === null, plain.timeStamp].join()"));
    }

    [Fact]
    public void EventConstructorRequiresATypeAndChecksTheInit()
    {
        using var s = Create();
        Assert.Equal("TypeError,TypeError,ok", s.Run(@"
            const probe = f => { try { f(); return 'ok'; } catch (e) { return e.name; } };
            [probe(() => new Event()), probe(() => new Event('x', 5)), probe(() => new Event('x', null))].join()"));
    }

    [Fact]
    public void EventPhaseConstantsExistOnTheConstructorAndPrototype()
    {
        using var s = Create();
        Assert.Equal("0,1,2,3,3", s.Run(@"
            [Event.NONE, Event.CAPTURING_PHASE, Event.AT_TARGET, Event.BUBBLING_PHASE, new Event('x').BUBBLING_PHASE].join()"));
    }

    [Fact]
    public void TimeStampComesFromTheLoopClock()
    {
        using var s = Create();
        s.Clock.Advance(250);
        Assert.Equal("250", s.Run("String(new Event('x').timeStamp)"));
        s.Clock.Advance(50);
        Assert.Equal("300", s.Run("String(new Event('x').timeStamp)"));
    }

    [Fact]
    public void ScriptDispatchedEventsAreNeverTrusted()
    {
        using var s = Create();
        Assert.Equal("false", s.Run(@"
            let trusted;
            const btn = document.getElementById('btn');
            btn.addEventListener('go', e => { trusted = e.isTrusted; });
            btn.dispatchEvent(new Event('go'));
            String(trusted)"));
    }

    [Fact]
    public void CustomEventCarriesDetail()
    {
        using var s = Create();
        Assert.Equal("true,42,,true", s.Run(@"
            let got;
            const btn = document.getElementById('btn');
            btn.addEventListener('ping', e => { got = e; });
            btn.dispatchEvent(new CustomEvent('ping', { detail: { n: 42 }, bubbles: true }));
            const first = got;
            const bare = new CustomEvent('bare');
            [got instanceof Event && got instanceof CustomEvent, first.detail.n, bare.detail, first.bubbles].join()"));
    }

    [Fact]
    public void EventsCanBeSubclassed()
    {
        using var s = Create();
        Assert.Equal("true,true,7", s.Run(@"
            class Tick extends Event {
                constructor(n) { super('tick', { bubbles: true }); this.n = n; }
            }
            let got;
            document.getElementById('outer').addEventListener('tick', e => { got = e; });
            document.getElementById('btn').dispatchEvent(new Tick(7));
            [got instanceof Tick, got instanceof Event, got.n].join()"));
    }

    [Fact]
    public void FreeStandingEventTargetsDispatchWithoutAPath()
    {
        using var s = Create();
        Assert.Equal("go:true:1", s.Run(@"
            class Bus extends EventTarget {}
            const bus = new Bus();
            const log = [];
            bus.addEventListener('go', e => log.push(e.type + ':' + (e.target === bus) + ':' + e.composedPath().length));
            bus.dispatchEvent(new Event('go', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void DocumentEventsReachTheWindowExceptLoad()
    {
        using var s = Create();
        Assert.Equal("click", s.Run(@"
            const log = [];
            window.addEventListener('click', e => log.push(e.type));
            window.addEventListener('load', e => log.push(e.type));
            document.dispatchEvent(new Event('click', { bubbles: true }));
            document.dispatchEvent(new Event('load', { bubbles: true }));
            log.join()"));
    }

    [Fact]
    public void DetachedNodesHaveATargetOnlyPath()
    {
        using var s = Create();
        Assert.Equal("1", s.Run(@"
            const el = document.createElement('div');
            let length = -1;
            el.addEventListener('go', e => { length = e.composedPath().length; });
            el.dispatchEvent(new Event('go', { bubbles: true }));
            String(length)"));
    }

    [Fact]
    public void InitEventAndCreateEventSupportTheLegacyFlow()
    {
        using var s = Create();
        Assert.Equal("legacy,true,true,InvalidStateError,NotSupportedError", s.Run(@"
            const btn = document.getElementById('btn');
            let got;
            btn.addEventListener('legacy', e => { got = e; });
            const ev = document.createEvent('Event');
            let early;
            try { btn.dispatchEvent(ev); } catch (e) { early = e.name; }
            ev.initEvent('legacy', true, true);
            btn.dispatchEvent(ev);
            let bad;
            try { document.createEvent('NoSuchEvent'); } catch (e) { bad = e.name; }
            [got.type, got.bubbles, got.cancelable, early, bad].join()"));
    }

    [Fact]
    public void AbortControllerAbortsItsSignalWithAReason()
    {
        using var s = Create();
        Assert.Equal("false,true,AbortError,true,abort", s.Run(@"
            const c = new AbortController();
            const before = c.signal.aborted;
            let type;
            c.signal.addEventListener('abort', e => { type = e.type; });
            c.abort();
            [before, c.signal.aborted, c.signal.reason.name, c.signal.reason instanceof DOMException, type].join()"));
    }

    [Fact]
    public void AbortUsesTheGivenReasonAndFiresOnlyOnce()
    {
        using var s = Create();
        Assert.Equal("because,1", s.Run(@"
            const c = new AbortController();
            let fired = 0;
            c.signal.addEventListener('abort', () => fired++);
            c.abort('because');
            c.abort('again');
            c.signal.reason + ',' + fired"));
    }

    [Fact]
    public void ThrowIfAbortedThrowsTheReasonOnlyWhenAborted()
    {
        using var s = Create();
        Assert.Equal("ok,stop", s.Run(@"
            const c = new AbortController();
            const probe = () => { try { c.signal.throwIfAborted(); return 'ok'; } catch (e) { return e; } };
            const first = probe();
            c.abort('stop');
            first + ',' + probe()"));
    }

    [Fact]
    public void AbortSignalAbortIsAlreadyAborted()
    {
        using var s = Create();
        Assert.Equal("true,AbortError,custom", s.Run(@"
            const a = AbortSignal.abort();
            const b = AbortSignal.abort('custom');
            [a.aborted, a.reason.name, b.reason].join()"));
    }

    [Fact]
    public void AbortSignalTimeoutAbortsWithATimeoutErrorWhenTheTimerFires()
    {
        using var s = Create();
        Assert.Equal("false", s.Run("globalThis.t = AbortSignal.timeout(100); String(t.aborted)"));
        s.Clock.Advance(99);
        s.Loop.SpinUntilIdle();
        Assert.Equal("false", s.Run("String(t.aborted)"));
        s.Clock.Advance(1);
        s.Loop.SpinUntilIdle();
        Assert.Equal("true,TimeoutError", s.Run("[t.aborted, t.reason.name].join()"));
    }

    [Fact]
    public void AbortSignalAnyFollowsItsSources()
    {
        using var s = Create();
        Assert.Equal("false,true,second,1", s.Run(@"
            const a = new AbortController(), b = new AbortController();
            const any = AbortSignal.any([a.signal, b.signal]);
            let fired = 0;
            any.addEventListener('abort', () => fired++);
            const before = any.aborted;
            b.abort('second');
            [before, any.aborted, any.reason, fired].join()"));
    }

    [Fact]
    public void AbortSignalAnyOfAnAbortedSignalIsAborted()
    {
        using var s = Create();
        Assert.Equal("true,pre", s.Run(@"
            const any = AbortSignal.any([new AbortController().signal, AbortSignal.abort('pre')]);
            [any.aborted, any.reason].join()"));
    }

    [Fact]
    public void AbortSignalAndControllerConstructorsAreGuarded()
    {
        using var s = Create();
        Assert.Equal("TypeError,true", s.Run(@"
            let name;
            try { new AbortSignal(); } catch (e) { name = e.name; }
            name + ',' + (new AbortController().signal instanceof EventTarget)"));
    }

    [Fact]
    public void TheEventSystemSurvivesTemplateContentAndWindowSharing()
    {
        using var s = Create("<!DOCTYPE html><html><body><template id='t'><b id='in'>x</b></template></body></html>");
        Assert.Equal("true,true", s.Run(@"
            const t = document.getElementById('t');
            [t instanceof EventTarget, t.content instanceof EventTarget].join()"));
    }
}

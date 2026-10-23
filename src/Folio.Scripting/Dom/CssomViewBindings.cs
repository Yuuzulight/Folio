using System.Numerics;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

/// <summary>
/// CSSOM View (https://www.w3.org/TR/cssom-view-1/) for scripts: element geometry and scrolling, the viewport and the scroll
/// position of the window, <c>elementFromPoint</c>, <c>matchMedia</c>, and the <c>scroll</c>, <c>resize</c> and media query
/// <c>change</c> events at the rendering opportunity. The numbers come from the host document's last layout; without a host
/// document (a bare tree) an element's size is what its style says and nothing scrolls.
/// </summary>
internal static class CssomViewBindings
{
    private static ElementNode El(object target) => (ElementNode)target;

    private static DomRect Zero => new(0, 0, 0, 0);

    private static ScriptValue Rounded(float value) => ScriptValue.FromNumber(MathF.Round(value, MidpointRounding.AwayFromZero));

    /// <summary>The members of <c>Element</c> that report geometry and scroll.</summary>
    internal static void DefineElementMembers(HostObjectTemplate t, DomContext context)
    {
        Folio.Document? Host() => context.HostDocument;

        DomRect BoundingRect(ElementNode element) => Host() is { } host
            ? host.ClientBoundingRect(element) is { } r ? new DomRect(r.X, r.Y, r.Width, r.Height) : Zero
            : DomGeometry.Rect(element, context.Layout);

        t.DefineMethod("getBoundingClientRect", (target, _, _) => context.WrapRect(BoundingRect(El(target))));
        t.DefineMethod("getClientRects", (target, _, _) =>
        {
            var element = El(target);
            var rects = Host() is { } host ? host.ClientRects(element).Select(r => new DomRect(r.X, r.Y, r.Width, r.Height)).ToList() : [DomGeometry.Rect(element, context.Layout)];
            return context.Engine.CreateArray(rects.Select(context.WrapRect).ToArray());
        });
        t.DefineProperty("clientTop", (target, _) => Host()?.LayoutBox(El(target)) is { } box ? Rounded(box.BorderTop) : ScriptValue.FromNumber(0));
        t.DefineProperty("clientLeft", (target, _) => Host()?.LayoutBox(El(target)) is { } box ? Rounded(box.BorderLeft) : ScriptValue.FromNumber(0));
        t.DefineProperty("clientWidth", (target, _) => Host() is { } host ? Rounded(host.GetClientWidth(El(target))) : ScriptValue.FromNumber(Math.Round(DomGeometry.Rect(El(target)).Width)));
        t.DefineProperty("clientHeight", (target, _) => Host() is { } host ? Rounded(host.GetClientHeight(El(target))) : ScriptValue.FromNumber(Math.Round(DomGeometry.Rect(El(target)).Height)));
        t.DefineProperty("scrollWidth", (target, _) => Host() is { } host ? Rounded(host.GetScrollWidth(El(target))) : ScriptValue.FromNumber(Math.Round(DomGeometry.Rect(El(target)).Width)));
        t.DefineProperty("scrollHeight", (target, _) => Host() is { } host ? Rounded(host.GetScrollHeight(El(target))) : ScriptValue.FromNumber(Math.Round(DomGeometry.Rect(El(target)).Height)));
        t.DefineProperty("scrollLeft", (target, _) => ScriptValue.FromNumber(context.GetScroll(El(target)).X),
            (target, _, value) => context.SetScroll(El(target), WebIdlConversions.ToDouble(value), context.GetScroll(El(target)).Y));
        t.DefineProperty("scrollTop", (target, _) => ScriptValue.FromNumber(context.GetScroll(El(target)).Y),
            (target, _, value) => context.SetScroll(El(target), context.GetScroll(El(target)).X, WebIdlConversions.ToDouble(value)));
        foreach (var name in new[] { "scroll", "scrollTo" })
        {
            t.DefineMethod(name, (target, _, args) =>
            {
                var element = El(target);
                var (x, y, behavior) = ScrollArguments(context, args, context.GetScroll(element));
                context.SetScroll(element, x, y, behavior);
                return ScriptValue.Undefined;
            });
        }
        t.DefineMethod("scrollBy", (target, _, args) =>
        {
            var element = El(target);
            var current = context.GetScroll(element);
            var (dx, dy, behavior) = ScrollArguments(context, args, (0, 0));
            context.SetScroll(element, current.X + dx, current.Y + dy, behavior);
            return ScriptValue.Undefined;
        });
        t.DefineMethod("scrollIntoView", (target, _, args) =>
        {
            string? behavior = null;
            if (args.Length > 0 && args[0].Kind is ScriptValueKind.Object && context.Engine.GetProperty(args[0], "behavior") is { Kind: ScriptValueKind.String } b)
                behavior = b.AsString();
            if (Host() is { } host)
                host.ScrollIntoView(El(target), behavior);
            return ScriptValue.Undefined;
        });
    }

    // scrollTo(x, y) and scrollTo({left, top, behavior}); an absent coordinate stays where it is.
    private static (double X, double Y, string? Behavior) ScrollArguments(DomContext context, ReadOnlySpan<ScriptValue> args, (double X, double Y) current)
    {
        if (args.Length == 0)
            return (current.X, current.Y, null);
        var first = args[0];
        if (first.Kind == ScriptValueKind.Object)
        {
            double Read(string name, double fallback) => context.Engine.GetProperty(first, name) is { IsUndefined: false } v ? FiniteOr(WebIdlConversions.ToDouble(v), fallback) : fallback;
            var behavior = context.Engine.GetProperty(first, "behavior") is { Kind: ScriptValueKind.String } b ? b.AsString() : null;
            return (Read("left", current.X), Read("top", current.Y), behavior);
        }
        if (args.Length < 2)
            throw WebIdlException.TypeError("Failed to execute 'scrollTo': 2 arguments required, but only 1 present.");
        return (FiniteOr(WebIdlConversions.ToDouble(args[0]), current.X), FiniteOr(WebIdlConversions.ToDouble(args[1]), current.Y), null);
    }

    private static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    /// <summary>The members of <c>HTMLElement</c> that report the layout box: <c>offsetParent</c> and the offsets.</summary>
    internal static void DefineHtmlElementMembers(HostObjectTemplate t, DomContext context)
    {
        Folio.Document? Host() => context.HostDocument;
        t.DefineProperty("offsetParent", (target, _) => Host()?.OffsetParent(El(target)) is { } parent ? context.Wrap(parent) : ScriptValue.Null);
        t.DefineProperty("offsetTop", (target, _) => Host()?.OffsetPosition(El(target)) is { } at ? Rounded(at.Y) : ScriptValue.FromNumber(0));
        t.DefineProperty("offsetLeft", (target, _) => Host()?.OffsetPosition(El(target)) is { } at ? Rounded(at.X) : ScriptValue.FromNumber(0));
        t.DefineProperty("offsetWidth", (target, _) => Host() is { } host ? Rounded(host.LayoutBox(El(target))?.Rect.Width ?? 0) : ScriptValue.FromNumber(Math.Round(DomGeometry.Rect(El(target)).Width)));
        t.DefineProperty("offsetHeight", (target, _) => Host() is { } host ? Rounded(host.LayoutBox(El(target))?.Rect.Height ?? 0) : ScriptValue.FromNumber(Math.Round(DomGeometry.Rect(El(target)).Height)));
    }

    /// <summary>The members of <c>Document</c> that ask what is where.</summary>
    internal static void DefineDocumentMembers(HostObjectTemplate t, DomContext context)
    {
        static (float X, float Y) Point(ReadOnlySpan<ScriptValue> args, string name)
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError($"Failed to execute '{name}' on 'Document': 2 arguments required, but only {args.Length} present.");
            return ((float)WebIdlConversions.ToDouble(args[0]), (float)WebIdlConversions.ToDouble(args[1]));
        }

        t.DefineMethod("elementFromPoint", (target, _, args) =>
        {
            var (x, y) = Point(args, "elementFromPoint");
            if (context.HostDocument is { } host)
                return host.ElementsAtClientPoint(x, y) is { Count: > 0 } hits ? context.Wrap(hits[0]) : ScriptValue.Null;
            var doc = (DocumentNode)target;
            var match = doc.Descendants.OfType<ElementNode>().Reverse().FirstOrDefault(element =>
            {
                if (element.ComputedStyle()?.Box.Display == Display.None)
                    return false;
                var rect = DomGeometry.Rect(element);
                return x >= rect.Left && x <= rect.Right && y >= rect.Top && y <= rect.Bottom;
            });
            return context.Wrap(match);
        });
        t.DefineMethod("elementsFromPoint", (target, _, args) =>
        {
            var (x, y) = Point(args, "elementsFromPoint");
            var hits = context.HostDocument?.ElementsAtClientPoint(x, y) ?? [];
            return context.Engine.CreateArray(hits.Select(context.Wrap).ToArray());
        });
        t.DefineProperty("scrollingElement", (target, _) => context.Wrap(((DocumentNode)target).DocumentElement));
    }

    // ---- the window ----

    internal static void InstallWindow(IScriptEngine engine, DomContext context)
    {
        Folio.Document? Host() => context.HostDocument;
        var host = new HostObjectTemplate("ViewportHost")
            .DefineProperty("innerWidth", (_, _) => ScriptValue.FromNumber(Host()?.Viewport is { } v ? v.Width : 800))
            .DefineProperty("innerHeight", (_, _) => ScriptValue.FromNumber(Host()?.Viewport is { } v ? v.Height : 600))
            .DefineProperty("devicePixelRatio", (_, _) => ScriptValue.FromNumber(Host()?.Viewport is { } v ? v.Scale : 1))
            .DefineProperty("scrollX", (_, _) => ScriptValue.FromNumber(Host() is { } h ? h.ViewportScroll.X : context.WindowScroll.X))
            .DefineProperty("scrollY", (_, _) => ScriptValue.FromNumber(Host() is { } h ? h.ViewportScroll.Y : context.WindowScroll.Y))
            .DefineMethod("scrollTo", (_, _, args) =>
            {
                var current = (X: (double)(Host()?.ViewportScroll.X ?? context.WindowScroll.X), Y: (double)(Host()?.ViewportScroll.Y ?? context.WindowScroll.Y));
                var (x, y, behavior) = ScrollArguments(context, args, current);
                context.ScrollWindow(x, y, behavior);
                return ScriptValue.Undefined;
            })
            .DefineMethod("scrollBy", (_, _, args) =>
            {
                var current = (X: (double)(Host()?.ViewportScroll.X ?? context.WindowScroll.X), Y: (double)(Host()?.ViewportScroll.Y ?? context.WindowScroll.Y));
                var (dx, dy, behavior) = ScrollArguments(context, args, (0, 0));
                context.ScrollWindow(current.X + dx, current.Y + dy, behavior);
                return ScriptValue.Undefined;
            })
            .DefineMethod("matches", (_, _, args) => ScriptValue.FromBoolean(args.Length > 0 && (args[0].AsString() is { } query) && (Host() is { } h ? h.MatchesMedia(query) : MatchesWithoutHost(query))))
            .DefineMethod("onRenderStep", (_, _, args) =>
            {
                context.RenderStepHandler = args.Length > 0 ? args[0] : ScriptValue.Undefined;
                return ScriptValue.Undefined;
            });
        engine.SetGlobalProperty("__folio_viewport_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript(WindowSource, "folio:viewport");
        engine.Evaluate("delete globalThis.__folio_viewport_host;", "folio:viewport-cleanup");
    }

    private static bool MatchesWithoutHost(string query) => Folio.Css.Conditions.MediaMatches(query, new Folio.Css.MediaContext(800, 600));

    private const string WindowSource = """
        (function (host) {
          'use strict';
          const defineProperty = Object.defineProperty;
          // Window attributes are accessors that a script may replace by assigning.
          function accessor(name, get) {
            defineProperty(globalThis, name, {
              get: get,
              set: function (value) { defineProperty(globalThis, name, { value: value, writable: true, enumerable: true, configurable: true }); },
              enumerable: true, configurable: true
            });
          }
          accessor('innerWidth', function () { return host.innerWidth; });
          accessor('innerHeight', function () { return host.innerHeight; });
          accessor('outerWidth', function () { return host.innerWidth; });
          accessor('outerHeight', function () { return host.innerHeight; });
          accessor('devicePixelRatio', function () { return host.devicePixelRatio; });
          accessor('scrollX', function () { return host.scrollX; });
          accessor('scrollY', function () { return host.scrollY; });
          accessor('pageXOffset', function () { return host.scrollX; });
          accessor('pageYOffset', function () { return host.scrollY; });
          function scrolling(name, fn) { defineProperty(globalThis, name, { value: fn, writable: true, enumerable: true, configurable: true }); }
          scrolling('scrollTo', function scrollTo() { host.scrollTo.apply(host, arguments); });
          scrolling('scroll', function scroll() { host.scrollTo.apply(host, arguments); });
          scrolling('scrollBy', function scrollBy() { host.scrollBy.apply(host, arguments); });

          // ---- matchMedia ----
          const lists = [];
          function MediaQueryListEvent(type, init) {
            const event = new Event(type, init);
            init = init || {};
            defineProperty(event, 'media', { value: init.media === undefined ? '' : String(init.media), enumerable: true });
            defineProperty(event, 'matches', { value: !!init.matches, enumerable: true });
            return event;
          }
          class MediaQueryList extends EventTarget {
            constructor(media) {
              super();
              defineProperty(this, '_media', { value: String(media) });
              defineProperty(this, '_last', { value: host.matches(String(media)), writable: true });
              defineProperty(this, '_onchange', { value: null, writable: true });
              defineProperty(this, '_legacy', { value: [] });
            }
            get media() { return this._media; }
            get matches() { return host.matches(this._media); }
            get onchange() { return this._onchange; }
            set onchange(value) { this._onchange = typeof value === 'function' ? value : null; }
            addListener(callback) { if (callback) this.addEventListener('change', callback); }
            removeListener(callback) { if (callback) this.removeEventListener('change', callback); }
          }
          defineProperty(MediaQueryList.prototype, Symbol.toStringTag, { value: 'MediaQueryList', configurable: true });
          defineProperty(globalThis, 'MediaQueryList', { value: MediaQueryList, writable: true, configurable: true });
          defineProperty(globalThis, 'MediaQueryListEvent', { value: MediaQueryListEvent, writable: true, configurable: true });
          globalThis.matchMedia = function matchMedia(query) {
            if (arguments.length === 0) throw new TypeError("Failed to execute 'matchMedia' on 'Window': 1 argument required, but only 0 present.");
            const list = new MediaQueryList(String(query));
            lists.push(new WeakRef(list));
            return list;
          };
          // At the rendering opportunity: each list whose answer changed gets a change event.
          host.onRenderStep(function (step) {
            if (step === 'resize') { globalThis.dispatchEvent(new Event('resize')); return; }
            if (step !== 'media') return;
            for (let i = lists.length - 1; i >= 0; i--) {
              const list = lists[i].deref();
              if (!list) { lists.splice(i, 1); continue; }
            }
            for (const ref of lists.slice()) {
              const list = ref.deref();
              if (!list) continue;
              const now = host.matches(list._media);
              if (now === list._last) continue;
              list._last = now;
              const event = MediaQueryListEvent('change', { media: list._media, matches: now });
              if (typeof list._onchange === 'function') { try { list._onchange.call(list, event); } catch (error) { } }
              list.dispatchEvent(event);
            }
          });
        })(__folio_viewport_host);
        """;
}

/// <summary>The steps of "update the rendering" that CSSOM View owns: the <c>resize</c> event, <c>scroll</c> events and media query changes.</summary>
internal sealed class DomRenderingHooks : RenderingStepsHooks
{
    private readonly DomContext _context;
    private readonly List<ElementNode> _scrolled = [];
    private (float Width, float Height)? _viewport;

    internal DomRenderingHooks(DomContext context)
    {
        _context = context;
        if (context.HostDocument is { } host)
            host.ScrollOffsetChanged += node =>
            {
                lock (_scrolled)
                {
                    if (!_scrolled.Contains(node))
                        _scrolled.Add(node);
                }
            };
        _viewport = context.HostDocument?.Viewport is { } v ? (v.Width, v.Height) : null;
    }

    public override void RunResizeSteps()
    {
        if (_context.HostDocument?.Viewport is not { } now)
            return;
        var changed = _viewport is { } before && (before.Width != now.Width || before.Height != now.Height);
        _viewport = (now.Width, now.Height);
        if (changed)
            _context.CallRenderStep("resize");
    }

    public override void RunScrollSteps()
    {
        ElementNode[] pending;
        lock (_scrolled)
        {
            pending = [.. _scrolled];
            _scrolled.Clear();
        }
        foreach (var node in pending)
        {
            var viewport = node == _context.Document.DocumentElement;
            // The viewport's scroll event is for the document and bubbles to the window; an element's goes to the element alone.
            _context.FireEvent(viewport ? _context.Document : node, "scroll", bubbles: viewport, cancelable: false);
        }
    }

    public override void RunMediaQueryChanges() => _context.CallRenderStep("media");
}

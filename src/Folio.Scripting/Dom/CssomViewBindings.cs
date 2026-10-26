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

    /// <summary>
    /// <c>ResizeObserver</c> and <c>IntersectionObserver</c> (https://drafts.csswg.org/resize-observer/, https://www.w3.org/TR/intersection-observer/).
    /// The observers are script classes; the host answers how big an element is and where it meets its root, from the last layout.
    /// </summary>
    internal static void InstallObservers(IScriptEngine engine, DomContext context)
    {
        Folio.Document? Host() => context.HostDocument;
        ScriptValue Numbers(params float[] values) => context.Engine.CreateArray([.. values.Select(v => ScriptValue.FromNumber(v))]);
        ElementNode? Element(ReadOnlySpan<ScriptValue> args, int index) => args.Length > index && context.TryUnwrapElement(args[index], out var e) ? e : null;

        var host = new HostObjectTemplate("ObserverHost")
            .DefineMethod("sizes", (_, _, args) =>
            {
                if (Element(args, 0) is not { } element)
                    return Numbers(0, 0, 0, 0, 0, 0);
                if (Host() is not { } doc)
                {
                    var rect = DomGeometry.Rect(element);
                    return Numbers((float)rect.Width, (float)rect.Height, (float)rect.Width, (float)rect.Height, 0, 0);
                }
                return doc.ObservedSizes(element) is { } s ? Numbers(s.BorderWidth, s.BorderHeight, s.ContentWidth, s.ContentHeight, s.PaddingLeft, s.PaddingTop) : Numbers(0, 0, 0, 0, 0, 0);
            })
            .DefineMethod("intersect", (_, _, args) =>
            {
                if (Host() is not { } doc || Element(args, 0) is not { } target)
                    return ScriptValue.Null;
                Span<float> margin = stackalloc float[4];
                for (var k = 0; k < 4 && args.Length > k + 2; k++)
                    margin[k] = (float)WebIdlConversions.ToDouble(args[k + 2]);
                if (doc.Intersect(target, Element(args, 1), margin[0], margin[1], margin[2], margin[3]) is not { } hit)
                    return ScriptValue.Null;
                var (t, i, r) = hit;
                return Numbers(t.X, t.Y, t.Width, t.Height, r.X, r.Y, r.Width, r.Height, i is { } m ? 1 : 0, i?.X ?? 0, i?.Y ?? 0, i?.Width ?? 0, i?.Height ?? 0);
            })
            .DefineMethod("onStep", (_, _, args) =>
            {
                context.ObserverStepHandler = args.Length > 0 ? args[0] : ScriptValue.Undefined;
                return ScriptValue.Undefined;
            });
        engine.SetGlobalProperty("__folio_observer_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript(ObserverSource, "folio:observers");
        engine.Evaluate("delete globalThis.__folio_observer_host;", "folio:observers-cleanup");
    }

    private const string ObserverSource = """
        (function (host) {
          'use strict';
          const defineProperty = Object.defineProperty;
          const now = function () { return typeof performance !== 'undefined' && typeof performance.now === 'function' ? performance.now() : Date.now(); };
          function def(name, value) { defineProperty(globalThis, name, { value: value, writable: true, enumerable: false, configurable: true }); }
          function hidden(object, name, value) { defineProperty(object, name, { value: value, writable: true, enumerable: false, configurable: true }); }
          function tag(C, name) { defineProperty(C.prototype, Symbol.toStringTag, { value: name, configurable: true }); }
          function isElement(value) { return value !== null && typeof value === 'object' && value.nodeType === 1; }
          function report(error) {
            try { if (typeof reportError === 'function') reportError(error); } catch (_) { }
          }
          // An instance of one of the read-only result classes, which scripts cannot construct.
          function make(C, fields) {
            const instance = Object.create(C.prototype);
            for (const name in fields) hidden(instance, name, fields[name]);
            return instance;
          }
          function getters(C, names) {
            for (const name of names)
              defineProperty(C.prototype, name, { get: function () { return this['_' + name]; }, enumerable: true, configurable: true });
          }

          if (typeof globalThis.DOMRectReadOnly !== 'function') {
            class DOMRectReadOnly {
              constructor(x, y, width, height) { hidden(this, '_r', [+x || 0, +y || 0, +width || 0, +height || 0]); }
              get x() { return this._r[0]; }
              get y() { return this._r[1]; }
              get width() { return this._r[2]; }
              get height() { return this._r[3]; }
              get top() { return Math.min(this._r[1], this._r[1] + this._r[3]); }
              get left() { return Math.min(this._r[0], this._r[0] + this._r[2]); }
              get right() { return Math.max(this._r[0], this._r[0] + this._r[2]); }
              get bottom() { return Math.max(this._r[1], this._r[1] + this._r[3]); }
              toJSON() { return { x: this.x, y: this.y, width: this.width, height: this.height, top: this.top, right: this.right, bottom: this.bottom, left: this.left }; }
            }
            tag(DOMRectReadOnly, 'DOMRectReadOnly');
            def('DOMRectReadOnly', DOMRectReadOnly);
          }
          const Rect = function (x, y, w, h) { return new globalThis.DOMRectReadOnly(x, y, w, h); };
          const depthOf = function (node) { let n = 0; for (let p = node.parentNode; p; p = p.parentNode) n++; return n; };

          // ---- ResizeObserver ----
          class ResizeObserverSize {
            constructor() { throw new TypeError('Illegal constructor'); }
          }
          getters(ResizeObserverSize, ['inlineSize', 'blockSize']);
          tag(ResizeObserverSize, 'ResizeObserverSize');
          class ResizeObserverEntry {
            constructor() { throw new TypeError('Illegal constructor'); }
          }
          getters(ResizeObserverEntry, ['target', 'contentRect', 'borderBoxSize', 'contentBoxSize', 'devicePixelContentBoxSize']);
          tag(ResizeObserverEntry, 'ResizeObserverEntry');

          const resizing = new Set();
          let resizeCount = 0;
          const size = function (w, h) { return Object.freeze(make(ResizeObserverSize, { _inlineSize: w, _blockSize: h })); };
          // The size of the observed box that decides whether a target changed: [width, height].
          function boxSize(observation, sizes) {
            switch (observation.box) {
              case 'border-box': return [sizes[0], sizes[1]];
              case 'device-pixel-content-box': return [Math.round(sizes[2] * devicePixelRatio), Math.round(sizes[3] * devicePixelRatio)];
              default: return [sizes[2], sizes[3]];
            }
          }
          class ResizeObserver {
            constructor(callback) {
              if (arguments.length === 0) throw new TypeError("Failed to construct 'ResizeObserver': 1 argument required, but only 0 present.");
              if (typeof callback !== 'function') throw new TypeError("Failed to construct 'ResizeObserver': The callback provided as parameter 1 is not a function.");
              hidden(this, '_callback', callback);
              hidden(this, '_observations', []);
              hidden(this, '_id', ++resizeCount);
            }
            observe(target, options) {
              if (arguments.length === 0) throw new TypeError("Failed to execute 'observe' on 'ResizeObserver': 1 argument required, but only 0 present.");
              if (!isElement(target)) throw new TypeError("Failed to execute 'observe' on 'ResizeObserver': parameter 1 is not of type 'Element'.");
              let box = 'content-box';
              if (options !== undefined && options !== null) {
                if (typeof options !== 'object' && typeof options !== 'function') throw new TypeError("Failed to execute 'observe' on 'ResizeObserver': parameter 2 ('options') is not an object.");
                if (options.box !== undefined) box = String(options.box);
                if (box !== 'content-box' && box !== 'border-box' && box !== 'device-pixel-content-box')
                  throw new TypeError("Failed to execute 'observe' on 'ResizeObserver': The provided value '" + box + "' is not a valid enum value of type ResizeObserverBoxOptions.");
              }
              this.unobserve(target);
              this._observations.push({ target: target, box: box, size: [0, 0] });
              resizing.add(this);
            }
            unobserve(target) {
              if (arguments.length === 0) throw new TypeError("Failed to execute 'unobserve' on 'ResizeObserver': 1 argument required, but only 0 present.");
              if (!isElement(target)) throw new TypeError("Failed to execute 'unobserve' on 'ResizeObserver': parameter 1 is not of type 'Element'.");
              const list = this._observations;
              for (let i = 0; i < list.length; i++) if (list[i].target === target) { list.splice(i, 1); break; }
              if (list.length === 0) resizing.delete(this);
            }
            disconnect() {
              this._observations.length = 0;
              resizing.delete(this);
            }
          }
          tag(ResizeObserver, 'ResizeObserver');

          // https://drafts.csswg.org/resize-observer/#broadcast-resize-notifications-section
          function runResizeObservers() {
            if (resizing.size === 0) return;
            let depth = 0;
            for (;;) {
              const observers = Array.from(resizing).sort(function (a, b) { return a._id - b._id; });
              let active = false;
              let skipped = false;
              const gathered = [];
              for (const observer of observers) {
                const changed = [];
                for (const observation of observer._observations) {
                  const current = boxSize(observation, host.sizes(observation.target));
                  if (current[0] === observation.size[0] && current[1] === observation.size[1]) continue;
                  if (depthOf(observation.target) > depth) changed.push([observation, host.sizes(observation.target)]); else skipped = true;
                }
                if (changed.length > 0) { active = true; gathered.push([observer, changed]); }
              }
              if (!active) {
                if (skipped) {
                  try {
                    globalThis.dispatchEvent(new ErrorEvent('error', { message: 'ResizeObserver loop completed with undelivered notifications.', cancelable: true }));
                  } catch (error) { report(error); }
                }
                return;
              }
              let shallowest = Infinity;
              for (const pair of gathered) {
                const observer = pair[0];
                const entries = [];
                // The sizes are those the layout had when the observations were gathered, not what an earlier callback changed since.
                for (const gatheredObservation of pair[1]) {
                  const observation = gatheredObservation[0];
                  const sizes = gatheredObservation[1];
                  observation.size = boxSize(observation, sizes);
                  const dpr = devicePixelRatio;
                  entries.push(make(ResizeObserverEntry, {
                    _target: observation.target,
                    _contentRect: Rect(sizes[4], sizes[5], sizes[2], sizes[3]),
                    _borderBoxSize: Object.freeze([size(sizes[0], sizes[1])]),
                    _contentBoxSize: Object.freeze([size(sizes[2], sizes[3])]),
                    _devicePixelContentBoxSize: Object.freeze([size(Math.round(sizes[2] * dpr), Math.round(sizes[3] * dpr))])
                  }));
                  shallowest = Math.min(shallowest, depthOf(observation.target));
                }
                try { observer._callback.call(observer, entries, observer); } catch (error) { report(error); }
              }
              depth = shallowest;
            }
          }
          def('ResizeObserver', ResizeObserver);
          def('ResizeObserverEntry', ResizeObserverEntry);
          def('ResizeObserverSize', ResizeObserverSize);

          // ---- IntersectionObserver ----
          class IntersectionObserverEntry {
            constructor() { throw new TypeError('Illegal constructor'); }
            toJSON() {
              return { time: this.time, rootBounds: this.rootBounds && this.rootBounds.toJSON(), boundingClientRect: this.boundingClientRect.toJSON(),
                intersectionRect: this.intersectionRect.toJSON(), isIntersecting: this.isIntersecting, intersectionRatio: this.intersectionRatio };
            }
          }
          getters(IntersectionObserverEntry, ['time', 'rootBounds', 'boundingClientRect', 'intersectionRect', 'isIntersecting', 'intersectionRatio', 'target']);
          tag(IntersectionObserverEntry, 'IntersectionObserverEntry');

          const intersecting = new Set();
          function syntax(message) {
            return typeof DOMException === 'function' ? new DOMException(message, 'SyntaxError') : new SyntaxError(message);
          }
          // "10px 5%" -> four [value, unit] pairs, top right bottom left.
          function parseMargin(text) {
            const tokens = String(text).trim().split(/\s+/);
            if (tokens.length === 0 || tokens.length > 4 || tokens[0] === '') throw syntax("Failed to construct 'IntersectionObserver': Failed to parse rootMargin.");
            const parsed = tokens.map(function (token) {
              const m = /^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)(px|%)$/.exec(token);
              if (!m) throw syntax("Failed to construct 'IntersectionObserver': rootMargin must be specified in pixels or percent.");
              return [parseFloat(m[1]), m[2]];
            });
            const [a, b, c, d] = parsed;
            switch (parsed.length) {
              case 1: return [a, a, a, a];
              case 2: return [a, b, a, b];
              case 3: return [a, b, c, b];
              default: return [a, b, c, d];
            }
          }
          class IntersectionObserver {
            constructor(callback, options) {
              if (arguments.length === 0) throw new TypeError("Failed to construct 'IntersectionObserver': 1 argument required, but only 0 present.");
              if (typeof callback !== 'function') throw new TypeError("Failed to construct 'IntersectionObserver': The callback provided as parameter 1 is not a function.");
              let root = null;
              let margin = '0px';
              let thresholds = [0];
              if (options !== undefined && options !== null) {
                if (typeof options !== 'object' && typeof options !== 'function') throw new TypeError("Failed to construct 'IntersectionObserver': parameter 2 ('options') is not an object.");
                if (options.root !== undefined && options.root !== null) {
                  if (options.root.nodeType === 9) root = null;
                  else if (isElement(options.root)) root = options.root;
                  else throw new TypeError("Failed to construct 'IntersectionObserver': Failed to read the 'root' property from 'IntersectionObserverInit': The provided value is not of type '(Document or Element)'.");
                }
                if (options.rootMargin !== undefined) margin = String(options.rootMargin);
                if (options.threshold !== undefined) {
                  const list = typeof options.threshold === 'object' && options.threshold !== null && Symbol.iterator in options.threshold ? Array.from(options.threshold) : [options.threshold];
                  thresholds = list.map(function (value) {
                    const n = Number(value);
                    if (n !== n) throw new TypeError("Failed to construct 'IntersectionObserver': The provided double value is non-finite.");
                    if (n < 0 || n > 1) throw new RangeError('Threshold values must be numbers between 0 and 1');
                    return n;
                  }).sort(function (x, y) { return x - y; });
                  if (thresholds.length === 0) thresholds = [0];
                }
              }
              const parsed = parseMargin(margin);
              hidden(this, '_callback', callback);
              hidden(this, '_root', root);
              hidden(this, '_margin', parsed);
              hidden(this, '_thresholds', Object.freeze(thresholds));
              hidden(this, '_observations', []);
              hidden(this, '_queue', []);
            }
            get root() { return this._root; }
            get rootMargin() { return this._margin.map(function (p) { return p[0] + p[1]; }).join(' '); }
            get thresholds() { return this._thresholds; }
            observe(target) {
              if (arguments.length === 0) throw new TypeError("Failed to execute 'observe' on 'IntersectionObserver': 1 argument required, but only 0 present.");
              if (!isElement(target)) throw new TypeError("Failed to execute 'observe' on 'IntersectionObserver': parameter 1 is not of type 'Element'.");
              if (this._observations.some(function (o) { return o.target === target; })) return;
              this._observations.push({ target: target, index: -1, intersecting: false });
              intersecting.add(this);
            }
            unobserve(target) {
              if (arguments.length === 0) throw new TypeError("Failed to execute 'unobserve' on 'IntersectionObserver': 1 argument required, but only 0 present.");
              if (!isElement(target)) throw new TypeError("Failed to execute 'unobserve' on 'IntersectionObserver': parameter 1 is not of type 'Element'.");
              const list = this._observations;
              for (let i = 0; i < list.length; i++) if (list[i].target === target) { list.splice(i, 1); break; }
              this._queue = this._queue.filter(function (entry) { return entry.target !== target; });
              if (list.length === 0) intersecting.delete(this);
            }
            disconnect() {
              this._observations.length = 0;
              this._queue.length = 0;
              intersecting.delete(this);
            }
            takeRecords() {
              const queued = this._queue;
              this._queue = [];
              return queued;
            }
          }
          tag(IntersectionObserver, 'IntersectionObserver');

          // The root margin in pixels: percentages are of the root's own width (left and right) or height (top and bottom).
          function pixelMargin(observer) {
            const root = observer._root;
            const w = root ? root.clientWidth : innerWidth;
            const h = root ? root.clientHeight : innerHeight;
            const m = observer._margin;
            const px = function (pair, of) { return pair[1] === '%' ? of * pair[0] / 100 : pair[0]; };
            return [px(m[0], h), px(m[1], w), px(m[2], h), px(m[3], w)];
          }

          // https://www.w3.org/TR/intersection-observer/#update-intersection-observations-algo
          function runIntersectionObservers() {
            if (intersecting.size === 0) return;
            for (const observer of Array.from(intersecting)) {
              const root = observer._root;
              const margin = pixelMargin(observer);
              const thresholds = observer._thresholds;
              for (const observation of observer._observations.slice()) {
                const target = observation.target;
                const contained = !root || (root !== target && root.contains(target));
                const hit = contained ? host.intersect(target, root, margin[0], margin[1], margin[2], margin[3]) : null;
                let isIntersecting = false;
                let ratio = 0;
                let boundingRect;
                let intersection = Rect(0, 0, 0, 0);
                let rootBounds = null;
                if (hit) {
                  boundingRect = Rect(hit[0], hit[1], hit[2], hit[3]);
                  rootBounds = Rect(hit[4], hit[5], hit[6], hit[7]);
                  if (hit[8]) {
                    isIntersecting = true;
                    intersection = Rect(hit[9], hit[10], hit[11], hit[12]);
                    const targetArea = hit[2] * hit[3];
                    ratio = targetArea === 0 ? 1 : Math.min(1, (hit[11] * hit[12]) / targetArea);
                  }
                } else {
                  const r = target.getBoundingClientRect();
                  boundingRect = Rect(r.x, r.y, r.width, r.height);
                  if (!root) rootBounds = Rect(-margin[3], -margin[0], innerWidth + margin[1] + margin[3], innerHeight + margin[0] + margin[2]);
                }
                let index = 0;
                if (isIntersecting) {
                  index = thresholds.length;
                  for (let i = 0; i < thresholds.length; i++) if (thresholds[i] > ratio) { index = i; break; }
                }
                if (index === observation.index && isIntersecting === observation.intersecting) continue;
                observation.index = index;
                observation.intersecting = isIntersecting;
                observer._queue.push(make(IntersectionObserverEntry, {
                  _time: now(), _rootBounds: rootBounds, _boundingClientRect: boundingRect, _intersectionRect: intersection,
                  _isIntersecting: isIntersecting, _intersectionRatio: ratio, _target: target
                }));
              }
            }
            for (const observer of Array.from(intersecting)) {
              if (observer._queue.length === 0) continue;
              const entries = observer.takeRecords();
              try { observer._callback.call(observer, entries, observer); } catch (error) { report(error); }
            }
          }
          def('IntersectionObserver', IntersectionObserver);
          def('IntersectionObserverEntry', IntersectionObserverEntry);

          host.onStep(function (step) {
            if (step === 'resize') runResizeObservers();
            else if (step === 'intersect') runIntersectionObservers();
          });
        })(__folio_observer_host);
        """;

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

    public override void RunResizeObservers() => _context.CallObserverStep("resize");

    public override void RunIntersectionObservers() => _context.CallObserverStep("intersect");
}

namespace Folio.Scripting.Dom;

/// <summary>
/// Installs the DOM events system onto an <see cref="IScriptEngine"/> (DOM §2 Events, study 17, #455):
/// <c>EventTarget</c> (nodes, the window and free-standing targets), <c>Event</c>, <c>CustomEvent</c>, <c>ErrorEvent</c>,
/// the dispatch algorithm, <c>AbortController</c> and <c>AbortSignal</c>.
/// </summary>
/// <remarks>
/// The event classes are script-level so that scripts can construct and subclass them. The host supplies two
/// services through a private bridge object: the event loop's clock (for <c>timeStamp</c>) and exception reporting.
/// </remarks>
internal static class DomEvents
{
    internal static void Install(IScriptEngine engine, HtmlEventLoop? eventLoop)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var bridge = new HostObjectTemplate("FolioEventsHost")
            .DefineMethod("now", (_, _, _) => ScriptValue.FromNumber(eventLoop?.Clock.NowMs ?? 0))
            .DefineMethod("report", (_, _, args) =>
            {
                var message = args.Length > 0 && args[0].IsString ? args[0].AsString()! : "Uncaught exception in event listener";
                var stack = args.Length > 1 && args[1].IsString ? args[1].AsString() : null;
                eventLoop?.ReportException(new ScriptRuntimeException(message, stack));
                return ScriptValue.Undefined;
            });

        engine.SetGlobalProperty("__folio_events_host", engine.CreateHostObject(bridge, new object()));
        engine.Evaluate(Source, "folio:events");
    }

    private const string Source = """
        (function(host) {
            'use strict';
            const global = globalThis;
            const NodeCtor = global.Node;

            const NONE = 0, CAPTURING_PHASE = 1, AT_TARGET = 2, BUBBLING_PHASE = 3;
            const INTERNAL = {};

            const eventState = new WeakMap();
            const targetListeners = new WeakMap();
            const signalState = new WeakMap();
            const controllerSignal = new WeakMap();
            let errorDispatchDepth = 0;

            function defineGlobal(name, value) {
                Object.defineProperty(global, name, { value: value, writable: true, configurable: true, enumerable: false });
            }

            function tag(ctor, name) {
                Object.defineProperty(ctor.prototype, Symbol.toStringTag, { value: name, configurable: true });
            }

            function isNode(value) {
                return typeof NodeCtor === 'function' && value instanceof NodeCtor;
            }

            function requireArgs(count, given, method, iface) {
                if (given < count)
                    throw new TypeError("Failed to execute '" + method + "' on '" + iface + "': " + count + " argument" + (count === 1 ? "" : "s") + " required, but only " + given + " present.");
            }

            function stateOf(event) {
                const s = eventState.get(event);
                if (s === undefined) throw new TypeError('Illegal invocation');
                return s;
            }

            function newState(type, bubbles, cancelable, composed) {
                return {
                    type: type, bubbles: bubbles, cancelable: cancelable, composed: composed,
                    target: null, currentTarget: null, phase: NONE, path: [],
                    stopPropagation: false, stopImmediate: false, canceled: false, inPassive: false,
                    dispatching: false, initialized: true, trusted: false, timeStamp: host.now()
                };
            }

            function initDictionary(init, iface) {
                if (init === undefined || init === null) return {};
                if (typeof init !== 'object' && typeof init !== 'function')
                    throw new TypeError("Failed to construct '" + iface + "': The provided value is not of type '" + iface + "Init'.");
                return init;
            }

            // ------------------------------------------------------------------ Event

            class Event {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'Event', 'Event');
                    const dict = initDictionary(init, 'Event');
                    eventState.set(this, newState(String(type), !!dict.bubbles, !!dict.cancelable, !!dict.composed));
                }
                get type() { return stateOf(this).type; }
                get target() { return stateOf(this).target; }
                get srcElement() { return stateOf(this).target; }
                get currentTarget() { return stateOf(this).currentTarget; }
                get eventPhase() { return stateOf(this).phase; }
                get bubbles() { return stateOf(this).bubbles; }
                get cancelable() { return stateOf(this).cancelable; }
                get composed() { return stateOf(this).composed; }
                get defaultPrevented() { return stateOf(this).canceled; }
                get isTrusted() { return stateOf(this).trusted; }
                get timeStamp() { return stateOf(this).timeStamp; }
                get returnValue() { return !stateOf(this).canceled; }
                set returnValue(value) { if (!value) this.preventDefault(); }
                get cancelBubble() { return stateOf(this).stopPropagation; }
                set cancelBubble(value) { if (value) this.stopPropagation(); }
                composedPath() {
                    const s = stateOf(this);
                    return s.dispatching ? s.path.slice() : [];
                }
                stopPropagation() { stateOf(this).stopPropagation = true; }
                stopImmediatePropagation() {
                    const s = stateOf(this);
                    s.stopPropagation = true;
                    s.stopImmediate = true;
                }
                preventDefault() {
                    const s = stateOf(this);
                    if (s.cancelable && !s.inPassive) s.canceled = true;
                }
                initEvent(type, bubbles, cancelable) {
                    requireArgs(1, arguments.length, 'initEvent', 'Event');
                    const s = stateOf(this);
                    if (s.dispatching) return;
                    s.initialized = true;
                    s.stopPropagation = false;
                    s.stopImmediate = false;
                    s.canceled = false;
                    s.trusted = false;
                    s.target = null;
                    s.type = String(type);
                    s.bubbles = !!bubbles;
                    s.cancelable = !!cancelable;
                }
            }
            for (const target of [Event, Event.prototype]) {
                Object.defineProperties(target, {
                    NONE: { value: NONE, enumerable: true },
                    CAPTURING_PHASE: { value: CAPTURING_PHASE, enumerable: true },
                    AT_TARGET: { value: AT_TARGET, enumerable: true },
                    BUBBLING_PHASE: { value: BUBBLING_PHASE, enumerable: true }
                });
            }
            tag(Event, 'Event');

            class CustomEvent extends Event {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'CustomEvent', 'CustomEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'CustomEvent');
                    eventState.get(this).detail = dict.detail === undefined ? null : dict.detail;
                }
                get detail() { return stateOf(this).detail === undefined ? null : stateOf(this).detail; }
                initCustomEvent(type, bubbles, cancelable, detail) {
                    requireArgs(1, arguments.length, 'initCustomEvent', 'CustomEvent');
                    const s = stateOf(this);
                    if (s.dispatching) return;
                    this.initEvent(type, bubbles, cancelable);
                    s.detail = detail === undefined ? null : detail;
                }
            }
            tag(CustomEvent, 'CustomEvent');

            class ErrorEvent extends Event {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'ErrorEvent', 'ErrorEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'ErrorEvent');
                    const s = eventState.get(this);
                    s.message = dict.message === undefined ? '' : String(dict.message);
                    s.filename = dict.filename === undefined ? '' : String(dict.filename);
                    s.lineno = dict.lineno === undefined ? 0 : dict.lineno >>> 0;
                    s.colno = dict.colno === undefined ? 0 : dict.colno >>> 0;
                    s.error = dict.error === undefined ? null : dict.error;
                }
                get message() { return stateOf(this).message; }
                get filename() { return stateOf(this).filename; }
                get lineno() { return stateOf(this).lineno; }
                get colno() { return stateOf(this).colno; }
                get error() { return stateOf(this).error; }
            }
            tag(ErrorEvent, 'ErrorEvent');

            // ------------------------------------------------------------------ dispatch

            // https://dom.spec.whatwg.org/#get-the-parent: a document's parent is the window, except for load.
            function getParent(target, event) {
                if (target === global || !isNode(target)) return null;
                const parent = target.parentNode;
                if (parent) return parent;
                if (target.nodeType === 9 && event.type !== 'load') return global;
                return null;
            }

            function reportError(error) {
                let message;
                let stack = null;
                try {
                    message = (error !== null && typeof error === 'object' && 'message' in error) ? String(error.message) : String(error);
                } catch (_) {
                    message = 'Uncaught exception in event listener';
                }
                try {
                    if (error !== null && typeof error === 'object' && error.stack) stack = String(error.stack);
                } catch (_) { }

                // A throwing window error listener is reported to the host only, never dispatched again.
                if (errorDispatchDepth === 0) {
                    errorDispatchDepth++;
                    try {
                        dispatch(global, new ErrorEvent('error', { cancelable: true, message: message, error: error }));
                    } finally {
                        errorDispatchDepth--;
                    }
                }
                host.report(message, stack);
            }

            function removeListener(target, listener) {
                listener.removed = true;
                const list = targetListeners.get(target);
                if (list === undefined) return;
                const index = list.indexOf(listener);
                if (index >= 0) list.splice(index, 1);
            }

            // https://dom.spec.whatwg.org/#concept-event-listener-inner-invoke
            function innerInvoke(target, event, s, listeners, phase) {
                for (const listener of listeners) {
                    if (listener.removed) continue;
                    if (phase === 'capturing' && !listener.capture) continue;
                    if (phase === 'bubbling' && listener.capture) continue;
                    if (listener.once) removeListener(target, listener);
                    if (listener.passive) s.inPassive = true;
                    try {
                        const callback = listener.callback;
                        if (typeof callback === 'function') {
                            callback.call(target, event);
                        } else {
                            const handler = callback.handleEvent;
                            if (typeof handler !== 'function')
                                throw new TypeError("Failed to execute the event listener: 'handleEvent' is not a function.");
                            handler.call(callback, event);
                        }
                    } catch (error) {
                        reportError(error);
                    }
                    s.inPassive = false;
                    if (s.stopImmediate) return;
                }
            }

            // https://dom.spec.whatwg.org/#concept-event-listener-invoke
            function invoke(target, event, s, phase) {
                if (s.stopPropagation) return;
                s.currentTarget = target;
                const all = targetListeners.get(target);
                if (all === undefined || all.length === 0) return;
                const type = s.type;
                const listeners = all.filter(function(l) { return l.type === type; });
                if (listeners.length === 0) return;
                innerInvoke(target, event, s, listeners, phase);
            }

            // https://dom.spec.whatwg.org/#concept-event-dispatch
            function dispatch(target, event) {
                const s = stateOf(event);
                s.dispatching = true;
                s.target = target;

                const path = [target];
                for (let parent = getParent(target, event); parent; parent = getParent(parent, event))
                    path.push(parent);
                s.path = path;

                for (let i = path.length - 1; i >= 0; i--) {
                    s.phase = i === 0 ? AT_TARGET : CAPTURING_PHASE;
                    invoke(path[i], event, s, 'capturing');
                }
                for (let i = 0; i < path.length; i++) {
                    if (i === 0) {
                        s.phase = AT_TARGET;
                    } else {
                        if (!s.bubbles) continue;
                        s.phase = BUBBLING_PHASE;
                    }
                    invoke(path[i], event, s, 'bubbling');
                }

                s.phase = NONE;
                s.currentTarget = null;
                s.path = [];
                s.dispatching = false;
                s.stopPropagation = false;
                s.stopImmediate = false;
                return !s.canceled;
            }

            // ------------------------------------------------------------------ EventTarget

            function resolveTarget(self) {
                return self === undefined || self === null ? global : self;
            }

            function isPassiveByDefault(target, type) {
                if (type !== 'touchstart' && type !== 'touchmove' && type !== 'wheel' && type !== 'mousewheel') return false;
                if (target === global) return true;
                if (!isNode(target)) return false;
                if (target.nodeType === 9) return true;
                const doc = target.ownerDocument;
                return !!doc && (target === doc.documentElement || target === doc.body);
            }

            function captureOf(options) {
                if (options !== null && typeof options === 'object') return !!options.capture;
                return !!options;
            }

            class EventTarget {
                constructor() { }
                addEventListener(type, callback, options) {
                    requireArgs(2, arguments.length, 'addEventListener', 'EventTarget');
                    const target = resolveTarget(this);
                    if (callback === null || callback === undefined) return;
                    if (typeof callback !== 'function' && typeof callback !== 'object')
                        throw new TypeError("Failed to execute 'addEventListener' on 'EventTarget': The callback provided as parameter 2 is not an object.");

                    type = String(type);
                    let capture = false, once = false, passive, signal;
                    if (options !== null && typeof options === 'object') {
                        capture = !!options.capture;
                        once = !!options.once;
                        passive = options.passive === undefined ? undefined : !!options.passive;
                        signal = options.signal;
                        if (signal !== undefined && !(signal instanceof AbortSignal))
                            throw new TypeError("Failed to execute 'addEventListener' on 'EventTarget': Failed to read the 'signal' property from 'AddEventListenerOptions': Failed to convert value to 'AbortSignal'.");
                    } else {
                        capture = !!options;
                    }
                    if (passive === undefined) passive = isPassiveByDefault(target, type);

                    if (signal !== undefined && signal.aborted) return;

                    let list = targetListeners.get(target);
                    if (list === undefined) {
                        list = [];
                        targetListeners.set(target, list);
                    }
                    for (const existing of list) {
                        if (existing.type === type && existing.callback === callback && existing.capture === capture) return;
                    }

                    const listener = { type: type, callback: callback, capture: capture, passive: passive, once: once, removed: false };
                    list.push(listener);
                    if (signal !== undefined)
                        signalState.get(signal).algorithms.push(function() { removeListener(target, listener); });
                }
                removeEventListener(type, callback, options) {
                    requireArgs(2, arguments.length, 'removeEventListener', 'EventTarget');
                    const target = resolveTarget(this);
                    if (callback === null || callback === undefined) return;
                    const list = targetListeners.get(target);
                    if (list === undefined) return;
                    type = String(type);
                    const capture = captureOf(options);
                    for (const listener of list) {
                        if (listener.type === type && listener.callback === callback && listener.capture === capture) {
                            removeListener(target, listener);
                            return;
                        }
                    }
                }
                dispatchEvent(event) {
                    requireArgs(1, arguments.length, 'dispatchEvent', 'EventTarget');
                    if (!(event instanceof Event) || eventState.get(event) === undefined)
                        throw new TypeError("Failed to execute 'dispatchEvent' on 'EventTarget': parameter 1 is not of type 'Event'.");
                    const s = eventState.get(event);
                    if (s.dispatching || !s.initialized)
                        throw new DOMException("Failed to execute 'dispatchEvent' on 'EventTarget': The event is already being dispatched, or was not initialized.", 'InvalidStateError');
                    return dispatch(resolveTarget(this), event);
                }
            }
            tag(EventTarget, 'EventTarget');

            // ------------------------------------------------------------------ AbortSignal / AbortController

            function signalOf(signal) {
                const s = signalState.get(signal);
                if (s === undefined) throw new TypeError('Illegal invocation');
                return s;
            }

            // https://dom.spec.whatwg.org/#abortsignal-signal-abort
            function signalAbort(signal, reason) {
                const s = signalOf(signal);
                if (s.aborted) return;
                const abortReason = reason === undefined
                    ? new DOMException('signal is aborted without reason', 'AbortError')
                    : reason;

                const toAbort = [signal];
                for (const dependent of s.dependents) {
                    if (!signalOf(dependent).aborted) toAbort.push(dependent);
                }
                for (const item of toAbort) {
                    const state = signalOf(item);
                    state.aborted = true;
                    state.reason = abortReason;
                }
                for (const item of toAbort) {
                    const state = signalOf(item);
                    const algorithms = state.algorithms;
                    state.algorithms = [];
                    for (const algorithm of algorithms) algorithm();
                }
                for (const item of toAbort) {
                    dispatch(item, new Event('abort'));
                }
            }

            function createSignal() {
                return new AbortSignal(INTERNAL);
            }

            class AbortSignal extends EventTarget {
                constructor(token) {
                    if (token !== INTERNAL) throw new TypeError('Illegal constructor');
                    super();
                    signalState.set(this, { aborted: false, reason: undefined, algorithms: [], dependents: [] });
                }
                get aborted() { return signalOf(this).aborted; }
                get reason() { return signalOf(this).reason; }
                throwIfAborted() {
                    const s = signalOf(this);
                    if (s.aborted) throw s.reason;
                }
                static abort(reason) {
                    const signal = createSignal();
                    signalAbort(signal, reason);
                    return signal;
                }
                static timeout(milliseconds) {
                    const ms = Number(milliseconds);
                    if (!Number.isFinite(ms) || ms < 0)
                        throw new TypeError("Failed to execute 'timeout' on 'AbortSignal': Value is outside the 'unsigned long long' value range.");
                    const signal = createSignal();
                    global.setTimeout(function() {
                        signalAbort(signal, new DOMException('signal timed out', 'TimeoutError'));
                    }, Math.floor(ms));
                    return signal;
                }
                static any(signals) {
                    const sources = Array.from(signals);
                    for (const source of sources) {
                        if (!(source instanceof AbortSignal))
                            throw new TypeError("Failed to execute 'any' on 'AbortSignal': Failed to convert value to 'AbortSignal'.");
                    }
                    const result = createSignal();
                    const state = signalOf(result);
                    for (const source of sources) {
                        const sourceState = signalOf(source);
                        if (sourceState.aborted) {
                            state.aborted = true;
                            state.reason = sourceState.reason;
                            return result;
                        }
                    }
                    for (const source of sources) signalOf(source).dependents.push(result);
                    return result;
                }
            }
            tag(AbortSignal, 'AbortSignal');

            class AbortController {
                constructor() { controllerSignal.set(this, createSignal()); }
                get signal() {
                    const signal = controllerSignal.get(this);
                    if (signal === undefined) throw new TypeError('Illegal invocation');
                    return signal;
                }
                abort(reason) { signalAbort(this.signal, reason); }
            }
            tag(AbortController, 'AbortController');

            // ------------------------------------------------------------------ wiring

            if (typeof NodeCtor === 'function') {
                Object.setPrototypeOf(NodeCtor.prototype, EventTarget.prototype);
                Object.setPrototypeOf(NodeCtor, EventTarget);

                const DocumentCtor = global.Document;
                if (typeof DocumentCtor === 'function') {
                    // https://dom.spec.whatwg.org/#dom-document-createevent (the interfaces that exist here)
                    Object.defineProperty(DocumentCtor.prototype, 'createEvent', {
                        value: function createEvent(iface) {
                            requireArgs(1, arguments.length, 'createEvent', 'Document');
                            let event;
                            switch (String(iface).toLowerCase()) {
                                case 'event': case 'events': case 'htmlevents': event = new Event(''); break;
                                case 'customevent': event = new CustomEvent(''); break;
                                default:
                                    throw new DOMException('The provided event type (\'' + iface + '\') is invalid.', 'NotSupportedError');
                            }
                            eventState.get(event).initialized = false;
                            return event;
                        },
                        writable: true, configurable: true, enumerable: false
                    });
                }
            }

            // The window: a plain EventTarget whose methods are installed directly on the global object.
            for (const method of ['addEventListener', 'removeEventListener', 'dispatchEvent']) {
                const impl = EventTarget.prototype[method];
                Object.defineProperty(global, method, {
                    value: function() { return impl.apply(global, arguments); },
                    writable: true, configurable: true, enumerable: false
                });
            }

            defineGlobal('Event', Event);
            defineGlobal('CustomEvent', CustomEvent);
            defineGlobal('ErrorEvent', ErrorEvent);
            defineGlobal('EventTarget', EventTarget);
            defineGlobal('AbortSignal', AbortSignal);
            defineGlobal('AbortController', AbortController);
        })(globalThis.__folio_events_host);
        delete globalThis.__folio_events_host;
        """;
}

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
    internal static EventHandlerAttributes Install(IScriptEngine engine, HtmlEventLoop? eventLoop)
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
        var sync = engine.Evaluate("globalThis.__folio_events_api.sync");
        var ensure = engine.Evaluate("globalThis.__folio_events_api.ensure");
        engine.Evaluate("delete globalThis.__folio_events_api");
        return new EventHandlerAttributes(engine, sync, ensure);
    }

    private const string Source = """
        globalThis.__folio_events_api = (function(host) {
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
                            const result = callback.call(target, event);
                            if (listener.handler !== undefined) processHandlerResult(listener.handler, event, result);
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

            // ------------------------------------------------------------------ event handlers
            // https://html.spec.whatwg.org/multipage/webappapis.html#event-handlers

            const GLOBAL_HANDLER_NAMES = [
                'abort', 'animationcancel', 'animationend', 'animationiteration', 'animationstart', 'auxclick',
                'beforeinput', 'beforematch', 'beforetoggle', 'blur', 'cancel', 'canplay', 'canplaythrough', 'change',
                'click', 'close', 'contextlost', 'contextmenu', 'contextrestored', 'copy', 'cuechange', 'cut',
                'dblclick', 'drag', 'dragend', 'dragenter', 'dragleave', 'dragover', 'dragstart', 'drop',
                'durationchange', 'emptied', 'ended', 'error', 'focus', 'formdata', 'gotpointercapture', 'input',
                'invalid', 'keydown', 'keypress', 'keyup', 'load', 'loadeddata', 'loadedmetadata', 'loadstart',
                'lostpointercapture', 'mousedown', 'mouseenter', 'mouseleave', 'mousemove', 'mouseout', 'mouseover',
                'mouseup', 'paste', 'pause', 'play', 'playing', 'pointercancel', 'pointerdown', 'pointerenter',
                'pointerleave', 'pointermove', 'pointerout', 'pointerover', 'pointerup', 'progress', 'ratechange',
                'reset', 'resize', 'scroll', 'scrollend', 'securitypolicyviolation', 'seeked', 'seeking', 'select',
                'slotchange', 'stalled', 'submit', 'suspend', 'timeupdate', 'toggle', 'touchcancel', 'touchend',
                'touchmove', 'touchstart', 'transitioncancel', 'transitionend', 'transitionrun', 'transitionstart',
                'volumechange', 'waiting', 'wheel'
            ];
            const WINDOW_HANDLER_NAMES = [
                'afterprint', 'beforeprint', 'beforeunload', 'hashchange', 'languagechange', 'message', 'messageerror',
                'offline', 'online', 'pagehide', 'pageshow', 'popstate', 'rejectionhandled', 'storage',
                'unhandledrejection', 'unload'
            ];
            const DOCUMENT_HANDLER_NAMES = ['readystatechange', 'visibilitychange'];
            const GLOBAL_HANDLER_SET = new Set(GLOBAL_HANDLER_NAMES);
            const WINDOW_HANDLER_SET = new Set(WINDOW_HANDLER_NAMES);
            // The handlers a body or frameset element forwards to the window.
            const WINDOW_REFLECTING_SET = new Set(['blur', 'error', 'focus', 'load', 'resize', 'scroll'].concat(WINDOW_HANDLER_NAMES));
            const HTML_NAMESPACE = 'http://www.w3.org/1999/xhtml';
            const SVG_NAMESPACE = 'http://www.w3.org/2000/svg';

            const handlerRecords = new WeakMap();

            function isBodyLike(target) {
                return isNode(target) && target.nodeType === 1 && target.namespaceURI === HTML_NAMESPACE
                    && (target.localName === 'body' || target.localName === 'frameset');
            }

            function handlerTarget(self, name) {
                const target = resolveTarget(self);
                return isBodyLike(target) && WINDOW_REFLECTING_SET.has(name) ? global : target;
            }

            function handlerRecord(target, name) {
                let records = handlerRecords.get(target);
                if (records === undefined) {
                    records = new Map();
                    handlerRecords.set(target, records);
                }
                let record = records.get(name);
                if (record === undefined) {
                    record = { name: name, target: target, kind: 'none', raw: null, fn: null, scopeElement: null, listener: null, dispatcher: null };
                    record.dispatcher = function(event) { return runHandler(record, this, event); };
                    records.set(name, record);
                }
                return record;
            }

            // Registers the handler's one listener, at the end of the list, unless it is already there.
            function activateHandler(record) {
                if (record.listener !== null) return;
                const target = record.target;
                const listener = {
                    type: record.name, callback: record.dispatcher, capture: false,
                    passive: isPassiveByDefault(target, record.name), once: false, removed: false, handler: record
                };
                let list = targetListeners.get(target);
                if (list === undefined) {
                    list = [];
                    targetListeners.set(target, list);
                }
                list.push(listener);
                record.listener = listener;
            }

            function deactivateHandler(record) {
                if (record.listener === null) return;
                removeListener(record.target, record.listener);
                record.listener = null;
            }

            function clearHandler(record) {
                record.kind = 'none';
                record.raw = null;
                record.fn = null;
                record.scopeElement = null;
                deactivateHandler(record);
            }

            // Compiles an event handler content attribute with the scope chain of the spec: the document, the form owner and the element.
            function compileHandler(record) {
                const body = record.raw;
                const element = record.scopeElement;
                const isWindowError = record.name === 'error' && record.target === global;
                const parameters = isWindowError ? 'event, source, lineno, colno, error' : 'event';
                record.kind = 'none';
                record.raw = null;
                record.scopeElement = null;
                try {
                    // Parses the body on its own, so it cannot break out of the function it is wrapped in below.
                    new Function(parameters, body);
                    const factory = new Function('__folio_document', '__folio_form', '__folio_element',
                        'with (__folio_document || {}) { with (__folio_form || {}) { with (__folio_element || {}) { return function(' + parameters + ') {\n' + body + '\n}; } } }');
                    let form = null;
                    try { form = (element && element.form) || null; } catch (_) { }
                    const owner = element ? element.ownerDocument : null;
                    record.fn = factory(owner, form, element);
                    record.kind = 'function';
                } catch (error) {
                    record.fn = null;
                    reportError(error);
                }
                return record.fn;
            }

            function runHandler(record, self, event) {
                let fn = record.kind === 'function' ? record.fn : null;
                if (record.kind === 'raw') fn = compileHandler(record);
                if (fn === null) return undefined;
                if (record.name === 'error' && record.target === global && event instanceof ErrorEvent)
                    return fn.call(self, event.message, event.filename, event.lineno, event.colno, event.error);
                return fn.call(self, event);
            }

            // https://html.spec.whatwg.org/multipage/webappapis.html#the-event-handler-processing-algorithm
            function processHandlerResult(record, event, result) {
                if (record.name === 'error' && record.target === global && event instanceof ErrorEvent) {
                    if (result === true) event.preventDefault();
                } else if (record.name !== 'beforeunload' && result === false) {
                    event.preventDefault();
                }
            }

            function getHandler(self, name) {
                const records = handlerRecords.get(handlerTarget(self, name));
                const record = records === undefined ? undefined : records.get(name);
                if (record === undefined) return null;
                if (record.kind === 'raw') compileHandler(record);
                return record.kind === 'function' ? record.fn : null;
            }

            function setHandler(self, name, value) {
                const record = handlerRecord(handlerTarget(self, name), name);
                if (typeof value !== 'function') {
                    clearHandler(record);
                    return;
                }
                record.kind = 'function';
                record.fn = value;
                record.raw = null;
                record.scopeElement = null;
                activateHandler(record);
            }

            function defineHandlerAccessors(proto, names) {
                if (proto === undefined || proto === null) return;
                for (const name of names) {
                    Object.defineProperty(proto, 'on' + name, {
                        get: function() { return getHandler(this, name); },
                        set: function(value) { setHandler(this, name, value); },
                        enumerable: true, configurable: true
                    });
                }
            }

            function handlerForAttribute(element, attribute) {
                if (!isNode(element) || element.nodeType !== 1) return null;
                const namespace = element.namespaceURI;
                if (namespace !== HTML_NAMESPACE && namespace !== SVG_NAMESPACE) return null;
                if (attribute.length < 3) return null;
                const lower = attribute.toLowerCase();
                if (lower.charCodeAt(0) !== 111 || lower.charCodeAt(1) !== 110) return null;
                const name = lower.slice(2);
                const body = isBodyLike(element);
                if (!GLOBAL_HANDLER_SET.has(name) && !(body && WINDOW_HANDLER_SET.has(name))) return null;
                return handlerRecord(body && WINDOW_REFLECTING_SET.has(name) ? global : element, name);
            }

            // The content attribute changed: its handler is replaced in its original listener position, or removed.
            function syncHandlerAttribute(element, attribute, value) {
                const record = handlerForAttribute(element, attribute);
                if (record === null) return;
                if (value === null || value === undefined) {
                    clearHandler(record);
                    return;
                }
                record.kind = 'raw';
                record.raw = String(value);
                record.fn = null;
                record.scopeElement = element;
                activateHandler(record);
            }

            // An element met the document (parsed, or inserted): its attributes become handlers unless they already are.
            function ensureHandlerAttribute(element, attribute, value) {
                const record = handlerForAttribute(element, attribute);
                if (record === null || record.listener !== null || record.kind !== 'none') return;
                syncHandlerAttribute(element, attribute, value);
            }

            defineHandlerAccessors(global.HTMLElement && global.HTMLElement.prototype, GLOBAL_HANDLER_NAMES);
            defineHandlerAccessors(global.SVGElement && global.SVGElement.prototype, GLOBAL_HANDLER_NAMES);
            defineHandlerAccessors(global.Document && global.Document.prototype, GLOBAL_HANDLER_NAMES.concat(DOCUMENT_HANDLER_NAMES));
            defineHandlerAccessors(global.HTMLBodyElement && global.HTMLBodyElement.prototype, WINDOW_HANDLER_NAMES);
            defineHandlerAccessors(global, GLOBAL_HANDLER_NAMES.concat(WINDOW_HANDLER_NAMES));

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

            return { sync: syncHandlerAttribute, ensure: ensureHandlerAttribute };
        })(globalThis.__folio_events_host);
        delete globalThis.__folio_events_host;
        """;
}

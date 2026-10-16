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
    internal static (EventHandlerAttributes Handlers, ScriptValue DispatchHostEvent) Install(IScriptEngine engine, HtmlEventLoop? eventLoop)
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
        var dispatchHostEvent = engine.Evaluate("globalThis.__folio_events_api.dispatchHostEvent");
        engine.Evaluate("delete globalThis.__folio_events_api");
        return (new EventHandlerAttributes(engine, sync, ensure), dispatchHostEvent);
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

            // ------------------------------------------------------------------ UIEvent & derived

            class UIEvent extends Event {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'UIEvent', 'UIEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'UIEvent');
                    const s = eventState.get(this);
                    s.view = dict.view === undefined ? null : dict.view;
                    s.detail = dict.detail === undefined ? 0 : dict.detail | 0;
                }
                get view() { return stateOf(this).view; }
                get detail() { return stateOf(this).detail; }
                initUIEvent(type, bubbles, cancelable, view, detail) {
                    requireArgs(1, arguments.length, 'initUIEvent', 'UIEvent');
                    const s = stateOf(this);
                    if (s.dispatching) return;
                    this.initEvent(type, bubbles, cancelable);
                    s.view = view === undefined ? null : view;
                    s.detail = detail === undefined ? 0 : detail | 0;
                }
            }
            tag(UIEvent, 'UIEvent');

            class MouseEvent extends UIEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'MouseEvent', 'MouseEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'MouseEvent');
                    const s = eventState.get(this);
                    s.screenX = Number(dict.screenX) || 0;
                    s.screenY = Number(dict.screenY) || 0;
                    s.clientX = Number(dict.clientX) || 0;
                    s.clientY = Number(dict.clientY) || 0;
                    s.ctrlKey = !!dict.ctrlKey;
                    s.shiftKey = !!dict.shiftKey;
                    s.altKey = !!dict.altKey;
                    s.metaKey = !!dict.metaKey;
                    s.button = dict.button === undefined ? 0 : dict.button | 0;
                    s.buttons = dict.buttons === undefined ? 0 : dict.buttons >>> 0;
                    s.relatedTarget = dict.relatedTarget === undefined ? null : dict.relatedTarget;
                }
                get screenX() { return stateOf(this).screenX; }
                get screenY() { return stateOf(this).screenY; }
                get clientX() { return stateOf(this).clientX; }
                get clientY() { return stateOf(this).clientY; }
                get x() { return this.clientX; }
                get y() { return this.clientY; }
                get pageX() { return this.clientX; }
                get pageY() { return this.clientY; }
                get ctrlKey() { return stateOf(this).ctrlKey; }
                get shiftKey() { return stateOf(this).shiftKey; }
                get altKey() { return stateOf(this).altKey; }
                get metaKey() { return stateOf(this).metaKey; }
                get button() { return stateOf(this).button; }
                get buttons() { return stateOf(this).buttons; }
                get relatedTarget() { return stateOf(this).relatedTarget; }
                getModifierState(key) {
                    const s = stateOf(this);
                    switch (String(key)) {
                        case 'Control': return s.ctrlKey;
                        case 'Shift': return s.shiftKey;
                        case 'Alt': return s.altKey;
                        case 'Meta': return s.metaKey;
                        default: return false;
                    }
                }
                initMouseEvent(type, bubbles, cancelable, view, detail, screenX, screenY, clientX, clientY, ctrlKey, altKey, shiftKey, metaKey, button, relatedTarget) {
                    requireArgs(1, arguments.length, 'initMouseEvent', 'MouseEvent');
                    const s = stateOf(this);
                    if (s.dispatching) return;
                    this.initUIEvent(type, bubbles, cancelable, view, detail);
                    s.screenX = Number(screenX) || 0;
                    s.screenY = Number(screenY) || 0;
                    s.clientX = Number(clientX) || 0;
                    s.clientY = Number(clientY) || 0;
                    s.ctrlKey = !!ctrlKey;
                    s.altKey = !!altKey;
                    s.shiftKey = !!shiftKey;
                    s.metaKey = !!metaKey;
                    s.button = button === undefined ? 0 : button | 0;
                    s.buttons = 0;
                    s.relatedTarget = relatedTarget === undefined ? null : relatedTarget;
                }
            }
            tag(MouseEvent, 'MouseEvent');

            class PointerEvent extends MouseEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'PointerEvent', 'PointerEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'PointerEvent');
                    const s = eventState.get(this);
                    s.pointerId = dict.pointerId === undefined ? 0 : dict.pointerId | 0;
                    s.width = dict.width === undefined ? 1 : Number(dict.width) || 0;
                    s.height = dict.height === undefined ? 1 : Number(dict.height) || 0;
                    s.pressure = dict.pressure === undefined ? 0 : Number(dict.pressure) || 0;
                    s.tangentialPressure = dict.tangentialPressure === undefined ? 0 : Number(dict.tangentialPressure) || 0;
                    s.tiltX = dict.tiltX === undefined ? 0 : dict.tiltX | 0;
                    s.tiltY = dict.tiltY === undefined ? 0 : dict.tiltY | 0;
                    s.twist = dict.twist === undefined ? 0 : dict.twist | 0;
                    s.pointerType = dict.pointerType === undefined ? '' : String(dict.pointerType);
                    s.isPrimary = dict.isPrimary === undefined ? false : !!dict.isPrimary;
                }
                get pointerId() { return stateOf(this).pointerId; }
                get width() { return stateOf(this).width; }
                get height() { return stateOf(this).height; }
                get pressure() { return stateOf(this).pressure; }
                get tangentialPressure() { return stateOf(this).tangentialPressure; }
                get tiltX() { return stateOf(this).tiltX; }
                get tiltY() { return stateOf(this).tiltY; }
                get twist() { return stateOf(this).twist; }
                get pointerType() { return stateOf(this).pointerType; }
                get isPrimary() { return stateOf(this).isPrimary; }
            }
            tag(PointerEvent, 'PointerEvent');

            class WheelEvent extends MouseEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'WheelEvent', 'WheelEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'WheelEvent');
                    const s = eventState.get(this);
                    s.deltaX = Number(dict.deltaX) || 0;
                    s.deltaY = Number(dict.deltaY) || 0;
                    s.deltaZ = Number(dict.deltaZ) || 0;
                    s.deltaMode = dict.deltaMode === undefined ? 0 : dict.deltaMode >>> 0;
                }
                get deltaX() { return stateOf(this).deltaX; }
                get deltaY() { return stateOf(this).deltaY; }
                get deltaZ() { return stateOf(this).deltaZ; }
                get deltaMode() { return stateOf(this).deltaMode; }
            }
            WheelEvent.DOM_DELTA_PIXEL = 0;
            WheelEvent.DOM_DELTA_LINE = 1;
            WheelEvent.DOM_DELTA_PAGE = 2;
            Object.defineProperties(WheelEvent.prototype, {
                DOM_DELTA_PIXEL: { value: 0, enumerable: true },
                DOM_DELTA_LINE: { value: 1, enumerable: true },
                DOM_DELTA_PAGE: { value: 2, enumerable: true }
            });
            tag(WheelEvent, 'WheelEvent');

            class KeyboardEvent extends UIEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'KeyboardEvent', 'KeyboardEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'KeyboardEvent');
                    const s = eventState.get(this);
                    s.key = dict.key === undefined ? '' : String(dict.key);
                    s.code = dict.code === undefined ? '' : String(dict.code);
                    s.location = dict.location === undefined ? 0 : dict.location >>> 0;
                    s.ctrlKey = !!dict.ctrlKey;
                    s.shiftKey = !!dict.shiftKey;
                    s.altKey = !!dict.altKey;
                    s.metaKey = !!dict.metaKey;
                    s.repeat = !!dict.repeat;
                    s.isComposing = !!dict.isComposing;
                }
                get key() { return stateOf(this).key; }
                get code() { return stateOf(this).code; }
                get location() { return stateOf(this).location; }
                get ctrlKey() { return stateOf(this).ctrlKey; }
                get shiftKey() { return stateOf(this).shiftKey; }
                get altKey() { return stateOf(this).altKey; }
                get metaKey() { return stateOf(this).metaKey; }
                get repeat() { return stateOf(this).repeat; }
                get isComposing() { return stateOf(this).isComposing; }
                getModifierState(key) {
                    const s = stateOf(this);
                    switch (String(key)) {
                        case 'Control': return s.ctrlKey;
                        case 'Shift': return s.shiftKey;
                        case 'Alt': return s.altKey;
                        case 'Meta': return s.metaKey;
                        default: return false;
                    }
                }
            }
            KeyboardEvent.DOM_KEY_LOCATION_STANDARD = 0;
            KeyboardEvent.DOM_KEY_LOCATION_LEFT = 1;
            KeyboardEvent.DOM_KEY_LOCATION_RIGHT = 2;
            KeyboardEvent.DOM_KEY_LOCATION_NUMPAD = 3;
            Object.defineProperties(KeyboardEvent.prototype, {
                DOM_KEY_LOCATION_STANDARD: { value: 0, enumerable: true },
                DOM_KEY_LOCATION_LEFT: { value: 1, enumerable: true },
                DOM_KEY_LOCATION_RIGHT: { value: 2, enumerable: true },
                DOM_KEY_LOCATION_NUMPAD: { value: 3, enumerable: true }
            });
            tag(KeyboardEvent, 'KeyboardEvent');

            class InputEvent extends UIEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'InputEvent', 'InputEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'InputEvent');
                    const s = eventState.get(this);
                    s.data = dict.data === undefined ? null : (dict.data === null ? null : String(dict.data));
                    s.isComposing = !!dict.isComposing;
                    s.inputType = dict.inputType === undefined ? '' : String(dict.inputType);
                }
                get data() { return stateOf(this).data; }
                get isComposing() { return stateOf(this).isComposing; }
                get inputType() { return stateOf(this).inputType; }
            }
            tag(InputEvent, 'InputEvent');

            class FocusEvent extends UIEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'FocusEvent', 'FocusEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'FocusEvent');
                    const s = eventState.get(this);
                    s.relatedTarget = dict.relatedTarget === undefined ? null : dict.relatedTarget;
                }
                get relatedTarget() { return stateOf(this).relatedTarget; }
            }
            tag(FocusEvent, 'FocusEvent');

            class CompositionEvent extends UIEvent {
                constructor(type, init) {
                    requireArgs(1, arguments.length, 'CompositionEvent', 'CompositionEvent');
                    super(type, init);
                    const dict = initDictionary(init, 'CompositionEvent');
                    const s = eventState.get(this);
                    s.data = dict.data === undefined ? '' : String(dict.data);
                }
                get data() { return stateOf(this).data; }
                initCompositionEvent(type, bubbles, cancelable, view, data) {
                    requireArgs(1, arguments.length, 'initCompositionEvent', 'CompositionEvent');
                    const s = stateOf(this);
                    if (s.dispatching) return;
                    this.initUIEvent(type, bubbles, cancelable, view, 0);
                    s.data = data === undefined ? '' : String(data);
                }
            }
            tag(CompositionEvent, 'CompositionEvent');

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
                            // Jint cannot suspend a synchronous event dispatch while an
                            // async listener awaits host work. Queue async listeners as a
                            // timer task; dispatch itself still completes synchronously.
                            if (callback.constructor && callback.constructor.name === 'AsyncFunction') {
                                setTimeout(callback.bind(target, event), 0);
                            } else {
                                const result = callback.call(target, event);
                                if (listener.handler !== undefined) processHandlerResult(listener.handler, event, result);
                            }
                        } else {
                            const handler = callback.handleEvent;
                            if (typeof handler !== 'function')
                                throw new TypeError("Failed to execute the event listener: 'handleEvent' is not a function.");
                            if (handler.constructor && handler.constructor.name === 'AsyncFunction') {
                                setTimeout(handler.bind(callback, event), 0);
                            } else {
                                handler.call(callback, event);
                            }
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

            // Pointer capture
            const pointerCaptures = new Map();

            function isElement(target) {
                return isNode(target) && target.nodeType === 1;
            }

            function setPointerCapture(element, pointerId) {
                if (!isElement(element)) throw new TypeError('Illegal invocation');
                pointerId = pointerId | 0;
                pointerCaptures.set(pointerId, element);
                try {
                    const ev = new PointerEvent('gotpointercapture', { pointerId: pointerId, bubbles: false, cancelable: false });
                    const s = eventState.get(ev);
                    if (s) s.trusted = true;
                    dispatch(element, ev);
                } catch (_) {}
            }

            function releasePointerCapture(element, pointerId) {
                if (!isElement(element)) throw new TypeError('Illegal invocation');
                pointerId = pointerId | 0;
                if (pointerCaptures.get(pointerId) === element) {
                    pointerCaptures.delete(pointerId);
                    try {
                        const ev = new PointerEvent('lostpointercapture', { pointerId: pointerId, bubbles: false, cancelable: false });
                        const s = eventState.get(ev);
                        if (s) s.trusted = true;
                        dispatch(element, ev);
                    } catch (_) {}
                }
            }

            function hasPointerCapture(element, pointerId) {
                if (!isElement(element)) throw new TypeError('Illegal invocation');
                pointerId = pointerId | 0;
                return pointerCaptures.get(pointerId) === element;
            }

            if (global.Element && global.Element.prototype) {
                Object.defineProperties(global.Element.prototype, {
                    setPointerCapture: {
                        value: function(pointerId) {
                            requireArgs(1, arguments.length, 'setPointerCapture', 'Element');
                            return setPointerCapture(this, pointerId);
                        },
                        writable: true, configurable: true, enumerable: true
                    },
                    releasePointerCapture: {
                        value: function(pointerId) {
                            requireArgs(1, arguments.length, 'releasePointerCapture', 'Element');
                            return releasePointerCapture(this, pointerId);
                        },
                        writable: true, configurable: true, enumerable: true
                    },
                    hasPointerCapture: {
                        value: function(pointerId) {
                            requireArgs(1, arguments.length, 'hasPointerCapture', 'Element');
                            return hasPointerCapture(this, pointerId);
                        },
                        writable: true, configurable: true, enumerable: true
                    }
                });
            }

            function createHostEvent(type, a) {
                // a is array or object
                const dict = Array.isArray(a) ? {
                    bubbles: !!a[0],
                    cancelable: !!a[1],
                    composed: !!a[2],
                    detail: a[3] | 0,
                    screenX: Number(a[4]) || 0,
                    screenY: Number(a[5]) || 0,
                    clientX: Number(a[6]) || 0,
                    clientY: Number(a[7]) || 0,
                    ctrlKey: !!a[8],
                    shiftKey: !!a[9],
                    altKey: !!a[10],
                    metaKey: !!a[11],
                    button: a[12] | 0,
                    buttons: a[13] >>> 0,
                    relatedTarget: a[14] || null,
                    pointerId: a[15] === undefined ? 0 : a[15] | 0,
                    width: a[16] === undefined ? 1 : Number(a[16]) || 0,
                    height: a[17] === undefined ? 1 : Number(a[17]) || 0,
                    pressure: Number(a[18]) || 0,
                    tangentialPressure: Number(a[19]) || 0,
                    tiltX: a[20] | 0,
                    tiltY: a[21] | 0,
                    twist: a[22] | 0,
                    pointerType: a[23] === undefined ? 'mouse' : String(a[23]),
                    isPrimary: a[24] === undefined ? true : !!a[24],
                    deltaX: Number(a[25]) || 0,
                    deltaY: Number(a[26]) || 0,
                    deltaZ: Number(a[27]) || 0,
                    deltaMode: a[28] >>> 0,
                    key: a[29] === undefined ? '' : String(a[29]),
                    code: a[30] === undefined ? '' : String(a[30]),
                    location: a[31] >>> 0,
                    repeat: !!a[32],
                    isComposing: !!a[33],
                    data: a[34] === undefined || a[34] === null ? null : String(a[34]),
                    inputType: a[35] === undefined ? '' : String(a[35])
                } : {
                    bubbles: !!a.bubbles,
                    cancelable: !!a.cancelable,
                    composed: !!a.composed,
                    detail: a.detail | 0,
                    screenX: a.screenX,
                    screenY: a.screenY,
                    clientX: a.clientX,
                    clientY: a.clientY,
                    ctrlKey: !!a.ctrlKey,
                    shiftKey: !!a.shiftKey,
                    altKey: !!a.altKey,
                    metaKey: !!a.metaKey,
                    button: a.button | 0,
                    buttons: a.buttons >>> 0,
                    relatedTarget: a.relatedTarget,
                    pointerId: a.pointerId | 0,
                    width: a.width,
                    height: a.height,
                    pressure: a.pressure,
                    tangentialPressure: a.tangentialPressure,
                    tiltX: a.tiltX | 0,
                    tiltY: a.tiltY | 0,
                    twist: a.twist | 0,
                    pointerType: a.pointerType,
                    isPrimary: !!a.isPrimary,
                    deltaX: a.deltaX,
                    deltaY: a.deltaY,
                    deltaZ: a.deltaZ,
                    deltaMode: a.deltaMode >>> 0,
                    key: a.key,
                    code: a.code,
                    location: a.location >>> 0,
                    repeat: !!a.repeat,
                    isComposing: !!a.isComposing,
                    data: a.data,
                    inputType: a.inputType
                };

                switch (type) {
                    case 'pointerdown':
                    case 'pointerup':
                    case 'pointermove':
                    case 'pointerover':
                    case 'pointerout':
                    case 'pointerenter':
                    case 'pointerleave':
                    case 'pointercancel':
                    case 'gotpointercapture':
                    case 'lostpointercapture':
                        return new PointerEvent(type, dict);

                    case 'mousedown':
                    case 'mouseup':
                    case 'mousemove':
                    case 'mouseover':
                    case 'mouseout':
                    case 'mouseenter':
                    case 'mouseleave':
                    case 'click':
                    case 'dblclick':
                    case 'auxclick':
                    case 'contextmenu':
                        return new MouseEvent(type, dict);

                    case 'wheel':
                        return new WheelEvent(type, dict);

                    case 'keydown':
                    case 'keyup':
                    case 'keypress':
                        return new KeyboardEvent(type, dict);

                    case 'input':
                    case 'beforeinput':
                        return new InputEvent(type, dict);

                    case 'focus':
                    case 'blur':
                    case 'focusin':
                    case 'focusout':
                        return new FocusEvent(type, dict);

                    case 'compositionstart':
                    case 'compositionupdate':
                    case 'compositionend':
                        return new CompositionEvent(type, dict);

                    default:
                        return new UIEvent(type, dict);
                }
            }

            function dispatchHostEvent(target, type, args) {
                target = resolveTarget(target);
                const pointerId = Array.isArray(args) ? (args[15] === undefined ? undefined : (args[15] | 0)) : (args ? args.pointerId : undefined);
                // Handle pointer capture routing
                if (pointerId !== undefined && type.startsWith('pointer') && type !== 'gotpointercapture' && type !== 'lostpointercapture') {
                    const captured = pointerCaptures.get(pointerId | 0);
                    if (captured && isElement(captured)) {
                        target = captured;
                    }
                }

                const ev = createHostEvent(type, args || {});
                const s = eventState.get(ev);
                if (s) {
                    s.trusted = true;
                }
                const notPrevented = dispatch(target, ev);

                // Auto release pointer capture on pointerup / pointercancel
                if (pointerId !== undefined && (type === 'pointerup' || type === 'pointercancel')) {
                    const pid = pointerId | 0;
                    const captured = pointerCaptures.get(pid);
                    if (captured) {
                        releasePointerCapture(captured, pid);
                    }
                }

                return notPrevented;
            }

            defineGlobal('Event', Event);
            defineGlobal('CustomEvent', CustomEvent);
            defineGlobal('ErrorEvent', ErrorEvent);
            defineGlobal('UIEvent', UIEvent);
            defineGlobal('MouseEvent', MouseEvent);
            defineGlobal('PointerEvent', PointerEvent);
            defineGlobal('WheelEvent', WheelEvent);
            defineGlobal('KeyboardEvent', KeyboardEvent);
            defineGlobal('InputEvent', InputEvent);
            defineGlobal('FocusEvent', FocusEvent);
            defineGlobal('CompositionEvent', CompositionEvent);
            defineGlobal('EventTarget', EventTarget);
            defineGlobal('AbortSignal', AbortSignal);
            defineGlobal('AbortController', AbortController);

            return {
                sync: syncHandlerAttribute,
                ensure: ensureHandlerAttribute,
                dispatchHostEvent: dispatchHostEvent
            };
        })(globalThis.__folio_events_host);
        delete globalThis.__folio_events_host;
        """;
}

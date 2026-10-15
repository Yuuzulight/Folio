using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Installs DOM constructors, prototype chains, and the global document onto an <see cref="IScriptEngine"/> (study 02, study 17, #451).
/// </summary>
internal static class DomBindings
{
    internal static ScriptWrapperCache Install(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null, HtmlEventLoop? eventLoop = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);

        var context = new DomContext(engine, document, wrapperCache);

        // 1. Install DOMException
        InstallDomException(engine);

        // 2. Install Prototype Hierarchy & Constructors
        InstallConstructors(engine);

        // 2b. Events: EventTarget joins the Node prototype chain, so it follows the constructors
        var (handlers, dispatchHostEvent) = DomEvents.Install(engine, eventLoop);

        // 3. Bind document to global scope
        var docWrapper = context.Wrap(document);
        engine.SetGlobalProperty("document", docWrapper);
        var styleHost = new HostObjectTemplate("StyleHost")
            .DefineMethod("getComputedStyle", (target, _, args) =>
            {
                if (args.Length == 0 || !context.Engine.TryGetHostTarget(args[0], out var node) || node is not ElementNode element)
                    throw WebIdlException.TypeError("getComputedStyle requires an Element.");
                return context.WrapComputedStyle(element);
            });
        engine.SetGlobalProperty("__folio_style_host", engine.CreateHostObject(styleHost, context));
        var mutationHost = new HostObjectTemplate("MutationObserverHost")
            .DefineMethod("create", (_, _, args) =>
            {
                if (args.Length == 0) throw WebIdlException.TypeError("MutationObserver callback must be callable.");
                return engine.CreateHostObject(context.Templates.MutationObserverTemplate, context.CreateMutationObserver(args[0]));
            });
        engine.SetGlobalProperty("__folio_mutation_host", engine.CreateHostObject(mutationHost, context));
        var canvasHost = new HostObjectTemplate("CanvasHost")
            .DefineMethod("createPath", (_, _, _) => engine.CreateHostObject(context.Templates.Path2DTemplate, new CanvasPathState()));
        engine.SetGlobalProperty("__folio_canvas_host", engine.CreateHostObject(canvasHost, context));
        engine.Evaluate("globalThis.getComputedStyle = (function(host) { return function(element, pseudo) { return host.getComputedStyle(element, pseudo); }; })(__folio_style_host); delete globalThis.__folio_style_host;");
        engine.Evaluate(@"
            if (typeof window === 'undefined') {
                globalThis.window = globalThis;
            }
            globalThis.innerWidth = 800;
            globalThis.innerHeight = 600;
            globalThis.devicePixelRatio = 1;
            globalThis.scrollX = 0;
            globalThis.scrollY = 0;
            Object.defineProperty(globalThis, 'pageXOffset', { get: function() { return globalThis.scrollX; } });
            Object.defineProperty(globalThis, 'pageYOffset', { get: function() { return globalThis.scrollY; } });
            globalThis.scrollTo = function(x, y) { globalThis.scrollX = Number(x) || 0; globalThis.scrollY = Number(y) || 0; };
            globalThis.scrollBy = function(x, y) { globalThis.scrollX += Number(x) || 0; globalThis.scrollY += Number(y) || 0; };
            globalThis.matchMedia = function(query) {
                query = String(query);
                const min = /min-width\s*:\s*(\d+)px/.exec(query);
                const max = /max-width\s*:\s*(\d+)px/.exec(query);
                const matches = (!min || globalThis.innerWidth >= Number(min[1])) && (!max || globalThis.innerWidth <= Number(max[1]));
                const listeners = [];
                return {
                    media: query,
                    matches: matches,
                    onchange: null,
                    addListener: function(fn) { if (typeof fn === 'function') listeners.push(fn); },
                    removeListener: function(fn) { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
                    addEventListener: function(type, fn) { if (type === 'change') this.addListener(fn); },
                    removeEventListener: function(type, fn) { if (type === 'change') this.removeListener(fn); },
                    dispatchEvent: function() { return true; }
                };
            };
            globalThis.CSS = globalThis.CSS || {};
            globalThis.CSS.supports = function(property, value) {
                if (arguments.length === 1) return /\(|\)/.test(String(property));
                property = String(property).trim().toLowerCase();
                value = String(value).trim();
                return property.length > 0 && value.length > 0 && !/[{};]/.test(value);
            };
            globalThis.CSS.escape = function(value) {
                value = String(value);
                return value.replace(/[^a-zA-Z0-9_-]/g, function(c) { return String.fromCharCode(92) + c; }).replace(/^[0-9]/, function(c) { return String.fromCharCode(92) + '3' + c + ' '; });
            };
            globalThis.fetch = function(input) {
                const url = String(input);
                if (url.slice(0, 5).toLowerCase() !== 'data:')
                    return Promise.reject(new TypeError('The requested URL is not allowed by Folio.'));
                const comma = url.indexOf(',');
                const header = comma < 0 ? '' : url.slice(5, comma);
                const payload = comma < 0 ? '' : url.slice(comma + 1);
                const body = header.toLowerCase().indexOf(';base64') >= 0
                    ? (typeof atob === 'function' ? atob(payload) : payload)
                    : decodeURIComponent(payload);
                return Promise.resolve({
                    ok: true, status: 200, url: url,
                    text: function() { return Promise.resolve(body); },
                    json: function() { return Promise.resolve(JSON.parse(body)); }
                });
            };
            globalThis.ResizeObserver = function(callback) {
                if (typeof callback !== 'function') throw new TypeError('ResizeObserver callback must be callable');
                this._callback = callback;
                this._targets = [];
            };
            ResizeObserver.prototype.observe = function(target) { if (this._targets.indexOf(target) < 0) this._targets.push(target); };
            ResizeObserver.prototype.unobserve = function(target) { const i = this._targets.indexOf(target); if (i >= 0) this._targets.splice(i, 1); };
            ResizeObserver.prototype.disconnect = function() { this._targets.length = 0; };
            globalThis.IntersectionObserver = function(callback, options) {
                if (typeof callback !== 'function') throw new TypeError('IntersectionObserver callback must be callable');
                this._callback = callback;
                this.root = options && options.root || null;
                this.rootMargin = options && options.rootMargin || '0px';
                this.thresholds = options && options.threshold !== undefined ? [].concat(options.threshold) : [0];
                this._targets = [];
            };
            IntersectionObserver.prototype.observe = function(target) { if (this._targets.indexOf(target) < 0) this._targets.push(target); };
            IntersectionObserver.prototype.unobserve = function(target) { const i = this._targets.indexOf(target); if (i >= 0) this._targets.splice(i, 1); };
            IntersectionObserver.prototype.disconnect = function() { this._targets.length = 0; };
            IntersectionObserver.prototype.takeRecords = function() { return []; };
            globalThis.MutationObserver = function(callback) {
                return __folio_mutation_host.create(callback);
            };
            globalThis.Path2D = function() { return __folio_canvas_host.createPath(); };
            globalThis.FormData = function(form) {
                this._entries = [];
                if (form && form.elements) for (const control of form.elements) if (control.name) this.append(control.name, control.value || '');
            };
            FormData.prototype.append = function(name, value) { this._entries.push([String(name), value]); };
            FormData.prototype.set = function(name, value) { this.delete(name); this.append(name, value); };
            FormData.prototype.get = function(name) { const e = this._entries.find(x => x[0] === String(name)); return e ? e[1] : null; };
            FormData.prototype.getAll = function(name) { return this._entries.filter(x => x[0] === String(name)).map(x => x[1]); };
            FormData.prototype.has = function(name) { return this._entries.some(x => x[0] === String(name)); };
            FormData.prototype.delete = function(name) { name = String(name); this._entries = this._entries.filter(x => x[0] !== name); };
            FormData.prototype.entries = function() { return this._entries[Symbol.iterator](); };
            FormData.prototype.keys = function() { return this._entries.map(x => x[0])[Symbol.iterator](); };
            FormData.prototype.values = function() { return this._entries.map(x => x[1])[Symbol.iterator](); };
            function makeStorage() {
                const data = Object.create(null);
                const storage = {
                    get length() { return Object.keys(data).length; },
                    key: function(index) { return Object.keys(data)[index] ?? null; },
                    getItem: function(key) { key = String(key); return Object.prototype.hasOwnProperty.call(data, key) ? data[key] : null; },
                    setItem: function(key, value) { key = String(key); value = String(value); let size = 0; for (const k of Object.keys(data)) size += k.length + data[k].length; size += key.length + value.length; if (size > 5 * 1024 * 1024) throw new DOMException('Storage quota exceeded', 'QuotaExceededError'); data[key] = value; },
                    removeItem: function(key) { delete data[String(key)]; },
                    clear: function() { for (const key of Object.keys(data)) delete data[key]; }
                };
                return new Proxy(storage, { get: function(target, key) { if (key in target) return target[key]; return target.getItem(key); }, set: function(target, key, value) { target.setItem(key, value); return true; }, deleteProperty: function(target, key) { target.removeItem(key); return true; }, ownKeys: function() { return Object.keys(data); } });
            }
            globalThis.localStorage = makeStorage();
            globalThis.sessionStorage = makeStorage();
            globalThis.URLSearchParams = function(init) {
                this._pairs = [];
                if (typeof init === 'string') for (const part of init.replace(/^\?/, '').split('&')) if (part) { const i = part.indexOf('='); this.append(decodeURIComponent(i < 0 ? part : part.slice(0, i)), decodeURIComponent(i < 0 ? '' : part.slice(i + 1))); }
            };
            URLSearchParams.prototype.append = function(k,v) { this._pairs.push([String(k), String(v)]); };
            URLSearchParams.prototype.set = function(k,v) { this.delete(k); this.append(k,v); };
            URLSearchParams.prototype.get = function(k) { const p = this._pairs.find(x => x[0] === String(k)); return p ? p[1] : null; };
            URLSearchParams.prototype.has = function(k) { return this._pairs.some(x => x[0] === String(k)); };
            URLSearchParams.prototype.delete = function(k) { k = String(k); this._pairs = this._pairs.filter(x => x[0] !== k); };
            URLSearchParams.prototype.toString = function() { return this._pairs.map(x => encodeURIComponent(x[0]) + '=' + encodeURIComponent(x[1])).join('&'); };
            globalThis.URL = function(value, base) { const raw = String(value); const match = /^(?:(\w+):\/\/([^\/]+))?([^?#]*)(\?[^#]*)?(#.*)?$/.exec(raw); this.protocol = match && match[1] ? match[1] + ':' : ''; this.host = match && match[2] ? match[2] : ''; this.origin = this.protocol && this.host ? this.protocol + '//' + this.host : ''; this.pathname = match && match[3] ? match[3] : '/'; this.search = match && match[4] ? match[4] : ''; this.hash = match && match[5] ? match[5] : ''; this.href = this.origin + this.pathname + this.search + this.hash; this.searchParams = new URLSearchParams(this.search); };
            globalThis.Option = function(text, value, defaultSelected, selected) { const option = document.createElement('option'); option.text = text === undefined ? '' : String(text); if (value !== undefined) option.value = String(value); if (defaultSelected) option.defaultSelected = true; if (selected) option.selected = true; return option; };
            globalThis.btoa = function(value) { const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'; const bytes = String(value); let out = ''; for (let i = 0; i < bytes.length; i += 3) { const a = bytes.charCodeAt(i), b = i + 1 < bytes.length ? bytes.charCodeAt(i + 1) : 0, c = i + 2 < bytes.length ? bytes.charCodeAt(i + 2) : 0; out += alphabet[a >> 2] + alphabet[((a & 3) << 4) | (b >> 4)] + (i + 1 < bytes.length ? alphabet[((b & 15) << 2) | (c >> 6)] : '=') + (i + 2 < bytes.length ? alphabet[c & 63] : '='); } return out; };
            globalThis.atob = function(value) { const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/'; const input = String(value).replace(/=+$/, ''); let out = ''; for (let i = 0; i < input.length; i += 4) { const a = alphabet.indexOf(input[i]), b = alphabet.indexOf(input[i + 1]), c = alphabet.indexOf(input[i + 2]), d = alphabet.indexOf(input[i + 3]); out += String.fromCharCode((a << 2) | (b >> 4)); if (c >= 0) out += String.fromCharCode(((b & 15) << 4) | (c >> 2)); if (d >= 0) out += String.fromCharCode(((c & 3) << 6) | d); } return out; };
            globalThis.crypto = { getRandomValues: function(array) { for (let i = 0; i < array.length; i++) array[i] = Math.floor(Math.random() * 256); return array; } };
            globalThis.TextEncoder = function() {};
            TextEncoder.prototype.encode = function(value) { const text = String(value); const bytes = new Uint8Array(text.length); for (let i = 0; i < text.length; i++) bytes[i] = text.charCodeAt(i) & 255; return bytes; };
            globalThis.TextDecoder = function() {};
            TextDecoder.prototype.decode = function(value) { let text = ''; for (let i = 0; i < value.length; i++) text += String.fromCharCode(value[i]); return text; };
            globalThis.structuredClone = function(value) { return JSON.parse(JSON.stringify(value)); };
            globalThis.console = globalThis.console || {};
            for (const method of ['log','info','warn','error','debug','dir','trace']) if (typeof console[method] !== 'function') console[method] = function() {};
        ");

        // 4. Event handler content attributes (#457): the parsed ones now, later ones as they change or are inserted.
        handlers.Scan(document, context);
        document.HandlerAttributeChanged += (element, name, value) => handlers.Changed(context.Wrap(element), name, value);
        document.NodeInserted += node => handlers.Scan(node, context);

        // 5. Trusted event dispatcher bridge (#458)
        document.EventDispatcher = (targetNode, eventType, args) =>
        {
            var targetWrapper = targetNode == document ? docWrapper : context.Wrap(targetNode);
            var jsArgs = new ScriptValue[]
            {
                ScriptValue.FromBoolean(args.Bubbles),            // 0
                ScriptValue.FromBoolean(args.Cancelable),         // 1
                ScriptValue.FromBoolean(args.Composed),           // 2
                ScriptValue.FromNumber(args.Detail),              // 3
                ScriptValue.FromNumber(args.ScreenX),             // 4
                ScriptValue.FromNumber(args.ScreenY),             // 5
                ScriptValue.FromNumber(args.ClientX),             // 6
                ScriptValue.FromNumber(args.ClientY),             // 7
                ScriptValue.FromBoolean(args.CtrlKey),            // 8
                ScriptValue.FromBoolean(args.ShiftKey),           // 9
                ScriptValue.FromBoolean(args.AltKey),             // 10
                ScriptValue.FromBoolean(args.MetaKey),            // 11
                ScriptValue.FromNumber(args.Button),              // 12
                ScriptValue.FromNumber(args.Buttons),             // 13
                args.RelatedTarget is not null ? (args.RelatedTarget == document ? docWrapper : context.Wrap(args.RelatedTarget)) : ScriptValue.Null, // 14
                ScriptValue.FromNumber(args.PointerId),           // 15
                ScriptValue.FromNumber(args.Width),               // 16
                ScriptValue.FromNumber(args.Height),              // 17
                ScriptValue.FromNumber(args.Pressure),            // 18
                ScriptValue.FromNumber(args.TangentialPressure),  // 19
                ScriptValue.FromNumber(args.TiltX),               // 20
                ScriptValue.FromNumber(args.TiltY),               // 21
                ScriptValue.FromNumber(args.Twist),               // 22
                ScriptValue.FromString(args.PointerType),         // 23
                ScriptValue.FromBoolean(args.IsPrimary),          // 24
                ScriptValue.FromNumber(args.DeltaX),              // 25
                ScriptValue.FromNumber(args.DeltaY),              // 26
                ScriptValue.FromNumber(args.DeltaZ),              // 27
                ScriptValue.FromNumber(args.DeltaMode),           // 28
                ScriptValue.FromString(args.Key),                 // 29
                ScriptValue.FromString(args.Code),                // 30
                ScriptValue.FromNumber(args.Location),            // 31
                ScriptValue.FromBoolean(args.Repeat),             // 32
                ScriptValue.FromBoolean(args.IsComposing),        // 33
                args.Data is not null ? ScriptValue.FromString(args.Data) : ScriptValue.Null, // 34
                ScriptValue.FromString(args.InputType)            // 35
            };

            var argsArray = engine.CreateArray(jsArgs);
            var result = engine.Call(dispatchHostEvent, ScriptValue.Undefined, [targetWrapper, ScriptValue.FromString(eventType), argsArray]);
            return result.IsBoolean ? result.AsBoolean() : true;
        };

        return context.WrapperCache;
    }

    private static void InstallDomException(IScriptEngine engine)
    {
        engine.Evaluate(@"
            (function() {
                if (typeof globalThis.DOMException === 'function') return;

                const codeMap = {
                    IndexSizeError: 1,
                    DOMStringSizeError: 2,
                    HierarchyRequestError: 3,
                    WrongDocumentError: 4,
                    InvalidCharacterError: 5,
                    NoDataAllowedError: 6,
                    NoModificationAllowedError: 7,
                    NotFoundError: 8,
                    NotSupportedError: 9,
                    InUseAttributeError: 10,
                    InvalidStateError: 11,
                    SyntaxError: 12,
                    InvalidModificationError: 13,
                    NamespaceError: 14,
                    InvalidAccessError: 15,
                    SecurityError: 18,
                    NetworkError: 19,
                    AbortError: 20,
                    TimeoutError: 23,
                    QuotaExceededError: 22
                };

                class DOMException extends Error {
                    constructor(message = '', name = 'Error') {
                        super(message);
                        this.name = name;
                        this.message = message;
                        this.code = codeMap[name] || 0;
                    }
                }

                DOMException.INDEX_SIZE_ERR = 1;
                DOMException.DOMSTRING_SIZE_ERR = 2;
                DOMException.HIERARCHY_REQUEST_ERR = 3;
                DOMException.WRONG_DOCUMENT_ERR = 4;
                DOMException.INVALID_CHARACTER_ERR = 5;
                DOMException.NO_DATA_ALLOWED_ERR = 6;
                DOMException.NO_MODIFICATION_ALLOWED_ERR = 7;
                DOMException.NOT_FOUND_ERR = 8;
                DOMException.NOT_SUPPORTED_ERR = 9;
                DOMException.INUSE_ATTRIBUTE_ERR = 10;
                DOMException.INVALID_STATE_ERR = 11;
                DOMException.SYNTAX_ERR = 12;
                DOMException.INVALID_MODIFICATION_ERR = 13;
                DOMException.NAMESPACE_ERR = 14;
                DOMException.INVALID_ACCESS_ERR = 15;
                DOMException.SECURITY_ERR = 18;
                DOMException.NETWORK_ERR = 19;
                DOMException.ABORT_ERR = 20;
                DOMException.TIMEOUT_ERR = 23;
                DOMException.QUOTA_EXCEEDED_ERR = 22;

                globalThis.DOMException = DOMException;
            })();
        ");
    }

    private static void InstallConstructors(IScriptEngine engine)
    {
        engine.Evaluate(@"
            (function() {
                function defClass(name, parent) {
                    const ctor = function() {
                        throw new TypeError('Illegal constructor');
                    };
                    Object.defineProperty(ctor, 'name', { value: name, configurable: true });
                    if (parent) {
                        Object.setPrototypeOf(ctor, parent);
                        ctor.prototype = Object.create(parent.prototype, {
                            constructor: { value: ctor, writable: true, configurable: true }
                        });
                    } else {
                        ctor.prototype = Object.create(Object.prototype, {
                            constructor: { value: ctor, writable: true, configurable: true }
                        });
                    }
                    globalThis[name] = ctor;
                    return ctor;
                }

                const Node = defClass('Node');
                Node.ELEMENT_NODE = 1;
                Node.ATTRIBUTE_NODE = 2;
                Node.TEXT_NODE = 3;
                Node.CDATA_SECTION_NODE = 4;
                Node.ENTITY_REFERENCE_NODE = 5;
                Node.ENTITY_NODE = 6;
                Node.PROCESSING_INSTRUCTION_NODE = 7;
                Node.COMMENT_NODE = 8;
                Node.DOCUMENT_NODE = 9;
                Node.DOCUMENT_TYPE_NODE = 10;
                Node.DOCUMENT_FRAGMENT_NODE = 11;
                Node.NOTATION_NODE = 12;

                Node.DOCUMENT_POSITION_DISCONNECTED = 0x01;
                Node.DOCUMENT_POSITION_PRECEDING = 0x02;
                Node.DOCUMENT_POSITION_FOLLOWING = 0x04;
                Node.DOCUMENT_POSITION_CONTAINS = 0x08;
                Node.DOCUMENT_POSITION_CONTAINED_BY = 0x10;
                Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC = 0x20;

                const Document = defClass('Document', Node);
                const DocumentType = defClass('DocumentType', Node);
                const DocumentFragment = defClass('DocumentFragment', Node);

                const CharacterData = defClass('CharacterData', Node);
                const Text = defClass('Text', CharacterData);
                const Comment = defClass('Comment', CharacterData);

                const Element = defClass('Element', Node);
                const SVGElement = defClass('SVGElement', Element);
                const HTMLElement = defClass('HTMLElement', Element);

                const Attr = defClass('Attr', Node);

                // Specific HTML element classes
                const htmlTags = [
                    'HTMLHtmlElement',
                    'HTMLHeadElement',
                    'HTMLBodyElement',
                    'HTMLTitleElement',
                    'HTMLDivElement',
                    'HTMLSpanElement',
                    'HTMLParagraphElement',
                    'HTMLAnchorElement',
                    'HTMLInputElement',
                    'HTMLButtonElement',
                    'HTMLImageElement',
                    'HTMLOptionElement',
                    'HTMLSelectElement',
                    'HTMLTextAreaElement',
                    'HTMLTemplateElement',
                    'HTMLScriptElement',
                    'HTMLUnknownElement'
                ];
                for (const tag of htmlTags) {
                    defClass(tag, HTMLElement);
                }

                // Collections
                const NodeList = defClass('NodeList');
                NodeList.prototype[Symbol.iterator] = Array.prototype.values;
                NodeList.prototype.values = Array.prototype.values;
                NodeList.prototype.keys = Array.prototype.keys;
                NodeList.prototype.entries = Array.prototype.entries;

                const HTMLCollection = defClass('HTMLCollection');
                HTMLCollection.prototype[Symbol.iterator] = Array.prototype.values;
                HTMLCollection.prototype.values = Array.prototype.values;

                const DOMTokenList = defClass('DOMTokenList');
                DOMTokenList.prototype[Symbol.iterator] = Array.prototype.values;
                DOMTokenList.prototype.values = Array.prototype.values;
                DOMTokenList.prototype.keys = Array.prototype.keys;
                DOMTokenList.prototype.entries = Array.prototype.entries;

                const DOMStringMap = defClass('DOMStringMap');

                const CSSStyleDeclaration = defClass('CSSStyleDeclaration');

                const NamedNodeMap = defClass('NamedNodeMap');
                NamedNodeMap.prototype[Symbol.iterator] = Array.prototype.values;
                NamedNodeMap.prototype.values = Array.prototype.values;
            })();
        ");
    }
}

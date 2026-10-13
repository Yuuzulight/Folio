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
        engine.Evaluate(@"
            if (typeof window === 'undefined') {
                globalThis.window = globalThis;
            }
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

                const NamedNodeMap = defClass('NamedNodeMap');
                NamedNodeMap.prototype[Symbol.iterator] = Array.prototype.values;
                NamedNodeMap.prototype.values = Array.prototype.values;
            })();
        ");
    }
}

using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Installs DOM constructors, prototype chains, and the global document onto an <see cref="IScriptEngine"/> (study 02, study 17, #451).
/// </summary>
internal static class DomBindings
{
    internal static ScriptWrapperCache Install(IScriptEngine engine, DocumentNode document, ScriptWrapperCache? wrapperCache = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(document);

        var context = new DomContext(engine, document, wrapperCache);

        // 1. Install DOMException
        InstallDomException(engine);

        // 2. Install Prototype Hierarchy & Constructors
        InstallConstructors(engine);

        // 3. Bind document to global scope
        var docWrapper = context.Wrap(document);
        engine.SetGlobalProperty("document", docWrapper);
        engine.Evaluate(@"
            if (typeof window === 'undefined') {
                globalThis.window = globalThis;
            }
        ");

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
                    'HTMLTemplateElement',
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
            })();
        ");
    }
}

using System.Text;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Defines host object templates for DOM interfaces conforming to W3C / WHATWG WebIDL specifications (study 02, #451).
/// </summary>
internal sealed class DomNodeTemplates
{
    private readonly DomContext _context;

    public DomNodeTemplates(DomContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        NodeTemplate = CreateNodeTemplate();
        ElementTemplate = CreateElementTemplate(NodeTemplate);
        HtmlElementTemplate = CreateHtmlElementTemplate(ElementTemplate);
        SvgElementTemplate = CreateSvgElementTemplate(ElementTemplate);
        CharacterDataTemplate = CreateCharacterDataTemplate(NodeTemplate);
        TextTemplate = CreateTextTemplate(CharacterDataTemplate);
        CommentTemplate = CreateCommentTemplate(CharacterDataTemplate);
        DocumentTypeTemplate = CreateDocumentTypeTemplate(NodeTemplate);
        DocumentFragmentTemplate = CreateDocumentFragmentTemplate(NodeTemplate);
        DocumentTemplate = CreateDocumentTemplate(NodeTemplate);
        NodeListTemplate = CreateNodeListTemplate();
        HtmlCollectionTemplate = CreateHtmlCollectionTemplate();

        DomTokenListTemplate = CreateDomTokenListTemplate();
        DomStringMapTemplate = CreateDomStringMapTemplate();
        NamedNodeMapTemplate = CreateNamedNodeMapTemplate();
        AttrTemplate = CreateAttrTemplate(NodeTemplate);

        HtmlInputElementTemplate = CreateHtmlInputElementTemplate(HtmlElementTemplate);
        HtmlButtonElementTemplate = CreateHtmlButtonElementTemplate(HtmlElementTemplate);
        HtmlAnchorElementTemplate = CreateHtmlAnchorElementTemplate(HtmlElementTemplate);
        HtmlImageElementTemplate = CreateHtmlImageElementTemplate(HtmlElementTemplate);
        HtmlOptionElementTemplate = CreateHtmlOptionElementTemplate(HtmlElementTemplate);
        HtmlSelectElementTemplate = CreateHtmlSelectElementTemplate(HtmlElementTemplate);
        HtmlTextAreaElementTemplate = CreateHtmlTextAreaElementTemplate(HtmlElementTemplate);
    }

    public HostObjectTemplate NodeTemplate { get; }
    public HostObjectTemplate ElementTemplate { get; }
    public HostObjectTemplate HtmlElementTemplate { get; }
    public HostObjectTemplate SvgElementTemplate { get; }
    public HostObjectTemplate CharacterDataTemplate { get; }
    public HostObjectTemplate TextTemplate { get; }
    public HostObjectTemplate CommentTemplate { get; }
    public HostObjectTemplate DocumentTypeTemplate { get; }
    public HostObjectTemplate DocumentFragmentTemplate { get; }
    public HostObjectTemplate DocumentTemplate { get; }
    public HostObjectTemplate NodeListTemplate { get; }
    public HostObjectTemplate HtmlCollectionTemplate { get; }
    public HostObjectTemplate DomTokenListTemplate { get; }
    public HostObjectTemplate DomStringMapTemplate { get; }
    public HostObjectTemplate NamedNodeMapTemplate { get; }
    public HostObjectTemplate AttrTemplate { get; }
    public HostObjectTemplate HtmlInputElementTemplate { get; }
    public HostObjectTemplate HtmlButtonElementTemplate { get; }
    public HostObjectTemplate HtmlAnchorElementTemplate { get; }
    public HostObjectTemplate HtmlImageElementTemplate { get; }
    public HostObjectTemplate HtmlOptionElementTemplate { get; }
    public HostObjectTemplate HtmlSelectElementTemplate { get; }
    public HostObjectTemplate HtmlTextAreaElementTemplate { get; }

    private HostObjectTemplate CreateNodeTemplate()
    {
        var t = new HostObjectTemplate("Node");

        // Constants
        t.DefineProperty("ELEMENT_NODE", (_, _) => ScriptValue.FromNumber(1));
        t.DefineProperty("ATTRIBUTE_NODE", (_, _) => ScriptValue.FromNumber(2));
        t.DefineProperty("TEXT_NODE", (_, _) => ScriptValue.FromNumber(3));
        t.DefineProperty("CDATA_SECTION_NODE", (_, _) => ScriptValue.FromNumber(4));
        t.DefineProperty("ENTITY_REFERENCE_NODE", (_, _) => ScriptValue.FromNumber(5));
        t.DefineProperty("ENTITY_NODE", (_, _) => ScriptValue.FromNumber(6));
        t.DefineProperty("PROCESSING_INSTRUCTION_NODE", (_, _) => ScriptValue.FromNumber(7));
        t.DefineProperty("COMMENT_NODE", (_, _) => ScriptValue.FromNumber(8));
        t.DefineProperty("DOCUMENT_NODE", (_, _) => ScriptValue.FromNumber(9));
        t.DefineProperty("DOCUMENT_TYPE_NODE", (_, _) => ScriptValue.FromNumber(10));
        t.DefineProperty("DOCUMENT_FRAGMENT_NODE", (_, _) => ScriptValue.FromNumber(11));
        t.DefineProperty("NOTATION_NODE", (_, _) => ScriptValue.FromNumber(12));

        t.DefineProperty("DOCUMENT_POSITION_DISCONNECTED", (_, _) => ScriptValue.FromNumber(0x01));
        t.DefineProperty("DOCUMENT_POSITION_PRECEDING", (_, _) => ScriptValue.FromNumber(0x02));
        t.DefineProperty("DOCUMENT_POSITION_FOLLOWING", (_, _) => ScriptValue.FromNumber(0x04));
        t.DefineProperty("DOCUMENT_POSITION_CONTAINS", (_, _) => ScriptValue.FromNumber(0x08));
        t.DefineProperty("DOCUMENT_POSITION_CONTAINED_BY", (_, _) => ScriptValue.FromNumber(0x10));
        t.DefineProperty("DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC", (_, _) => ScriptValue.FromNumber(0x20));

        // Node properties
        t.DefineProperty("nodeType", (target, _) =>
        {
            var node = (Node)target;
            var type = node switch
            {
                ElementNode => 1,
                Text => 3,
                Comment => 8,
                DocumentNode => 9,
                DocumentType => 10,
                DocumentFragment => 11,
                _ => 0,
            };
            return ScriptValue.FromNumber(type);
        });

        t.DefineProperty("nodeName", (target, _) =>
        {
            var node = (Node)target;
            var name = node switch
            {
                ElementNode el => el.Name.Namespace == Namespaces.Html ? el.LocalName.ToUpperInvariant() : el.LocalName,
                Text => "#text",
                Comment => "#comment",
                DocumentNode => "#document",
                DocumentType dt => dt.Name,
                DocumentFragment => "#document-fragment",
                _ => "",
            };
            return ScriptValue.FromString(name);
        });

        t.DefineProperty("nodeValue",
            (target, _) =>
            {
                var node = (Node)target;
                return node is CharacterData cd ? ScriptValue.FromString(cd.Data) : ScriptValue.Null;
            },
            (target, _, val) =>
            {
                var node = (Node)target;
                if (node is CharacterData cd && !val.IsNullOrUndefined)
                    cd.Data = WebIdlConversions.ToDOMString(val);
            });

        t.DefineProperty("ownerDocument", (target, _) =>
        {
            var node = (Node)target;
            return node is DocumentNode ? ScriptValue.Null : _context.Wrap(node.OwnerDocument);
        });

        t.DefineProperty("parentNode", (target, _) => _context.Wrap(((Node)target).Parent));
        t.DefineProperty("parentElement", (target, _) => _context.Wrap(((Node)target).Parent as ElementNode));

        t.DefineProperty("childNodes", (target, _) =>
        {
            var node = (Node)target;
            if (node is ContainerNode container)
                return _context.WrapNodeList(container);
            return _context.WrapNodeList(_context.Document.CreateFragment());
        });

        t.DefineProperty("firstChild", (target, _) =>
            _context.Wrap(((Node)target) is ContainerNode c ? c.FirstChild : null));

        t.DefineProperty("lastChild", (target, _) =>
            _context.Wrap(((Node)target) is ContainerNode c ? c.LastChild : null));

        t.DefineProperty("previousSibling", (target, _) => _context.Wrap(((Node)target).PreviousSibling));
        t.DefineProperty("nextSibling", (target, _) => _context.Wrap(((Node)target).NextSibling));

        t.DefineProperty("isConnected", (target, _) =>
        {
            for (Node? n = (Node)target; n is not null; n = n.Parent)
            {
                if (n is DocumentNode)
                    return ScriptValue.FromBoolean(true);
            }
            return ScriptValue.FromBoolean(false);
        });

        t.DefineProperty("textContent",
            (target, _) =>
            {
                var node = (Node)target;
                if (node is DocumentNode or DocumentType)
                    return ScriptValue.Null;
                if (node is CharacterData cd)
                    return ScriptValue.FromString(cd.Data);
                if (node is ContainerNode cn)
                    return ScriptValue.FromString(cn.TextContent ?? "");
                return ScriptValue.Null;
            },
            (target, _, val) =>
            {
                var node = (Node)target;
                if (node is DocumentNode or DocumentType)
                    return;

                var text = val.IsNullOrUndefined ? "" : WebIdlConversions.ToDOMString(val);
                if (node is CharacterData cd)
                {
                    cd.Data = text;
                    return;
                }

                if (node is ContainerNode container)
                {
                    while (container.FirstChild is { } child)
                        container.RemoveChild(child);

                    if (text.Length > 0)
                        container.AppendChild(container.OwnerDocument.CreateText(text));
                }
            });

        // Methods
        t.DefineMethod("hasChildNodes", (target, _, _) =>
            ScriptValue.FromBoolean(target is ContainerNode c && c.FirstChild is not null));

        t.DefineMethod("contains", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                return ScriptValue.FromBoolean(false);

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                return ScriptValue.FromBoolean(false);

            return ScriptValue.FromBoolean(((Node)target).IsInclusiveAncestorOf(otherNode));
        });

        t.DefineMethod("isSameNode", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                return ScriptValue.FromBoolean(false);

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                return ScriptValue.FromBoolean(false);

            return ScriptValue.FromBoolean(ReferenceEquals(target, otherNode));
        });

        t.DefineMethod("isEqualNode", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                return ScriptValue.FromBoolean(false);

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                return ScriptValue.FromBoolean(false);

            return ScriptValue.FromBoolean(IsEqualNode((Node)target, otherNode));
        });

        t.DefineMethod("compareDocumentPosition", (target, _, args) =>
        {
            if (args.Length == 0 || args[0].IsNullOrUndefined)
                throw WebIdlException.TypeError("Failed to execute 'compareDocumentPosition' on 'Node': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var otherTarget) || otherTarget is not Node otherNode)
                throw WebIdlException.TypeError("Failed to execute 'compareDocumentPosition' on 'Node': parameter 1 is not of type 'Node'.");

            return ScriptValue.FromNumber(CompareDocumentPosition((Node)target, otherNode));
        });

        t.DefineMethod("cloneNode", (target, _, args) =>
        {
            var deep = args.Length > 0 && WebIdlConversions.ToBoolean(args[0]);
            var node = (Node)target;
            if (node is DocumentNode)
                throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Cannot clone a Document.");

            var clone = node.Clone(deep);
            return _context.Wrap(clone);
        });

        t.DefineMethod("normalize", (target, _, _) =>
        {
            if (target is ContainerNode container)
                NormalizeContainer(container);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("appendChild", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'appendChild' on 'Node': 1 argument required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node cannot have children.");

            return DomTreeMutation.AppendChild(_context, container, args[0]);
        });

        t.DefineMethod("insertBefore", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'insertBefore' on 'Node': 2 arguments required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node cannot have children.");

            return DomTreeMutation.InsertBefore(_context, container, args[0], args[1]);
        });

        t.DefineMethod("removeChild", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'removeChild' on 'Node': 1 argument required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "Node has no children.");

            return DomTreeMutation.RemoveChild(_context, container, args[0]);
        });

        t.DefineMethod("replaceChild", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'replaceChild' on 'Node': 2 arguments required.");
            if (target is not ContainerNode container)
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node cannot have children.");

            return DomTreeMutation.ReplaceChild(_context, container, args[0], args[1]);
        });

        return t;
    }

    private HostObjectTemplate CreateElementTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("Element", nodeProto);

        t.DefineProperty("tagName", (target, _) =>
        {
            var el = (ElementNode)target;
            return ScriptValue.FromString(el.Name.Namespace == Namespaces.Html ? el.LocalName.ToUpperInvariant() : el.LocalName);
        });

        t.DefineProperty("localName", (target, _) => ScriptValue.FromString(((ElementNode)target).LocalName));
        t.DefineProperty("namespaceURI", (target, _) =>
        {
            var uri = ((ElementNode)target).NamespaceUri;
            return string.IsNullOrEmpty(uri) ? ScriptValue.Null : ScriptValue.FromString(uri);
        });

        t.DefineProperty("id",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("id") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("id", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("className",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("class") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("class", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("classList", (target, _) => _context.WrapClassList((ElementNode)target));

        t.DefineProperty("attributes", (target, _) => _context.WrapAttributes((ElementNode)target));

        // ParentNode members
        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((ElementNode)target));
        t.DefineProperty("firstElementChild", (target, _) =>
            _context.Wrap(((ElementNode)target).Children.OfType<ElementNode>().FirstOrDefault()));
        t.DefineProperty("lastElementChild", (target, _) =>
            _context.Wrap(((ElementNode)target).Children.OfType<ElementNode>().LastOrDefault()));
        t.DefineProperty("childElementCount", (target, _) =>
            ScriptValue.FromNumber(((ElementNode)target).Children.OfType<ElementNode>().Count()));

        // NonDocumentTypeChildNode members
        t.DefineProperty("previousElementSibling", (target, _) =>
        {
            for (var node = ((ElementNode)target).PreviousSibling; node is not null; node = node.PreviousSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        t.DefineProperty("nextElementSibling", (target, _) =>
        {
            for (var node = ((ElementNode)target).NextSibling; node is not null; node = node.NextSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        // Attributes methods
        t.DefineMethod("getAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("getAttribute requires 1 argument.");
            var name = WebIdlConversions.ToDOMString(args[0]);
            var val = ((ElementNode)target).GetAttribute(name);
            return val is null ? ScriptValue.Null : ScriptValue.FromString(val);
        });

        t.DefineMethod("setAttribute", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("setAttribute requires 2 arguments.");
            var name = WebIdlConversions.ToDOMString(args[0]);
            var val = WebIdlConversions.ToDOMString(args[1]);
            ((ElementNode)target).SetAttribute(name, val);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("hasAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.FromBoolean(false);
            var name = WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromBoolean(((ElementNode)target).GetAttribute(name) is not null);
        });

        t.DefineMethod("removeAttribute", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var name = WebIdlConversions.ToDOMString(args[0]);
                ((ElementNode)target).RemoveAttribute(name);
            }
            return ScriptValue.Undefined;
        });

        t.DefineMethod("toggleAttribute", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'toggleAttribute' on 'Element': 1 argument required.");

            var el = (ElementNode)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            var has = el.GetAttribute(name) is not null;

            bool shouldHave;
            if (args.Length > 1)
            {
                shouldHave = WebIdlConversions.ToBoolean(args[1]);
            }
            else
            {
                shouldHave = !has;
            }

            if (shouldHave)
            {
                if (!has)
                    el.SetAttribute(name, "");
                return ScriptValue.FromBoolean(true);
            }
            else
            {
                if (has)
                    el.RemoveAttribute(name);
                return ScriptValue.FromBoolean(false);
            }
        });

        t.DefineMethod("getAttributeNames", (target, _, _) =>
        {
            var el = (ElementNode)target;
            var attrs = el.Attributes;
            var names = new ScriptValue[attrs.Length];
            for (var i = 0; i < attrs.Length; i++)
            {
                names[i] = ScriptValue.FromString(el.OwnerDocument.TextOf(attrs[i].Name));
            }
            return _context.Engine.CreateArray(names);
        });

        t.DefineMethod("hasAttributes", (target, _, _) =>
        {
            var el = (ElementNode)target;
            return ScriptValue.FromBoolean(el.Attributes.Length > 0);
        });

        t.DefineMethod("getAttributeNode", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'getAttributeNode' on 'Element': 1 argument required.");

            var el = (ElementNode)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            if (el.GetAttribute(name) is null)
                return ScriptValue.Null;

            return _context.WrapAttr(el, name);
        });

        t.DefineMethod("setAttributeNode", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'setAttributeNode' on 'Element': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var hostTarget) || hostTarget is not LiveAttr attr)
                throw WebIdlException.TypeError("parameter 1 is not of type 'Attr'.");

            var el = (ElementNode)target;
            if (attr.Element is not null && !ReferenceEquals(attr.Element, el))
                throw WebIdlException.DomException(DomExceptionNames.InUseAttributeError, "The attribute is already in use by another element.");

            LiveAttr? oldAttr = null;
            if (el.GetAttribute(attr.Name) is not null)
            {
                var wrappedOld = _context.WrapAttr(el, attr.Name);
                if (_context.Engine.TryGetHostTarget(wrappedOld, out var oldTarget) && oldTarget is LiveAttr la)
                    oldAttr = la;
            }

            el.SetAttribute(attr.Name, attr.Value);
            attr.Element = el;

            return oldAttr is not null ? _context.WrapAttr(oldAttr.Element!, oldAttr.Name) : ScriptValue.Null;
        });

        t.DefineMethod("removeAttributeNode", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'removeAttributeNode' on 'Element': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var hostTarget) || hostTarget is not LiveAttr attr)
                throw WebIdlException.TypeError("parameter 1 is not of type 'Attr'.");

            var el = (ElementNode)target;
            if (!ReferenceEquals(attr.Element, el) || el.GetAttribute(attr.Name) is null)
                throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The attribute could not be found.");

            var val = el.GetAttribute(attr.Name) ?? attr.Value;
            el.RemoveAttribute(attr.Name);
            attr.Detach(val);
            return args[0];
        });

        // ParentNode mutation methods
        t.DefineMethod("append", (target, _, args) =>
        {
            DomTreeMutation.Append(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("prepend", (target, _, args) =>
        {
            DomTreeMutation.Prepend(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceChildren", (target, _, args) =>
        {
            DomTreeMutation.ReplaceChildren(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        // ChildNode mutation methods
        t.DefineMethod("before", (target, _, args) =>
        {
            DomTreeMutation.Before(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("after", (target, _, args) =>
        {
            DomTreeMutation.After(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceWith", (target, _, args) =>
        {
            DomTreeMutation.ReplaceWith(_context, (ElementNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, _) =>
        {
            DomTreeMutation.Remove((ElementNode)target);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateHtmlElementTemplate(IHostObjectTemplate elementProto)
    {
        var t = new HostObjectTemplate("HTMLElement", elementProto);

        t.DefineProperty("dataset", (target, _) => _context.WrapDataset((ElementNode)target));

        t.DefineProperty("title",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("title") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("title", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("lang",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("lang") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("lang", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("dir",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("dir") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("dir", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("hidden",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("hidden") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("hidden", "");
                else
                    ((ElementNode)target).RemoveAttribute("hidden");
            });

        t.DefineProperty("tabIndex",
            (target, _) =>
            {
                var el = (ElementNode)target;
                var val = el.GetAttribute("tabindex");
                if (val is not null && int.TryParse(val, out var parsed))
                    return ScriptValue.FromNumber(parsed);
                return ScriptValue.FromNumber(-1);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToInt32(val);
                ((ElementNode)target).SetAttribute("tabindex", num.ToString());
            });

        return t;
    }

    private static HostObjectTemplate CreateSvgElementTemplate(IHostObjectTemplate elementProto)
    {
        return new HostObjectTemplate("SVGElement", elementProto);
    }

    private HostObjectTemplate CreateCharacterDataTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("CharacterData", nodeProto);

        t.DefineProperty("data",
            (target, _) => ScriptValue.FromString(((CharacterData)target).Data),
            (target, _, val) => ((CharacterData)target).Data = WebIdlConversions.ToDOMString(val));

        t.DefineProperty("length", (target, _) =>
            ScriptValue.FromNumber(((CharacterData)target).Data.Length));

        t.DefineProperty("previousElementSibling", (target, _) =>
        {
            for (var node = ((CharacterData)target).PreviousSibling; node is not null; node = node.PreviousSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        t.DefineProperty("nextElementSibling", (target, _) =>
        {
            for (var node = ((CharacterData)target).NextSibling; node is not null; node = node.NextSibling)
            {
                if (node is ElementNode el)
                    return _context.Wrap(el);
            }
            return ScriptValue.Null;
        });

        t.DefineMethod("substringData", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("substringData requires 2 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var count = WebIdlConversions.ToInt32(args[1]);

            if (offset < 0 || offset > cd.Data.Length || count < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var len = Math.Min(count, cd.Data.Length - offset);
            return ScriptValue.FromString(cd.Data.Substring(offset, len));
        });

        t.DefineMethod("appendData", (target, _, args) =>
        {
            if (args.Length > 0)
            {
                var cd = (CharacterData)target;
                cd.Data += WebIdlConversions.ToDOMString(args[0]);
            }
            return ScriptValue.Undefined;
        });

        t.DefineMethod("insertData", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("insertData requires 2 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var data = WebIdlConversions.ToDOMString(args[1]);

            if (offset < 0 || offset > cd.Data.Length)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            cd.Data = cd.Data.Insert(offset, data);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("deleteData", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("deleteData requires 2 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var count = WebIdlConversions.ToInt32(args[1]);

            if (offset < 0 || offset > cd.Data.Length || count < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var len = Math.Min(count, cd.Data.Length - offset);
            cd.Data = cd.Data.Remove(offset, len);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceData", (target, _, args) =>
        {
            if (args.Length < 3)
                throw WebIdlException.TypeError("replaceData requires 3 arguments.");

            var cd = (CharacterData)target;
            var offset = WebIdlConversions.ToInt32(args[0]);
            var count = WebIdlConversions.ToInt32(args[1]);
            var data = WebIdlConversions.ToDOMString(args[2]);

            if (offset < 0 || offset > cd.Data.Length || count < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var len = Math.Min(count, cd.Data.Length - offset);
            cd.Data = cd.Data.Remove(offset, len).Insert(offset, data);
            return ScriptValue.Undefined;
        });

        // ChildNode mutation
        t.DefineMethod("before", (target, _, args) =>
        {
            DomTreeMutation.Before(_context, (CharacterData)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("after", (target, _, args) =>
        {
            DomTreeMutation.After(_context, (CharacterData)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceWith", (target, _, args) =>
        {
            DomTreeMutation.ReplaceWith(_context, (CharacterData)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, _) =>
        {
            DomTreeMutation.Remove((CharacterData)target);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateTextTemplate(IHostObjectTemplate charDataProto)
    {
        var t = new HostObjectTemplate("Text", charDataProto);

        t.DefineMethod("splitText", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("splitText requires 1 argument.");

            var text = (Text)target;
            var offset = WebIdlConversions.ToInt32(args[0]);

            if (offset < 0 || offset > text.Data.Length)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "Index out of bounds.");

            var remainder = text.Data[offset..];
            text.Data = text.Data[..offset];

            var newText = text.OwnerDocument.CreateText(remainder);
            if (text.Parent is { } parent)
                parent.InsertBefore(newText, text.NextSibling);

            return _context.Wrap(newText);
        });

        return t;
    }

    private static HostObjectTemplate CreateCommentTemplate(IHostObjectTemplate charDataProto)
    {
        return new HostObjectTemplate("Comment", charDataProto);
    }

    private HostObjectTemplate CreateDocumentTypeTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("DocumentType", nodeProto);

        t.DefineProperty("name", (target, _) => ScriptValue.FromString(((DocumentType)target).Name));
        t.DefineProperty("publicId", (target, _) => ScriptValue.FromString(((DocumentType)target).PublicId));
        t.DefineProperty("systemId", (target, _) => ScriptValue.FromString(((DocumentType)target).SystemId));

        t.DefineMethod("before", (target, _, args) =>
        {
            DomTreeMutation.Before(_context, (DocumentType)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("after", (target, _, args) =>
        {
            DomTreeMutation.After(_context, (DocumentType)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceWith", (target, _, args) =>
        {
            DomTreeMutation.ReplaceWith(_context, (DocumentType)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, _) =>
        {
            DomTreeMutation.Remove((DocumentType)target);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateDocumentFragmentTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("DocumentFragment", nodeProto);

        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((DocumentFragment)target));
        t.DefineProperty("firstElementChild", (target, _) =>
            _context.Wrap(((DocumentFragment)target).Children.OfType<ElementNode>().FirstOrDefault()));
        t.DefineProperty("lastElementChild", (target, _) =>
            _context.Wrap(((DocumentFragment)target).Children.OfType<ElementNode>().LastOrDefault()));
        t.DefineProperty("childElementCount", (target, _) =>
            ScriptValue.FromNumber(((DocumentFragment)target).Children.OfType<ElementNode>().Count()));

        t.DefineMethod("append", (target, _, args) =>
        {
            DomTreeMutation.Append(_context, (DocumentFragment)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("prepend", (target, _, args) =>
        {
            DomTreeMutation.Prepend(_context, (DocumentFragment)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceChildren", (target, _, args) =>
        {
            DomTreeMutation.ReplaceChildren(_context, (DocumentFragment)target, args);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateDocumentTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("Document", nodeProto);

        t.DefineProperty("documentElement", (target, _) =>
            _context.Wrap(((DocumentNode)target).DocumentElement));

        t.DefineProperty("head", (target, _) =>
        {
            var doc = (DocumentNode)target;
            var head = doc.DocumentElement?.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "head");
            return _context.Wrap(head);
        });

        t.DefineProperty("body", (target, _) =>
        {
            var doc = (DocumentNode)target;
            var body = doc.DocumentElement?.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && (e.LocalName == "body" || e.LocalName == "frameset"));
            return _context.Wrap(body);
        });

        t.DefineProperty("title",
            (target, _) =>
            {
                var doc = (DocumentNode)target;
                var titleEl = doc.DocumentElement?.Descendants.OfType<ElementNode>()
                    .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "title");
                return ScriptValue.FromString(titleEl?.TextContent ?? "");
            },
            (target, _, val) =>
            {
                var doc = (DocumentNode)target;
                var text = WebIdlConversions.ToDOMString(val);
                var titleEl = doc.DocumentElement?.Descendants.OfType<ElementNode>()
                    .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "title");
                if (titleEl is not null)
                {
                    while (titleEl.FirstChild is { } c)
                        titleEl.RemoveChild(c);
                    if (text.Length > 0)
                        titleEl.AppendChild(doc.CreateText(text));
                }
                else
                {
                    var head = doc.DocumentElement?.Children.OfType<ElementNode>()
                        .FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "head");
                    if (head is not null)
                    {
                        var newTitle = doc.CreateElement(Namespaces.Html, "title");
                        if (text.Length > 0)
                            newTitle.AppendChild(doc.CreateText(text));
                        head.AppendChild(newTitle);
                    }
                }
            });

        // ParentNode
        t.DefineProperty("children", (target, _) => _context.WrapHtmlCollection((DocumentNode)target));
        t.DefineProperty("firstElementChild", (target, _) =>
            _context.Wrap(((DocumentNode)target).DocumentElement));
        t.DefineProperty("lastElementChild", (target, _) =>
            _context.Wrap(((DocumentNode)target).DocumentElement));
        t.DefineProperty("childElementCount", (target, _) =>
            ScriptValue.FromNumber(((DocumentNode)target).DocumentElement is not null ? 1 : 0));

        // Factory methods
        t.DefineMethod("createElement", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("createElement requires at least 1 argument.");

            var localName = WebIdlConversions.ToDOMString(args[0]).ToLowerInvariant();
            var doc = (DocumentNode)target;
            var el = doc.CreateElement(Namespaces.Html, localName);
            return _context.Wrap(el);
        });

        t.DefineMethod("createElementNS", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("createElementNS requires 2 arguments.");

            var nsStr = args[0].IsNullOrUndefined ? null : WebIdlConversions.ToDOMString(args[0]);
            var qualifiedName = WebIdlConversions.ToDOMString(args[1]);

            var doc = (DocumentNode)target;
            var nsAtom = nsStr is null ? Atom.None : doc.Intern(nsStr);
            var localName = qualifiedName;
            var colon = qualifiedName.IndexOf(':');
            if (colon >= 0)
                localName = qualifiedName[(colon + 1)..];

            var el = doc.CreateElement(nsAtom, localName);
            return _context.Wrap(el);
        });

        t.DefineMethod("createTextNode", (target, _, args) =>
        {
            var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
            var doc = (DocumentNode)target;
            return _context.Wrap(doc.CreateText(text));
        });

        t.DefineMethod("createComment", (target, _, args) =>
        {
            var text = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
            var doc = (DocumentNode)target;
            return _context.Wrap(doc.CreateComment(text));
        });

        t.DefineMethod("createDocumentFragment", (target, _, _) =>
        {
            var doc = (DocumentNode)target;
            return _context.Wrap(doc.CreateFragment());
        });

        t.DefineMethod("getElementById", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var id = WebIdlConversions.ToDOMString(args[0]);
            var doc = (DocumentNode)target;
            var match = doc.Descendants.OfType<ElementNode>()
                .FirstOrDefault(e => e.GetAttribute("id") == id);

            return _context.Wrap(match);
        });

        t.DefineMethod("append", (target, _, args) =>
        {
            DomTreeMutation.Append(_context, (DocumentNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("prepend", (target, _, args) =>
        {
            DomTreeMutation.Prepend(_context, (DocumentNode)target, args);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("replaceChildren", (target, _, args) =>
        {
            DomTreeMutation.ReplaceChildren(_context, (DocumentNode)target, args);
            return ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateNodeListTemplate()
    {
        var t = new HostObjectTemplate("NodeList");

        t.DefineProperty("length", (target, _) =>
        {
            var list = (LiveNodeList)target;
            return ScriptValue.FromNumber(list.Container.Children.Count());
        });

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var list = (LiveNodeList)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            if (idx < 0)
                return ScriptValue.Null;

            var child = list.Container.Children.ElementAtOrDefault(idx);
            return _context.Wrap(child);
        });

        t.DefineMethod("forEach", (target, thisVal, args) =>
        {
            if (args.Length == 0 || !args[0].IsFunction)
                throw WebIdlException.TypeError("callback must be a function.");

            var callback = args[0];
            var thisArg = args.Length > 1 ? args[1] : ScriptValue.Undefined;
            var list = (LiveNodeList)target;
            var snapshot = list.Container.Children.ToList();

            for (var i = 0; i < snapshot.Count; i++)
            {
                var childVal = _context.Wrap(snapshot[i]);
                _context.Engine.Call(callback, thisArg, [childVal, ScriptValue.FromNumber(i), thisVal]);
            }
            return ScriptValue.Undefined;
        });

        t.DefineIndexedProperty((target, _, index) =>
        {
            if (index < 0)
                return ScriptValue.Undefined;

            var list = (LiveNodeList)target;
            var child = list.Container.Children.ElementAtOrDefault(index);
            return child is not null ? _context.Wrap(child) : ScriptValue.Undefined;
        });

        return t;
    }

    private HostObjectTemplate CreateHtmlCollectionTemplate()
    {
        var t = new HostObjectTemplate("HTMLCollection");

        t.DefineProperty("length", (target, _) =>
        {
            var col = (LiveHtmlCollection)target;
            return ScriptValue.FromNumber(col.Container.Children.OfType<ElementNode>().Count());
        });

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var col = (LiveHtmlCollection)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            if (idx < 0)
                return ScriptValue.Null;

            var child = col.Container.Children.OfType<ElementNode>().ElementAtOrDefault(idx);
            return _context.Wrap(child);
        });

        t.DefineMethod("namedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var col = (LiveHtmlCollection)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            var match = col.Container.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.GetAttribute("id") == name || e.GetAttribute("name") == name);

            return _context.Wrap(match);
        });

        t.DefineIndexedProperty((target, _, index) =>
        {
            if (index < 0)
                return ScriptValue.Undefined;

            var col = (LiveHtmlCollection)target;
            var child = col.Container.Children.OfType<ElementNode>().ElementAtOrDefault(index);
            return child is not null ? _context.Wrap(child) : ScriptValue.Undefined;
        });

        t.DefineNamedProperty((target, _, name) =>
        {
            var col = (LiveHtmlCollection)target;
            var match = col.Container.Children.OfType<ElementNode>()
                .FirstOrDefault(e => e.GetAttribute("id") == name || e.GetAttribute("name") == name);

            return match is not null ? _context.Wrap(match) : ScriptValue.Undefined;
        });

        return t;
    }

    private static bool IsEqualNode(Node a, Node b)
    {
        if (ReferenceEquals(a, b))
            return true;

        if (a.GetType() != b.GetType())
            return false;

        switch (a)
        {
            case DocumentType dtA when b is DocumentType dtB:
                if (dtA.Name != dtB.Name || dtA.PublicId != dtB.PublicId || dtA.SystemId != dtB.SystemId)
                    return false;
                break;

            case ElementNode elA when b is ElementNode elB:
                if (elA.Name != elB.Name)
                    return false;
                if (elA.Attributes.Length != elB.Attributes.Length)
                    return false;
                foreach (var attr in elA.Attributes)
                {
                    if (elB.GetAttribute(elA.OwnerDocument.TextOf(attr.Name)) != attr.Value)
                        return false;
                }
                break;

            case CharacterData cdA when b is CharacterData cdB:
                if (cdA.Data != cdB.Data)
                    return false;
                break;
        }

        if (a is ContainerNode cnA && b is ContainerNode cnB)
        {
            var childrenA = cnA.Children.ToList();
            var childrenB = cnB.Children.ToList();
            if (childrenA.Count != childrenB.Count)
                return false;

            for (var i = 0; i < childrenA.Count; i++)
            {
                if (!IsEqualNode(childrenA[i], childrenB[i]))
                    return false;
            }
        }

        return true;
    }

    private static ushort CompareDocumentPosition(Node a, Node b)
    {
        if (ReferenceEquals(a, b))
            return 0;

        Node rootA = a;
        while (rootA.Parent is not null) rootA = rootA.Parent;

        Node rootB = b;
        while (rootB.Parent is not null) rootB = rootB.Parent;

        if (!ReferenceEquals(rootA, rootB))
        {
            return 0x01 | 0x20 | 0x02; // Disconnected | ImplementationSpecific | Preceding
        }

        if (a.IsInclusiveAncestorOf(b))
            return 0x10 | 0x04; // ContainedBy | Following

        if (b.IsInclusiveAncestorOf(a))
            return 0x08 | 0x02; // Contains | Preceding

        // Tree order in common ancestor root
        if (rootA is ContainerNode containerRoot)
        {
            for (var node = containerRoot.FirstChild; node is not null; node = node.NextInTree(containerRoot))
            {
                if (ReferenceEquals(node, a))
                    return 0x04; // Following (b follows a)
                if (ReferenceEquals(node, b))
                    return 0x02; // Preceding (b precedes a)
            }
        }

        return 0x01;
    }

    private static void NormalizeContainer(ContainerNode container)
    {
        for (var child = container.FirstChild; child is not null;)
        {
            var next = child.NextSibling;
            if (child is Text text)
            {
                if (text.Data.Length == 0)
                {
                    container.RemoveChild(text);
                }
                else
                {
                    while (next is Text nextText)
                    {
                        var following = nextText.NextSibling;
                        text.Data += nextText.Data;
                        container.RemoveChild(nextText);
                        next = following;
                    }
                }
            }
            else if (child is ContainerNode childContainer)
            {
                NormalizeContainer(childContainer);
            }
            child = next;
        }
    }

    private HostObjectTemplate CreateDomTokenListTemplate()
    {
        var t = new HostObjectTemplate("DOMTokenList");

        t.DefineProperty("length", (target, _) =>
        {
            var list = (LiveDomTokenList)target;
            return ScriptValue.FromNumber(list.Length);
        });

        t.DefineProperty("value",
            (target, _) => ScriptValue.FromString(((LiveDomTokenList)target).Value),
            (target, _, val) => ((LiveDomTokenList)target).Value = WebIdlConversions.ToDOMString(val));

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var list = (LiveDomTokenList)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            var item = list.Item(idx);
            return item is not null ? ScriptValue.FromString(item) : ScriptValue.Null;
        });

        t.DefineMethod("contains", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.FromBoolean(false);

            var list = (LiveDomTokenList)target;
            var token = WebIdlConversions.ToDOMString(args[0]);
            return ScriptValue.FromBoolean(list.Contains(token));
        });

        t.DefineMethod("add", (target, _, args) =>
        {
            var list = (LiveDomTokenList)target;
            var tokens = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
                tokens[i] = WebIdlConversions.ToDOMString(args[i]);

            list.Add(tokens);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("remove", (target, _, args) =>
        {
            var list = (LiveDomTokenList)target;
            var tokens = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
                tokens[i] = WebIdlConversions.ToDOMString(args[i]);

            list.Remove(tokens);
            return ScriptValue.Undefined;
        });

        t.DefineMethod("toggle", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'toggle' on 'DOMTokenList': 1 argument required.");

            var list = (LiveDomTokenList)target;
            var token = WebIdlConversions.ToDOMString(args[0]);
            bool? force = args.Length > 1 ? WebIdlConversions.ToBoolean(args[1]) : null;

            return ScriptValue.FromBoolean(list.Toggle(token, force));
        });

        t.DefineMethod("replace", (target, _, args) =>
        {
            if (args.Length < 2)
                throw WebIdlException.TypeError("Failed to execute 'replace' on 'DOMTokenList': 2 arguments required.");

            var list = (LiveDomTokenList)target;
            var oldToken = WebIdlConversions.ToDOMString(args[0]);
            var newToken = WebIdlConversions.ToDOMString(args[1]);

            return ScriptValue.FromBoolean(list.Replace(oldToken, newToken));
        });

        t.DefineMethod("supports", (_, _, _) =>
            throw WebIdlException.TypeError("DOMTokenList has no supported tokens."));

        t.DefineIndexedProperty((target, _, index) =>
        {
            var list = (LiveDomTokenList)target;
            var item = list.Item(index);
            return item is not null ? ScriptValue.FromString(item) : ScriptValue.Undefined;
        });

        t.DefineMethod("forEach", (target, thisVal, args) =>
        {
            if (args.Length == 0 || !args[0].IsFunction)
                throw WebIdlException.TypeError("callback must be a function.");

            var callback = args[0];
            var thisArg = args.Length > 1 ? args[1] : ScriptValue.Undefined;
            var list = (LiveDomTokenList)target;
            var tokens = list.GetTokens();

            for (var i = 0; i < tokens.Count; i++)
            {
                _context.Engine.Call(callback, thisArg, [ScriptValue.FromString(tokens[i]), ScriptValue.FromNumber(i), thisVal]);
            }
            return ScriptValue.Undefined;
        });

        return t;
    }

    private static HostObjectTemplate CreateDomStringMapTemplate()
    {
        var t = new HostObjectTemplate("DOMStringMap");

        t.DefineNamedProperty(
            getter: (target, _, name) =>
            {
                var map = (LiveDomStringMap)target;
                var val = map.Get(name);
                return val is not null ? ScriptValue.FromString(val) : ScriptValue.Undefined;
            },
            setter: (target, _, name, value) =>
            {
                var map = (LiveDomStringMap)target;
                var strVal = WebIdlConversions.ToDOMString(value);
                map.Set(name, strVal);
            },
            deleter: (target, _, name) =>
            {
                var map = (LiveDomStringMap)target;
                return map.Delete(name);
            },
            enumerator: (target, _) =>
            {
                var map = (LiveDomStringMap)target;
                return map.EnumerateProperties();
            });

        return t;
    }

    private HostObjectTemplate CreateNamedNodeMapTemplate()
    {
        var t = new HostObjectTemplate("NamedNodeMap");

        t.DefineProperty("length", (target, _) =>
        {
            var map = (LiveNamedNodeMap)target;
            return ScriptValue.FromNumber(map.Length);
        });

        t.DefineMethod("item", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var map = (LiveNamedNodeMap)target;
            var idx = WebIdlConversions.ToInt32(args[0]);
            var name = map.GetAttributeName(idx);
            return name is not null ? _context.WrapAttr(map.Element, name) : ScriptValue.Null;
        });

        t.DefineMethod("getNamedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                return ScriptValue.Null;

            var map = (LiveNamedNodeMap)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            if (map.Element.GetAttribute(name) is null)
                return ScriptValue.Null;

            return _context.WrapAttr(map.Element, name);
        });

        t.DefineMethod("setNamedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'setNamedItem' on 'NamedNodeMap': 1 argument required.");

            if (!_context.Engine.TryGetHostTarget(args[0], out var hostTarget) || hostTarget is not LiveAttr attr)
                throw WebIdlException.TypeError("parameter 1 is not of type 'Attr'.");

            var map = (LiveNamedNodeMap)target;
            if (attr.Element is not null && !ReferenceEquals(attr.Element, map.Element))
                throw WebIdlException.DomException(DomExceptionNames.InUseAttributeError, "The attribute is already in use by another element.");

            var oldAttr = map.Element.GetAttribute(attr.Name) is not null ? _context.WrapAttr(map.Element, attr.Name) : ScriptValue.Null;
            map.Element.SetAttribute(attr.Name, attr.Value);
            attr.Element = map.Element;

            return oldAttr;
        });

        t.DefineMethod("removeNamedItem", (target, _, args) =>
        {
            if (args.Length == 0)
                throw WebIdlException.TypeError("Failed to execute 'removeNamedItem' on 'NamedNodeMap': 1 argument required.");

            var map = (LiveNamedNodeMap)target;
            var name = WebIdlConversions.ToDOMString(args[0]);
            if (map.Element.GetAttribute(name) is null)
                throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The attribute could not be found.");

            var attrNode = _context.WrapAttr(map.Element, name);
            var val = map.Element.GetAttribute(name) ?? "";
            map.Element.RemoveAttribute(name);
            if (_context.Engine.TryGetHostTarget(attrNode, out var hostTarget) && hostTarget is LiveAttr attr)
                attr.Detach(val);

            return attrNode;
        });

        t.DefineIndexedProperty((target, _, index) =>
        {
            var map = (LiveNamedNodeMap)target;
            var name = map.GetAttributeName(index);
            return name is not null ? _context.WrapAttr(map.Element, name) : ScriptValue.Undefined;
        });

        t.DefineNamedProperty(
            getter: (target, _, name) =>
            {
                var map = (LiveNamedNodeMap)target;
                if (map.Element.GetAttribute(name) is null)
                    return ScriptValue.Undefined;

                return _context.WrapAttr(map.Element, name);
            },
            enumerator: (target, _) =>
            {
                var map = (LiveNamedNodeMap)target;
                return map.EnumerateNames();
            });

        return t;
    }

    private HostObjectTemplate CreateAttrTemplate(IHostObjectTemplate nodeProto)
    {
        var t = new HostObjectTemplate("Attr", nodeProto);

        t.DefineProperty("name", (target, _) => ScriptValue.FromString(((LiveAttr)target).Name));
        t.DefineProperty("localName", (target, _) => ScriptValue.FromString(((LiveAttr)target).LocalName));
        t.DefineProperty("specified", (_, _) => ScriptValue.FromBoolean(true));

        t.DefineProperty("value",
            (target, _) => ScriptValue.FromString(((LiveAttr)target).Value),
            (target, _, val) => ((LiveAttr)target).Value = WebIdlConversions.ToDOMString(val));

        t.DefineProperty("ownerElement", (target, _) =>
        {
            var el = ((LiveAttr)target).Element;
            return el is not null ? _context.Wrap(el) : ScriptValue.Null;
        });

        return t;
    }

    private HostObjectTemplate CreateHtmlInputElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLInputElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var el = (ElementNode)target;
                return ScriptValue.FromString(el.Value ?? el.GetAttribute("value") ?? "");
            },
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Value = WebIdlConversions.ToDOMString(val);
            });

        t.DefineProperty("checked",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).IsChecked),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Checked = WebIdlConversions.ToBoolean(val);
            });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("type",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("type") ?? "text"),
            (target, _, val) => ((ElementNode)target).SetAttribute("type", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("placeholder",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("placeholder") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("placeholder", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("readOnly",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("readonly") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("readonly", "");
                else
                    ((ElementNode)target).RemoveAttribute("readonly");
            });

        t.DefineProperty("required",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("required") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("required", "");
                else
                    ((ElementNode)target).RemoveAttribute("required");
            });

        return t;
    }

    private HostObjectTemplate CreateHtmlButtonElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLButtonElement", htmlElementProto);

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("type",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("type") ?? "submit"),
            (target, _, val) => ((ElementNode)target).SetAttribute("type", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("value",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("value") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("value", WebIdlConversions.ToDOMString(val)));

        return t;
    }

    private HostObjectTemplate CreateHtmlAnchorElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLAnchorElement", htmlElementProto);

        t.DefineProperty("href",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("href") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("href", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("target",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("target") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("target", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("rel",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("rel") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("rel", WebIdlConversions.ToDOMString(val)));

        return t;
    }

    private HostObjectTemplate CreateHtmlImageElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLImageElement", htmlElementProto);

        t.DefineProperty("src",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("src") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("src", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("alt",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("alt") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("alt", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("width",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("width");
                return ScriptValue.FromNumber(val is not null && int.TryParse(val, out var w) ? w : 0);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("width", num.ToString());
            });

        t.DefineProperty("height",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("height");
                return ScriptValue.FromNumber(val is not null && int.TryParse(val, out var h) ? h : 0);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("height", num.ToString());
            });

        return t;
    }

    private HostObjectTemplate CreateHtmlOptionElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLOptionElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var el = (ElementNode)target;
                return ScriptValue.FromString(el.GetAttribute("value") ?? el.TextContent);
            },
            (target, _, val) => ((ElementNode)target).SetAttribute("value", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("selected",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).IsSelected),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Selected = WebIdlConversions.ToBoolean(val);
            });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("text",
            (target, _) => ScriptValue.FromString(((ElementNode)target).TextContent),
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                var text = WebIdlConversions.ToDOMString(val);
                while (el.FirstChild is { } c)
                    el.RemoveChild(c);
                if (text.Length > 0)
                    el.AppendChild(el.OwnerDocument.CreateText(text));
            });

        return t;
    }

    private HostObjectTemplate CreateHtmlSelectElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLSelectElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var select = (ElementNode)target;
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                var selected = options.FirstOrDefault(o => o.IsSelected) ?? options.FirstOrDefault();
                if (selected is null)
                    return ScriptValue.FromString("");

                return ScriptValue.FromString(selected.GetAttribute("value") ?? selected.TextContent);
            },
            (target, _, val) =>
            {
                var select = (ElementNode)target;
                var strVal = WebIdlConversions.ToDOMString(val);
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                foreach (var opt in options)
                {
                    var optVal = opt.GetAttribute("value") ?? opt.TextContent;
                    opt.Selected = optVal == strVal;
                }
            });

        t.DefineProperty("selectedIndex",
            (target, _) =>
            {
                var select = (ElementNode)target;
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i].IsSelected)
                        return ScriptValue.FromNumber(i);
                }
                return ScriptValue.FromNumber(options.Count > 0 ? 0 : -1);
            },
            (target, _, val) =>
            {
                var select = (ElementNode)target;
                var idx = WebIdlConversions.ToInt32(val);
                var options = select.Descendants.OfType<ElementNode>().Where(e => e.LocalName == "option").ToList();
                for (var i = 0; i < options.Count; i++)
                {
                    options[i].Selected = i == idx;
                }
            });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        return t;
    }

    private HostObjectTemplate CreateHtmlTextAreaElementTemplate(IHostObjectTemplate htmlElementProto)
    {
        var t = new HostObjectTemplate("HTMLTextAreaElement", htmlElementProto);

        t.DefineProperty("value",
            (target, _) =>
            {
                var el = (ElementNode)target;
                return ScriptValue.FromString(el.Value ?? el.TextContent);
            },
            (target, _, val) =>
            {
                var el = (ElementNode)target;
                el.Value = WebIdlConversions.ToDOMString(val);
            });

        t.DefineProperty("disabled",
            (target, _) => ScriptValue.FromBoolean(((ElementNode)target).GetAttribute("disabled") is not null),
            (target, _, val) =>
            {
                if (WebIdlConversions.ToBoolean(val))
                    ((ElementNode)target).SetAttribute("disabled", "");
                else
                    ((ElementNode)target).RemoveAttribute("disabled");
            });

        t.DefineProperty("name",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("name") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("name", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("placeholder",
            (target, _) => ScriptValue.FromString(((ElementNode)target).GetAttribute("placeholder") ?? ""),
            (target, _, val) => ((ElementNode)target).SetAttribute("placeholder", WebIdlConversions.ToDOMString(val)));

        t.DefineProperty("rows",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("rows");
                return ScriptValue.FromNumber(val is not null && uint.TryParse(val, out var r) ? r : 2);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("rows", num.ToString());
            });

        t.DefineProperty("cols",
            (target, _) =>
            {
                var val = ((ElementNode)target).GetAttribute("cols");
                return ScriptValue.FromNumber(val is not null && uint.TryParse(val, out var c) ? c : 20);
            },
            (target, _, val) =>
            {
                var num = WebIdlConversions.ToUint32(val);
                ((ElementNode)target).SetAttribute("cols", num.ToString());
            });

        return t;
    }
}


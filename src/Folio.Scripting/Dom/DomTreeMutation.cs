using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Implements DOM tree mutation operations and validity checks conforming to W3C / WHATWG DOM specifications (study 02, #451).
/// </summary>
internal static class DomTreeMutation
{
    public static void EnsurePreInsertionValidity(ContainerNode parent, Node node, Node? child)
    {
        if (node.OwnerDocument != parent.OwnerDocument)
            throw WebIdlException.DomException(DomExceptionNames.WrongDocumentError, "Node belongs to another document.");

        if (node is DocumentNode)
            throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document cannot be inserted.");

        if (node.IsInclusiveAncestorOf(parent))
            throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A node cannot be inserted into itself or its descendants.");

        if (child is not null && child.Parent != parent)
            throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "Reference child node is not a child of this node.");

        if (node is DocumentType && parent is not DocumentNode)
            throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A DocumentType can only be inserted into a Document.");

        if (parent is DocumentNode doc)
        {
            EnsureDocumentChildAllowed(doc, node, child);
        }
    }

    public static void EnsurePreRemoveValidity(ContainerNode parent, Node child)
    {
        if (child.Parent != parent)
            throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The node to be removed is not a child of this node.");
    }

    public static void EnsurePreReplaceValidity(ContainerNode parent, Node node, Node child)
    {
        if (child.Parent != parent)
            throw WebIdlException.DomException(DomExceptionNames.NotFoundError, "The node to be replaced is not a child of this node.");

        if (ReferenceEquals(node, child))
            return;

        if (node.OwnerDocument != parent.OwnerDocument)
            throw WebIdlException.DomException(DomExceptionNames.WrongDocumentError, "Node belongs to another document.");

        if (node is DocumentNode)
            throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document cannot be inserted.");

        if (node.IsInclusiveAncestorOf(parent))
            throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A node cannot be inserted into itself or its descendants.");

        if (node is DocumentType && parent is not DocumentNode)
            throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A DocumentType can only be inserted into a Document.");

        if (parent is DocumentNode doc)
        {
            EnsureDocumentReplaceAllowed(doc, node, child);
        }
    }

    private static void EnsureDocumentChildAllowed(DocumentNode doc, Node node, Node? child)
    {
        switch (node)
        {
            case Text:
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Text cannot be a child of a Document.");

            case DocumentFragment fragment:
                var elements = fragment.Children.Count(n => n is ElementNode);
                if (elements > 1 || fragment.Children.Any(n => n is Text))
                    throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one element and no text.");
                if (elements == 1)
                {
                    if (doc.DocumentElement is not null || (child is not null && DoctypeAtOrAfter(child)))
                        throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one element, after its doctype.");
                }
                break;

            case ElementNode:
                if (doc.DocumentElement is not null || (child is not null && DoctypeAtOrAfter(child)))
                    throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one element, after its doctype.");
                break;

            case DocumentType:
                if (doc.Children.Any(n => n is DocumentType) || ElementBefore(doc, child))
                    throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one doctype, before its element.");
                break;
        }
    }

    private static void EnsureDocumentReplaceAllowed(DocumentNode doc, Node node, Node child)
    {
        switch (node)
        {
            case Text:
                throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Text cannot be a child of a Document.");

            case DocumentFragment fragment:
                var elements = fragment.Children.Count(n => n is ElementNode);
                if (elements > 1 || fragment.Children.Any(n => n is Text))
                    throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one element and no text.");
                if (elements == 1)
                {
                    var existingElement = doc.Children.OfType<ElementNode>().FirstOrDefault(e => !ReferenceEquals(e, child));
                    if (existingElement is not null || DoctypeAtOrAfter(child.NextSibling))
                        throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one element, after its doctype.");
                }
                break;

            case ElementNode:
                var currentElement = doc.Children.OfType<ElementNode>().FirstOrDefault(e => !ReferenceEquals(e, child));
                if (currentElement is not null || DoctypeAtOrAfter(child.NextSibling))
                    throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one element, after its doctype.");
                break;

            case DocumentType:
                var existingDoctype = doc.Children.OfType<DocumentType>().FirstOrDefault(d => !ReferenceEquals(d, child));
                if (existingDoctype is not null || ElementBefore(doc, child))
                    throw WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "A Document can have only one doctype, before its element.");
                break;
        }
    }

    private static bool ElementBefore(DocumentNode doc, Node? child)
    {
        for (var node = child is null ? doc.LastChild : child.PreviousSibling; node is not null; node = node.PreviousSibling)
        {
            if (node is ElementNode)
                return true;
        }
        return false;
    }

    private static bool DoctypeAtOrAfter(Node? child)
    {
        for (var node = child; node is not null; node = node.NextSibling)
        {
            if (node is DocumentType)
                return true;
        }
        return false;
    }

    public static Node ConvertToNode(DomContext context, ScriptValue val)
    {
        if (context.Engine.TryGetHostTarget(val, out var target) && target is Node node)
            return node;

        var str = WebIdlConversions.ToDOMString(val);
        return context.Document.CreateText(str);
    }

    public static Node? ConvertMutationNodes(DomContext context, ReadOnlySpan<ScriptValue> args)
    {
        if (args.Length == 0)
            return null;

        if (args.Length == 1)
            return ConvertToNode(context, args[0]);

        var fragment = context.Document.CreateFragment();
        for (var i = 0; i < args.Length; i++)
        {
            fragment.AppendChild(ConvertToNode(context, args[i]));
        }
        return fragment;
    }

    private static void NotifyConnectedIframes(DomContext context, Node node)
    {
        if (node is ElementNode el && el.LocalName == "iframe")
        {
            var iframe = context.GetIframe(el);
            iframe.ElementObject = context.Wrap(el);
            iframe.LoadInlineDocument();
        }
        if (node is ContainerNode container)
        {
            for (var c = container.FirstChild; c is not null; c = c.NextSibling)
                NotifyConnectedIframes(context, c);
        }
    }

    public static ScriptValue AppendChild(DomContext context, ContainerNode parent, ScriptValue childVal)
    {
        var node = context.UnwrapNode(childVal);
        EnsurePreInsertionValidity(parent, node, null);
        parent.AppendChild(node);
        NotifyConnectedIframes(context, node);
        return childVal;
    }

    public static ScriptValue InsertBefore(DomContext context, ContainerNode parent, ScriptValue nodeVal, ScriptValue childVal)
    {
        var node = context.UnwrapNode(nodeVal);
        var child = context.UnwrapNodeOptional(childVal);
        EnsurePreInsertionValidity(parent, node, child);
        parent.InsertBefore(node, child);
        NotifyConnectedIframes(context, node);
        return nodeVal;
    }

    public static ScriptValue RemoveChild(DomContext context, ContainerNode parent, ScriptValue childVal)
    {
        var child = context.UnwrapNode(childVal);
        EnsurePreRemoveValidity(parent, child);
        parent.RemoveChild(child);
        return childVal;
    }

    public static ScriptValue ReplaceChild(DomContext context, ContainerNode parent, ScriptValue nodeVal, ScriptValue childVal)
    {
        var node = context.UnwrapNode(nodeVal);
        var child = context.UnwrapNode(childVal);
        EnsurePreReplaceValidity(parent, node, child);
        parent.ReplaceChild(node, child);
        return childVal;
    }

    public static void Append(DomContext context, ContainerNode parent, ReadOnlySpan<ScriptValue> args)
    {
        if (ConvertMutationNodes(context, args) is { } node)
        {
            EnsurePreInsertionValidity(parent, node, null);
            parent.AppendChild(node);
        }
    }

    public static void Prepend(DomContext context, ContainerNode parent, ReadOnlySpan<ScriptValue> args)
    {
        if (ConvertMutationNodes(context, args) is { } node)
        {
            EnsurePreInsertionValidity(parent, node, parent.FirstChild);
            parent.InsertBefore(node, parent.FirstChild);
        }
    }

    public static void ReplaceChildren(DomContext context, ContainerNode parent, ReadOnlySpan<ScriptValue> args)
    {
        var node = ConvertMutationNodes(context, args);
        if (node is not null)
            EnsurePreInsertionValidity(parent, node, null);

        while (parent.FirstChild is { } c)
            parent.RemoveChild(c);

        if (node is not null)
            parent.AppendChild(node);
    }

    public static void Remove(Node node)
    {
        if (node.Parent is { } parent)
            parent.RemoveChild(node);
    }

    public static void Before(DomContext context, Node node, ReadOnlySpan<ScriptValue> args)
    {
        if (node.Parent is { } parent && ConvertMutationNodes(context, args) is { } toInsert)
        {
            EnsurePreInsertionValidity(parent, toInsert, node);
            parent.InsertBefore(toInsert, node);
        }
    }

    public static void After(DomContext context, Node node, ReadOnlySpan<ScriptValue> args)
    {
        if (node.Parent is { } parent && ConvertMutationNodes(context, args) is { } toInsert)
        {
            EnsurePreInsertionValidity(parent, toInsert, node.NextSibling);
            parent.InsertBefore(toInsert, node.NextSibling);
        }
    }

    public static void ReplaceWith(DomContext context, Node node, ReadOnlySpan<ScriptValue> args)
    {
        if (node.Parent is { } parent)
        {
            var next = node.NextSibling;
            var toInsert = ConvertMutationNodes(context, args);
            if (toInsert is not null)
                EnsurePreInsertionValidity(parent, toInsert, next);

            parent.RemoveChild(node);
            if (toInsert is not null)
                parent.InsertBefore(toInsert, next);
        }
    }
}

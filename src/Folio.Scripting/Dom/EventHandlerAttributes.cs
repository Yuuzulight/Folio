using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Keeps event handler content attributes (<c>onclick="..."</c>) and their handlers in step (HTML: event handlers, #457).
/// The handlers themselves live in the script-level events code; this tells it which attributes exist and when they change.
/// </summary>
internal sealed class EventHandlerAttributes(IScriptEngine engine, ScriptValue sync, ScriptValue ensure)
{
    /// <summary>An attribute was set (<paramref name="value"/>) or removed (null): its handler is replaced or removed.</summary>
    public void Changed(ScriptValue element, string name, string? value) =>
        engine.Call(sync, ScriptValue.Undefined, [element, ScriptValue.FromString(name), value is null ? ScriptValue.Null : ScriptValue.FromString(value)]);

    /// <summary>
    /// Makes handlers of the on* attributes of <paramref name="root"/> and its descendants, except those that already have
    /// one, so an element parsed or inserted before any script ran has its handler registered in the right position.
    /// </summary>
    public void Scan(Node root, DomContext context)
    {
        if (root is ElementNode element)
            ScanElement(element, context);

        if (root is ContainerNode container)
        {
            foreach (var descendant in container.Descendants.OfType<ElementNode>())
                ScanElement(descendant, context);
        }
    }

    private void ScanElement(ElementNode element, DomContext context)
    {
        foreach (var attribute in element.Attributes)
        {
            var name = element.OwnerDocument.TextOf(attribute.Name);
            if (name.Length < 3 || name[0] is not ('o' or 'O') || name[1] is not ('n' or 'N'))
                continue;

            engine.Call(ensure, ScriptValue.Undefined,
                [context.Wrap(element), ScriptValue.FromString(name), ScriptValue.FromString(attribute.Value)]);
        }
    }
}

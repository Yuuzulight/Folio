using Folio.Css;
using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>
/// Helper methods for DOM selector matching and querying according to WHATWG DOM §4.2.6 / §4.2.9.
/// </summary>
internal static class DomSelectors
{
    public static SelectorList ParseSelectorList(DocumentNode doc, ScriptValue selectorVal)
    {
        var str = WebIdlConversions.ToDOMString(selectorVal);
        if (string.IsNullOrWhiteSpace(str))
            throw WebIdlException.DomException(DomExceptionNames.SyntaxError, "The provided selector string is empty.");

        var (source, values) = CssParser.ParseComponentValues(str);
        return SelectorParser.Parse(source, values, doc.Intern)
            ?? throw WebIdlException.DomException(DomExceptionNames.SyntaxError, $"'{str}' is not a valid selector.");
    }

    public static ElementNode? QuerySelector(ContainerNode scope, ScriptValue selectorVal)
    {
        var selectorList = ParseSelectorList(scope.OwnerDocument, selectorVal);
        var context = new MatchContext();
        for (var node = scope.NextInTree(scope); node is not null; node = node.NextInTree(scope))
        {
            if (node is ElementNode el && SelectorMatcher.Matches(selectorList, el, context))
                return el;
        }
        return null;
    }

    public static List<Node> QuerySelectorAll(ContainerNode scope, ScriptValue selectorVal)
    {
        var selectorList = ParseSelectorList(scope.OwnerDocument, selectorVal);
        var context = new MatchContext();
        var results = new List<Node>();
        for (var node = scope.NextInTree(scope); node is not null; node = node.NextInTree(scope))
        {
            if (node is ElementNode el && SelectorMatcher.Matches(selectorList, el, context))
                results.Add(el);
        }
        return results;
    }

    public static bool Matches(ElementNode element, ScriptValue selectorVal)
    {
        var selectorList = ParseSelectorList(element.OwnerDocument, selectorVal);
        var context = new MatchContext();
        return SelectorMatcher.Matches(selectorList, element, context);
    }

    public static ElementNode? Closest(ElementNode element, ScriptValue selectorVal)
    {
        var selectorList = ParseSelectorList(element.OwnerDocument, selectorVal);
        var context = new MatchContext();
        for (Node? curr = element; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el && SelectorMatcher.Matches(selectorList, el, context))
                return el;
        }
        return null;
    }
}

using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed class CssStyleSheetState(ElementNode source)
{
    private string _sourceText = source.TextContent ?? "";
    public ElementNode Source { get; } = source;
    public List<string> Rules { get; } = Parse(source.TextContent ?? "");

    public void RefreshFromSource()
    {
        var text = Source.TextContent ?? "";
        if (text == _sourceText)
            return;
        Rules.Clear();
        Rules.AddRange(Parse(text));
        _sourceText = text;
    }

    private static List<string> Parse(string text) => text.Split('}', StringSplitOptions.RemoveEmptyEntries)
        .Select(rule => rule.Trim() + "}").Where(rule => rule.Contains('{')).ToList();
}

internal sealed class CssRuleState(string cssText)
{
    public string CssText { get; set; } = cssText;
}

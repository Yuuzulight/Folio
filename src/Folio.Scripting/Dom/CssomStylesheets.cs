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

internal sealed class CssRuleState
{
    public CssRuleState(string cssText)
    {
        CssText = cssText;
        SelectorText = cssText[..Math.Max(0, cssText.IndexOf('{'))].Trim();
        Style = new CssRuleStyleState(cssText);
    }

    public string CssText { get; set; }
    public string SelectorText { get; }
    public CssRuleStyleState Style { get; }
}

internal sealed class CssRuleStyleState(string cssText)
{
    public Dictionary<string, string> Properties { get; } = Parse(cssText);

    private static Dictionary<string, string> Parse(string text) =>
        text.Contains('{')
            ? text[(text.IndexOf('{') + 1)..].TrimEnd('}', ' ').Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split(':', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
}

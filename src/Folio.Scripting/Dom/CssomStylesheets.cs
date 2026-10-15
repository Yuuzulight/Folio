using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed class CssStyleSheetState(string text)
{
    public List<string> Rules { get; } = Parse(text);

    private static List<string> Parse(string text) => text.Split('}', StringSplitOptions.RemoveEmptyEntries)
        .Select(rule => rule.Trim() + "}").Where(rule => rule.Contains('{')).ToList();
}

internal sealed class CssRuleState(string cssText)
{
    public string CssText { get; set; } = cssText;
}

using System.Text;
using Folio.Css;
using Folio.Dom;

namespace Folio.Layout;

/// <summary>
/// CSS counters during box building (https://www.w3.org/TR/css-lists-3/#auto-numbering): each counter is in scope
/// for its originating element, that element's descendants and its following siblings.
/// </summary>
internal sealed class CounterState
{
    private sealed class Counter(Node origin, int value, bool reversed)
    {
        public Node Origin { get; } = origin;
        public int Value { get; set; } = value;
        public bool Reversed { get; } = reversed;
    }

    private readonly Dictionary<string, List<Counter>> _counters = new(StringComparer.Ordinal);

    /// <summary>
    /// Applies an element's (or pseudo-element's) counter properties: reset, then increment, then set. List items
    /// also increment list-item by 1 (by -1 for a reversed counter) unless counter-increment names it.
    /// </summary>
    /// <param name="reversedStart">Computes the start of a reversed counter reset without a value.</param>
    public void Apply(Node origin, IReadOnlyList<CounterChange> reset, IReadOnlyList<CounterChange> increment,
                      IReadOnlyList<CounterChange> set, bool isListItem, Func<string, int>? reversedStart = null)
    {
        foreach (var change in reset)
            Instantiate(change.Name, origin, change.Value ?? reversedStart?.Invoke(change.Name) ?? 0, change.Reversed);
        foreach (var change in increment)
            Innermost(change.Name, origin).Value += change.Value ?? 1;
        if (isListItem && !increment.Any(c => c.Name == "list-item"))
        {
            var listItem = Innermost("list-item", origin);
            listItem.Value += listItem.Reversed ? -1 : 1;
        }
        foreach (var change in set)
            Innermost(change.Name, origin).Value = change.Value ?? 0;
    }

    /// <summary>Called after an element's subtree: counters its children created go out of scope.</summary>
    public void Leave(Node element)
    {
        foreach (var stack in _counters.Values)
        {
            while (stack.Count > 0 && stack[^1].Origin.Parent == element)
                stack.RemoveAt(stack.Count - 1);
        }
    }

    /// <summary>The innermost value, instantiating the counter at <paramref name="origin"/> if none is in scope.</summary>
    public int Value(string name, Node origin) => Innermost(name, origin).Value;

    /// <summary>All values in scope, outermost first (for counters()).</summary>
    public IReadOnlyList<int> Values(string name, Node origin)
    {
        Innermost(name, origin);
        return _counters[name].Select(c => c.Value).ToList();
    }

    private Counter Innermost(string name, Node origin) =>
        _counters.TryGetValue(name, out var stack) && stack.Count > 0 ? stack[^1] : Instantiate(name, origin, 0, false);

    // https://www.w3.org/TR/css-lists-3/#instantiate-counter: a new counter replaces one from itself or a previous sibling.
    private Counter Instantiate(string name, Node origin, int value, bool reversed)
    {
        if (!_counters.TryGetValue(name, out var stack))
            _counters[name] = stack = [];
        if (stack.Count > 0 && (stack[^1].Origin == origin || stack[^1].Origin.Parent == origin.Parent))
            stack.RemoveAt(stack.Count - 1);
        var counter = new Counter(origin, value, reversed);
        stack.Add(counter);
        return counter;
    }
}

/// <summary>The predefined counter styles Folio renders (https://www.w3.org/TR/css-counter-styles-3/#predefined-counters).</summary>
internal static class CounterStyles
{
    /// <summary>A counter value in a style; unknown styles use decimal, as the spec requires.</summary>
    public static string Format(int value, string style)
    {
        switch (style)
        {
            case "none":
                return "";
            case "disc":
                return "•";
            case "circle":
                return "◦";
            case "square":
                return "▪";
            case "decimal-leading-zero":
                return value is >= 0 and < 10 ? "0" + value : Decimal(value);
            case "lower-alpha" or "lower-latin":
                return Alphabetic(value, "abcdefghijklmnopqrstuvwxyz") ?? Decimal(value);
            case "upper-alpha" or "upper-latin":
                return Alphabetic(value, "ABCDEFGHIJKLMNOPQRSTUVWXYZ") ?? Decimal(value);
            case "lower-greek":
                return Alphabetic(value, "αβγδεζηθικλμνξοπρστυφχψω") ?? Decimal(value);
            case "lower-roman":
                return Roman(value)?.ToLowerInvariant() ?? Decimal(value);
            case "upper-roman":
                return Roman(value) ?? Decimal(value);
            default:
                return Decimal(value);
        }
    }

    /// <summary>A list marker's text: the counter formatted with the style's suffix (". ", or a space for bullets).</summary>
    public static string Marker(int value, ListStyleType type)
    {
        if (type.Symbol is { } symbol)
            return symbol;
        if (type.CounterStyle is not { } style || style == "none")
            return "";
        var text = Format(value, style);
        return style is "disc" or "circle" or "square" ? text + " " : text + ". ";
    }

    private static string Decimal(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Alphabetic systems start at 1: a..z, aa, ab, ...
    private static string? Alphabetic(int value, string symbols)
    {
        if (value < 1)
            return null;
        var result = new StringBuilder();
        while (value > 0)
        {
            value--;
            result.Insert(0, symbols[value % symbols.Length]);
            value /= symbols.Length;
        }
        return result.ToString();
    }

    // Additive roman numerals for 1..3999.
    private static string? Roman(int value)
    {
        if (value is < 1 or > 3999)
            return null;
        (int Value, string Symbol)[] table =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];
        var result = new StringBuilder();
        foreach (var (v, s) in table)
        {
            while (value >= v)
            {
                result.Append(s);
                value -= v;
            }
        }
        return result.ToString();
    }
}

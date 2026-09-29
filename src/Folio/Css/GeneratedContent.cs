namespace Folio.Css;

/// <summary>One part of a <c>content</c> value (https://www.w3.org/TR/css-content-3/#content-property).</summary>
internal abstract record ContentItem;

internal sealed record ContentString(string Text) : ContentItem;

internal sealed record ContentAttr(string Name) : ContentItem;

internal sealed record ContentCounter(string Name, string Style) : ContentItem;

internal sealed record ContentCounters(string Name, string Separator, string Style) : ContentItem;

/// <summary><c>open-quote</c>, <c>close-quote</c>, <c>no-open-quote</c>, <c>no-close-quote</c>.</summary>
internal sealed record ContentQuote(bool Open, bool Emit) : ContentItem;

internal enum ContentKind
{
    Normal,
    None,
    Items,
}

internal sealed record ContentValue(ContentKind Kind, IReadOnlyList<ContentItem> Items)
{
    public static ContentValue Normal { get; } = new(ContentKind.Normal, []);
    public static ContentValue None { get; } = new(ContentKind.None, []);

    public override string ToString() => Kind switch
    {
        ContentKind.Normal => "normal",
        ContentKind.None => "none",
        _ => string.Join(" ", Items.Select(i => i switch
        {
            ContentString s => $"\"{s.Text}\"",
            ContentAttr a => $"attr({a.Name})",
            ContentCounter c => $"counter({c.Name}, {c.Style})",
            ContentCounters c => $"counters({c.Name}, \"{c.Separator}\", {c.Style})",
            ContentQuote { Open: true, Emit: true } => "open-quote",
            ContentQuote { Open: false, Emit: true } => "close-quote",
            ContentQuote { Open: true } => "no-open-quote",
            _ => "no-close-quote",
        })),
    };
}

internal sealed record ContentSpecified(ContentValue Content) : CssValue;

/// <summary>An entry of <c>counter-reset</c>, <c>counter-increment</c> or <c>counter-set</c> (https://www.w3.org/TR/css-lists-3/#auto-numbering).</summary>
/// <param name="Value">Null for a reversed counter reset without a value (its start is computed from the list).</param>
internal readonly record struct CounterChange(string Name, int? Value, bool Reversed = false)
{
    public override string ToString() => (Reversed ? $"reversed({Name})" : Name) + (Value is { } v ? $" {v}" : "");
}

internal sealed record CounterListValue(IReadOnlyList<CounterChange> Changes) : CssValue
{
    public override string ToString() => Changes.Count == 0 ? "none" : string.Join(" ", Changes);
}

/// <summary><c>list-style-type</c>: a counter style name, a string, or none (both null).</summary>
internal readonly record struct ListStyleType(string? CounterStyle, string? Symbol)
{
    public static ListStyleType None => default;

    public override string ToString() => CounterStyle ?? (Symbol is { } s ? $"\"{s}\"" : "none");
}

internal sealed record ListStyleTypeValue(ListStyleType Type) : CssValue;

internal static class GeneratedContentParsing
{
    private static readonly string[] Reserved = ["none", "initial", "inherit", "unset", "revert", "revert-layer", "default"];

    /// <summary><c>content</c>: normal | none | [ string | counter() | counters() | attr() | quotes ]+ [ / alt ]?</summary>
    public static CssValue? Content(ValueReader r)
    {
        if (r.Keyword("normal") is not null)
            return new ContentSpecified(ContentValue.Normal);
        if (r.Keyword("none") is not null)
            return new ContentSpecified(ContentValue.None);

        var items = new List<ContentItem>();
        while (!r.AtEnd)
        {
            if (r.Delim('/'))
            {
                // Alternative text for speech; not rendered.
                while (!r.AtEnd && r.OneValue() is not null)
                {
                }
                break;
            }
            if (r.String() is { } text)
                items.Add(new ContentString(text));
            else if (r.Keyword("open-quote", "close-quote", "no-open-quote", "no-close-quote") is { } quote)
                items.Add(new ContentQuote(quote.Contains("open"), !quote.StartsWith("no-", StringComparison.Ordinal)));
            else if (r.Function("attr") is { } attr && attr.Ident() is { } attrName && attr.AtEnd)
                items.Add(new ContentAttr(attrName));
            else if (r.Function("counter") is { } counter && CounterArgs(counter, withSeparator: false) is { } c)
                items.Add(new ContentCounter(c.Name, c.Style));
            else if (r.Function("counters") is { } counters && CounterArgs(counters, withSeparator: true) is { } cs)
                items.Add(new ContentCounters(cs.Name, cs.Separator!, cs.Style));
            else
                return null;
        }
        return items.Count == 0 ? null : new ContentSpecified(new ContentValue(ContentKind.Items, items));
    }

    private static (string Name, string? Separator, string Style)? CounterArgs(ValueReader r, bool withSeparator)
    {
        if (r.Ident() is not { } name || Reserved.Contains(name.ToLowerInvariant()))
            return null;
        string? separator = null;
        if (withSeparator)
        {
            if (!r.Comma() || r.String() is not { } s)
                return null;
            separator = s;
        }
        var style = "decimal";
        if (r.Comma())
        {
            if (r.Ident() is not { } styleName)
                return null;
            style = styleName.ToLowerInvariant();
        }
        return r.AtEnd ? (name, separator, style) : null;
    }

    /// <summary>counter-reset / counter-increment / counter-set: none | [ name integer? ]+ (reset also takes reversed()).</summary>
    public static CssValue? Counters(ValueReader r, int defaultValue, bool allowReversed)
    {
        if (r.Keyword("none") is not null)
            return new CounterListValue([]);
        var changes = new List<CounterChange>();
        while (!r.AtEnd)
        {
            var reversed = false;
            string? name;
            if (allowReversed && r.Function("reversed") is { } fn)
            {
                name = fn.Ident();
                if (name is null || !fn.AtEnd)
                    return null;
                reversed = true;
            }
            else
            {
                name = r.Ident();
            }
            if (name is null || Reserved.Contains(name.ToLowerInvariant()))
                return null;
            var value = r.Integer();
            changes.Add(new CounterChange(name, value ?? (reversed ? null : defaultValue), reversed));
        }
        return changes.Count == 0 ? null : new CounterListValue(changes);
    }

    /// <summary>list-style-type: a counter style name, a string, or none.</summary>
    public static CssValue? ListStyleType(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new ListStyleTypeValue(Css.ListStyleType.None);
        if (r.String() is { } symbol)
            return new ListStyleTypeValue(new ListStyleType(null, symbol));
        if (r.Ident() is { } name && !Reserved.Contains(name.ToLowerInvariant()))
            return new ListStyleTypeValue(new ListStyleType(name.ToLowerInvariant(), null));
        return null;
    }
}

using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

internal enum CssomKind
{
    Style, Media, Supports, Container, LayerBlock, LayerStatement, Scope, StartingStyle,
    Import, FontFace, Page, Keyframes, Keyframe, Namespace, Property, NestedDeclarations,
}

/// <summary>
/// One rule of a CSSOM style sheet (https://drafts.csswg.org/cssom/#css-rules): a parsed, validated copy of what Folio's
/// parser kept, with the CSSOM's serialisation (<see cref="CssText"/>). The text of the owning element is not the model:
/// edits here are written back to the element's <see cref="ElementNode.SheetText"/> override as a whole sheet.
/// </summary>
internal sealed class CssomRule(int id, CssomKind kind)
{
    public int Id { get; } = id;
    public CssomKind Kind { get; } = kind;

    /// <summary>The prelude as the CSSOM prints it: a selector list, a condition, a layer or keyframes name, a page selector, a keyframe selector.</summary>
    public string Prelude { get; set; } = "";

    public List<LiveStyleDeclaration.Entry> Declarations { get; set; } = [];
    public List<CssomRule> Children { get; } = [];

    // @import and @namespace
    public string Href { get; set; } = "";
    public string MediaText { get; set; } = "";
    public string? LayerName { get; set; }
    public string? SupportsText { get; set; }
    public string Prefix { get; set; } = "";
    public string[] LayerNames { get; set; } = [];

    /// <summary>The owning sheet or rule; null once the rule has been deleted.</summary>
    public CssomRule? ParentRule { get; set; }
    public CssomSheet? Sheet { get; set; }

    public bool IsGrouping => Kind is CssomKind.Style or CssomKind.Media or CssomKind.Supports or CssomKind.Container or CssomKind.LayerBlock or CssomKind.Scope or CssomKind.StartingStyle;

    /// <summary>The legacy numeric type of CSSOM §6.4.2 (0 for the rule types added since).</summary>
    public int Type => Kind switch
    {
        CssomKind.Style => 1, CssomKind.Import => 3, CssomKind.Media => 4, CssomKind.FontFace => 5, CssomKind.Page => 6,
        CssomKind.Keyframes => 7, CssomKind.Keyframe => 8, CssomKind.Namespace => 10, CssomKind.Supports => 12, _ => 0,
    };

    public string CssText => Kind switch
    {
        CssomKind.Style => Block(Prelude, includeChildren: true),
        CssomKind.FontFace => Block("@font-face", false),
        CssomKind.Page => Block(Prelude.Length > 0 ? "@page " + Prelude : "@page", false),
        CssomKind.Keyframe => Block(Prelude, false),
        CssomKind.Property => Block("@property " + Prelude, false),
        CssomKind.Media => Group("@media " + Prelude),
        CssomKind.Supports => Group("@supports " + Prelude),
        CssomKind.Container => Group("@container " + Prelude),
        CssomKind.LayerBlock => Group(Prelude.Length > 0 ? "@layer " + Prelude : "@layer"),
        CssomKind.Scope => Group(Prelude.Length > 0 ? "@scope " + Prelude : "@scope"),
        CssomKind.StartingStyle => Group("@starting-style"),
        CssomKind.Keyframes => Group("@keyframes " + Prelude),
        CssomKind.Import => ImportText(),
        CssomKind.Namespace => $"@namespace {(Prefix.Length > 0 ? Prefix + " " : "")}url(\"{Href}\");",
        CssomKind.LayerStatement => "@layer " + string.Join(", ", LayerNames) + ";",
        CssomKind.NestedDeclarations => DeclarationText(),
        _ => "",
    };

    public string DeclarationText() => string.Join(" ", Declarations.Select(d => $"{d.Name}: {d.Value}{(d.Important ? " !important" : "")};"));

    private string Block(string header, bool includeChildren)
    {
        var parts = new List<string>();
        if (Declarations.Count > 0)
            parts.Add(DeclarationText());
        if (includeChildren)
            parts.AddRange(Children.Select(c => c.CssText));
        return parts.Count == 0 ? header + " { }" : header + " { " + string.Join(" ", parts) + " }";
    }

    private string Group(string header) =>
        Children.Count == 0 ? header + " {\n}" : header + " {\n" + string.Join("\n", Children.Select(c => Indent(c.CssText))) + "\n}";

    private static string Indent(string text) => "  " + text.Replace("\n", "\n  ");

    private string ImportText()
    {
        var text = new StringBuilder($"@import url(\"{Href}\")");
        if (LayerName is not null)
            text.Append(LayerName.Length > 0 ? $" layer({LayerName})" : " layer");
        if (SupportsText is not null)
            text.Append($" supports({SupportsText})");
        if (MediaText.Length > 0)
            text.Append(' ').Append(MediaText);
        return text.Append(';').ToString();
    }

    /// <summary>This rule and everything under it.</summary>
    public IEnumerable<CssomRule> Descendants()
    {
        yield return this;
        foreach (var child in Children)
        {
            foreach (var inner in child.Descendants())
                yield return inner;
        }
    }
}

/// <summary>
/// A style or link element's sheet: its rules, kept in step with the element's text (or the loaded file) and written back
/// as an override when the page edits them.
/// </summary>
internal sealed class CssomSheet(int id, ElementNode owner)
{
    public int Id { get; } = id;
    public ElementNode Owner { get; } = owner;
    public List<CssomRule> Rules { get; } = [];

    /// <summary>What the rules were parsed from or last written as; a different text from the element means they are parsed again.</summary>
    public string? Text { get; set; }
}

/// <summary>Reads CSS text into <see cref="CssomRule"/>s with Folio's parser and prints component values the CSSOM's way.</summary>
internal static class CssomParser
{
    public static List<CssomRule> Parse(string text, Func<CssomKind, CssomRule> create, int maxRules)
    {
        var sheet = CssParser.ParseStyleSheet(text, maxRules);
        var rules = new List<CssomRule>();
        foreach (var rule in sheet.Rules)
        {
            if (Convert(rule, sheet.Source, create, nested: false, inKeyframes: false, null) is { } converted)
                rules.Add(converted);
        }
        return rules;
    }

    /// <summary>Parses one rule (CSSOM "parse a rule"); null when the text is not exactly one valid rule.</summary>
    public static CssomRule? ParseOne(string text, Func<CssomKind, CssomRule> create, bool nested, bool inKeyframes)
    {
        var sheet = CssParser.ParseStyleSheet(text.Trim());
        // A rule with an unknown at-rule name or an invalid selector is dropped by the parser's recovery: no rule, a syntax error.
        if (sheet.Rules.Count != 1)
            return null;
        return Convert(sheet.Rules[0], sheet.Source, create, nested, inKeyframes, null);
    }

    private static CssomRule? Convert(CssRule rule, string source, Func<CssomKind, CssomRule> create, bool nested, bool inKeyframes, CssomRule? parent)
    {
        switch (rule)
        {
            case StyleRule style:
            {
                if (inKeyframes)
                {
                    var key = KeyframeSelector(Print(style.Prelude, source, Mode.Plain));
                    if (key is null)
                        return null;
                    var keyframe = create(CssomKind.Keyframe);
                    keyframe.Prelude = key;
                    keyframe.Declarations = Declarations(style.Declarations, source, validate: true);
                    return keyframe;
                }
                if (SelectorParser.Parse(source, style.Prelude, _ => new Atom(1), null, nested) is null)
                    return null;
                var result = create(CssomKind.Style);
                result.Prelude = Print(style.Prelude, source, Mode.Selector);
                result.Declarations = Declarations(style.Declarations, source, validate: true);
                AddChildren(result, style.Rules, source, create, nested: true, inKeyframes: false);
                return result;
            }
            case NestedDeclarations nestedDeclarations:
            {
                var result = create(CssomKind.NestedDeclarations);
                result.Declarations = Declarations(nestedDeclarations.Declarations, source, validate: true);
                return result;
            }
            case AtRule at:
                return ConvertAtRule(at, source, create, nested);
            default:
                return null;
        }
    }

    private static CssomRule? ConvertAtRule(AtRule at, string source, Func<CssomKind, CssomRule> create, bool nested)
    {
        var name = at.Name.ToLowerInvariant();
        var prelude = at.Prelude;
        switch (name)
        {
            case "media" when at.HasBlock:
            case "supports" when at.HasBlock:
            case "container" when at.HasBlock:
            case "scope" when at.HasBlock:
            {
                var kind = name switch { "media" => CssomKind.Media, "supports" => CssomKind.Supports, "container" => CssomKind.Container, _ => CssomKind.Scope };
                var result = create(kind);
                result.Prelude = Print(prelude, source, kind is CssomKind.Media or CssomKind.Supports or CssomKind.Container ? Mode.Condition : Mode.Plain);
                AddChildren(result, at.Rules, source, create, nested, inKeyframes: false);
                AddGroupDeclarations(result, at, source, create);
                return result;
            }
            case "starting-style" when at.HasBlock:
            {
                var result = create(CssomKind.StartingStyle);
                AddChildren(result, at.Rules, source, create, nested, inKeyframes: false);
                AddGroupDeclarations(result, at, source, create);
                return result;
            }
            case "layer":
            {
                var names = Print(prelude, source, Mode.Plain).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (at.HasBlock)
                {
                    var result = create(CssomKind.LayerBlock);
                    result.Prelude = names.Length > 0 ? names[0] : "";
                    AddChildren(result, at.Rules, source, create, nested, inKeyframes: false);
                    return result;
                }
                if (names.Length == 0)
                    return null;
                var statement = create(CssomKind.LayerStatement);
                statement.LayerNames = names;
                return statement;
            }
            case "import" when !at.HasBlock:
                return ConvertImport(at, source, create);
            case "font-face" when at.HasBlock:
            {
                var result = create(CssomKind.FontFace);
                result.Declarations = Declarations(at.Declarations, source, validate: false);
                return result;
            }
            case "page" when at.HasBlock:
            {
                var result = create(CssomKind.Page);
                result.Prelude = Print(prelude, source, Mode.Plain);
                result.Declarations = Declarations(at.Declarations, source, validate: false);
                return result;
            }
            case "keyframes" or "-webkit-keyframes" when at.HasBlock:
            {
                var keyframeName = Print(prelude, source, Mode.Plain);
                if (keyframeName.Length == 0)
                    return null;
                var result = create(CssomKind.Keyframes);
                result.Prelude = keyframeName;
                AddChildren(result, at.Rules, source, create, nested: false, inKeyframes: true);
                return result;
            }
            case "namespace" when !at.HasBlock:
            {
                var parts = prelude.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
                string prefix = "", uri;
                if (parts is [PreservedToken { Token.Kind: CssTokenKind.Ident } p, var target])
                {
                    prefix = p.Token.Value;
                    uri = UrlOf(target) ?? "";
                }
                else if (parts is [var only])
                    uri = UrlOf(only) ?? "";
                else
                    return null;
                if (uri.Length == 0)
                    return null;
                var result = create(CssomKind.Namespace);
                result.Prefix = prefix;
                result.Href = uri;
                return result;
            }
            case "property" when at.HasBlock:
            {
                var result = create(CssomKind.Property);
                result.Prelude = Print(prelude, source, Mode.Plain);
                result.Declarations = Declarations(at.Declarations, source, validate: false);
                return result;
            }
            default:
                return null; // @charset is not a rule, and an unknown at-rule is dropped, as in a browser
        }
    }

    private static CssomRule? ConvertImport(AtRule at, string source, Func<CssomKind, CssomRule> create)
    {
        var items = at.Prelude.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        if (items.Count == 0 || UrlOf(items[0]) is not { } href)
            return null;
        var result = create(CssomKind.Import);
        result.Href = href;
        var rest = new List<ComponentValue>();
        foreach (var item in items.Skip(1))
        {
            if (item is CssFunction { Name: var function } layerFunction && function.Equals("layer", StringComparison.OrdinalIgnoreCase))
                result.LayerName = Print(layerFunction.Arguments, source, Mode.Plain);
            else if (item is PreservedToken { Token: { Kind: CssTokenKind.Ident } layer } && layer.Value.Equals("layer", StringComparison.OrdinalIgnoreCase) && result.LayerName is null)
                result.LayerName = "";
            else if (item is CssFunction { Name: var supports } supportsFunction && supports.Equals("supports", StringComparison.OrdinalIgnoreCase))
                result.SupportsText = Print(supportsFunction.Arguments, source, Mode.Condition);
            else
                rest.Add(item);
        }
        result.MediaText = Print(rest, source, Mode.Condition);
        return result;
    }

    private static string? UrlOf(ComponentValue value) => value switch
    {
        PreservedToken { Token: { Kind: CssTokenKind.String or CssTokenKind.Url } token } => token.Value,
        CssFunction { Name: var name } function when name.Equals("url", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.FirstOrDefault(a => a is PreservedToken { Token.Kind: CssTokenKind.String }) is PreservedToken { Token: var token } => token.Value,
        _ => null,
    };

    private static void AddGroupDeclarations(CssomRule group, AtRule at, string source, Func<CssomKind, CssomRule> create)
    {
        // Declarations directly in a grouping at-rule inside a style rule (CSS Nesting) are kept as a nested declarations rule.
        if (at.Declarations.Count > 0)
        {
            var nested = create(CssomKind.NestedDeclarations);
            nested.Declarations = Declarations(at.Declarations, source, validate: true);
            if (nested.Declarations.Count > 0)
                group.Children.Insert(0, nested);
        }
    }

    private static void AddChildren(CssomRule parent, List<CssRule> rules, string source, Func<CssomKind, CssomRule> create, bool nested, bool inKeyframes)
    {
        foreach (var child in rules)
        {
            if (Convert(child, source, create, nested, inKeyframes, parent) is { } converted)
                parent.Children.Add(converted);
        }
    }

    // A declaration the cascade can't use (an unknown property, an invalid value) is dropped from a style rule, as browsers do.
    private static List<LiveStyleDeclaration.Entry> Declarations(List<Declaration> declarations, string source, bool validate)
    {
        var entries = new List<LiveStyleDeclaration.Entry>();
        foreach (var declaration in declarations)
        {
            if (declaration.Value.Count == 0 && !declaration.IsCustomProperty)
                continue;
            if (validate && !declaration.IsCustomProperty && CascadeData.Parse(source, [declaration]).Count == 0)
                continue;
            var value = declaration.IsCustomProperty ? CustomValue(declaration.Value, source) : Print(declaration.Value, source, Mode.Value);
            var index = entries.FindIndex(e => e.Name == declaration.Name);
            var entry = new LiveStyleDeclaration.Entry(declaration.Name, value, declaration.Important);
            if (index >= 0)
                entries.RemoveAt(index);
            entries.Add(entry);
        }
        return entries;
    }

    private static string CustomValue(List<ComponentValue> values, string source) =>
        values.Count == 0 ? "" : source[values[0].Start..values[^1].End].Trim();

    // https://drafts.csswg.org/css-animations-1/#typedef-keyframe-selector: from | to | <percentage>, comma separated.
    private static string? KeyframeSelector(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            if (lower is "from" or "to")
                continue;
            if (!lower.EndsWith('%') || !double.TryParse(lower[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent) || percent is < 0 or > 100)
                return null;
        }
        return string.Join(", ", parts.Select(p => p.ToLowerInvariant()));
    }

    public enum Mode { Plain, Selector, Condition, Value }

    /// <summary>Prints component values the CSSOM's way: single spaces, ", " after a comma, strings and urls in double quotes.</summary>
    public static string Print(List<ComponentValue> values, string source, Mode mode)
    {
        var text = new StringBuilder();
        var space = false;
        void Word(string word)
        {
            if (space && text.Length > 0 && text[^1] is not ('(' or '[') && !(mode == Mode.Condition && text[^1] == ' '))
                text.Append(' ');
            space = false;
            text.Append(word);
        }
        foreach (var value in values)
        {
            switch (value)
            {
                case PreservedToken { Token: var token }:
                    switch (token.Kind)
                    {
                        case CssTokenKind.Whitespace:
                            space = true;
                            break;
                        case CssTokenKind.Comma:
                            while (text.Length > 0 && text[^1] == ' ')
                                text.Length--;
                            text.Append(',');
                            space = true;
                            break;
                        case CssTokenKind.Colon when mode == Mode.Condition:
                            while (text.Length > 0 && text[^1] == ' ')
                                text.Length--;
                            text.Append(':');
                            space = true;
                            break;
                        case CssTokenKind.Delim when mode == Mode.Selector && token.Value is ">" or "+" or "~":
                            while (text.Length > 0 && text[^1] == ' ')
                                text.Length--;
                            text.Append(text.Length > 0 ? " " : "").Append(token.Value);
                            space = true;
                            break;
                        case CssTokenKind.String:
                            Word(Quote(token.Value));
                            break;
                        case CssTokenKind.Url:
                            Word($"url({Quote(token.Value)})");
                            break;
                        default:
                            Word(source[token.Start..token.End]);
                            break;
                    }
                    break;
                case CssFunction function:
                    Word(function.Name + "(" + Print(function.Arguments, source, mode == Mode.Selector ? Mode.Plain : mode) + ")");
                    break;
                case SimpleBlock block:
                {
                    var (open, close) = block.Open switch { CssTokenKind.LeftParen => ("(", ")"), CssTokenKind.LeftBracket => ("[", "]"), _ => ("{", "}") };
                    Word(open + Print(block.Contents, source, mode == Mode.Selector ? Mode.Plain : mode) + close);
                    break;
                }
            }
        }
        return text.ToString().Trim();
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\a ") + "\"";
}

using Folio.Dom;

namespace Folio.Css;

/// <summary>What media queries are evaluated against (docs/study/03-css-parsing-and-selectors.md, @media).</summary>
internal sealed record MediaContext(float Width, float Height, float DeviceScale = 1, bool DarkColorScheme = false)
{
    /// <summary>M1 resolves animations to their end state, so it reports reduced motion.</summary>
    public bool ReducedMotion { get; init; } = true;
}

/// <summary>
/// Media query lists (https://www.w3.org/TR/mediaqueries-4/) and <c>@supports</c> conditions
/// (https://www.w3.org/TR/css-conditional-3/#at-supports), evaluated with three-valued logic:
/// anything unknown counts as false.
/// </summary>
internal static class Conditions
{
    public static bool MediaMatches(string source, List<ComponentValue> prelude, MediaContext media)
    {
        var queries = Split(prelude, v => v is PreservedToken { Token.Kind: CssTokenKind.Comma });
        if (queries.Count == 1 && queries[0].Count == 0)
            return true; // an empty list matches
        return queries.Any(q => MediaQuery(source, q, media) == true);
    }

    public static bool MediaMatches(string text, MediaContext media)
    {
        var (source, values) = CssParser.ParseComponentValues(text);
        return MediaMatches(source, values, media);
    }

    // [ not | only ]? <media-type> [ and <media-condition-without-or> ]? | <media-condition>
    private static bool? MediaQuery(string source, List<ComponentValue> values, MediaContext media)
    {
        if (values.Count == 0)
            return false; // an empty query in a list is invalid and matches nothing
        var i = 0;
        var negate = false;
        if (values is [PreservedToken { Token.Kind: CssTokenKind.Ident } modifier, PreservedToken { Token.Kind: CssTokenKind.Ident }, ..]
            && (modifier.Token.IsIdent("not") || modifier.Token.IsIdent("only")))
        {
            negate = modifier.Token.IsIdent("not");
            i = 1;
        }
        if (values[i] is not PreservedToken { Token.Kind: CssTokenKind.Ident } type || type.Token.IsIdent("not"))
            return Condition(source, values, media, allowOr: true);

        bool? result = type.Token.Value.ToLowerInvariant() is "all" or "screen"; // print and the deprecated types match nothing
        if (++i < values.Count)
        {
            if (values[i] is not PreservedToken and || !and.Token.IsIdent("and") || i + 1 >= values.Count)
                return false;
            result = And(result, Condition(source, values[(i + 1)..], media, allowOr: false));
        }
        return negate ? !result : result;
    }

    private static List<List<ComponentValue>> Split(List<ComponentValue> values, Func<ComponentValue, bool> separator)
    {
        var parts = new List<List<ComponentValue>> { new() };
        foreach (var v in values)
        {
            if (separator(v))
                parts.Add([]);
            else if (v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace })
                parts[^1].Add(v);
        }
        return parts;
    }

    // <media-condition> / <supports-condition>: not <in-parens> | <in-parens> [ and | or <in-parens> ]*.
    private static bool? Condition(string source, List<ComponentValue> values, MediaContext? media, bool allowOr)
    {
        var items = values.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        if (items.Count == 0)
            return null;
        if (items[0] is PreservedToken n && n.Token.IsIdent("not"))
            return items.Count == 2 ? !InParens(source, items[1], media) : null;

        var result = InParens(source, items[0], media);
        string? op = null;
        for (var i = 1; i < items.Count; i += 2)
        {
            if (items[i] is not PreservedToken { Token.Kind: CssTokenKind.Ident } word || i + 1 >= items.Count)
                return null;
            var thisOp = word.Token.Value.ToLowerInvariant();
            if (thisOp is not ("and" or "or") || (op is not null && op != thisOp) || (thisOp == "or" && !allowOr))
                return null; // mixing and/or without parentheses is invalid
            op = thisOp;
            var next = InParens(source, items[i + 1], media);
            result = op == "and" ? And(result, next) : Or(result, next);
        }
        return result;
    }

    private static bool? And(bool? a, bool? b) => a == false || b == false ? false : a == true && b == true ? true : null;

    private static bool? Or(bool? a, bool? b) => a == true || b == true ? true : a == false && b == false ? false : null;

    private static bool? InParens(string source, ComponentValue value, MediaContext? media)
    {
        switch (value)
        {
            case SimpleBlock { Open: CssTokenKind.LeftParen } block:
                var inner = block.Contents.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
                if (inner.Count > 0 && (inner[0] is SimpleBlock || inner[0] is PreservedToken t && t.Token.IsIdent("not")))
                    return Condition(source, block.Contents, media, allowOr: true);
                if (inner.Count > 0 && inner.Skip(1).FirstOrDefault() is PreservedToken w && (w.Token.IsIdent("and") || w.Token.IsIdent("or")))
                    return Condition(source, block.Contents, media, allowOr: true);
                return media is null ? SupportsDeclaration(source, block.Contents) : Feature(block.Contents, media);
            case CssFunction f when media is null && f.Name.Equals("selector", StringComparison.OrdinalIgnoreCase):
                return SelectorParser.Parse(source, f.Arguments, name => new Atom(1)) is not null;
            default:
                return null; // <general-enclosed>
        }
    }

    // ---------------------------------------------------------------- @supports

    public static bool Supports(string source, List<ComponentValue> prelude) => Condition(source, prelude, null, allowOr: true) == true;

    private static bool SupportsDeclaration(string source, List<ComponentValue> contents)
    {
        var (blockSource, block) = CssParser.ParseBlockContents(Text(source, contents));
        if (block.Declarations is not [var declaration] || block.Rules.Count > 0)
            return false;
        return declaration.IsCustomProperty || Properties.Parse(blockSource, declaration) is not null;
    }

    private static string Text(string source, List<ComponentValue> values) =>
        values.Count == 0 ? "" : source[values[0].Start..values[^1].End];

    // ---------------------------------------------------------------- media features

    private static bool? Feature(List<ComponentValue> contents, MediaContext media)
    {
        var items = contents.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        if (items is [PreservedToken { Token.Kind: CssTokenKind.Ident } boolean])
            return BooleanFeature(boolean.Token.Value.ToLowerInvariant(), media);
        if (items is [PreservedToken { Token.Kind: CssTokenKind.Ident } name, PreservedToken { Token.Kind: CssTokenKind.Colon }, .. var rest])
            return PlainFeature(name.Token.Value.ToLowerInvariant(), rest, media);
        return RangeFeature(items, media);
    }

    private static bool? BooleanFeature(string name, MediaContext media) => name switch
    {
        "width" or "height" or "color" or "hover" or "any-hover" or "pointer" or "any-pointer" or "orientation"
            or "aspect-ratio" or "resolution" => true,
        "monochrome" or "grid" => false,
        "prefers-reduced-motion" => media.ReducedMotion,
        "prefers-color-scheme" => true,
        _ => null,
    };

    private static bool? PlainFeature(string name, List<ComponentValue> value, MediaContext media)
    {
        var prefix = name.StartsWith("min-", StringComparison.Ordinal) ? "min" : name.StartsWith("max-", StringComparison.Ordinal) ? "max" : null;
        var feature = prefix is null ? name : name[4..];
        var op = prefix switch { "min" => ">=", "max" => "<=", _ => "=" };

        if (value is [PreservedToken { Token.Kind: CssTokenKind.Ident } keyword] && prefix is null)
        {
            var k = keyword.Token.Value.ToLowerInvariant();
            return feature switch
            {
                "orientation" => k == (media.Height >= media.Width ? "portrait" : "landscape"),
                "prefers-color-scheme" => k == (media.DarkColorScheme ? "dark" : "light"),
                "prefers-reduced-motion" => k == (media.ReducedMotion ? "reduce" : "no-preference"),
                "hover" or "any-hover" => k == "hover",
                "pointer" or "any-pointer" => k == "fine",
                "scripting" => k == "none",
                _ => null,
            };
        }
        return Compare(feature, op, value, media);
    }

    // (width >= 600px), (400px < width < 800px), (600px <= width)
    private static bool? RangeFeature(List<ComponentValue> items, MediaContext media)
    {
        var parts = new List<object>(); // ComponentValue groups and operator strings
        var current = new List<ComponentValue>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is PreservedToken t && (t.Token.IsDelim('<') || t.Token.IsDelim('>') || t.Token.IsDelim('=')))
            {
                var op = t.Token.Value;
                if (op != "=" && i + 1 < items.Count && items[i + 1] is PreservedToken eq && eq.Token.IsDelim('=') && eq.Start == t.End)
                {
                    op += "=";
                    i++;
                }
                parts.Add(current);
                parts.Add(op);
                current = [];
            }
            else
            {
                current.Add(items[i]);
            }
        }
        parts.Add(current);

        static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };
        static string? Name(object part) =>
            part is List<ComponentValue> { Count: 1 } list && list[0] is PreservedToken { Token.Kind: CssTokenKind.Ident } n
                ? n.Token.Value.ToLowerInvariant()
                : null;

        switch (parts.Count)
        {
            case 3 when Name(parts[0]) is { } left:
                return Compare(left, (string)parts[1], (List<ComponentValue>)parts[2], media);
            case 3 when Name(parts[2]) is { } right:
                return Compare(right, Flip((string)parts[1]), (List<ComponentValue>)parts[0], media);
            case 5 when Name(parts[2]) is { } middle:
                return And(
                    Compare(middle, Flip((string)parts[1]), (List<ComponentValue>)parts[0], media),
                    Compare(middle, (string)parts[3], (List<ComponentValue>)parts[4], media));
            default:
                return null;
        }
    }

    // feature <op> value
    private static bool? Compare(string feature, string op, List<ComponentValue> value, MediaContext media)
    {
        float? actual, expected;
        switch (feature)
        {
            case "width" or "height":
                actual = feature == "width" ? media.Width : media.Height;
                expected = MediaLength(value, media);
                break;
            case "aspect-ratio":
                actual = media.Width / media.Height;
                expected = Ratio(value);
                break;
            case "resolution":
                actual = media.DeviceScale;
                expected = Resolution(value);
                break;
            default:
                return null;
        }
        if (expected is null)
            return null;
        const float epsilon = 1e-4f;
        return op switch
        {
            "=" => Math.Abs(actual!.Value - expected.Value) < epsilon,
            "<" => actual < expected - epsilon,
            "<=" => actual <= expected + epsilon,
            ">" => actual > expected + epsilon,
            ">=" => actual >= expected - epsilon,
            _ => null,
        };
    }

    // Lengths in media queries: em and rem use the initial font size (16px); viewport units use the viewport.
    private static float? MediaLength(List<ComponentValue> value, MediaContext media)
    {
        if (value is not [PreservedToken token])
            return null;
        if (token.Token.Kind == CssTokenKind.Number && token.Token.Number == 0)
            return 0;
        if (token.Token.Kind != CssTokenKind.Dimension || ValueReader.UnitOf(token.Token.Value) is not { } unit)
            return null;
        var n = (float)token.Token.Number;
        return unit switch
        {
            LengthUnit.Em or LengthUnit.Rem => n * 16,
            LengthUnit.Ex or LengthUnit.Ch => n * 8,
            LengthUnit.Vw => n * media.Width / 100,
            LengthUnit.Vh => n * media.Height / 100,
            LengthUnit.Vmin => n * Math.Min(media.Width, media.Height) / 100,
            LengthUnit.Vmax => n * Math.Max(media.Width, media.Height) / 100,
            _ => new Style.ComputeContext(Style.ComputedStyle.Initial, 16, media.Width, media.Height).ToPx(new Length(n, unit)),
        };
    }

    private static float? Ratio(List<ComponentValue> value)
    {
        if (value is [PreservedToken { Token.Kind: CssTokenKind.Number } single])
            return (float)single.Token.Number;
        if (value is [PreservedToken { Token.Kind: CssTokenKind.Number } a, PreservedToken slash, PreservedToken { Token.Kind: CssTokenKind.Number } b]
            && slash.Token.IsDelim('/') && b.Token.Number != 0)
            return (float)(a.Token.Number / b.Token.Number);
        return null;
    }

    private static float? Resolution(List<ComponentValue> value) =>
        value is [PreservedToken { Token.Kind: CssTokenKind.Dimension } d]
            ? d.Token.Value.ToLowerInvariant() switch
            {
                "dppx" or "x" => (float)d.Token.Number,
                "dpi" => (float)(d.Token.Number / 96),
                "dpcm" => (float)(d.Token.Number * 2.54 / 96),
                _ => null,
            }
            : null;
}

using System.Globalization;
using Folio.Css;

namespace Folio.Style;

/// <summary>
/// A registered custom property's <c>syntax</c> (https://www.w3.org/TR/css-properties-values-api-1/#syntax-strings):
/// alternatives separated by <c>|</c>, each a data type name or an identifier, optionally followed by <c>+</c>
/// (space-separated list) or <c>#</c> (comma-separated list). Null from <see cref="Parse"/> for <c>*</c> is not an
/// error: use <see cref="Universal"/>.
/// </summary>
internal sealed class PropertySyntax
{
    private readonly List<(string Type, bool IsIdent, char Multiplier)> _components;

    private PropertySyntax(List<(string, bool, char)> components) => _components = components;

    /// <summary><c>*</c>: any value, kept as tokens.</summary>
    public static PropertySyntax Universal { get; } = new([]);

    public bool IsUniversal => _components.Count == 0;

    private static readonly HashSet<string> DataTypes = new(StringComparer.Ordinal)
    {
        "length", "number", "percentage", "length-percentage", "color", "image", "url", "integer", "angle", "time",
        "resolution", "transform-function", "custom-ident", "transform-list",
    };

    /// <summary>The syntax, or null when the string is not a valid syntax string.</summary>
    public static PropertySyntax? Parse(string text)
    {
        text = text.Trim();
        if (text == "*")
            return Universal;
        var components = new List<(string, bool, char)>();
        foreach (var part in text.Split('|'))
        {
            var component = part.Trim();
            var multiplier = component.EndsWith('+') || component.EndsWith('#') ? component[^1] : '\0';
            if (multiplier != '\0')
                component = component[..^1];
            if (component.StartsWith('<') && component.EndsWith('>') && DataTypes.Contains(component[1..^1]))
            {
                // <transform-list> is already a list and takes no multiplier.
                if (component == "<transform-list>" && multiplier != '\0')
                    return null;
                components.Add((component[1..^1], false, multiplier));
            }
            else if (IsIdent(component) && component is not ("inherit" or "initial" or "unset" or "revert" or "revert-layer" or "default"))
            {
                components.Add((component, true, multiplier));
            }
            else
            {
                return null;
            }
        }
        return new PropertySyntax(components);
    }

    private static bool IsIdent(string text) =>
        text.Length > 0 && (char.IsAsciiLetter(text[0]) || text[0] is '_' or '-' || text[0] > 0x7F)
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' || c > 0x7F);

    /// <summary>
    /// The computed value of <paramref name="text"/> as text, or null when it does not match the syntax (the value is
    /// then invalid at computed-value time). Lengths become px, angles deg, times s; other types keep their tokens.
    /// </summary>
    /// <param name="context">What relative lengths resolve against; null accepts only absolute lengths
    /// (a registration's initial value must be computationally independent).</param>
    public string? Compute(string text, ComputeContext? context)
    {
        if (IsUniversal)
            return text;
        foreach (var (type, isIdent, multiplier) in _components)
        {
            if (Match(text, type, isIdent, multiplier, context) is { } computed)
                return computed;
        }
        return null;
    }

    /// <summary>
    /// The value <paramref name="p"/> of the way between two computed values
    /// (https://www.w3.org/TR/css-properties-values-api-1/#animation-behavior-of-custom-properties): colours in
    /// premultiplied sRGB; anything else number by number where the two have the same shape (the same tokens and
    /// functions, numbers with the same units), rounded where only an integer matches; null when they do not pair up or the result does not
    /// match the syntax, and the value then flips half way. Values of the universal syntax never interpolate.
    /// </summary>
    public string? Interpolate(string from, string to, double p)
    {
        if (IsUniversal)
            return null;
        if (SingleColor(from) is { } a && SingleColor(to) is { } b)
            return Interpolation.Lerp(a, b, p) is CssColor color ? Compute(color.ToString(), null) : null;
        var (sourceA, valuesA) = CssParser.ParseComponentValues(from);
        var (sourceB, valuesB) = CssParser.ParseComponentValues(to);
        var text = new System.Text.StringBuilder();
        var round = false;
        if (!Lerp(valuesA, valuesB))
            return null;
        if (Compute(text.ToString(), null) is { } value)
            return value;
        // An <integer> takes whole numbers: round the numbers that were integers at both ends.
        (text, round) = (text.Clear(), true);
        return Lerp(valuesA, valuesB) ? Compute(text.ToString(), null) : null;

        bool Lerp(List<ComponentValue> x, List<ComponentValue> y)
        {
            if (x.Count != y.Count)
                return false;
            for (var i = 0; i < x.Count; i++)
            {
                switch (x[i], y[i])
                {
                    case (PreservedToken { Token: var s }, PreservedToken { Token: var t })
                        when s.Kind == t.Kind && s.Kind is CssTokenKind.Number or CssTokenKind.Percentage or CssTokenKind.Dimension
                             && string.Equals(s.Value, t.Value, StringComparison.OrdinalIgnoreCase):
                        var value = s.Number + (t.Number - s.Number) * p;
                        if (round && s.Kind == CssTokenKind.Number && s.IsInteger && t.IsInteger)
                            value = Math.Round(value, MidpointRounding.AwayFromZero);
                        text.Append(N((float)value)).Append(s.Kind == CssTokenKind.Percentage ? "%" : s.Kind == CssTokenKind.Dimension ? s.Value : "");
                        break;
                    case (CssFunction f, CssFunction g) when string.Equals(f.Name, g.Name, StringComparison.OrdinalIgnoreCase):
                        text.Append(f.Name).Append('(');
                        if (!Lerp(f.Arguments, g.Arguments))
                            return false;
                        text.Append(')');
                        break;
                    default:
                        var (u, v) = (sourceA[x[i].Start..x[i].End], sourceB[y[i].Start..y[i].End]);
                        if (u != v || x[i] is CssFunction or SimpleBlock)
                            return false;
                        text.Append(u);
                        break;
                }
            }
            return true;
        }
    }

    // The colour a value is, when it is exactly one colour other than currentcolor.
    private static CssColor? SingleColor(string text)
    {
        var (source, values) = CssParser.ParseComponentValues(text.Trim());
        var r = new ValueReader(source, values);
        return r.ColorSpecified() is { } value && r.AtEnd && Fixed.Color(value, CssColor.CurrentColor) is { IsCurrentColor: false } color ? color : null;
    }

    private static string? Match(string text, string type, bool isIdent, char multiplier, ComputeContext? context)
    {
        // ponytail: reparses the text per alternative; syntaxes have one or two.
        var (source, values) = CssParser.ParseComponentValues(text);
        var r = new ValueReader(source, values);
        var items = new List<string>();
        do
        {
            if (r.AtEnd || One(r, type, isIdent, context) is not { } item)
                return null;
            items.Add(item);
        } while (multiplier == '+' ? !r.AtEnd : multiplier == '#' && r.Comma());
        return r.AtEnd ? string.Join(multiplier == '#' ? ", " : " ", items) : null;
    }

    private static string? One(ValueReader r, string type, bool isIdent, ComputeContext? context)
    {
        var start = r.Mark;
        string? Text() => r.Mark > start ? r.TextSince(start) : null;
        if (isIdent)
            return r.Ident() == type ? type : null;
        switch (type)
        {
            case "length":
                return r.LengthPercentage(allowPercent: false) is { } length && Absolute(length, context) ? Px(context, length) : null;
            case "percentage":
                return r.LengthPercentage() is PercentageValue p ? N(p.Percent) + "%" : null;
            case "length-percentage":
                return r.LengthPercentage() is { } lp && Absolute(lp, context) ? LengthPercentage(context, lp) ?? Text() : null;
            case "number":
                return r.Number() is { } number ? N(number) : null;
            case "integer":
                return r.Integer() is { } integer ? integer.ToString(CultureInfo.InvariantCulture) : null;
            case "angle":
                return TransformProperties.Angle(r, allowZero: false) is { } angle ? N(angle.Degrees) + "deg" : null;
            case "time":
                return r.Dimension() is { } time && time.Unit.ToLowerInvariant() is var unit && unit is "s" or "ms"
                    ? N(unit == "ms" ? time.Value / 1000 : time.Value) + "s" : null;
            case "resolution":
                return r.Dimension() is { } res && res.Unit.ToLowerInvariant() is var u && u is "dpi" or "dpcm" or "dppx" or "x"
                    ? N(u switch { "dpi" => res.Value / 96, "dpcm" => res.Value * 2.54f / 96, _ => res.Value }) + "dppx" : null;
            case "color":
                return r.ColorSpecified() is { } color ? Color(color, context) : null;
            case "url":
                return r.Url() is not null ? Text() : null;
            case "image":
                return BackgroundParsing.Image(r) is not null ? Text() : null;
            case "custom-ident":
                return r.Ident() is { } ident && ident.ToLowerInvariant() is not ("inherit" or "initial" or "unset" or "revert" or "revert-layer" or "default")
                    ? ident : null;
            case "transform-function":
                return TransformProperties.OneFunction(r) ? Text() : null;
            default: // transform-list
                return TransformProperties.OneFunction(r) ? ListOfFunctions(r, start) : null;
        }
    }

    private static string ListOfFunctions(ValueReader r, int start)
    {
        while (TransformProperties.OneFunction(r))
        {
        }
        return r.TextSince(start);
    }

    // Relative lengths need an element; a registration's initial value may only use absolute ones.
    private static bool Absolute(CssValue value, ComputeContext? context) => context is not null || value switch
    {
        LengthValue l => l.Length.Unit is LengthUnit.Px or LengthUnit.Cm or LengthUnit.Mm or LengthUnit.Q or LengthUnit.In or LengthUnit.Pt or LengthUnit.Pc,
        PercentageValue => true,
        _ => false, // calc() may hide relative units
    };

    private static ComputeContext Fixed => new(ComputedStyle.Initial, 16, 0, 0);

    private static string Px(ComputeContext? context, CssValue length) => N((context ?? Fixed).LengthPercentage(length).Px) + "px";

    // A calc() that stays non-linear keeps its tokens (null here).
    private static string? LengthPercentage(ComputeContext? context, CssValue value)
    {
        var computed = (context ?? Fixed).LengthPercentage(value);
        return computed.Calc is not null ? null
            : computed.Percent == 0 ? N(computed.Px) + "px"
            : computed.Px == 0 ? N(computed.Percent) + "%"
            : $"calc({N(computed.Percent)}% + {N(computed.Px)}px)";
    }

    // currentcolor stays a keyword; other colours compute to rgb().
    private static string Color(CssValue value, ComputeContext? context)
    {
        var ctx = context ?? Fixed;
        return ctx.Color(value, CssColor.CurrentColor).ToString();
    }

    private static string N(float value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);
}

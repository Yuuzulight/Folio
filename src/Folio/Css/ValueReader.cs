using System.Globalization;

namespace Folio.Css;

internal static partial class NamedColors
{
    public static bool TryGet(string name, out CssColor color)
    {
        if (Table.TryGetValue(name, out var rgb))
        {
            color = CssColor.FromRgb24(rgb);
            return true;
        }
        color = default;
        return false;
    }
}

/// <summary>
/// A cursor over a declaration's component values with parsers for the value types of CSS Values 4 and Color 4.
/// Each Try method consumes its value on success and leaves the position unchanged on failure.
/// </summary>
internal sealed class ValueReader(string source, List<ComponentValue> values)
{
    private int _pos;

    /// <summary>The text the component values index into.</summary>
    public string Source => source;

    public bool AtEnd
    {
        get
        {
            SkipWhitespace();
            return _pos >= values.Count;
        }
    }

    /// <summary>Takes the next component value as a reader of its own (for shorthands that try several grammars).</summary>
    public ValueReader OneValue()
    {
        var next = Next();
        if (next is null)
            return new ValueReader(source, []);
        _pos++;
        return new ValueReader(source, [next]);
    }

    /// <summary>A fresh reader at this one's position.</summary>
    public ValueReader Copy() => new(source, values) { _pos = _pos };

    private void SkipWhitespace()
    {
        while (_pos < values.Count && values[_pos] is PreservedToken { Token.Kind: CssTokenKind.Whitespace })
            _pos++;
    }

    private ComponentValue? Next()
    {
        SkipWhitespace();
        return _pos < values.Count ? values[_pos] : null;
    }

    private T? Take<T>(T? value) where T : class
    {
        if (value is not null)
            _pos++;
        return value;
    }

    public string? Keyword(params ReadOnlySpan<string> allowed)
    {
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Ident } t)
        {
            var lower = t.Token.Value.ToLowerInvariant();
            if (allowed.Length == 0 || allowed.Contains(lower))
            {
                _pos++;
                return lower;
            }
        }
        return null;
    }

    /// <summary>The position, to <see cref="Reset"/> to after a failed attempt.</summary>
    public int Mark => _pos;

    public void Reset(int mark) => _pos = mark;

    /// <summary>The component values not read yet.</summary>
    public List<ComponentValue> Rest()
    {
        var rest = values.Skip(_pos).ToList();
        _pos = values.Count;
        return rest;
    }

    /// <summary>Whether the next value is a comma (without consuming it).</summary>
    public bool PeekComma() => Next() is PreservedToken { Token.Kind: CssTokenKind.Comma };

    /// <summary>A URL: <c>url(x)</c> or <c>url("x")</c>.</summary>
    public string? Url()
    {
        switch (Next())
        {
            case PreservedToken { Token.Kind: CssTokenKind.Url } url:
                _pos++;
                return url.Token.Value;
            case CssFunction f when f.Name.Equals("url", StringComparison.OrdinalIgnoreCase):
                var args = new ValueReader(source, f.Arguments);
                if (args.String() is { } text && args.AtEnd)
                {
                    _pos++;
                    return text;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>A function of this name with any arguments, returned as its source text.</summary>
    public string? FunctionText(string name)
    {
        if (Next() is CssFunction f && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && f.Arguments.Any(a => a is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }))
        {
            _pos++;
            return source[f.Start..f.End];
        }
        return null;
    }

    /// <summary>Any identifier, as written.</summary>
    public string? Ident()
    {
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Ident } t)
        {
            _pos++;
            return t.Token.Value;
        }
        return null;
    }

    /// <summary>The source text of the values read since <paramref name="mark"/>, without surrounding whitespace.</summary>
    public string TextSince(int mark)
    {
        var read = values.Skip(mark).Take(_pos - mark).Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        return read.Count == 0 ? "" : source[read[0].Start..read[^1].End];
    }

    /// <summary>Any dimension: its number and its unit as written.</summary>
    public (float Value, string Unit)? Dimension()
    {
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Dimension } d)
        {
            _pos++;
            return ((float)d.Token.Number, d.Token.Value);
        }
        return null;
    }

    /// <summary>A function of this name (ASCII case-insensitive): consumes it and reads its arguments.</summary>
    public ValueReader? Function(string name)
    {
        if (Next() is CssFunction f && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            _pos++;
            return new ValueReader(source, f.Arguments);
        }
        return null;
    }

    /// <summary>A non-negative flex value (<c>fr</c>).</summary>
    public float? Flex()
    {
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Dimension } d && d.Token.Value.Equals("fr", StringComparison.OrdinalIgnoreCase) && d.Token.Number >= 0)
        {
            _pos++;
            return (float)d.Token.Number;
        }
        return null;
    }

    /// <summary>A bracketed list of line names (<c>[a b]</c>); null when the next value is not one.</summary>
    public List<string>? LineNames()
    {
        if (Next() is not SimpleBlock { Open: CssTokenKind.LeftBracket } block)
            return null;
        var names = new List<string>();
        foreach (var value in block.Contents)
        {
            if (value is PreservedToken { Token.Kind: CssTokenKind.Ident } ident && !ident.Token.Value.Equals("span", StringComparison.OrdinalIgnoreCase)
                && !ident.Token.Value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                names.Add(ident.Token.Value);
            else if (value is not PreservedToken { Token.Kind: CssTokenKind.Whitespace })
                return null;
        }
        _pos++;
        return names;
    }

    public bool Comma()
    {
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Comma })
        {
            _pos++;
            return true;
        }
        return false;
    }

    public bool Delim(char c)
    {
        if (Next() is PreservedToken t && t.Token.IsDelim(c))
        {
            _pos++;
            return true;
        }
        return false;
    }

    public string? String() => Next() is PreservedToken { Token.Kind: CssTokenKind.String } t ? Take(t)!.Token.Value : null;

    public float? Number(bool nonNegative = false)
    {
        var start = _pos;
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Number } t && (!nonNegative || t.Token.Number >= 0))
        {
            _pos++;
            return (float)t.Token.Number;
        }
        if (Math(CalcType.Number) is { } calc && Evaluate(calc.Node) is { } number)
            return nonNegative ? System.Math.Max(0, number) : number;
        _pos = start;
        return null;
    }

    public int? Integer()
    {
        if (Next() is PreservedToken { Token.Kind: CssTokenKind.Number, Token.IsInteger: true } t)
        {
            _pos++;
            return (int)System.Math.Clamp(t.Token.Number, int.MinValue, int.MaxValue);
        }
        return null;
    }

    /// <summary>A length, percentage (if allowed), unitless zero, or math function of those.</summary>
    public CssValue? LengthPercentage(bool allowPercent = true, bool nonNegative = false)
    {
        var next = Next();
        switch (next)
        {
            case PreservedToken { Token.Kind: CssTokenKind.Dimension } d when UnitOf(d.Token.Value) is { } unit
                                                                             && (!nonNegative || d.Token.Number >= 0):
                _pos++;
                return new LengthValue(new Length((float)d.Token.Number, unit));
            case PreservedToken { Token.Kind: CssTokenKind.Percentage } p when allowPercent && (!nonNegative || p.Token.Number >= 0):
                _pos++;
                return new PercentageValue((float)p.Token.Number);
            case PreservedToken { Token.Kind: CssTokenKind.Number } n when n.Token.Number == 0:
                _pos++;
                return new LengthValue(Length.Zero);
            case CssFunction:
                return Math(allowPercent ? CalcType.Length | CalcType.Percent : CalcType.Length);
            default:
                return null;
        }
    }

    public static LengthUnit? UnitOf(string unit) => unit.ToLowerInvariant() switch
    {
        "px" => LengthUnit.Px,
        "em" => LengthUnit.Em,
        "rem" => LengthUnit.Rem,
        "ex" => LengthUnit.Ex,
        "ch" => LengthUnit.Ch,
        "vw" => LengthUnit.Vw,
        "vh" or "dvh" or "svh" or "lvh" => LengthUnit.Vh,
        "vmin" => LengthUnit.Vmin,
        "vmax" => LengthUnit.Vmax,
        "cm" => LengthUnit.Cm,
        "mm" => LengthUnit.Mm,
        "q" => LengthUnit.Q,
        "in" => LengthUnit.In,
        "pt" => LengthUnit.Pt,
        "pc" => LengthUnit.Pc,
        _ => null,
    };

    // ---------------------------------------------------------------- math functions

    /// <summary>
    /// <c>calc()</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c> whose type fits <paramref name="allowed"/>
    /// (https://www.w3.org/TR/css-values-4/#math). A result outside the property's range is clamped when the
    /// value is computed, as the spec says for math functions, so ranges are not checked here.
    /// </summary>
    public CalcValue? Math(CalcType allowed)
    {
        if (Next() is not CssFunction function || MathFunction(function) is not { } node)
            return null;
        var type = TypeOf(node);
        if (type == CalcType.None || (type & ~allowed) != 0)
            return null;
        _pos++;
        return new CalcValue(node, type);
    }

    private CalcNode? MathFunction(CssFunction function)
    {
        var name = function.Name.ToLowerInvariant();
        if (name is not ("calc" or "min" or "max" or "clamp"))
            return null;
        var args = SplitCommas(function.Arguments);
        switch (name)
        {
            case "calc":
                return args.Count == 1 ? Sum(args[0]) : null;
            case "min" or "max":
                var nodes = args.Select(Sum).ToList();
                return nodes.Count > 0 && nodes.All(n => n is not null) ? new CalcMinMax(name == "max", nodes!) : null;
            default:
                if (args.Count != 3)
                    return null;
                var (min, value, max) = (Sum(args[0]), Sum(args[1]), Sum(args[2]));
                return min is null || value is null || max is null ? null : new CalcClamp(min, value, max);
        }
    }

    private static List<List<ComponentValue>> SplitCommas(List<ComponentValue> values)
    {
        var parts = new List<List<ComponentValue>> { new() };
        foreach (var v in values)
        {
            if (v is PreservedToken { Token.Kind: CssTokenKind.Comma })
                parts.Add([]);
            else
                parts[^1].Add(v);
        }
        return parts;
    }

    // <calc-sum> = <calc-product> [ [ '+' | '-' ] <calc-product> ]*, operators surrounded by whitespace.
    private CalcNode? Sum(List<ComponentValue> values)
    {
        var terms = new List<(char Op, List<ComponentValue> Values)> { ('+', new()) };
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (v is PreservedToken t && (t.Token.IsDelim('+') || t.Token.IsDelim('-'))
                && i > 0 && values[i - 1] is PreservedToken { Token.Kind: CssTokenKind.Whitespace }
                && i + 1 < values.Count && values[i + 1] is PreservedToken { Token.Kind: CssTokenKind.Whitespace })
                terms.Add((t.Token.Value[0], []));
            else
                terms[^1].Values.Add(v);
        }

        CalcNode? result = null;
        foreach (var (op, termValues) in terms)
        {
            var product = Product(termValues);
            if (product is null)
                return null;
            if (op == '-')
                product = new CalcProduct(new CalcNumber(-1), product);
            result = result is null ? product : new CalcSum(result, product);
        }
        return result;
    }

    // <calc-product> = <calc-value> [ [ '*' | '/' ] <calc-value> ]*
    private CalcNode? Product(List<ComponentValue> values)
    {
        var items = values.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        if (items.Count == 0 || items.Count % 2 == 0)
            return null;
        var result = Value(items[0]);
        for (var i = 1; i < items.Count && result is not null; i += 2)
        {
            var right = Value(items[i + 1]);
            if (right is null || items[i] is not PreservedToken op)
                return null;
            if (op.Token.IsDelim('*'))
                result = new CalcProduct(result, right);
            else if (op.Token.IsDelim('/'))
                result = new CalcQuotient(result, right);
            else
                return null;
        }
        return result;
    }

    private CalcNode? Value(ComponentValue value) => value switch
    {
        PreservedToken { Token.Kind: CssTokenKind.Number } n => new CalcNumber((float)n.Token.Number),
        PreservedToken { Token.Kind: CssTokenKind.Percentage } p => new CalcPercent((float)p.Token.Number),
        PreservedToken { Token.Kind: CssTokenKind.Dimension } d when UnitOf(d.Token.Value) is { } unit => new CalcLength(new Length((float)d.Token.Number, unit)),
        SimpleBlock { Open: CssTokenKind.LeftParen } block => Sum(block.Contents),
        CssFunction f => MathFunction(f),
        _ => null,
    };

    // https://www.w3.org/TR/css-values-4/#determine-the-type-of-a-calculation
    public static CalcType TypeOf(CalcNode node)
    {
        switch (node)
        {
            case CalcNumber:
                return CalcType.Number;
            case CalcLength:
                return CalcType.Length;
            case CalcPercent:
                return CalcType.Percent;
            case CalcSum sum:
                var (l, r) = (TypeOf(sum.Left), TypeOf(sum.Right));
                if (l == CalcType.None || r == CalcType.None)
                    return CalcType.None;
                if ((l == CalcType.Number) != (r == CalcType.Number))
                    return CalcType.None;
                return l | r;
            case CalcProduct product:
                var (a, b) = (TypeOf(product.Left), TypeOf(product.Right));
                if (a == CalcType.None || b == CalcType.None || (a != CalcType.Number && b != CalcType.Number))
                    return CalcType.None;
                return a == CalcType.Number ? b : a;
            case CalcQuotient quotient:
                var (n, d) = (TypeOf(quotient.Left), TypeOf(quotient.Right));
                return n == CalcType.None || d != CalcType.Number ? CalcType.None : n;
            case CalcMinMax minMax:
                return Combine(minMax.Arguments);
            case CalcClamp clamp:
                return Combine([clamp.Min, clamp.Value, clamp.Max]);
            default:
                return CalcType.None;
        }
    }

    private static CalcType Combine(IReadOnlyList<CalcNode> nodes)
    {
        var types = nodes.Select(TypeOf).ToList();
        if (types.Contains(CalcType.None) || types.Any(t => t == CalcType.Number) && types.Any(t => t != CalcType.Number))
            return CalcType.None;
        return types.Aggregate(CalcType.None, (all, t) => all | t);
    }

    /// <summary>Evaluates a number-only calculation.</summary>
    public static float? Evaluate(CalcNode node) => node switch
    {
        CalcNumber n => n.Value,
        CalcSum s => Evaluate(s.Left) + Evaluate(s.Right),
        CalcProduct p => Evaluate(p.Left) * Evaluate(p.Right),
        CalcQuotient q => Evaluate(q.Left) / Evaluate(q.Right),
        CalcMinMax m => m.Arguments.Select(Evaluate).Aggregate((x, y) => x is null || y is null ? null : m.IsMax ? System.Math.Max(x.Value, y.Value) : System.Math.Min(x.Value, y.Value)),
        CalcClamp c => Evaluate(c.Min) is { } min && Evaluate(c.Value) is { } value && Evaluate(c.Max) is { } max ? System.Math.Max(min, System.Math.Min(value, max)) : null,
        _ => null,
    };

    // ---------------------------------------------------------------- colours

    /// <summary>
    /// A <c>&lt;color&gt;</c> as a specified value: a <see cref="ColorValue"/>, or a <see cref="ColorExpressionValue"/>
    /// when it depends on the element (color-mix() with currentcolor, light-dark()).
    /// </summary>
    public CssValue? ColorSpecified() => ColorExpression() switch
    {
        null => null,
        var e when ColorResolver.NeedsElement(e) && e is not ColorLiteral => new ColorExpressionValue(e),
        var e => new ColorValue(ColorResolver.Resolve(e, CssColor.CurrentColor, dark: false)),
    };

    /// <summary>A colour that needs no element: currentcolor is kept symbolic, element-dependent expressions fail.</summary>
    public CssColor? Color()
    {
        var start = _pos;
        var e = ColorExpression();
        if (e is ColorLiteral literal)
            return literal.Color;
        if (e is not null && !ColorResolver.NeedsElement(e))
            return ColorResolver.Resolve(e, CssColor.Black, dark: false);
        _pos = start;
        return null;
    }

    /// <summary>
    /// A <c>&lt;color&gt;</c> (https://www.w3.org/TR/css-color-4/, https://www.w3.org/TR/css-color-5/): hex, named,
    /// currentcolor, transparent, rgb(a), hsl(a), hwb, lab, lch, oklab, oklch, color-mix(), light-dark().
    /// </summary>
    public ColorExpression? ColorExpression()
    {
        if (Next() is CssFunction f)
        {
            var name = f.Name.ToLowerInvariant();
            ColorExpression? expression = name switch
            {
                "color-mix" => ColorMix(f.Arguments),
                "light-dark" => LightDark(f.Arguments),
                _ => null,
            };
            if (expression is not null)
            {
                _pos++;
                return expression;
            }
            if (name is "color-mix" or "light-dark")
                return null;
        }
        return LiteralColor() is { } color ? new ColorLiteral(color) : null;
    }

    private ColorExpression? ColorMix(List<ComponentValue> arguments)
    {
        var parts = SplitCommas(arguments);
        if (parts.Count != 3)
            return null;

        var method = new ValueReader(source, parts[0]);
        if (method.Keyword("in") is null)
            return null;
        ColorSpace? space = method.Keyword("srgb", "srgb-linear", "lab", "oklab", "lch", "oklch", "xyz", "xyz-d50", "xyz-d65", "hsl", "hwb") switch
        {
            "srgb" => ColorSpace.Srgb,
            "srgb-linear" => ColorSpace.SrgbLinear,
            "lab" => ColorSpace.Lab,
            "oklab" => ColorSpace.Oklab,
            "lch" => ColorSpace.Lch,
            "oklch" => ColorSpace.Oklch,
            "xyz" or "xyz-d65" => ColorSpace.XyzD65,
            "xyz-d50" => ColorSpace.XyzD50,
            "hsl" => ColorSpace.Hsl,
            "hwb" => ColorSpace.Hwb,
            _ => null,
        };
        if (space is null)
            return null;
        var hue = HueInterpolation.Shorter;
        if (method.Keyword("shorter", "longer", "increasing", "decreasing") is { } hueMethod)
        {
            if (!ColorSpaces.IsPolar(space.Value) || method.Keyword("hue") is null)
                return null;
            hue = Enum.Parse<HueInterpolation>(hueMethod, ignoreCase: true);
        }
        if (!method.AtEnd)
            return null;

        (ColorExpression Color, float? Percent)? Operand(List<ComponentValue> values)
        {
            var reader = new ValueReader(source, values);
            var percent = reader.LengthPercentage() as PercentageValue;
            var color = reader.ColorExpression();
            percent ??= reader.LengthPercentage() as PercentageValue;
            if (color is null || !reader.AtEnd || percent is { Percent: < 0 or > 100 })
                return null;
            return (color, percent?.Percent);
        }

        if (Operand(parts[1]) is not { } first || Operand(parts[2]) is not { } second)
            return null;
        if (first.Percent is 0 && second.Percent is 0)
            return null;
        return new ColorMix(space.Value, hue, first.Color, first.Percent, second.Color, second.Percent);
    }

    private ColorExpression? LightDark(List<ComponentValue> arguments)
    {
        var parts = SplitCommas(arguments);
        if (parts.Count != 2)
            return null;
        var light = new ValueReader(source, parts[0]);
        var dark = new ValueReader(source, parts[1]);
        var l = light.ColorExpression();
        var d = dark.ColorExpression();
        return l is not null && d is not null && light.AtEnd && dark.AtEnd ? new LightDark(l, d) : null;
    }

    private CssColor? LiteralColor()
    {
        switch (Next())
        {
            case PreservedToken { Token.Kind: CssTokenKind.Hash } hash:
                if (Hex(hash.Token.Value) is not { } hex)
                    return null;
                _pos++;
                return hex;
            case PreservedToken { Token.Kind: CssTokenKind.Ident } ident:
                var name = ident.Token.Value;
                CssColor color;
                if (name.Equals("currentcolor", StringComparison.OrdinalIgnoreCase))
                    color = CssColor.CurrentColor;
                else if (name.Equals("transparent", StringComparison.OrdinalIgnoreCase))
                    color = CssColor.Transparent;
                else if (!NamedColors.TryGet(name, out color))
                    return null;
                _pos++;
                return color;
            case CssFunction function:
                var result = function.Name.ToLowerInvariant() switch
                {
                    "rgb" or "rgba" => Rgb(function.Arguments),
                    "hsl" or "hsla" => HslOrHwb(function.Arguments, hwb: false),
                    "hwb" => HslOrHwb(function.Arguments, hwb: true),
                    "lab" or "oklab" => LabLike(function.Arguments, ok: function.Name.Equals("oklab", StringComparison.OrdinalIgnoreCase), polar: false),
                    "lch" or "oklch" => LabLike(function.Arguments, ok: function.Name.Equals("oklch", StringComparison.OrdinalIgnoreCase), polar: true),
                    _ => null,
                };
                if (result is not null)
                    _pos++;
                return result;
            default:
                return null;
        }
    }

    private static CssColor? Hex(string digits)
    {
        // Only hex digits: a hash token can hold other characters through escapes, which number parsing would skip.
        if (digits.Length is not (3 or 4 or 6 or 8) || !digits.All(char.IsAsciiHexDigit))
            return null;
        int Digit(int i) => Convert.ToInt32(digits.Substring(i, 1), 16);
        int Pair(int i) => Convert.ToInt32(digits.Substring(i, 2), 16);
        return digits.Length switch
        {
            3 => new CssColor(Digit(0) * 17 / 255f, Digit(1) * 17 / 255f, Digit(2) * 17 / 255f, 1),
            4 => new CssColor(Digit(0) * 17 / 255f, Digit(1) * 17 / 255f, Digit(2) * 17 / 255f, Digit(3) * 17 / 255f),
            6 => new CssColor(Pair(0) / 255f, Pair(2) / 255f, Pair(4) / 255f, 1),
            _ => new CssColor(Pair(0) / 255f, Pair(2) / 255f, Pair(4) / 255f, Pair(6) / 255f),
        };
    }

    // Splits colour function arguments into the legacy comma form or the modern space form with "/ alpha".
    private (List<ComponentValue> Channels, ComponentValue? Alpha, bool Legacy)? ColorArguments(List<ComponentValue> arguments)
    {
        var items = arguments.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        if (items.Any(v => v is PreservedToken { Token.Kind: CssTokenKind.Comma }))
        {
            var parts = SplitCommas(arguments)
                .Select(p => p.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList())
                .ToList();
            if (parts.Count is not (3 or 4) || parts.Any(p => p.Count != 1))
                return null;
            return (parts.Take(3).Select(p => p[0]).ToList(), parts.Count == 4 ? parts[3][0] : null, true);
        }
        var slash = items.FindIndex(v => v is PreservedToken t && t.Token.IsDelim('/'));
        if (slash >= 0)
            return slash == 3 && items.Count == 5 ? (items.Take(3).ToList(), items[4], false) : null;
        return items.Count == 3 ? (items, null, false) : null;
    }

    // A channel: number, percentage (of <paramref name="percentScale"/>), or "none" (modern syntax only, as 0).
    private float? Channel(ComponentValue value, float percentScale, bool legacy, bool? wantPercent = null)
    {
        switch (value)
        {
            case PreservedToken { Token.Kind: CssTokenKind.Number } n when wantPercent != true:
                return (float)n.Token.Number;
            case PreservedToken { Token.Kind: CssTokenKind.Percentage } p when wantPercent != false:
                return (float)p.Token.Number / 100 * percentScale;
            case PreservedToken { Token.Kind: CssTokenKind.Ident } i when !legacy && i.Token.IsIdent("none"):
                return 0;
            case CssFunction:
                var reader = new ValueReader(source, [value]);
                var calc = reader.Math(CalcType.Number);
                return calc is null ? null : Evaluate(calc.Node);
            default:
                return null;
        }
    }

    private float? Alpha(ComponentValue? value, bool legacy) =>
        value is null ? 1 : Channel(value, 1, legacy) is { } a ? System.Math.Clamp(a, 0, 1) : null;

    private CssColor? Rgb(List<ComponentValue> arguments)
    {
        if (ColorArguments(arguments) is not var (channels, alphaValue, legacy))
            return null;
        // Legacy syntax needs all numbers or all percentages.
        bool? percent = legacy ? channels[0] is PreservedToken { Token.Kind: CssTokenKind.Percentage } : null;
        var rgb = channels.Select(c => Channel(c, 255, legacy, percent)).ToList();
        var alpha = Alpha(alphaValue, legacy);
        if (rgb.Contains(null) || alpha is null)
            return null;
        return new CssColor(Clamp01(rgb[0]!.Value / 255), Clamp01(rgb[1]!.Value / 255), Clamp01(rgb[2]!.Value / 255), alpha.Value);
    }

    private CssColor? HslOrHwb(List<ComponentValue> arguments, bool hwb)
    {
        if (ColorArguments(arguments) is not var (channels, alphaValue, legacy) || (hwb && legacy))
            return null;
        var hue = Hue(channels[0], legacy);
        var second = Channel(channels[1], 100, legacy, legacy ? true : null);
        var third = Channel(channels[2], 100, legacy, legacy ? true : null);
        var alpha = Alpha(alphaValue, legacy);
        if (hue is null || second is null || third is null || alpha is null)
            return null;
        var (r, g, b) = hwb
            ? HwbToRgb(hue.Value, second.Value / 100, third.Value / 100)
            : HslToRgb(hue.Value, Clamp01(second.Value / 100), Clamp01(third.Value / 100));
        return new CssColor(Clamp01(r), Clamp01(g), Clamp01(b), alpha.Value);
    }

    // lab(), lch(), oklab(), oklch() (https://www.w3.org/TR/css-color-4/#specifying-lab-lch): modern syntax only;
    // results outside sRGB are gamut mapped.
    private CssColor? LabLike(List<ComponentValue> arguments, bool ok, bool polar)
    {
        if (ColorArguments(arguments) is not var (channels, alphaValue, legacy) || legacy)
            return null;
        var lightness = Channel(channels[0], ok ? 1 : 100, legacy: false);
        var second = Channel(channels[1], ok ? 0.4f : polar ? 150 : 125, legacy: false);
        var third = polar ? Hue(channels[2], legacy: false) : Channel(channels[2], ok ? 0.4f : 125, legacy: false);
        var alpha = Alpha(alphaValue, legacy: false);
        if (lightness is null || second is null || third is null || alpha is null)
            return null;

        var l = System.Math.Clamp(lightness.Value, 0, ok ? 1 : 100);
        (double, double, double) rgb = (ok, polar) switch
        {
            (false, false) => ColorSpaces.LabToSrgb(l, second.Value, third.Value),
            (true, false) => ColorSpaces.OklabToSrgb(l, second.Value, third.Value),
            (false, true) => ColorSpaces.LchToSrgb(l, System.Math.Max(0, second.Value), third.Value),
            _ => ColorSpaces.OklchToSrgb(l, System.Math.Max(0, second.Value), third.Value),
        };
        var mapped = ColorSpaces.GamutMapToSrgb(rgb);
        return new CssColor((float)mapped.R, (float)mapped.G, (float)mapped.B, alpha.Value);
    }

    private float? Hue(ComponentValue value, bool legacy)
    {
        switch (value)
        {
            case PreservedToken { Token.Kind: CssTokenKind.Dimension } d:
                var degrees = d.Token.Value.ToLowerInvariant() switch
                {
                    "deg" => d.Token.Number,
                    "grad" => d.Token.Number * 0.9,
                    "rad" => d.Token.Number * 180 / System.Math.PI,
                    "turn" => d.Token.Number * 360,
                    _ => double.NaN,
                };
                return double.IsNaN(degrees) ? null : (float)degrees;
            default:
                return Channel(value, float.NaN, legacy, wantPercent: false);
        }
    }

    private static float Clamp01(float v) => System.Math.Clamp(v, 0, 1);

    // https://www.w3.org/TR/css-color-4/#hsl-to-rgb
    private static (float, float, float) HslToRgb(float hue, float saturation, float lightness)
    {
        hue %= 360;
        if (hue < 0)
            hue += 360;
        float F(float n)
        {
            var k = (n + hue / 30) % 12;
            var a = saturation * System.Math.Min(lightness, 1 - lightness);
            return lightness - a * System.Math.Max(-1, System.Math.Min(System.Math.Min(k - 3, 9 - k), 1));
        }
        return (F(0), F(8), F(4));
    }

    // https://www.w3.org/TR/css-color-4/#hwb-to-rgb
    private static (float, float, float) HwbToRgb(float hue, float white, float black)
    {
        if (white + black >= 1)
        {
            var gray = white / (white + black);
            return (gray, gray, gray);
        }
        var (r, g, b) = HslToRgb(hue, 1, 0.5f);
        var scale = 1 - white - black;
        return (r * scale + white, g * scale + white, b * scale + white);
    }

    // ---------------------------------------------------------------- fonts

    /// <summary>https://www.w3.org/TR/css-fonts-4/#font-family-prop: strings or identifier sequences, comma-separated.</summary>
    public FontFamilyValue? FontFamily()
    {
        var families = new List<string>();
        var start = _pos;
        while (true)
        {
            if (String() is { } quoted)
            {
                families.Add(quoted);
            }
            else
            {
                var words = new List<string>();
                while (Next() is PreservedToken { Token.Kind: CssTokenKind.Ident } word)
                {
                    words.Add(word.Token.Value);
                    _pos++;
                }
                if (words.Count == 0)
                {
                    _pos = start;
                    return null;
                }
                // A lone generic family keyword is lowercased; other names keep their case.
                var name = string.Join(' ', words);
                families.Add(words.Count == 1 && IsGenericFamily(name) ? name.ToLowerInvariant() : name);
            }
            if (!Comma())
                break;
        }
        return new FontFamilyValue(families);
    }

    private static bool IsGenericFamily(string name) => name.ToLowerInvariant() is
        "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy" or "system-ui" or "ui-serif" or "ui-sans-serif"
        or "ui-monospace" or "ui-rounded" or "math" or "emoji" or "fangsong";
}

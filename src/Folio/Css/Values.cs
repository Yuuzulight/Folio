namespace Folio.Css;

/// <summary>https://www.w3.org/TR/css-values-4/#lengths (dvh/svh/lvh parse as vh: the viewport is fixed).</summary>
internal enum LengthUnit
{
    Px,
    Em,
    Rem,
    Ex,
    Ch,
    Vw,
    Vh,
    Vmin,
    Vmax,
    Cm,
    Mm,
    Q,
    In,
    Pt,
    Pc,
}

internal readonly record struct Length(float Value, LengthUnit Unit)
{
    public static Length Zero => new(0, LengthUnit.Px);
}

/// <summary>A colour in sRGB, components 0..1, not premultiplied; or the symbolic <c>currentcolor</c>.</summary>
internal readonly record struct CssColor(float R, float G, float B, float A, bool IsCurrentColor = false)
{
    public static CssColor Transparent => new(0, 0, 0, 0);
    public static CssColor Black => new(0, 0, 0, 1);
    public static CssColor CurrentColor => new(0, 0, 0, 0, IsCurrentColor: true);

    public static CssColor FromRgb24(int rgb, float alpha = 1) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, alpha);

    /// <summary>Resolves <c>currentcolor</c> against the element's <c>color</c>.</summary>
    public CssColor Resolve(CssColor current) => IsCurrentColor ? current : this;

    public override string ToString()
    {
        if (IsCurrentColor)
            return "currentcolor";
        static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0, 1) * 255);
        return A >= 1
            ? $"rgb({Byte(R)}, {Byte(G)}, {Byte(B)})"
            : $"rgba({Byte(R)}, {Byte(G)}, {Byte(B)}, {Math.Round(A, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)})";
    }
}

/// <summary>Kinds a <c>calc()</c> expression can produce (https://www.w3.org/TR/css-values-4/#calc-type-checking).</summary>
[Flags]
internal enum CalcType
{
    None = 0,
    Number = 1,
    Length = 2,
    Percent = 4,
}

/// <summary>A math function tree: <c>calc()</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c>.</summary>
internal abstract record CalcNode;

internal sealed record CalcNumber(float Value) : CalcNode;

internal sealed record CalcLength(Length Length) : CalcNode;

internal sealed record CalcPercent(float Value) : CalcNode;

internal sealed record CalcSum(CalcNode Left, CalcNode Right) : CalcNode;

internal sealed record CalcProduct(CalcNode Left, CalcNode Right) : CalcNode;

internal sealed record CalcQuotient(CalcNode Left, CalcNode Right) : CalcNode;

internal sealed record CalcMinMax(bool IsMax, IReadOnlyList<CalcNode> Arguments) : CalcNode;

internal sealed record CalcClamp(CalcNode Min, CalcNode Value, CalcNode Max) : CalcNode;

/// <summary>A specified value, as parsed from a declaration.</summary>
internal abstract record CssValue;

/// <summary>An identifier keyword, ASCII-lowercased.</summary>
internal sealed record KeywordValue(string Keyword) : CssValue;

internal sealed record LengthValue(Length Length) : CssValue;

internal sealed record PercentageValue(float Percent) : CssValue;

internal sealed record NumberValue(float Number) : CssValue;

internal sealed record CalcValue(CalcNode Node, CalcType Type) : CssValue;

internal sealed record ColorValue(CssColor Color) : CssValue;

/// <summary>A specified corner radius: horizontal and vertical length-percentages.</summary>
internal sealed record RadiusValue(CssValue X, CssValue Y) : CssValue;

/// <summary>One specified shadow: two to four lengths, an optional colour, and inset.</summary>
internal sealed record ShadowSpecified(IReadOnlyList<CssValue> Lengths, CssValue? Color, bool Inset);

/// <summary>A specified shadow list; empty for none.</summary>
internal sealed record ShadowListValue(IReadOnlyList<ShadowSpecified> Shadows) : CssValue;

/// <summary>A specified <c>quotes</c> list of pairs.</summary>
internal sealed record QuotesValue(Style.QuotesGroup Quotes) : CssValue;

/// <summary>A specified <c>text-indent</c>: a length-percentage and its keywords.</summary>
internal sealed record TextIndentValue(CssValue Length, bool Hanging, bool EachLine) : CssValue;

/// <summary>A colour that depends on the element: <c>color-mix()</c> with <c>currentcolor</c>, or <c>light-dark()</c>.</summary>
internal abstract record ColorExpression;

internal sealed record ColorLiteral(CssColor Color) : ColorExpression;

internal sealed record ColorMix(ColorSpace Space, HueInterpolation Hue, ColorExpression First, float? FirstPercent,
                                ColorExpression Second, float? SecondPercent) : ColorExpression;

internal sealed record LightDark(ColorExpression Light, ColorExpression Dark) : ColorExpression;

/// <summary>A specified colour resolved at computed-value time (it needs currentcolor or the used colour scheme).</summary>
internal sealed record ColorExpressionValue(ColorExpression Expression) : CssValue;

/// <summary><c>color-scheme</c> (https://www.w3.org/TR/css-color-adjust-1/#color-scheme-prop): normal, or the schemes an element supports.</summary>
internal readonly record struct ColorSchemeValue(bool Light, bool Dark, bool Only)
{
    public static ColorSchemeValue Normal => default;

    public bool IsNormal => !Light && !Dark;

    /// <summary>The used scheme: dark when the element supports it and the host prefers it, or supports only dark.</summary>
    public bool UsesDark(bool prefersDark) => Dark && (prefersDark || !Light);

    public override string ToString() => IsNormal ? "normal" : string.Join(" ", new[] { Light ? "light" : null, Dark ? "dark" : null, Only ? "only" : null }.Where(s => s is not null));
}

internal sealed record ColorSchemeSpecified(ColorSchemeValue Scheme) : CssValue;

/// <summary>Resolves colour expressions (https://www.w3.org/TR/css-color-5/#color-mix, #light-dark).</summary>
internal static class ColorResolver
{
    public static bool NeedsElement(ColorExpression e) => e switch
    {
        ColorLiteral l => l.Color.IsCurrentColor,
        ColorMix m => NeedsElement(m.First) || NeedsElement(m.Second),
        _ => true,
    };

    public static CssColor Resolve(ColorExpression e, CssColor currentColor, bool dark) => e switch
    {
        ColorLiteral l => l.Color.Resolve(currentColor),
        LightDark ld => Resolve(dark ? ld.Dark : ld.Light, currentColor, dark),
        ColorMix m => Mix(m, Resolve(m.First, currentColor, dark), Resolve(m.Second, currentColor, dark)),
        _ => CssColor.Black,
    };

    private static CssColor Mix(ColorMix mix, CssColor first, CssColor second)
    {
        // Percentage normalisation: missing ones complete to 100%, a sum under 100% scales the alpha.
        double p1, p2;
        if (mix.FirstPercent is null && mix.SecondPercent is null)
            (p1, p2) = (50, 50);
        else
            (p1, p2) = (mix.FirstPercent ?? 100 - mix.SecondPercent!.Value, mix.SecondPercent ?? 100 - mix.FirstPercent!.Value);
        var sum = p1 + p2;
        var alphaMultiplier = sum < 100 ? sum / 100 : 1;
        var mixed = Interpolate(first, second, p2 / sum, mix.Space, mix.Hue);
        return mixed with { A = (float)(mixed.A * alphaMultiplier) };
    }

    /// <summary>
    /// The colour a fraction <paramref name="t"/> of the way from one colour to another, interpolated premultiplied in a
    /// colour space with a hue method (https://www.w3.org/TR/css-color-4/#interpolation), gamut mapped to sRGB.
    /// </summary>
    public static CssColor Interpolate(CssColor first, CssColor second, double t, ColorSpace space, HueInterpolation hueMethod)
    {
        var a = ColorSpaces.FromSrgb(space, (first.R, first.G, first.B));
        var b = ColorSpaces.FromSrgb(space, (second.R, second.G, second.B));
        double[] ca = [a.Item1, a.Item2, a.Item3], cb = [b.Item1, b.Item2, b.Item3];
        var hue = ColorSpaces.IsPolar(space) ? ColorSpaces.HueIndex(space) : -1;

        if (hue >= 0)
        {
            if (double.IsNaN(ca[hue]))
                ca[hue] = double.IsNaN(cb[hue]) ? 0 : cb[hue];
            if (double.IsNaN(cb[hue]))
                cb[hue] = ca[hue];
            (ca[hue], cb[hue]) = FixHues(ca[hue], cb[hue], hueMethod);
        }

        var alpha = first.A * (1 - t) + second.A * t;
        var result = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (i == hue)
            {
                result[i] = (ca[i] * (1 - t) + cb[i] * t) % 360;
                continue;
            }
            // Interpolate premultiplied by alpha.
            var premultiplied = ca[i] * first.A * (1 - t) + cb[i] * second.A * t;
            result[i] = alpha == 0 ? 0 : premultiplied / alpha;
        }

        var rgb = ColorSpaces.GamutMapToSrgb(ColorSpaces.ToSrgb(space, (result[0], result[1], result[2])));
        return new CssColor((float)rgb.R, (float)rgb.G, (float)rgb.B, (float)alpha);
    }

    // https://www.w3.org/TR/css-color-4/#hue-interpolation
    private static (double, double) FixHues(double h1, double h2, HueInterpolation method)
    {
        h1 = (h1 % 360 + 360) % 360;
        h2 = (h2 % 360 + 360) % 360;
        var d = h2 - h1;
        switch (method)
        {
            case HueInterpolation.Shorter:
                if (d > 180) h1 += 360;
                else if (d < -180) h2 += 360;
                break;
            case HueInterpolation.Longer:
                if (d is > 0 and < 180) h1 += 360;
                else if (d is > -180 and <= 0) h2 += 360;
                break;
            case HueInterpolation.Increasing:
                if (h2 < h1) h2 += 360;
                break;
            case HueInterpolation.Decreasing:
                if (h1 < h2) h1 += 360;
                break;
        }
        return (h1, h2);
    }
}

internal sealed record FontFamilyValue(IReadOnlyList<string> Families) : CssValue;

/// <summary>https://www.w3.org/TR/css-cascade-5/#defaulting-keywords</summary>
internal enum CssWideKeyword
{
    Initial,
    Inherit,
    Unset,
    Revert,
    RevertLayer,
}

internal sealed record CssWideValue(CssWideKeyword Keyword) : CssValue;

/// <summary>
/// A value containing <c>var()</c>, kept as text until computed-value time
/// (https://www.w3.org/TR/css-variables-1/#variables-in-shorthands). For a longhand of a shorthand,
/// <paramref name="Shorthand"/> names the shorthand whose substituted value is parsed.
/// </summary>
internal sealed record UnparsedValue(string Text, string? Shorthand) : CssValue;

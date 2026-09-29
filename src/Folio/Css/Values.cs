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

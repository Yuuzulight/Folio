using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

/// <summary>Filter functions as filter primitives: their equivalents in https://drafts.csswg.org/filter-effects-1/#ShorthandEquivalents.</summary>
internal static class FilterPrimitives
{
    /// <summary>A filter list's primitives, currentcolor resolved against <paramref name="currentColor"/>; null for none.</summary>
    public static Filter[]? Of(FilterList list, CssColor currentColor) =>
        list.IsNone ? null : [.. list.Functions.Select(f => Of(f, currentColor))];

    /// <summary>
    /// A filter list's primitives for a layer, with opacity() functions at the end taken out as an opacity to multiply
    /// the layer's by: scaling alpha last is what the layer's opacity does, so the two composite exactly alike.
    /// </summary>
    public static (Filter[]? Filters, float Opacity) ForLayer(FilterList list, CssColor currentColor)
    {
        var (count, opacity) = (list.Functions.Count, 1f);
        while (count > 0 && list.Functions[count - 1] is { Name: "opacity" } last)
        {
            opacity *= Math.Min(last.Amount, 1);
            count--;
        }
        return (count == 0 ? null : [.. list.Functions.Take(count).Select(f => Of(f, currentColor))], opacity);
    }

    public static Filter Of(FilterFunction function, CssColor currentColor)
    {
        switch (function.Name)
        {
            case "blur":
                return new Filter(FilterKind.Blur, function.Amount);
            case "drop-shadow":
                var c = function.Color.Resolve(currentColor);
                return new Filter(FilterKind.DropShadow, function.Amount, Offset: new(function.X, function.Y), Color: new Rgba(c.R, c.G, c.B, c.A));
            default:
                return new Filter(FilterKind.ColorMatrix, Matrix: ColorMatrix(function.Name, function.Amount));
        }
    }

    // The component transfers (brightness, contrast, invert, opacity) are linear functions or two-entry tables, so each
    // is a matrix too. Amounts of grayscale, sepia, invert and opacity above 1 count as 1.
    private static float[] ColorMatrix(string name, float a)
    {
        var s = 1 - Math.Min(a, 1);
        switch (name)
        {
            case "grayscale":
                return
                [
                    0.2126f + 0.7874f * s, 0.7152f - 0.7152f * s, 0.0722f - 0.0722f * s, 0, 0,
                    0.2126f - 0.2126f * s, 0.7152f + 0.2848f * s, 0.0722f - 0.0722f * s, 0, 0,
                    0.2126f - 0.2126f * s, 0.7152f - 0.7152f * s, 0.0722f + 0.9278f * s, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            case "sepia":
                return
                [
                    0.393f + 0.607f * s, 0.769f - 0.769f * s, 0.189f - 0.189f * s, 0, 0,
                    0.349f - 0.349f * s, 0.686f + 0.314f * s, 0.168f - 0.168f * s, 0, 0,
                    0.272f - 0.272f * s, 0.534f - 0.534f * s, 0.131f + 0.869f * s, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            // feColorMatrix type="saturate" (Filter Effects 1 §9.6).
            case "saturate":
                return
                [
                    0.213f + 0.787f * a, 0.715f - 0.715f * a, 0.072f - 0.072f * a, 0, 0,
                    0.213f - 0.213f * a, 0.715f + 0.285f * a, 0.072f - 0.072f * a, 0, 0,
                    0.213f - 0.213f * a, 0.715f - 0.715f * a, 0.072f + 0.928f * a, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            // feColorMatrix type="hueRotate" (Filter Effects 1 §9.6).
            case "hue-rotate":
                var (cos, sin) = (MathF.Cos(a * MathF.PI / 180), MathF.Sin(a * MathF.PI / 180));
                return
                [
                    0.213f + 0.787f * cos - 0.213f * sin, 0.715f - 0.715f * cos - 0.715f * sin, 0.072f - 0.072f * cos + 0.928f * sin, 0, 0,
                    0.213f - 0.213f * cos + 0.143f * sin, 0.715f + 0.285f * cos + 0.140f * sin, 0.072f - 0.072f * cos - 0.283f * sin, 0, 0,
                    0.213f - 0.213f * cos - 0.787f * sin, 0.715f - 0.715f * cos + 0.715f * sin, 0.072f + 0.928f * cos + 0.072f * sin, 0, 0,
                    0, 0, 0, 1, 0,
                ];
            // A table of [amount, 1 - amount]: C' = amount + (1 - 2 × amount) C.
            case "invert":
                return Linear(1 - 2 * (1 - s), 1 - s);
            // An alpha table of [0, amount].
            case "opacity":
                return [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1 - s, 0];
            case "contrast":
                return Linear(a, 0.5f - 0.5f * a);
            default: // brightness
                return Linear(a, 0);
        }
    }

    private static float[] Linear(float slope, float intercept) =>
        [slope, 0, 0, 0, intercept, 0, slope, 0, 0, intercept, 0, 0, slope, 0, intercept, 0, 0, 0, 1, 0];
}

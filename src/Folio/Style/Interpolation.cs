using System.Numerics;
using Folio.Css;

namespace Folio.Style;

/// <summary>
/// Interpolation of computed values by animation type (https://www.w3.org/TR/css-values-4/#combining-values, and each
/// property's definition): numbers, lengths and percentages, colours, transform lists, shadows and filters. Anything
/// else, or a pair the rules cannot combine, is discrete: the caller then takes the start value below half way and
/// the end value from there.
/// </summary>
// ponytail: colours interpolate in premultiplied sRGB (what legacy colours use; modern ones should use Oklab), calc()
// lengths and currentcolor are discrete, and transform lists whose functions do not pair up are discrete rather than
// interpolated as matrices.
internal static class Interpolation
{
    /// <summary>The value <paramref name="p"/> of the way from <paramref name="a"/> to <paramref name="b"/>; null when discrete.</summary>
    public static object? Lerp(object? a, object? b, double p) => (a, b) switch
    {
        (float x, float y) => Num(x, y, p),
        (int x, int y) => (int)Math.Round(x + (y - x) * p),
        (CssColor x, CssColor y) => Color(x, y, p),
        (LengthPercentage x, LengthPercentage y) => Length(x, y, p),
        (SizeValue { Kind: SizeKind.Length } x, SizeValue { Kind: SizeKind.Length } y) => Length(x.Length, y.Length, p) is { } l ? SizeValue.Of(l) : null,
        (LineHeight x, LineHeight y) when !x.IsNormal && !y.IsNormal && (x.Px is null) == (y.Px is null) =>
            new LineHeight(false, Num(x.Number, y.Number, p), x.Px is { } px ? Num(px, y.Px!.Value, p) : null),
        (TransformOrigin x, TransformOrigin y) => Length(x.X, y.X, p) is { } ox && Length(x.Y, y.Y, p) is { } oy ? new TransformOrigin(ox, oy, Num(x.Z, y.Z, p)) : null,
        (BackgroundPosition x, BackgroundPosition y) => Length(x.X, y.X, p) is { } px && Length(x.Y, y.Y, p) is { } py ? new BackgroundPosition(px, py) : null,
        (TransformList x, TransformList y) => Transforms(x, y, p),
        (FilterList x, FilterList y) => Filters(x, y, p),
        (IReadOnlyList<Shadow> x, IReadOnlyList<Shadow> y) => Shadows(x, y, p),
        (IReadOnlyList<BackgroundPosition> x, IReadOnlyList<BackgroundPosition> y) when x.Count == y.Count =>
            Pairwise(x, y, (u, v) => Lerp(u, v, p) is BackgroundPosition w ? w : null),
        _ => null,
    };

    private static float Num(float a, float b, double p) => (float)(a + (b - a) * p);

    private static LengthPercentage? Length(LengthPercentage a, LengthPercentage b, double p) =>
        a.Calc is null && b.Calc is null ? new LengthPercentage(Num(a.Px, b.Px, p), Num(a.Percent, b.Percent, p)) : null;

    // Premultiplied, then divided again (https://www.w3.org/TR/css-color-4/#interpolation-alpha).
    private static CssColor? Color(CssColor a, CssColor b, double p)
    {
        if (a.IsCurrentColor || b.IsCurrentColor)
            return null;
        var alpha = Num(a.A, b.A, p);
        if (alpha <= 0)
            return CssColor.Transparent;
        float C(float x, float y) => Math.Clamp(Num(x * a.A, y * b.A, p) / alpha, 0, 1);
        return new CssColor(C(a.R, b.R), C(a.G, b.G), C(a.B, b.B), Math.Clamp(alpha, 0, 1));
    }

    private static List<T>? Pairwise<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, T?> lerp) where T : struct
    {
        var result = new List<T>(a.Count);
        for (var i = 0; i < a.Count; i++)
        {
            if (lerp(a[i], b[i]) is not { } value)
                return null;
            result.Add(value);
        }
        return result;
    }

    // Lists of different lengths are padded with transparent zero shadows of the other's kind; an outer and an inset
    // shadow do not interpolate (https://www.w3.org/TR/css-backgrounds-3/#box-shadow).
    private static List<Shadow>? Shadows(IReadOnlyList<Shadow> a, IReadOnlyList<Shadow> b, double p)
    {
        var result = new List<Shadow>(Math.Max(a.Count, b.Count));
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var (x, y) = (i < a.Count ? a[i] : Zero(b[i]), i < b.Count ? b[i] : Zero(a[i]));
            if (x.Inset != y.Inset || Color(x.Color, y.Color, p) is not { } color)
                return null;
            result.Add(new Shadow(Num(x.X, y.X, p), Num(x.Y, y.Y, p), Math.Max(0, Num(x.Blur, y.Blur, p)), Num(x.Spread, y.Spread, p), color, x.Inset));
        }
        return result;

        static Shadow Zero(Shadow like) => new(0, 0, 0, 0, CssColor.Transparent, like.Inset);
    }

    // Transform lists (https://www.w3.org/TR/css-transforms-2/#interpolation-of-transform-functions): none and a shorter
    // list take identity functions of the other's kinds; then each pair of the same primitive interpolates.
    private static TransformList? Transforms(TransformList a, TransformList b, double p)
    {
        var count = Math.Max(a.Ops.Count, b.Ops.Count);
        var ops = new List<TransformOp>(count);
        for (var i = 0; i < count; i++)
        {
            var (x, y) = (i < a.Ops.Count ? a.Ops[i] : Identity(b.Ops[i]), i < b.Ops.Count ? b.Ops[i] : Identity(a.Ops[i]));
            if (Transform(x, y, p) is not { } op)
                return null;
            ops.Add(op);
        }
        return new TransformList(ops);
    }

    private static TransformOp? Identity(TransformOp like) => like switch
    {
        TranslateOp => new TranslateOp(LengthPercentage.Zero, LengthPercentage.Zero, 0),
        ScaleOp => new ScaleOp(1, 1, 1),
        RotateOp r => r with { Degrees = 0 },
        SkewOp => new SkewOp(0, 0),
        MatrixOp => new MatrixOp(Matrix4x4.Identity),
        _ => null,
    };

    private static TransformOp? Transform(TransformOp? a, TransformOp? b, double p) => (a, b) switch
    {
        (TranslateOp x, TranslateOp y) => Length(x.X, y.X, p) is { } tx && Length(x.Y, y.Y, p) is { } ty ? new TranslateOp(tx, ty, Num(x.Z, y.Z, p)) : null,
        (ScaleOp x, ScaleOp y) => new ScaleOp(Num(x.X, y.X, p), Num(x.Y, y.Y, p), Num(x.Z, y.Z, p)),
        (RotateOp x, RotateOp y) when Vector3.Normalize(new(x.X, x.Y, x.Z)) == Vector3.Normalize(new(y.X, y.Y, y.Z)) => x with { Degrees = Num(x.Degrees, y.Degrees, p) },
        (SkewOp x, SkewOp y) => new SkewOp(Num(x.XDegrees, y.XDegrees, p), Num(x.YDegrees, y.YDegrees, p)),
        (MatrixOp x, MatrixOp y) when x.Matrix == y.Matrix => x,
        _ => null,
    };

    // Filter lists (https://drafts.fxtf.org/filter-effects-1/#interpolation-of-filters): none and a shorter list take the
    // other's functions at their initial values; then functions of the same name interpolate.
    private static FilterList? Filters(FilterList a, FilterList b, double p)
    {
        var count = Math.Max(a.Functions.Count, b.Functions.Count);
        var functions = new List<FilterFunction>(count);
        for (var i = 0; i < count; i++)
        {
            var (x, y) = (i < a.Functions.Count ? a.Functions[i] : Initial(b.Functions[i]), i < b.Functions.Count ? b.Functions[i] : Initial(a.Functions[i]));
            if (x.Name != y.Name || Color(x.Color, y.Color, p) is not { } color)
                return null;
            functions.Add(new FilterFunction(x.Name, Math.Max(0, Num(x.Amount, y.Amount, p)), Num(x.X, y.X, p), Num(x.Y, y.Y, p), color));
        }
        return new FilterList(functions);

        static FilterFunction Initial(FilterFunction like) => like.Name switch
        {
            "brightness" or "contrast" or "opacity" or "saturate" => new FilterFunction(like.Name, 1),
            "drop-shadow" => new FilterFunction(like.Name, 0, 0, 0, CssColor.Transparent),
            _ => new FilterFunction(like.Name, 0),
        };
    }
}

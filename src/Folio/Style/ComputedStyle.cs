using Folio.Css;

namespace Folio.Style;

internal enum Display
{
    Inline,
    Block,
    InlineBlock,
    FlowRoot,
    ListItem,
    Flex,
    InlineFlex,
    Grid,
    InlineGrid,
    Table,
    InlineTable,
    TableRowGroup,
    TableHeaderGroup,
    TableFooterGroup,
    TableRow,
    TableCell,
    TableColumnGroup,
    TableColumn,
    TableCaption,
    Contents,
    None,
}

internal enum Position { Static, Relative, Absolute, Fixed, Sticky }

internal enum FloatSide { None, Left, Right, InlineStart, InlineEnd }

internal enum Clear { None, Left, Right, Both, InlineStart, InlineEnd }

internal enum BoxSizing { ContentBox, BorderBox }

internal enum Visibility { Visible, Hidden, Collapse }

internal enum Overflow { Visible, Hidden, Clip, Scroll, Auto }

internal enum BorderStyle { None, Hidden, Dotted, Dashed, Solid, Double, Groove, Ridge, Inset, Outset }

internal enum FontStyle { Normal, Italic, Oblique }

/// <summary>
/// A computed length-percentage: <c>Px + Percent% of the basis</c>, or a <c>calc()</c> tree that is not linear
/// in the basis (e.g. <c>min(50%, 300px)</c>), kept with its lengths already in px (docs/study/04-cascade-and-computed-values.md).
/// </summary>
internal readonly record struct LengthPercentage(float Px, float Percent = 0, CalcNode? Calc = null)
{
    public static LengthPercentage Zero => default;

    public bool HasPercent => Percent != 0 || Calc is not null;

    public float Resolve(float basis) => Calc is null ? Px + Percent / 100 * basis : Evaluate(Calc, basis);

    private static float Evaluate(CalcNode node, float basis) => node switch
    {
        CalcNumber n => n.Value,
        CalcLength l => l.Length.Value,
        CalcPercent p => p.Value / 100 * basis,
        CalcSum s => Evaluate(s.Left, basis) + Evaluate(s.Right, basis),
        CalcProduct p => Evaluate(p.Left, basis) * Evaluate(p.Right, basis),
        CalcQuotient q => Evaluate(q.Left, basis) / Evaluate(q.Right, basis),
        CalcMinMax m => m.IsMax ? m.Arguments.Max(a => Evaluate(a, basis)) : m.Arguments.Min(a => Evaluate(a, basis)),
        CalcClamp c => Math.Max(Evaluate(c.Min, basis), Math.Min(Evaluate(c.Value, basis), Evaluate(c.Max, basis))),
        _ => 0,
    };

    public override string ToString()
    {
        static string N(float v) => Math.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Calc is not null ? "calc(...)" : Percent == 0 ? $"{N(Px)}px" : Px == 0 ? $"{N(Percent)}%" : $"{N(Px)}px + {N(Percent)}%";
    }
}

internal enum SizeKind { Length, Auto, None, MinContent, MaxContent, FitContent }

/// <summary>A size or offset: a length-percentage, or one of the keywords the property allows.</summary>
internal readonly record struct SizeValue(SizeKind Kind, LengthPercentage Length = default)
{
    public static SizeValue Auto => new(SizeKind.Auto);
    public static SizeValue None => new(SizeKind.None);

    public static SizeValue Of(LengthPercentage length) => new(SizeKind.Length, length);

    public override string ToString() => Kind switch
    {
        SizeKind.Length => Length.ToString(),
        SizeKind.MinContent => "min-content",
        SizeKind.MaxContent => "max-content",
        SizeKind.FitContent => "fit-content",
        _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary><c>line-height</c>: <c>normal</c>, a number (inherited as a number), or a length in px.</summary>
internal readonly record struct LineHeight(bool IsNormal, float Number, float? Px)
{
    public static LineHeight Normal => new(true, 0, null);

    public override string ToString() => IsNormal ? "normal"
        : Px is { } px ? $"{Math.Round(px, 3).ToString(System.Globalization.CultureInfo.InvariantCulture)}px"
        : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

// Style groups (docs/study/04-cascade-and-computed-values.md, storage option B): immutable, shared by reference
// between elements, replaced whole when any member changes.

/// <summary>Inherited: font properties.</summary>
internal sealed record FontGroup(IReadOnlyList<string> Family, float Size, int Weight, FontStyle Style, LineHeight LineHeight);

/// <summary>Inherited: other inherited properties.</summary>
internal sealed record InheritedGroup(CssColor Color, Visibility Visibility);

internal sealed record BoxGroup(
    Display Display, Position Position, FloatSide Float, Clear Clear, BoxSizing BoxSizing,
    Overflow OverflowX, Overflow OverflowY, int? ZIndex, float Opacity);

internal sealed record SizeGroup(SizeValue Width, SizeValue Height, SizeValue MinWidth, SizeValue MinHeight, SizeValue MaxWidth, SizeValue MaxHeight);

/// <summary>Margins, paddings and insets, each top/right/bottom/left.</summary>
internal sealed record SpacingGroup(
    SizeValue MarginTop, SizeValue MarginRight, SizeValue MarginBottom, SizeValue MarginLeft,
    LengthPercentage PaddingTop, LengthPercentage PaddingRight, LengthPercentage PaddingBottom, LengthPercentage PaddingLeft,
    SizeValue Top, SizeValue Right, SizeValue Bottom, SizeValue Left);

/// <summary>
/// Border widths are stored as specified (in px) and read through the computed accessors, which apply
/// "zero if the style is none or hidden" (https://www.w3.org/TR/css-backgrounds-3/#border-width), so a later
/// style change still finds the width.
/// </summary>
internal sealed record BorderGroup(
    float TopWidthPx, float RightWidthPx, float BottomWidthPx, float LeftWidthPx,
    BorderStyle TopStyle, BorderStyle RightStyle, BorderStyle BottomStyle, BorderStyle LeftStyle,
    CssColor TopColor, CssColor RightColor, CssColor BottomColor, CssColor LeftColor)
{
    public float TopWidth => Visible(TopStyle) ? TopWidthPx : 0;
    public float RightWidth => Visible(RightStyle) ? RightWidthPx : 0;
    public float BottomWidth => Visible(BottomStyle) ? BottomWidthPx : 0;
    public float LeftWidth => Visible(LeftStyle) ? LeftWidthPx : 0;

    private static bool Visible(BorderStyle style) => style is not (BorderStyle.None or BorderStyle.Hidden);
}

internal sealed record BackgroundGroup(CssColor Color);

/// <summary>An element's computed style: references to shared groups.</summary>
internal sealed class ComputedStyle
{
    public required FontGroup Font { get; init; }
    public required InheritedGroup Inherited { get; init; }
    public required BoxGroup Box { get; init; }
    public required SizeGroup Size { get; init; }
    public required SpacingGroup Spacing { get; init; }
    public required BorderGroup Border { get; init; }
    public required BackgroundGroup Background { get; init; }

    /// <summary>The style of the root's parent: every property at its initial value.</summary>
    public static ComputedStyle Initial { get; } = Properties.InitialStyle();
}

/// <summary>What computing a value needs besides the value itself.</summary>
internal sealed class ComputeContext(ComputedStyle parent, float rootFontSize, float viewportWidth, float viewportHeight)
{
    public ComputedStyle Parent { get; } = parent;
    public float RootFontSize { get; } = rootFontSize;
    public float ViewportWidth { get; } = viewportWidth;
    public float ViewportHeight { get; } = viewportHeight;

    /// <summary>The element's own computed font size, once font-size has been computed (em units refer to it).</summary>
    public float FontSize { get; set; } = parent.Font.Size;

    /// <summary>Converts a length to px. In font-size itself, em refers to the parent's font size.</summary>
    // ponytail: ex and ch use the 0.5em fallback the spec allows when font metrics are unavailable;
    // they switch to real metrics when the font system exists (study 11).
    public float ToPx(Length length, bool forFontSize = false)
    {
        var em = forFontSize ? Parent.Font.Size : FontSize;
        return length.Unit switch
        {
            LengthUnit.Px => length.Value,
            LengthUnit.Em => length.Value * em,
            LengthUnit.Rem => length.Value * RootFontSize,
            LengthUnit.Ex or LengthUnit.Ch => length.Value * em / 2,
            LengthUnit.Vw => length.Value * ViewportWidth / 100,
            LengthUnit.Vh => length.Value * ViewportHeight / 100,
            LengthUnit.Vmin => length.Value * Math.Min(ViewportWidth, ViewportHeight) / 100,
            LengthUnit.Vmax => length.Value * Math.Max(ViewportWidth, ViewportHeight) / 100,
            LengthUnit.In => length.Value * 96,
            LengthUnit.Cm => length.Value * 96 / 2.54f,
            LengthUnit.Mm => length.Value * 96 / 25.4f,
            LengthUnit.Q => length.Value * 96 / 101.6f,
            LengthUnit.Pt => length.Value * 96 / 72,
            LengthUnit.Pc => length.Value * 16,
            _ => length.Value,
        };
    }

    /// <summary>Computes a length, percentage or math function; lengths become px, percentages stay.</summary>
    public LengthPercentage LengthPercentage(CssValue value, bool forFontSize = false, bool nonNegative = false)
    {
        var result = value switch
        {
            LengthValue l => new LengthPercentage(ToPx(l.Length, forFontSize)),
            PercentageValue p => new LengthPercentage(0, p.Percent),
            CalcValue c => Calc(c.Node, forFontSize),
            _ => Folio.Style.LengthPercentage.Zero,
        };
        return nonNegative && result.Calc is null && result.Percent == 0 && result.Px < 0 ? Folio.Style.LengthPercentage.Zero : result;
    }

    private LengthPercentage Calc(CalcNode node, bool forFontSize)
    {
        var resolved = ResolveLengths(node, forFontSize);
        return Linear(resolved) is { } linear ? new LengthPercentage(linear.Px, linear.Percent) : new LengthPercentage(0, 0, resolved);
    }

    private CalcNode ResolveLengths(CalcNode node, bool forFontSize) => node switch
    {
        CalcLength l => new CalcLength(new Length(ToPx(l.Length, forFontSize), LengthUnit.Px)),
        CalcSum s => new CalcSum(ResolveLengths(s.Left, forFontSize), ResolveLengths(s.Right, forFontSize)),
        CalcProduct p => new CalcProduct(ResolveLengths(p.Left, forFontSize), ResolveLengths(p.Right, forFontSize)),
        CalcQuotient q => new CalcQuotient(ResolveLengths(q.Left, forFontSize), ResolveLengths(q.Right, forFontSize)),
        CalcMinMax m => new CalcMinMax(m.IsMax, m.Arguments.Select(a => ResolveLengths(a, forFontSize)).ToList()),
        CalcClamp c => new CalcClamp(ResolveLengths(c.Min, forFontSize), ResolveLengths(c.Value, forFontSize), ResolveLengths(c.Max, forFontSize)),
        _ => node,
    };

    // The expression as px + percent%, when it is linear in the percentage basis (sums and scalings).
    private static (float Px, float Percent)? Linear(CalcNode node)
    {
        switch (node)
        {
            case CalcLength l:
                return (l.Length.Value, 0);
            case CalcPercent p:
                return (0, p.Value);
            case CalcNumber:
                return null;
            case CalcSum s:
                return Linear(s.Left) is { } sl && Linear(s.Right) is { } sr ? (sl.Px + sr.Px, sl.Percent + sr.Percent) : null;
            case CalcProduct p:
                if (ValueReader.Evaluate(p.Left) is { } left && Linear(p.Right) is { } pr)
                    return (pr.Px * left, pr.Percent * left);
                if (ValueReader.Evaluate(p.Right) is { } right && Linear(p.Left) is { } pl)
                    return (pl.Px * right, pl.Percent * right);
                return null;
            case CalcQuotient q:
                return ValueReader.Evaluate(q.Right) is { } divisor && Linear(q.Left) is { } ql ? (ql.Px / divisor, ql.Percent / divisor) : null;
            default:
                // min(), max() and clamp() fold only when they hold no percentage.
                return ContainsPercent(node) ? null : (EvaluatePx(node), 0);
        }
    }

    private static bool ContainsPercent(CalcNode node) => node switch
    {
        CalcPercent => true,
        CalcSum s => ContainsPercent(s.Left) || ContainsPercent(s.Right),
        CalcProduct p => ContainsPercent(p.Left) || ContainsPercent(p.Right),
        CalcQuotient q => ContainsPercent(q.Left) || ContainsPercent(q.Right),
        CalcMinMax m => m.Arguments.Any(ContainsPercent),
        CalcClamp c => ContainsPercent(c.Min) || ContainsPercent(c.Value) || ContainsPercent(c.Max),
        _ => false,
    };

    private static float EvaluatePx(CalcNode node) => new LengthPercentage(0, 0, node).Resolve(0);
}

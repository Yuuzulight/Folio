using System.Collections.Immutable;
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

internal enum Isolation { Auto, Isolate }

internal enum VerticalAlignKind { Baseline, Sub, Super, TextTop, TextBottom, Middle, Top, Bottom, Length }

/// <summary>A computed <c>vertical-align</c>: a keyword, or a raise by a length or a percentage of the line height.</summary>
internal readonly record struct VerticalAlign(VerticalAlignKind Kind, LengthPercentage Length = default)
{
    public override string ToString() => Kind == VerticalAlignKind.Length ? Length.ToString()
        : string.Concat(Kind.ToString().Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString()));
}

internal enum BorderStyle { None, Hidden, Dotted, Dashed, Solid, Double, Groove, Ridge, Inset, Outset }

internal enum FontStyle { Normal, Italic, Oblique }

internal enum WhiteSpaceCollapse { Collapse, Preserve, PreserveBreaks, PreserveSpaces, BreakSpaces }

internal enum TextWrapMode { Wrap, Nowrap }

internal enum ListStylePosition { Outside, Inside }

internal enum TextAlign { Start, End, Left, Right, Center, Justify }

internal enum Direction { Ltr, Rtl }

/// <summary>
/// A computed shadow (https://www.w3.org/TR/css-backgrounds-3/#box-shadow, css-text-decor-3 §4): offsets, blur radius
/// and spread in px, and its colour (currentcolor kept symbolic). Text shadows have no spread and are never inset.
/// </summary>
internal readonly record struct Shadow(float X, float Y, float Blur, float Spread, CssColor Color, bool Inset)
{
    public override string ToString()
    {
        static string N(float v) => Math.Round(v, 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + "px";
        return (Inset ? "inset " : "") + $"{N(X)} {N(Y)} {N(Blur)} {N(Spread)} {Color}";
    }
}

/// <summary>Box shadows (not inherited), in the order written: the first is painted on top.</summary>
internal sealed record ShadowGroup(IReadOnlyList<Shadow> Box)
{
    public static ShadowGroup Initial { get; } = new([]);
}

/// <summary>https://www.w3.org/TR/css-text-3/#text-transform-property (full-size-kana is accepted and does nothing)</summary>
internal enum TextTransform { None, Capitalize, Uppercase, Lowercase, FullWidth, FullSizeKana }

/// <summary>https://www.w3.org/TR/css-text-3/#word-break-property</summary>
internal enum WordBreakStyle { Normal, BreakAll, KeepAll, BreakWord }

/// <summary>https://www.w3.org/TR/css-text-3/#overflow-wrap-property</summary>
internal enum OverflowWrap { Normal, BreakWord, Anywhere }

/// <summary>A computed <c>tab-size</c>: a number of spaces, or a length in px.</summary>
internal readonly record struct TabSize(float Value, bool IsLength)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + (IsLength ? "px" : "");
}

/// <summary>Spacing and breaking of text (inherited): letter-spacing, word-spacing, tab-size, word-break, overflow-wrap and text-transform.</summary>
internal sealed record SpacingTextGroup(float LetterSpacing, float WordSpacing, TabSize TabSize, WordBreakStyle WordBreak, OverflowWrap OverflowWrap, TextTransform Transform)
{
    public static SpacingTextGroup Initial { get; } = new(0, 0, new TabSize(8, false), WordBreakStyle.Normal, OverflowWrap.Normal, TextTransform.None);
}

/// <summary>https://www.w3.org/TR/css-ui-4/#outline-style (auto draws as solid)</summary>
internal enum OutlineStyle { Auto, None, Dotted, Dashed, Solid, Double, Groove, Ridge, Inset, Outset }

/// <summary>https://www.w3.org/TR/css-overflow-3/#text-overflow (a string value is drawn as the ellipsis)</summary>
internal enum TextOverflow { Clip, Ellipsis }

/// <summary>Outlines (not inherited). The width reads as zero when the style is none, as for borders.</summary>
internal sealed record OutlineGroup(float WidthPx, OutlineStyle Style, CssColor Color, float Offset)
{
    public static OutlineGroup Initial { get; } = new(3, OutlineStyle.None, CssColor.CurrentColor, 0);

    public float Width => Style == OutlineStyle.None ? 0 : WidthPx;
}

/// <summary>
/// Inherited user interface properties Folio records for interaction (M3) and form controls: cursor, accent-color and
/// scrollbar-color (null is auto).
/// </summary>
internal sealed record UiGroup(string Cursor, CssColor? AccentColor, string ScrollbarColor)
{
    public static UiGroup Initial { get; } = new("auto", null, "auto");
}

internal enum Hyphens { Manual, None, Auto }

/// <summary>A computed <c>text-indent</c> (https://www.w3.org/TR/css-text-3/#text-indent-property).</summary>
internal readonly record struct TextIndent(LengthPercentage Length, bool Hanging = false, bool EachLine = false)
{
    public override string ToString() => Length + (Hanging ? " hanging" : "") + (EachLine ? " each-line" : "");
}

/// <summary>https://www.w3.org/TR/css-images-3/#the-object-fit</summary>
internal enum ObjectFit { Fill, Contain, Cover, None, ScaleDown }

/// <summary>https://www.w3.org/TR/css-images-3/#the-image-rendering (only pixelated and crisp-edges change the drawing)</summary>
internal enum ImageRendering { Auto, Smooth, HighQuality, Pixelated, CrispEdges }

internal enum TableLayoutMode { Auto, Fixed }

internal enum BorderCollapse { Separate, Collapse }

internal enum CaptionSide { Top, Bottom }

internal enum EmptyCells { Show, Hide }

internal enum UnicodeBidi { Normal, Embed, Isolate, BidiOverride, IsolateOverride, Plaintext }

/// <summary>https://www.w3.org/TR/css-text-decor-3/#text-decoration-line-property (blink is accepted and not drawn)</summary>
[Flags]
internal enum TextDecorationLine { None = 0, Underline = 1, Overline = 2, LineThrough = 4, Blink = 8 }

internal enum TextDecorationStyle { Solid, Double, Dotted, Dashed, Wavy }

/// <summary>https://www.w3.org/TR/css-text-decor-4/#text-decoration-skip-ink-property (all acts as auto: ideographs are not told apart)</summary>
internal enum SkipInk { Auto, None, All }

/// <summary>Text decorations (not inherited). A null thickness is auto or from-font: the font's own.</summary>
internal sealed record DecorationGroup(TextDecorationLine Line, TextDecorationStyle Style, CssColor Color, float? Thickness)
{
    public static DecorationGroup Initial { get; } = new(TextDecorationLine.None, TextDecorationStyle.Solid, CssColor.CurrentColor, null);
}

/// <summary>
/// A decoration applied to an element's text by it or an ancestor (its decorating box,
/// https://www.w3.org/TR/css-text-decor-3/#line-decoration), with its colour resolved; <see cref="Outer"/> is the
/// next one out. A null offset is <c>text-underline-offset: auto</c>.
/// </summary>
internal sealed record AppliedDecoration(TextDecorationLine Line, TextDecorationStyle Style, CssColor Color, float? Thickness, float? Offset,
                                         AppliedDecoration? Outer);

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

internal enum SizeKind { Length, Auto, None, MinContent, MaxContent, FitContent, Content }

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
        SizeKind.Content => "content",
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
/// <remarks>
/// <see cref="VariantNumeric"/> and <see cref="FeatureSettings"/> keep their keywords and feature tags for the shapers;
/// neither shaper applies OpenType features yet.
/// </remarks>
internal sealed record FontGroup(IReadOnlyList<string> Family, float Size, int Weight, FontStyle Style, LineHeight LineHeight,
                                 float Stretch, string VariantCaps, string VariantNumeric = "normal", string FeatureSettings = "normal");

/// <summary>Inherited: other inherited properties, and the decorations propagated to the element's text.</summary>
internal sealed record InheritedGroup(CssColor Color, Visibility Visibility, ColorSchemeValue ColorScheme, AppliedDecoration? Decorations = null,
                                      ImageRendering ImageRendering = ImageRendering.Auto);

internal sealed record BoxGroup(
    Display Display, Position Position, FloatSide Float, Clear Clear, BoxSizing BoxSizing,
    Overflow OverflowX, Overflow OverflowY, int? ZIndex, float Opacity, Isolation Isolation = Isolation.Auto,
    VerticalAlign VerticalAlign = default, UnicodeBidi UnicodeBidi = UnicodeBidi.Normal, TableLayoutMode TableLayout = TableLayoutMode.Auto,
    TextOverflow TextOverflow = TextOverflow.Clip, int? LineClamp = null, string ScrollbarGutter = "auto", string ScrollbarWidth = "auto");

/// <summary>A computed corner radius: horizontal and vertical (https://www.w3.org/TR/css-backgrounds-3/#border-radius).</summary>
internal readonly record struct CornerRadius(LengthPercentage X, LengthPercentage Y)
{
    public override string ToString() => X == Y ? X.ToString() : $"{X} {Y}";
}

/// <param name="AspectRatio">The preferred aspect ratio (width / height) from aspect-ratio; null for auto.</param>
internal sealed record SizeGroup(SizeValue Width, SizeValue Height, SizeValue MinWidth, SizeValue MinHeight, SizeValue MaxWidth, SizeValue MaxHeight,
                                 float? AspectRatio = null);

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
    CssColor TopColor, CssColor RightColor, CssColor BottomColor, CssColor LeftColor,
    CornerRadius TopLeftRadius = default, CornerRadius TopRightRadius = default,
    CornerRadius BottomRightRadius = default, CornerRadius BottomLeftRadius = default)
{
    public float TopWidth => Visible(TopStyle) ? TopWidthPx : 0;
    public float RightWidth => Visible(RightStyle) ? RightWidthPx : 0;
    public float BottomWidth => Visible(BottomStyle) ? BottomWidthPx : 0;
    public float LeftWidth => Visible(LeftStyle) ? LeftWidthPx : 0;

    private static bool Visible(BorderStyle style) => style is not (BorderStyle.None or BorderStyle.Hidden);
}

/// <summary>A computed background position: offsets from the left and top edges.</summary>
internal readonly record struct BackgroundPosition(LengthPercentage X, LengthPercentage Y)
{
    public override string ToString() => $"{X} {Y}";
}

internal readonly record struct BackgroundSize(BackgroundSizeKind Kind, SizeValue Width, SizeValue Height)
{
    public override string ToString() => Kind switch
    {
        BackgroundSizeKind.Cover => "cover",
        BackgroundSizeKind.Contain => "contain",
        _ => $"{Width} {Height}",
    };
}

/// <summary>Backgrounds: the colour and per-layer lists (layer count is the image list's; others repeat when shorter).</summary>
internal sealed record BackgroundGroup(
    CssColor Color, IReadOnlyList<ImageValue> Images, IReadOnlyList<BackgroundPosition> Positions, IReadOnlyList<BackgroundSize> Sizes,
    IReadOnlyList<RepeatStyle> Repeats, IReadOnlyList<BackgroundAttachment> Attachments, IReadOnlyList<BackgroundBox> Origins,
    IReadOnlyList<BackgroundBox> Clips);

/// <summary>Inherited: white space handling, alignment, list markers and the inherited table properties.</summary>
internal sealed record TextGroup(WhiteSpaceCollapse WhiteSpaceCollapse, TextWrapMode TextWrapMode, ListStyleType ListStyleType, ListStylePosition ListStylePosition,
                                 TextAlign TextAlign = TextAlign.Start, Direction Direction = Direction.Ltr,
                                 BorderCollapse BorderCollapse = BorderCollapse.Separate, float BorderSpacingX = 0, float BorderSpacingY = 0,
                                 CaptionSide CaptionSide = CaptionSide.Top, EmptyCells EmptyCells = EmptyCells.Show,
                                 float? UnderlineOffset = null,
                                 TextIndent TextIndent = default, TextAlign? TextAlignLast = null, Hyphens Hyphens = Hyphens.Manual,
                                 ImageValue? ListStyleImage = null, IReadOnlyList<Shadow>? TextShadows = null, SkipInk SkipInk = SkipInk.Auto);

internal enum FlexDirection { Row, RowReverse, Column, ColumnReverse }

internal enum FlexWrap { Nowrap, Wrap, WrapReverse }

/// <summary>justify-content and align-content (css-align-3 §4 and §5).</summary>
internal enum ContentAlign { Normal, FlexStart, FlexEnd, Center, SpaceBetween, SpaceAround, SpaceEvenly, Stretch, Start, End, Left, Right }

/// <summary>align-items and align-self (css-align-3 §6); Auto only for align-self.</summary>
internal enum ItemAlign { Auto, Normal, Stretch, FlexStart, FlexEnd, Center, Baseline, LastBaseline, Start, End, SelfStart, SelfEnd }

/// <summary>Flexible box layout properties, and the gaps grid shares (not inherited).</summary>
internal sealed record FlexGroup(
    FlexDirection Direction, FlexWrap Wrap, ContentAlign JustifyContent, ItemAlign AlignItems, ItemAlign AlignSelf,
    ContentAlign AlignContent, float Grow, float Shrink, SizeValue Basis, int Order, LengthPercentage RowGap, LengthPercentage ColumnGap)
{
    public static FlexGroup Initial { get; } = new(FlexDirection.Row, FlexWrap.Nowrap, ContentAlign.Normal, ItemAlign.Normal, ItemAlign.Auto,
        ContentAlign.Normal, 0, 1, SizeValue.Auto, 0, default, default);
}

/// <summary>Inherited: the quotation marks of open-quote and close-quote, outermost pair first; null is auto.</summary>
internal sealed record QuotesGroup(IReadOnlyList<(string Open, string Close)>? Pairs)
{
    public static QuotesGroup Initial { get; } = new((IReadOnlyList<(string, string)>?)null);

    public override string ToString() => Pairs is null ? "auto" : Pairs.Count == 0 ? "none"
        : string.Join(" ", Pairs.Select(p => $"\"{p.Open}\" \"{p.Close}\""));
}

/// <summary>How replaced content fits its box (not inherited): object-fit and object-position.</summary>
internal sealed record ReplacedGroup(ObjectFit Fit, BackgroundPosition Position)
{
    public static ReplacedGroup Initial { get; } = new(ObjectFit.Fill, new BackgroundPosition(new LengthPercentage(0, 50), new LengthPercentage(0, 50)));
}

/// <summary>Generated content and counters (not inherited).</summary>
internal sealed record GeneratedGroup(ContentValue Content, IReadOnlyList<CounterChange> CounterReset, IReadOnlyList<CounterChange> CounterIncrement, IReadOnlyList<CounterChange> CounterSet);

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
    public required TextGroup Text { get; init; }
    public required GeneratedGroup Generated { get; init; }
    public FlexGroup Flex { get; init; } = FlexGroup.Initial;
    public GridGroup Grid { get; init; } = GridGroup.Initial;
    public ShadowGroup Shadows { get; init; } = ShadowGroup.Initial;
    public SpacingTextGroup TextSpacing { get; init; } = SpacingTextGroup.Initial;
    public QuotesGroup Quotes { get; init; } = QuotesGroup.Initial;
    public OutlineGroup Outline { get; init; } = OutlineGroup.Initial;
    public UiGroup Ui { get; init; } = UiGroup.Initial;
    public DecorationGroup Decoration { get; init; } = DecorationGroup.Initial;
    public TransformGroup Transform { get; init; } = TransformGroup.Initial;
    public ReplacedGroup Replaced { get; init; } = ReplacedGroup.Initial;
    public EffectsGroup Effects { get; init; } = EffectsGroup.Initial;
    public MaskGroup Mask { get; init; } = MaskGroup.Initial;

    /// <summary>Custom properties (inherited): name to value text, after var() substitution.</summary>
    public ImmutableDictionary<string, string> Custom { get; init; } = ImmutableDictionary.Create<string, string>(StringComparer.Ordinal);

    /// <summary>The style of the root's parent: every property at its initial value.</summary>
    public static ComputedStyle Initial { get; } = Properties.InitialStyle();
}

/// <summary>
/// The x-height and the advance of the "0" glyph of a font group's first available font, as fractions of the font
/// size (0 when the font does not have one); null when no font is available. Layout provides it.
/// </summary>
internal delegate (float XHeight, float ZeroAdvance)? FontMeasure(FontGroup font);

/// <summary>What computing a value needs besides the value itself.</summary>
internal sealed class ComputeContext(ComputedStyle parent, float rootFontSize, float viewportWidth, float viewportHeight)
{
    public ComputedStyle Parent { get; } = parent;
    public float RootFontSize { get; } = rootFontSize;
    public float ViewportWidth { get; } = viewportWidth;
    public float ViewportHeight { get; } = viewportHeight;

    /// <summary>The host's preferred colour scheme (prefers-color-scheme).</summary>
    public bool PrefersDark { get; init; }

    /// <summary>The <c>@property</c> registrations in effect, whose custom properties compute for their syntax.</summary>
    public IReadOnlyDictionary<string, RegisteredProperty>? Registered { get; init; }

    /// <summary>Whether the element uses the dark scheme (its color-scheme and the preference), for light-dark().</summary>
    public bool UsesDark { get; set; }

    /// <summary>The element's computed color, once computed (currentcolor in other properties' expressions).</summary>
    public CssColor CurrentColor { get; set; } = parent.Inherited.Color;

    /// <summary>
    /// Computes a colour: a plain colour keeps currentcolor symbolic; an expression resolves against
    /// <paramref name="currentColor"/> and the used colour scheme.
    /// </summary>
    public CssColor Color(CssValue value, CssColor currentColor) => value switch
    {
        ColorExpressionValue e => ColorResolver.Resolve(e.Expression, currentColor, UsesDark),
        ColorValue c => c.Color,
        _ => CssColor.CurrentColor,
    };

    /// <summary>The element's computed custom properties, used to substitute var() in other properties.</summary>
    public ImmutableDictionary<string, string> Custom { get; set; } = parent.Custom;

    /// <summary>The element's own computed font size, once font-size has been computed (em units refer to it).</summary>
    public float FontSize { get; set; } = parent.Font.Size;

    /// <summary>Measures the first available font for ex and ch; without one, both are 0.5em.</summary>
    public FontMeasure? Measure { get; init; }

    /// <summary>The element's own font properties, once computed (ex and ch measure its first available font).</summary>
    public FontGroup? Font { get; set; }

    /// <summary>Converts a length to px. In font-size itself, em, ex and ch refer to the parent's font.</summary>
    public float ToPx(Length length, bool forFontSize = false)
    {
        var em = forFontSize ? Parent.Font.Size : FontSize;
        return length.Unit switch
        {
            LengthUnit.Px => length.Value,
            LengthUnit.Em => length.Value * em,
            LengthUnit.Rem => length.Value * RootFontSize,
            LengthUnit.Ex or LengthUnit.Ch => length.Value * em * FontRatio(length.Unit, forFontSize ? Parent.Font : Font ?? Parent.Font),
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

    // https://www.w3.org/TR/css-values-4/#font-relative-lengths: the x-height and the advance of "0" of the first
    // available font, as fractions of its size; 0.5 when it has none or none can be measured.
    private float FontRatio(LengthUnit unit, FontGroup font)
    {
        if (Measure?.Invoke(font) is not { } metrics)
            return 0.5f;
        var ratio = unit == LengthUnit.Ex ? metrics.XHeight : metrics.ZeroAdvance;
        return ratio > 0 ? ratio : 0.5f;
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

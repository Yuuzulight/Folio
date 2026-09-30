using Folio.Style;

namespace Folio.Css;

/// <summary>Longhand properties known to Folio; the table in <see cref="Properties"/> gives each its grammar and computation.</summary>
internal enum PropertyId
{
    Display,
    Position,
    Float,
    Clear,
    BoxSizing,
    Visibility,
    OverflowX,
    OverflowY,
    ZIndex,
    Opacity,
    Width,
    Height,
    MinWidth,
    MinHeight,
    MaxWidth,
    MaxHeight,
    MarginTop,
    MarginRight,
    MarginBottom,
    MarginLeft,
    PaddingTop,
    PaddingRight,
    PaddingBottom,
    PaddingLeft,
    Top,
    Right,
    Bottom,
    Left,
    BorderTopWidth,
    BorderRightWidth,
    BorderBottomWidth,
    BorderLeftWidth,
    BorderTopStyle,
    BorderRightStyle,
    BorderBottomStyle,
    BorderLeftStyle,
    BorderTopColor,
    BorderRightColor,
    BorderBottomColor,
    BorderLeftColor,
    Color,
    BackgroundColor,
    FontFamily,
    FontSize,
    FontWeight,
    FontStyle,
    LineHeight,
    WhiteSpaceCollapse,
    TextWrapMode,
    ListStyleType,
    ListStylePosition,
    Content,
    CounterReset,
    CounterIncrement,
    CounterSet,
    ColorScheme,
    BackgroundImage,
    BackgroundPosition,
    BackgroundSize,
    BackgroundRepeat,
    BackgroundAttachment,
    BackgroundOrigin,
    BackgroundClip,
    FontStretch,
    FontVariantCaps,
    BorderTopLeftRadius,
    BorderTopRightRadius,
    BorderBottomRightRadius,
    BorderBottomLeftRadius,
    Isolation,
    TextAlign,
    VerticalAlign,
    Direction,
    UnicodeBidi,
    FlexDirection,
    FlexWrap,
    JustifyContent,
    AlignItems,
    AlignSelf,
    AlignContent,
    FlexGrow,
    FlexShrink,
    FlexBasis,
    Order,
    RowGap,
    ColumnGap,
    GridTemplateColumns,
    GridTemplateRows,
    GridAutoColumns,
    GridAutoRows,
    GridAutoFlow,
    GridRowStart,
    GridRowEnd,
    GridColumnStart,
    GridColumnEnd,
    JustifyItems,
    JustifySelf,
    GridTemplateAreas,
    TableLayout,
    BorderCollapse,
    BorderSpacing,
    CaptionSide,
    EmptyCells,
    TextDecorationLine,
    TextDecorationStyle,
    TextDecorationColor,
    TextDecorationThickness,
    TextUnderlineOffset,
    TextIndent,
    TextAlignLast,
    Hyphens,
    BoxShadow,
    TextShadow,
    LetterSpacing,
    WordSpacing,
    TabSize,
    WordBreak,
    OverflowWrap,
    TextTransform,
    FontVariantNumeric,
    FontFeatureSettings,
    Quotes,
    OutlineWidth,
    OutlineStyle,
    OutlineColor,
    OutlineOffset,
    AspectRatio,
    TextOverflow,
    LineClamp,
    Cursor,
    AccentColor,
    ScrollbarGutter,
    ScrollbarWidth,
    ScrollbarColor,
    ListStyleImage,
    Transform,
    Translate,
    Rotate,
    Scale,
    TransformOrigin,
    ObjectFit,
    ObjectPosition,
    ImageRendering,
    TextDecorationSkipInk,
    Filter,
    BackdropFilter,
    MixBlendMode,
    BackgroundBlendMode,
    ClipPath,
}

/// <summary>One longhand: its grammar, initial value, inheritance and how its computed value is stored.</summary>
internal abstract class Property(PropertyId id, string name, bool inherited, string initialText)
{
    public PropertyId Id { get; } = id;
    public string Name { get; } = name;
    public bool Inherited { get; } = inherited;

    /// <summary>The initial value, parsed from the table's text with the property's own grammar.</summary>
    public CssValue Initial { get; private set; } = null!;

    internal void InitializeInitial()
    {
        var (source, values) = CssParser.ParseComponentValues(initialText);
        Initial = Parse(new ValueReader(source, values)) ?? throw new InvalidOperationException($"Bad initial value for {Name}.");
    }

    /// <summary>Parses the whole value (not CSS-wide keywords); null if invalid.</summary>
    public abstract CssValue? Parse(ValueReader reader);

    /// <summary>Computes a specified value (not a CSS-wide keyword) into the builder.</summary>
    public abstract void Apply(StyleBuilder builder, CssValue value, ComputeContext context);

    /// <summary>Copies the parent's computed value.</summary>
    public abstract void Inherit(StyleBuilder builder, ComputedStyle parent);

    /// <summary>The computed value as text, for tests and diagnostics.</summary>
    public abstract string Describe(ComputedStyle style);
}

internal sealed class Property<T>(
    PropertyId id, string name, bool inherited, string initial,
    Func<ValueReader, CssValue?> parse,
    Func<CssValue, ComputeContext, T> compute,
    Func<ComputedStyle, T> get,
    Action<StyleBuilder, T> set) : Property(id, name, inherited, initial)
{
    public override CssValue? Parse(ValueReader reader)
    {
        var value = parse(reader);
        return value is not null && reader.AtEnd ? value : null;
    }

    public override void Apply(StyleBuilder builder, CssValue value, ComputeContext context) => set(builder, compute(value, context));

    public override void Inherit(StyleBuilder builder, ComputedStyle parent) => set(builder, get(parent));

    public override string Describe(ComputedStyle style) => Format(get(style));

    private static string Format(object? value) => value switch
    {
        null => "auto",
        float f => Math.Round(f, 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Enum e => string.Join(" ", e.ToString().Split(", ").Select(name => string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString())))),
        IReadOnlyList<string> list => string.Join(", ", list),
        IReadOnlyList<CounterChange> counters => counters.Count == 0 ? "none" : string.Join(" ", counters),
        string text => text,
        System.Collections.IEnumerable items => string.Join(", ", items.Cast<object?>().Select(Format)),
        _ => value.ToString() ?? "",
    };
}

/// <summary>
/// The property table (docs/study/03-css-parsing-and-selectors.md: one place per property for grammar, initial value,
/// inheritance and computed storage) and the shorthands that expand into its longhands.
/// </summary>
internal static class Properties
{
    private static readonly Property[] Table;
    private static readonly Dictionary<string, Property> ByName;

    // A static constructor runs after every field initializer, so the keyword tables below exist by then.
    static Properties()
    {
        Table = BuildTable();
        ByName = Table.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public static IReadOnlyList<Property> All => Table;

    public static Property Get(PropertyId id) => Table[(int)id];

    public static Property? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>
    /// Parses a declaration into longhand values: CSS-wide keywords, longhands, or shorthands expanded.
    /// Null for an unknown property or an invalid value (the declaration is then dropped).
    /// </summary>
    public static List<(PropertyId Id, CssValue Value)>? Parse(string source, Declaration declaration)
    {
        var longhand = Find(declaration.Name);
        var shorthand = longhand is null ? Shorthands.GetValueOrDefault(declaration.Name) : null;
        if (longhand is null && shorthand is null)
            return null;

        var reader = new ValueReader(source, declaration.Value);
        if (reader.Keyword("initial", "inherit", "unset", "revert", "revert-layer") is { } wide && reader.AtEnd)
        {
            var keyword = new CssWideValue(wide switch
            {
                "initial" => CssWideKeyword.Initial,
                "inherit" => CssWideKeyword.Inherit,
                "unset" => CssWideKeyword.Unset,
                "revert" => CssWideKeyword.Revert,
                _ => CssWideKeyword.RevertLayer,
            });
            return longhand is not null ? [(longhand.Id, keyword)] : shorthand!.Longhands.Select(id => (id, (CssValue)keyword)).ToList();
        }

        if (ContainsVar(declaration.Value))
        {
            var text = declaration.Value.Count == 0 ? "" : source[declaration.Value[0].Start..declaration.Value[^1].End];
            return longhand is not null
                ? [(longhand.Id, new UnparsedValue(text, null))]
                : shorthand!.Longhands.Select(id => (id, (CssValue)new UnparsedValue(text, declaration.Name))).ToList();
        }

        reader = new ValueReader(source, declaration.Value);
        if (longhand is not null)
            return longhand.Parse(reader) is { } value ? [(longhand.Id, value)] : null;
        var expanded = shorthand!.Expand(reader);
        return expanded is not null && reader.AtEnd ? expanded : null;
    }

    /// <summary>Longhands a shorthand sets, or null for a name that is not a shorthand.</summary>
    public static IReadOnlyList<PropertyId>? LonghandsOf(string shorthand) => Shorthands.GetValueOrDefault(shorthand)?.Longhands;

    public static bool ContainsVar(List<ComponentValue> values) => values.Any(v => v switch
    {
        CssFunction f => f.Name.Equals("var", StringComparison.OrdinalIgnoreCase) || ContainsVar(f.Arguments),
        SimpleBlock b => ContainsVar(b.Contents),
        _ => false,
    });

    // ---------------------------------------------------------------- table

    private static Property[] BuildTable()
    {
        var rows = new List<Property>
        {
            Keywords(PropertyId.Display, "display", false, "inline", DisplayKeywords, b => b.Box.Display, (b, v) => b.Box = b.Box with { Display = v }),
            Keywords(PropertyId.Position, "position", false, "static", Enum<Position>("static", "relative", "absolute", "fixed", "sticky"), s => s.Box.Position, (b, v) => b.Box = b.Box with { Position = v }),
            Keywords(PropertyId.Float, "float", false, "none", Enum<FloatSide>("none", "left", "right", "inline-start", "inline-end"), s => s.Box.Float, (b, v) => b.Box = b.Box with { Float = v }),
            Keywords(PropertyId.Clear, "clear", false, "none", Enum<Clear>("none", "left", "right", "both", "inline-start", "inline-end"), s => s.Box.Clear, (b, v) => b.Box = b.Box with { Clear = v }),
            Keywords(PropertyId.BoxSizing, "box-sizing", false, "content-box", Enum<BoxSizing>("content-box", "border-box"), s => s.Box.BoxSizing, (b, v) => b.Box = b.Box with { BoxSizing = v }),
            Keywords(PropertyId.Visibility, "visibility", true, "visible", Enum<Visibility>("visible", "hidden", "collapse"), s => s.Inherited.Visibility, (b, v) => b.Inherited = b.Inherited with { Visibility = v }),
            Keywords(PropertyId.OverflowX, "overflow-x", false, "visible", OverflowKeywords, s => s.Box.OverflowX, (b, v) => b.Box = b.Box with { OverflowX = v }),
            Keywords(PropertyId.OverflowY, "overflow-y", false, "visible", OverflowKeywords, s => s.Box.OverflowY, (b, v) => b.Box = b.Box with { OverflowY = v }),
            new Property<int?>(PropertyId.ZIndex, "z-index", false, "auto",
                r => r.Keyword("auto") is not null ? new KeywordValue("auto") : r.Integer() is { } i ? new NumberValue(i) : null,
                (v, _) => v is NumberValue n ? (int)n.Number : null,
                s => s.Box.ZIndex, (b, v) => b.Box = b.Box with { ZIndex = v }),
            new Property<float>(PropertyId.Opacity, "opacity", false, "1",
                r => r.Number() is { } n ? new NumberValue(n) : r.LengthPercentage() is PercentageValue p ? p : null,
                (v, _) => Math.Clamp(v is PercentageValue p ? p.Percent / 100 : ((NumberValue)v).Number, 0, 1),
                s => s.Box.Opacity, (b, v) => b.Box = b.Box with { Opacity = v }),

            Size(PropertyId.Width, "width", "auto", "auto", s => s.Size.Width, (b, v) => b.Size = b.Size with { Width = v }),
            Size(PropertyId.Height, "height", "auto", "auto", s => s.Size.Height, (b, v) => b.Size = b.Size with { Height = v }),
            Size(PropertyId.MinWidth, "min-width", "auto", "auto", s => s.Size.MinWidth, (b, v) => b.Size = b.Size with { MinWidth = v }),
            Size(PropertyId.MinHeight, "min-height", "auto", "auto", s => s.Size.MinHeight, (b, v) => b.Size = b.Size with { MinHeight = v }),
            Size(PropertyId.MaxWidth, "max-width", "none", "none", s => s.Size.MaxWidth, (b, v) => b.Size = b.Size with { MaxWidth = v }),
            Size(PropertyId.MaxHeight, "max-height", "none", "none", s => s.Size.MaxHeight, (b, v) => b.Size = b.Size with { MaxHeight = v }),

            Offset(PropertyId.MarginTop, "margin-top", "0", s => s.Spacing.MarginTop, (b, v) => b.Spacing = b.Spacing with { MarginTop = v }),
            Offset(PropertyId.MarginRight, "margin-right", "0", s => s.Spacing.MarginRight, (b, v) => b.Spacing = b.Spacing with { MarginRight = v }),
            Offset(PropertyId.MarginBottom, "margin-bottom", "0", s => s.Spacing.MarginBottom, (b, v) => b.Spacing = b.Spacing with { MarginBottom = v }),
            Offset(PropertyId.MarginLeft, "margin-left", "0", s => s.Spacing.MarginLeft, (b, v) => b.Spacing = b.Spacing with { MarginLeft = v }),
            Padding(PropertyId.PaddingTop, "padding-top", s => s.Spacing.PaddingTop, (b, v) => b.Spacing = b.Spacing with { PaddingTop = v }),
            Padding(PropertyId.PaddingRight, "padding-right", s => s.Spacing.PaddingRight, (b, v) => b.Spacing = b.Spacing with { PaddingRight = v }),
            Padding(PropertyId.PaddingBottom, "padding-bottom", s => s.Spacing.PaddingBottom, (b, v) => b.Spacing = b.Spacing with { PaddingBottom = v }),
            Padding(PropertyId.PaddingLeft, "padding-left", s => s.Spacing.PaddingLeft, (b, v) => b.Spacing = b.Spacing with { PaddingLeft = v }),
            Offset(PropertyId.Top, "top", "auto", s => s.Spacing.Top, (b, v) => b.Spacing = b.Spacing with { Top = v }),
            Offset(PropertyId.Right, "right", "auto", s => s.Spacing.Right, (b, v) => b.Spacing = b.Spacing with { Right = v }),
            Offset(PropertyId.Bottom, "bottom", "auto", s => s.Spacing.Bottom, (b, v) => b.Spacing = b.Spacing with { Bottom = v }),
            Offset(PropertyId.Left, "left", "auto", s => s.Spacing.Left, (b, v) => b.Spacing = b.Spacing with { Left = v }),

            BorderWidth(PropertyId.BorderTopWidth, "border-top-width", s => s.Border.TopWidth, (b, v) => b.Border = b.Border with { TopWidthPx = v }),
            BorderWidth(PropertyId.BorderRightWidth, "border-right-width", s => s.Border.RightWidth, (b, v) => b.Border = b.Border with { RightWidthPx = v }),
            BorderWidth(PropertyId.BorderBottomWidth, "border-bottom-width", s => s.Border.BottomWidth, (b, v) => b.Border = b.Border with { BottomWidthPx = v }),
            BorderWidth(PropertyId.BorderLeftWidth, "border-left-width", s => s.Border.LeftWidth, (b, v) => b.Border = b.Border with { LeftWidthPx = v }),
            Keywords(PropertyId.BorderTopStyle, "border-top-style", false, "none", BorderStyleKeywords, s => s.Border.TopStyle, (b, v) => b.Border = b.Border with { TopStyle = v }),
            Keywords(PropertyId.BorderRightStyle, "border-right-style", false, "none", BorderStyleKeywords, s => s.Border.RightStyle, (b, v) => b.Border = b.Border with { RightStyle = v }),
            Keywords(PropertyId.BorderBottomStyle, "border-bottom-style", false, "none", BorderStyleKeywords, s => s.Border.BottomStyle, (b, v) => b.Border = b.Border with { BottomStyle = v }),
            Keywords(PropertyId.BorderLeftStyle, "border-left-style", false, "none", BorderStyleKeywords, s => s.Border.LeftStyle, (b, v) => b.Border = b.Border with { LeftStyle = v }),
            Color(PropertyId.BorderTopColor, "border-top-color", "currentcolor", s => s.Border.TopColor, (b, v) => b.Border = b.Border with { TopColor = v }),
            Color(PropertyId.BorderRightColor, "border-right-color", "currentcolor", s => s.Border.RightColor, (b, v) => b.Border = b.Border with { RightColor = v }),
            Color(PropertyId.BorderBottomColor, "border-bottom-color", "currentcolor", s => s.Border.BottomColor, (b, v) => b.Border = b.Border with { BottomColor = v }),
            Color(PropertyId.BorderLeftColor, "border-left-color", "currentcolor", s => s.Border.LeftColor, (b, v) => b.Border = b.Border with { LeftColor = v }),

            // color: currentcolor means the parent's colour (https://www.w3.org/TR/css-color-4/#resolving-other-colors).
            new Property<CssColor>(PropertyId.Color, "color", true, "black",
                r => r.ColorSpecified(),
                (v, ctx) => ctx.Color(v, ctx.Parent.Inherited.Color).Resolve(ctx.Parent.Inherited.Color),
                s => s.Inherited.Color, (b, v) => b.Inherited = b.Inherited with { Color = v }),
            Color(PropertyId.BackgroundColor, "background-color", "transparent", s => s.Background.Color, (b, v) => b.Background = b.Background with { Color = v }),

            new Property<IReadOnlyList<string>>(PropertyId.FontFamily, "font-family", true, "serif",
                r => r.FontFamily(),
                (v, _) => ((FontFamilyValue)v).Families,
                s => s.Font.Family, (b, v) => b.Font = b.Font with { Family = v }),
            new Property<float>(PropertyId.FontSize, "font-size", true, "medium",
                r => r.Keyword(FontSizeKeywords.Keys.ToArray()) is { } k ? new KeywordValue(k)
                    : r.Keyword("smaller", "larger") is { } rel ? new KeywordValue(rel)
                    : r.LengthPercentage(nonNegative: true),
                ComputeFontSize,
                s => s.Font.Size, (b, v) => b.Font = b.Font with { Size = v }),
            new Property<int>(PropertyId.FontWeight, "font-weight", true, "normal",
                r => r.Keyword("normal", "bold", "bolder", "lighter") is { } k ? new KeywordValue(k)
                    : r.Number() is { } n && n is >= 1 and <= 1000 ? new NumberValue(n) : null,
                ComputeFontWeight,
                s => s.Font.Weight, (b, v) => b.Font = b.Font with { Weight = v }),
            Keywords(PropertyId.FontStyle, "font-style", true, "normal", Enum<Style.FontStyle>("normal", "italic", "oblique"), s => s.Font.Style, (b, v) => b.Font = b.Font with { Style = v }),
            new Property<LineHeight>(PropertyId.LineHeight, "line-height", true, "normal",
                r => r.Keyword("normal") is not null ? new KeywordValue("normal")
                    : r.Number(nonNegative: true) is { } n ? new NumberValue(n)
                    : r.LengthPercentage(nonNegative: true),
                (v, ctx) => v switch
                {
                    KeywordValue => LineHeight.Normal,
                    NumberValue n => new LineHeight(false, n.Number, null),
                    _ => new LineHeight(false, 0, ctx.LengthPercentage(v, nonNegative: true).Resolve(ctx.FontSize)),
                },
                s => s.Font.LineHeight, (b, v) => b.Font = b.Font with { LineHeight = v }),

            Keywords(PropertyId.WhiteSpaceCollapse, "white-space-collapse", true, "collapse",
                Enum<WhiteSpaceCollapse>("collapse", "preserve", "preserve-breaks", "preserve-spaces", "break-spaces"),
                s => s.Text.WhiteSpaceCollapse, (b, v) => b.Text = b.Text with { WhiteSpaceCollapse = v }),
            Keywords(PropertyId.TextWrapMode, "text-wrap-mode", true, "wrap", Enum<TextWrapMode>("wrap", "nowrap"),
                s => s.Text.TextWrapMode, (b, v) => b.Text = b.Text with { TextWrapMode = v }),
            new Property<ListStyleType>(PropertyId.ListStyleType, "list-style-type", true, "disc",
                GeneratedContentParsing.ListStyleType,
                (v, _) => ((ListStyleTypeValue)v).Type,
                s => s.Text.ListStyleType, (b, v) => b.Text = b.Text with { ListStyleType = v }),
            Keywords(PropertyId.ListStylePosition, "list-style-position", true, "outside", Enum<ListStylePosition>("outside", "inside"),
                s => s.Text.ListStylePosition, (b, v) => b.Text = b.Text with { ListStylePosition = v }),
            new Property<ContentValue>(PropertyId.Content, "content", false, "normal",
                GeneratedContentParsing.Content,
                (v, _) => ((ContentSpecified)v).Content,
                s => s.Generated.Content, (b, v) => b.Generated = b.Generated with { Content = v }),
            Counters(PropertyId.CounterReset, "counter-reset", 0, allowReversed: true,
                s => s.Generated.CounterReset, (b, v) => b.Generated = b.Generated with { CounterReset = v }),
            Counters(PropertyId.CounterIncrement, "counter-increment", 1, allowReversed: false,
                s => s.Generated.CounterIncrement, (b, v) => b.Generated = b.Generated with { CounterIncrement = v }),
            Counters(PropertyId.CounterSet, "counter-set", 0, allowReversed: false,
                s => s.Generated.CounterSet, (b, v) => b.Generated = b.Generated with { CounterSet = v }),
            // https://www.w3.org/TR/css-color-adjust-1/#color-scheme-prop
            new Property<ColorSchemeValue>(PropertyId.ColorScheme, "color-scheme", true, "normal",
                r =>
                {
                    if (r.Keyword("normal") is not null)
                        return new ColorSchemeSpecified(ColorSchemeValue.Normal);
                    bool light = false, dark = false, only = false, any = false;
                    while (r.Ident() is { } word)
                    {
                        any = true;
                        switch (word.ToLowerInvariant())
                        {
                            case "light": light = true; break;
                            case "dark": dark = true; break;
                            case "only": only = true; break;
                            case "normal": return null;
                        }
                    }
                    return any && r.AtEnd ? new ColorSchemeSpecified(new ColorSchemeValue(light, dark, only)) : null;
                },
                (v, _) => ((ColorSchemeSpecified)v).Scheme,
                s => s.Inherited.ColorScheme, (b, v) => b.Inherited = b.Inherited with { ColorScheme = v }),

            new Property<IReadOnlyList<ImageValue>>(PropertyId.BackgroundImage, "background-image", false, "none",
                BackgroundParsing.ImageList,
                (v, ctx) => ((LayerListValue<ImageValue>)v).Items
                    .Select(i => i is GradientImage g ? g with { Computed = GradientParsing.Compute(g.Specified, ctx) } : i).ToList(),
                s => s.Background.Images, (b, v) => b.Background = b.Background with { Images = v }),
            Layers<PositionSpecified, Style.BackgroundPosition>(PropertyId.BackgroundPosition, "background-position", "0% 0%",
                BackgroundParsing.Position,
                (p, ctx) => new Style.BackgroundPosition(FromEdge(ctx.LengthPercentage(p.X), p.XFromEnd), FromEdge(ctx.LengthPercentage(p.Y), p.YFromEnd)),
                s => s.Background.Positions, (b, v) => b.Background = b.Background with { Positions = v }),
            Layers<SizeSpecified, BackgroundSize>(PropertyId.BackgroundSize, "background-size", "auto",
                BackgroundParsing.Size,
                (z, ctx) => new BackgroundSize(z.Kind,
                    z.Width is null ? SizeValue.Auto : SizeValue.Of(ctx.LengthPercentage(z.Width, nonNegative: true)),
                    z.Height is null ? SizeValue.Auto : SizeValue.Of(ctx.LengthPercentage(z.Height, nonNegative: true))),
                s => s.Background.Sizes, (b, v) => b.Background = b.Background with { Sizes = v }),
            Layers<RepeatStyle, RepeatStyle>(PropertyId.BackgroundRepeat, "background-repeat", "repeat",
                BackgroundParsing.Repeat, (x, _) => x,
                s => s.Background.Repeats, (b, v) => b.Background = b.Background with { Repeats = v }),
            Layers<BackgroundAttachment, BackgroundAttachment>(PropertyId.BackgroundAttachment, "background-attachment", "scroll",
                BackgroundParsing.Attachment, (x, _) => x,
                s => s.Background.Attachments, (b, v) => b.Background = b.Background with { Attachments = v }),
            Layers<BackgroundBox, BackgroundBox>(PropertyId.BackgroundOrigin, "background-origin", "padding-box",
                r => BackgroundParsing.Box(r, allowText: false), (x, _) => x,
                s => s.Background.Origins, (b, v) => b.Background = b.Background with { Origins = v }),
            Layers<BackgroundBox, BackgroundBox>(PropertyId.BackgroundClip, "background-clip", "border-box",
                r => BackgroundParsing.Box(r, allowText: true), (x, _) => x,
                s => s.Background.Clips, (b, v) => b.Background = b.Background with { Clips = v }),

            // https://www.w3.org/TR/css-fonts-4/#font-stretch-prop (as a percentage of normal)
            new Property<float>(PropertyId.FontStretch, "font-stretch", true, "normal",
                r => r.Keyword(FontStretchKeywords.Keys.ToArray()) is { } k ? new PercentageValue(FontStretchKeywords[k])
                    : r.LengthPercentage(allowPercent: true, nonNegative: true) is PercentageValue p ? p : null,
                (v, _) => ((PercentageValue)v).Percent,
                s => s.Font.Stretch, (b, v) => b.Font = b.Font with { Stretch = v }),
            new Property<string>(PropertyId.FontVariantCaps, "font-variant-caps", true, "normal",
                r => r.Keyword("normal", "small-caps", "all-small-caps", "petite-caps", "all-petite-caps", "unicase", "titling-caps") is { } k ? new KeywordValue(k) : null,
                (v, _) => ((KeywordValue)v).Keyword,
                s => s.Font.VariantCaps, (b, v) => b.Font = b.Font with { VariantCaps = v }),

            Radius(PropertyId.BorderTopLeftRadius, "border-top-left-radius", s => s.Border.TopLeftRadius, (b, v) => b.Border = b.Border with { TopLeftRadius = v }),
            Radius(PropertyId.BorderTopRightRadius, "border-top-right-radius", s => s.Border.TopRightRadius, (b, v) => b.Border = b.Border with { TopRightRadius = v }),
            Radius(PropertyId.BorderBottomRightRadius, "border-bottom-right-radius", s => s.Border.BottomRightRadius, (b, v) => b.Border = b.Border with { BottomRightRadius = v }),
            Radius(PropertyId.BorderBottomLeftRadius, "border-bottom-left-radius", s => s.Border.BottomLeftRadius, (b, v) => b.Border = b.Border with { BottomLeftRadius = v }),
            // https://www.w3.org/TR/compositing-1/#isolation
            Keywords(PropertyId.Isolation, "isolation", false, "auto", Enum<Isolation>("auto", "isolate"), s => s.Box.Isolation, (b, v) => b.Box = b.Box with { Isolation = v }),
            // https://drafts.csswg.org/compositing-2/#mix-blend-mode and #background-blend-mode (plus-lighter blends whole elements only).
            Keywords(PropertyId.MixBlendMode, "mix-blend-mode", false, "normal", BlendKeywords, s => s.Effects.MixBlendMode, (b, v) => b.Effects = b.Effects with { MixBlendMode = v }),
            Layers<Style.BlendMode, Style.BlendMode>(PropertyId.BackgroundBlendMode, "background-blend-mode", "normal",
                r => r.Keyword("plus-lighter") is null && r.Keyword([.. BlendKeywords.Keys]) is { } k ? BlendKeywords[k] : null, (x, _) => x,
                s => s.Effects.BackgroundBlendModes, (b, v) => b.Effects = b.Effects with { BackgroundBlendModes = v }),
            // https://www.w3.org/TR/css-text-3/#text-align-property (match-parent is not supported)
            Keywords(PropertyId.TextAlign, "text-align", true, "start", TextAlignKeywords,
                s => s.Text.TextAlign, (b, v) => b.Text = b.Text with { TextAlign = v }),
            // https://www.w3.org/TR/CSS22/visudet.html#propdef-vertical-align
            new Property<VerticalAlign>(PropertyId.VerticalAlign, "vertical-align", false, "baseline",
                r => r.Keyword(VerticalAlignKeywords.Keys.ToArray()) is { } k ? new KeywordValue(k) : r.LengthPercentage(),
                (v, ctx) => v is KeywordValue k ? new VerticalAlign(VerticalAlignKeywords[k.Keyword]) : new VerticalAlign(VerticalAlignKind.Length, ctx.LengthPercentage(v)),
                s => s.Box.VerticalAlign, (b, v) => b.Box = b.Box with { VerticalAlign = v }),
            // https://www.w3.org/TR/css-writing-modes-3/#direction and #unicode-bidi
            Keywords(PropertyId.Direction, "direction", true, "ltr", Enum<Direction>("ltr", "rtl"),
                s => s.Text.Direction, (b, v) => b.Text = b.Text with { Direction = v }),
            Keywords(PropertyId.UnicodeBidi, "unicode-bidi", false, "normal",
                Enum<UnicodeBidi>("normal", "embed", "isolate", "bidi-override", "isolate-override", "plaintext"),
                s => s.Box.UnicodeBidi, (b, v) => b.Box = b.Box with { UnicodeBidi = v }),

            // https://www.w3.org/TR/css-flexbox-1/ and css-align-3 (safe and unsafe are accepted and ignored)
            Keywords(PropertyId.FlexDirection, "flex-direction", false, "row", Enum<FlexDirection>("row", "row-reverse", "column", "column-reverse"),
                s => s.Flex.Direction, (b, v) => b.Flex = b.Flex with { Direction = v }),
            Keywords(PropertyId.FlexWrap, "flex-wrap", false, "nowrap", Enum<FlexWrap>("nowrap", "wrap", "wrap-reverse"),
                s => s.Flex.Wrap, (b, v) => b.Flex = b.Flex with { Wrap = v }),
            Aligned(PropertyId.JustifyContent, "justify-content", ContentAlignKeywords, s => s.Flex.JustifyContent, (b, v) => b.Flex = b.Flex with { JustifyContent = v }),
            Aligned(PropertyId.AlignContent, "align-content", ContentAlignKeywords, s => s.Flex.AlignContent, (b, v) => b.Flex = b.Flex with { AlignContent = v }),
            Aligned(PropertyId.AlignItems, "align-items", ItemAlignKeywords, s => s.Flex.AlignItems, (b, v) => b.Flex = b.Flex with { AlignItems = v }),
            Aligned(PropertyId.AlignSelf, "align-self", ItemAlignKeywords, s => s.Flex.AlignSelf, (b, v) => b.Flex = b.Flex with { AlignSelf = v }, "auto"),
            new Property<float>(PropertyId.FlexGrow, "flex-grow", false, "0",
                r => r.Number(nonNegative: true) is { } n ? new NumberValue(n) : null, (v, _) => ((NumberValue)v).Number,
                s => s.Flex.Grow, (b, v) => b.Flex = b.Flex with { Grow = v }),
            new Property<float>(PropertyId.FlexShrink, "flex-shrink", false, "1",
                r => r.Number(nonNegative: true) is { } n ? new NumberValue(n) : null, (v, _) => ((NumberValue)v).Number,
                s => s.Flex.Shrink, (b, v) => b.Flex = b.Flex with { Shrink = v }),
            new Property<SizeValue>(PropertyId.FlexBasis, "flex-basis", false, "auto",
                r => r.Keyword("auto", "content", "min-content", "max-content", "fit-content") is { } k ? new KeywordValue(k) : r.LengthPercentage(nonNegative: true),
                (v, ctx) => v switch
                {
                    KeywordValue { Keyword: "auto" } => SizeValue.Auto,
                    KeywordValue { Keyword: "content" } => new SizeValue(SizeKind.Content),
                    KeywordValue { Keyword: "min-content" } => new SizeValue(SizeKind.MinContent),
                    KeywordValue { Keyword: "max-content" } => new SizeValue(SizeKind.MaxContent),
                    KeywordValue => new SizeValue(SizeKind.FitContent),
                    _ => SizeValue.Of(ctx.LengthPercentage(v, nonNegative: true)),
                },
                s => s.Flex.Basis, (b, v) => b.Flex = b.Flex with { Basis = v }),
            new Property<int>(PropertyId.Order, "order", false, "0",
                r => r.Integer() is { } i ? new NumberValue(i) : null, (v, _) => (int)((NumberValue)v).Number,
                s => s.Flex.Order, (b, v) => b.Flex = b.Flex with { Order = v }),
            Gap(PropertyId.RowGap, "row-gap", s => s.Flex.RowGap, (b, v) => b.Flex = b.Flex with { RowGap = v }),
            Gap(PropertyId.ColumnGap, "column-gap", s => s.Flex.ColumnGap, (b, v) => b.Flex = b.Flex with { ColumnGap = v }),

            // https://www.w3.org/TR/css-grid-1/
            Tracks(PropertyId.GridTemplateColumns, "grid-template-columns", s => s.Grid.TemplateColumns, (b, v) => b.Grid = b.Grid with { TemplateColumns = v }),
            Tracks(PropertyId.GridTemplateRows, "grid-template-rows", s => s.Grid.TemplateRows, (b, v) => b.Grid = b.Grid with { TemplateRows = v }),
            AutoTracks(PropertyId.GridAutoColumns, "grid-auto-columns", s => s.Grid.AutoColumns, (b, v) => b.Grid = b.Grid with { AutoColumns = v }),
            AutoTracks(PropertyId.GridAutoRows, "grid-auto-rows", s => s.Grid.AutoRows, (b, v) => b.Grid = b.Grid with { AutoRows = v }),
            new Property<string>(PropertyId.GridAutoFlow, "grid-auto-flow", false, "row", GridParsing.AutoFlow,
                (v, _) => v is GridAutoFlowValue f ? (f.Column ? "column" : "row") + (f.Dense ? " dense" : "") : "row",
                s => (s.Grid.AutoFlowColumn ? "column" : "row") + (s.Grid.Dense ? " dense" : ""),
                (b, v) => b.Grid = b.Grid with { AutoFlowColumn = v.StartsWith("column", StringComparison.Ordinal), Dense = v.EndsWith("dense", StringComparison.Ordinal) }),
            Placement(PropertyId.GridRowStart, "grid-row-start", s => s.Grid.RowStart, (b, v) => b.Grid = b.Grid with { RowStart = v }),
            Placement(PropertyId.GridRowEnd, "grid-row-end", s => s.Grid.RowEnd, (b, v) => b.Grid = b.Grid with { RowEnd = v }),
            Placement(PropertyId.GridColumnStart, "grid-column-start", s => s.Grid.ColumnStart, (b, v) => b.Grid = b.Grid with { ColumnStart = v }),
            Placement(PropertyId.GridColumnEnd, "grid-column-end", s => s.Grid.ColumnEnd, (b, v) => b.Grid = b.Grid with { ColumnEnd = v }),
            // legacy is accepted and acts as normal.
            Aligned(PropertyId.JustifyItems, "justify-items", JustifyKeywords, s => s.Grid.JustifyItems, (b, v) => b.Grid = b.Grid with { JustifyItems = v }),
            Aligned(PropertyId.JustifySelf, "justify-self", JustifyKeywords, s => s.Grid.JustifySelf, (b, v) => b.Grid = b.Grid with { JustifySelf = v }, "auto"),
            // https://www.w3.org/TR/css-tables-3/
            Keywords(PropertyId.TableLayout, "table-layout", false, "auto", Enum<TableLayoutMode>("auto", "fixed"),
                s => s.Box.TableLayout, (b, v) => b.Box = b.Box with { TableLayout = v }),
            Keywords(PropertyId.BorderCollapse, "border-collapse", true, "separate", Enum<BorderCollapse>("separate", "collapse"),
                s => s.Text.BorderCollapse, (b, v) => b.Text = b.Text with { BorderCollapse = v }),
            new Property<(float X, float Y)>(PropertyId.BorderSpacing, "border-spacing", true, "0",
                r => r.LengthPercentage(allowPercent: false, nonNegative: true) is { } x
                    ? new RadiusValue(x, r.LengthPercentage(allowPercent: false, nonNegative: true) ?? x) : null,
                (v, ctx) => (ctx.LengthPercentage(((RadiusValue)v).X).Px, ctx.LengthPercentage(((RadiusValue)v).Y).Px),
                s => (s.Text.BorderSpacingX, s.Text.BorderSpacingY), (b, v) => b.Text = b.Text with { BorderSpacingX = v.X, BorderSpacingY = v.Y }),
            Keywords(PropertyId.CaptionSide, "caption-side", true, "top", Enum<CaptionSide>("top", "bottom"),
                s => s.Text.CaptionSide, (b, v) => b.Text = b.Text with { CaptionSide = v }),
            Keywords(PropertyId.EmptyCells, "empty-cells", true, "show", Enum<EmptyCells>("show", "hide"),
                s => s.Text.EmptyCells, (b, v) => b.Text = b.Text with { EmptyCells = v }),
            new Property<GridAreas>(PropertyId.GridTemplateAreas, "grid-template-areas", false, "none", GridParsing.Areas,
                (v, _) => ((GridAreasValue)v).Areas, s => s.Grid.Areas, (b, v) => b.Grid = b.Grid with { Areas = v }),

            // https://www.w3.org/TR/css-text-decor-4/: percentages of thickness and offset are of 1em.
            new Property<TextDecorationLine>(PropertyId.TextDecorationLine, "text-decoration-line", false, "none", DecorationLine,
                (v, _) => ((KeywordValue)v).Keyword.Split(' ').Aggregate(TextDecorationLine.None, (line, k) => line | DecorationLineKeywords.GetValueOrDefault(k)),
                s => s.Decoration.Line, (b, v) => b.Decoration = b.Decoration with { Line = v }),
            Keywords(PropertyId.TextDecorationStyle, "text-decoration-style", false, "solid", Enum<TextDecorationStyle>("solid", "double", "dotted", "dashed", "wavy"),
                s => s.Decoration.Style, (b, v) => b.Decoration = b.Decoration with { Style = v }),
            Color(PropertyId.TextDecorationColor, "text-decoration-color", "currentcolor", s => s.Decoration.Color, (b, v) => b.Decoration = b.Decoration with { Color = v }),
            new Property<float?>(PropertyId.TextDecorationThickness, "text-decoration-thickness", false, "auto",
                r => r.Keyword("auto", "from-font") is { } k ? new KeywordValue(k) : r.LengthPercentage(),
                (v, ctx) => v is KeywordValue ? null : ctx.LengthPercentage(v).Resolve(ctx.FontSize),
                s => s.Decoration.Thickness, (b, v) => b.Decoration = b.Decoration with { Thickness = v }),
            new Property<float?>(PropertyId.TextUnderlineOffset, "text-underline-offset", true, "auto",
                r => r.Keyword("auto") is { } k ? new KeywordValue(k) : r.LengthPercentage(),
                (v, ctx) => v is KeywordValue ? null : ctx.LengthPercentage(v).Resolve(ctx.FontSize),
                s => s.Text.UnderlineOffset, (b, v) => b.Text = b.Text with { UnderlineOffset = v }),

            // https://www.w3.org/TR/css-text-4/#letter-spacing-property and #word-spacing-property: normal is 0;
            // percentages are of 1em.
            TextSpacingLength(PropertyId.LetterSpacing, "letter-spacing", s => s.TextSpacing.LetterSpacing, (b, v) => b.TextSpacing = b.TextSpacing with { LetterSpacing = v }),
            TextSpacingLength(PropertyId.WordSpacing, "word-spacing", s => s.TextSpacing.WordSpacing, (b, v) => b.TextSpacing = b.TextSpacing with { WordSpacing = v }),
            // https://www.w3.org/TR/css-text-3/#tab-size-property: a non-negative number of spaces or length.
            new Property<TabSize>(PropertyId.TabSize, "tab-size", true, "8",
                r => r.Number(nonNegative: true) is { } n ? new NumberValue(n) : r.LengthPercentage(allowPercent: false, nonNegative: true),
                (v, ctx) => v is NumberValue n ? new TabSize(n.Number, false) : new TabSize(Math.Max(0, ctx.LengthPercentage(v).Resolve(0)), true),
                s => s.TextSpacing.TabSize, (b, v) => b.TextSpacing = b.TextSpacing with { TabSize = v }),
            Keywords(PropertyId.WordBreak, "word-break", true, "normal", Enum<WordBreakStyle>("normal", "break-all", "keep-all", "break-word"),
                s => s.TextSpacing.WordBreak, (b, v) => b.TextSpacing = b.TextSpacing with { WordBreak = v }),
            Keywords(PropertyId.OverflowWrap, "overflow-wrap", true, "normal", Enum<OverflowWrap>("normal", "break-word", "anywhere"),
                s => s.TextSpacing.OverflowWrap, (b, v) => b.TextSpacing = b.TextSpacing with { OverflowWrap = v }),
            Keywords(PropertyId.TextTransform, "text-transform", true, "none",
                Enum<TextTransform>("none", "capitalize", "uppercase", "lowercase", "full-width", "full-size-kana"),
                s => s.TextSpacing.Transform, (b, v) => b.TextSpacing = b.TextSpacing with { Transform = v }),
            // https://www.w3.org/TR/css-fonts-4/#font-variant-numeric-prop: normal | [ figure || spacing || fraction || ordinal || slashed-zero ]
            new Property<string>(PropertyId.FontVariantNumeric, "font-variant-numeric", true, "normal", VariantNumeric,
                (v, _) => ((KeywordValue)v).Keyword, s => s.Font.VariantNumeric, (b, v) => b.Font = b.Font with { VariantNumeric = v }),
            // https://www.w3.org/TR/css-fonts-4/#font-feature-settings-prop: normal | [ <string> [ <integer> | on | off ]? ]#
            new Property<string>(PropertyId.FontFeatureSettings, "font-feature-settings", true, "normal", FeatureSettings,
                (v, _) => ((KeywordValue)v).Keyword, s => s.Font.FeatureSettings, (b, v) => b.Font = b.Font with { FeatureSettings = v }),
            // https://www.w3.org/TR/css-content-3/#quotes-property: auto | none | [ <string> <string> ]+
            new Property<QuotesGroup>(PropertyId.Quotes, "quotes", true, "auto",
                r =>
                {
                    if (r.Keyword("auto", "none") is { } k)
                        return new KeywordValue(k);
                    var pairs = new List<(string, string)>();
                    while (!r.AtEnd)
                    {
                        if (r.String() is not { } open || r.String() is not { } close)
                            return null;
                        pairs.Add((open, close));
                    }
                    return pairs.Count == 0 ? null : new QuotesValue(new QuotesGroup(pairs));
                },
                (v, _) => v switch
                {
                    KeywordValue { Keyword: "none" } => new QuotesGroup([]),
                    QuotesValue q => q.Quotes,
                    _ => QuotesGroup.Initial,
                },
                s => s.Quotes, (b, v) => b.Quotes = v),
            // https://www.w3.org/TR/css-text-3/#text-indent-property: <length-percentage> && hanging? && each-line?
            new Property<TextIndent>(PropertyId.TextIndent, "text-indent", true, "0",
                r =>
                {
                    CssValue? length = null;
                    var keywords = new List<string>();
                    while (!r.AtEnd)
                    {
                        if (length is null && r.LengthPercentage() is { } l)
                            length = l;
                        else if (r.Keyword("hanging", "each-line") is { } k && !keywords.Contains(k))
                            keywords.Add(k);
                        else
                            return null;
                    }
                    return length is null ? null : new TextIndentValue(length, keywords.Contains("hanging"), keywords.Contains("each-line"));
                },
                (v, ctx) => v is TextIndentValue t ? new TextIndent(ctx.LengthPercentage(t.Length), t.Hanging, t.EachLine) : default,
                s => s.Text.TextIndent, (b, v) => b.Text = b.Text with { TextIndent = v }),
            // https://www.w3.org/TR/css-text-3/#text-align-last-property (auto is null)
            new Property<Style.TextAlign?>(PropertyId.TextAlignLast, "text-align-last", true, "auto",
                r => r.Keyword("auto", "start", "end", "left", "right", "center", "justify") is { } k ? new KeywordValue(k) : null,
                (v, _) => ((KeywordValue)v).Keyword == "auto" ? null : TextAlignKeywords[((KeywordValue)v).Keyword],
                s => s.Text.TextAlignLast, (b, v) => b.Text = b.Text with { TextAlignLast = v }),
            // https://www.w3.org/TR/css-text-3/#hyphens-property (auto hyphenates only at soft hyphens: there are no dictionaries)
            // https://www.w3.org/TR/css-ui-4/#outline-props (outline-color: auto is currentcolor)
            BorderWidth(PropertyId.OutlineWidth, "outline-width", s => s.Outline.WidthPx, (b, v) => b.Outline = b.Outline with { WidthPx = v }),
            Keywords(PropertyId.OutlineStyle, "outline-style", false, "none",
                Enum<OutlineStyle>("auto", "none", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset"),
                s => s.Outline.Style, (b, v) => b.Outline = b.Outline with { Style = v }),
            new Property<CssColor>(PropertyId.OutlineColor, "outline-color", false, "auto",
                r => r.Keyword("auto") is not null ? new ColorValue(CssColor.CurrentColor) : r.ColorSpecified(),
                (v, ctx) => ctx.Color(v, ctx.CurrentColor),
                s => s.Outline.Color, (b, v) => b.Outline = b.Outline with { Color = v }),
            new Property<float>(PropertyId.OutlineOffset, "outline-offset", false, "0",
                r => r.LengthPercentage(allowPercent: false),
                (v, ctx) => ctx.LengthPercentage(v).Resolve(0),
                s => s.Outline.Offset, (b, v) => b.Outline = b.Outline with { Offset = v }),
            // https://www.w3.org/TR/css-sizing-4/#aspect-ratio: auto || <ratio> (auto with a ratio prefers a natural one)
            new Property<float?>(PropertyId.AspectRatio, "aspect-ratio", false, "auto",
                r =>
                {
                    CssValue? ratio = null;
                    var auto = false;
                    while (!r.AtEnd)
                    {
                        if (!auto && r.Keyword("auto") is not null)
                            auto = true;
                        else if (ratio is null && r.Number(nonNegative: true) is { } width)
                        {
                            var height = r.Delim('/') ? r.Number(nonNegative: true) : 1;
                            if (height is null)
                                return null;
                            ratio = new NumberValue(width > 0 && height > 0 ? width / height.Value : 0);
                        }
                        else
                            return null;
                    }
                    return ratio ?? (auto ? new KeywordValue("auto") : null);
                },
                (v, _) => v is NumberValue { Number: > 0 } n ? n.Number : null,
                s => s.Size.AspectRatio, (b, v) => b.Size = b.Size with { AspectRatio = v }),
            // https://www.w3.org/TR/css-overflow-3/#text-overflow (one value, for the end of the line)
            new Property<TextOverflow>(PropertyId.TextOverflow, "text-overflow", false, "clip",
                r => r.Keyword("clip", "ellipsis") is { } k ? new KeywordValue(k) : r.String() is not null ? new KeywordValue("ellipsis") : null,
                (v, _) => ((KeywordValue)v).Keyword == "clip" ? TextOverflow.Clip : TextOverflow.Ellipsis,
                s => s.Box.TextOverflow, (b, v) => b.Box = b.Box with { TextOverflow = v }),
            // https://www.w3.org/TR/css-overflow-4/#line-clamp: none | <integer [1,∞]> (also as -webkit-line-clamp)
            new Property<int?>(PropertyId.LineClamp, "line-clamp", false, "none",
                r => r.Keyword("none") is not null ? new KeywordValue("none") : r.Integer() is { } n && n >= 1 ? new NumberValue(n) : null,
                (v, _) => v is NumberValue n ? (int)n.Number : null,
                s => s.Box.LineClamp, (b, v) => b.Box = b.Box with { LineClamp = v }),
            // https://www.w3.org/TR/css-ui-4/#cursor: [ <url> [ <x> <y> ]? , ]* <keyword>, kept as its keyword for M3.
            new Property<string>(PropertyId.Cursor, "cursor", true, "auto",
                r =>
                {
                    while (r.Copy().Url() is not null)
                    {
                        r.Url();
                        if (r.Number() is not null && r.Number() is null)
                            return null;
                        if (!r.Comma())
                            return null;
                    }
                    return r.Keyword(CursorKeywords) is { } k ? new KeywordValue(k) : null;
                },
                (v, _) => ((KeywordValue)v).Keyword,
                s => s.Ui.Cursor, (b, v) => b.Ui = b.Ui with { Cursor = v }),
            // https://www.w3.org/TR/css-ui-4/#widget-accent: auto | <color>
            new Property<CssColor?>(PropertyId.AccentColor, "accent-color", true, "auto",
                r => r.Keyword("auto") is not null ? new KeywordValue("auto") : r.ColorSpecified(),
                (v, ctx) => v is KeywordValue ? null : ctx.Color(v, ctx.CurrentColor).Resolve(ctx.CurrentColor),
                s => s.Ui.AccentColor, (b, v) => b.Ui = b.Ui with { AccentColor = v }),
            // https://www.w3.org/TR/css-overflow-3/#scrollbar-gutter-property and css-scrollbars-1: recorded until
            // scroll containers have scrollbars (M3).
            new Property<string>(PropertyId.ScrollbarGutter, "scrollbar-gutter", false, "auto",
                r => r.Keyword("auto") is not null ? new KeywordValue("auto")
                    : r.Keyword("stable") is not null ? new KeywordValue(r.Keyword("both-edges") is not null ? "stable both-edges" : "stable")
                    : r.Keyword("both-edges") is not null && r.Keyword("stable") is not null ? new KeywordValue("stable both-edges") : null,
                (v, _) => ((KeywordValue)v).Keyword,
                s => s.Box.ScrollbarGutter, (b, v) => b.Box = b.Box with { ScrollbarGutter = v }),
            new Property<string>(PropertyId.ScrollbarWidth, "scrollbar-width", false, "auto",
                r => r.Keyword("auto", "thin", "none") is { } k ? new KeywordValue(k) : null,
                (v, _) => ((KeywordValue)v).Keyword,
                s => s.Box.ScrollbarWidth, (b, v) => b.Box = b.Box with { ScrollbarWidth = v }),
            new Property<string>(PropertyId.ScrollbarColor, "scrollbar-color", true, "auto",
                r => r.Keyword("auto") is not null ? new KeywordValue("auto")
                    : r.ColorSpecified() is { } thumb && r.ColorSpecified() is { } track ? new RadiusValue(thumb, track) : null,
                (v, ctx) => v is RadiusValue pair ? $"{ctx.Color(pair.X, ctx.CurrentColor).Resolve(ctx.CurrentColor)} {ctx.Color(pair.Y, ctx.CurrentColor).Resolve(ctx.CurrentColor)}" : "auto",
                s => s.Ui.ScrollbarColor, (b, v) => b.Ui = b.Ui with { ScrollbarColor = v }),
            // https://www.w3.org/TR/css-lists-3/#image-markers: a url() image that loads is the marker.
            new Property<ImageValue>(PropertyId.ListStyleImage, "list-style-image", true, "none",
                r => r.Keyword("none") is not null ? new ImageSpecified(NoImage.Instance) : BackgroundParsing.Image(r) is { } image ? new ImageSpecified(image) : null,
                (v, _) => ((ImageSpecified)v).Image,
                s => s.Text.ListStyleImage ?? NoImage.Instance, (b, v) => b.Text = b.Text with { ListStyleImage = v }),
            // https://www.w3.org/TR/css-backgrounds-3/#box-shadow and https://www.w3.org/TR/css-text-decor-3/#text-shadow-property
            new Property<IReadOnlyList<Shadow>>(PropertyId.BoxShadow, "box-shadow", false, "none", r => ShadowList(r, box: true),
                (v, ctx) => ComputeShadows((ShadowListValue)v, ctx), s => s.Shadows.Box, (b, v) => b.Shadows = b.Shadows with { Box = v }),
            new Property<IReadOnlyList<Shadow>>(PropertyId.TextShadow, "text-shadow", true, "none", r => ShadowList(r, box: false),
                (v, ctx) => ComputeShadows((ShadowListValue)v, ctx), s => s.Text.TextShadows ?? [], (b, v) => b.Text = b.Text with { TextShadows = v.Count == 0 ? null : v }),
            Keywords(PropertyId.Hyphens, "hyphens", true, "manual", Enum<Hyphens>("manual", "none", "auto"),
                s => s.Text.Hyphens, (b, v) => b.Text = b.Text with { Hyphens = v }),
            // https://www.w3.org/TR/css-images-3/#the-object-fit, #the-object-position, #the-image-rendering
            Keywords(PropertyId.ObjectFit, "object-fit", false, "fill", Enum<ObjectFit>("fill", "contain", "cover", "none", "scale-down"),
                s => s.Replaced.Fit, (b, v) => b.Replaced = b.Replaced with { Fit = v }),
            new Property<Style.BackgroundPosition>(PropertyId.ObjectPosition, "object-position", false, "50% 50%",
                r => BackgroundParsing.Position(r) is { } p ? new PositionValue(p) : null,
                (v, ctx) => ComputePosition(((PositionValue)v).Position, ctx),
                s => s.Replaced.Position, (b, v) => b.Replaced = b.Replaced with { Position = v }),
            Keywords(PropertyId.ImageRendering, "image-rendering", true, "auto", Enum<ImageRendering>("auto", "smooth", "high-quality", "pixelated", "crisp-edges"),
                s => s.Inherited.ImageRendering, (b, v) => b.Inherited = b.Inherited with { ImageRendering = v }),
            Keywords(PropertyId.TextDecorationSkipInk, "text-decoration-skip-ink", true, "auto", Enum<SkipInk>("auto", "none", "all"),
                s => s.Text.SkipInk, (b, v) => b.Text = b.Text with { SkipInk = v }),
        };

        rows.AddRange(TransformProperties.Rows);
        rows.AddRange(FilterProperties.Rows);
        rows.Add(ShapeProperties.Row);

        var table = new Property[System.Enum.GetValues<PropertyId>().Length];
        foreach (var row in rows)
        {
            table[(int)row.Id] = row;
            row.InitializeInitial();
        }
        if (table.Any(p => p is null))
            throw new InvalidOperationException("Every PropertyId needs a table row.");
        return table;
    }

    private static readonly Dictionary<string, Display> DisplayKeywords = new()
    {
        ["inline"] = Display.Inline, ["block"] = Display.Block, ["inline-block"] = Display.InlineBlock,
        ["flow-root"] = Display.FlowRoot, ["list-item"] = Display.ListItem, ["flex"] = Display.Flex,
        ["inline-flex"] = Display.InlineFlex, ["grid"] = Display.Grid, ["inline-grid"] = Display.InlineGrid,
        ["table"] = Display.Table, ["inline-table"] = Display.InlineTable, ["table-row-group"] = Display.TableRowGroup,
        ["table-header-group"] = Display.TableHeaderGroup, ["table-footer-group"] = Display.TableFooterGroup,
        ["table-row"] = Display.TableRow, ["table-cell"] = Display.TableCell, ["table-column-group"] = Display.TableColumnGroup,
        ["table-column"] = Display.TableColumn, ["table-caption"] = Display.TableCaption, ["contents"] = Display.Contents,
        ["none"] = Display.None,
    };

    private static readonly Dictionary<string, ContentAlign> ContentAlignKeywords = new()
    {
        ["normal"] = ContentAlign.Normal, ["flex-start"] = ContentAlign.FlexStart, ["flex-end"] = ContentAlign.FlexEnd,
        ["center"] = ContentAlign.Center, ["space-between"] = ContentAlign.SpaceBetween, ["space-around"] = ContentAlign.SpaceAround,
        ["space-evenly"] = ContentAlign.SpaceEvenly, ["stretch"] = ContentAlign.Stretch, ["start"] = ContentAlign.Start,
        ["end"] = ContentAlign.End, ["left"] = ContentAlign.Left, ["right"] = ContentAlign.Right,
    };

    private static readonly Dictionary<string, ItemAlign> ItemAlignKeywords = new()
    {
        ["auto"] = ItemAlign.Auto, ["normal"] = ItemAlign.Normal, ["stretch"] = ItemAlign.Stretch, ["flex-start"] = ItemAlign.FlexStart,
        ["flex-end"] = ItemAlign.FlexEnd, ["center"] = ItemAlign.Center, ["baseline"] = ItemAlign.Baseline, ["start"] = ItemAlign.Start,
        ["end"] = ItemAlign.End, ["self-start"] = ItemAlign.SelfStart, ["self-end"] = ItemAlign.SelfEnd,
    };

    // An alignment keyword, optionally after safe/unsafe (overflow alignment is not supported) or first/last (baseline).
    private static Property<T> Aligned<T>(PropertyId id, string name, Dictionary<string, T> keywords,
                                          Func<ComputedStyle, T> get, Action<StyleBuilder, T> set, string initial = "normal") where T : struct, Enum =>
        new(id, name, false, initial,
            r =>
            {
                var position = r.Keyword("first", "last");
                if (position is null)
                    r.Keyword("safe", "unsafe");
                var keyword = r.Keyword(keywords.Keys.Where(k => k != "auto" || initial == "auto").ToArray());
                if (keyword is null || position is not null && keyword != "baseline")
                    return null;
                return new KeywordValue(position == "last" ? "last baseline" : keyword);
            },
            (v, _) => ((KeywordValue)v).Keyword == "last baseline" ? (T)(object)ItemAlign.LastBaseline : keywords[((KeywordValue)v).Keyword],
            get, set);

    private static readonly Dictionary<string, ItemAlign> JustifyKeywords = new(ItemAlignKeywords)
    {
        ["left"] = ItemAlign.Start, ["right"] = ItemAlign.End, ["legacy"] = ItemAlign.Normal,
    };

    private static Property<TrackList> Tracks(PropertyId id, string name, Func<ComputedStyle, TrackList> get, Action<StyleBuilder, TrackList> set) =>
        new(id, name, false, "none", GridParsing.TrackList,
            (v, ctx) => v is TrackListValue list
                ? new TrackList(list.Tracks.Select(t => GridParsing.Compute(t, ctx)).ToList(), list.LineNames,
                    list.Repeat is { } r ? new AutoRepeat(r.Index, r.Fit, r.Tracks.Select(t => GridParsing.Compute(t, ctx)).ToList(), r.Names) : null)
                : TrackList.None,
            get, set);

    private static Property<IReadOnlyList<TrackSize>> AutoTracks(PropertyId id, string name, Func<ComputedStyle, IReadOnlyList<TrackSize>> get,
                                                              Action<StyleBuilder, IReadOnlyList<TrackSize>> set) =>
        new(id, name, false, "auto", GridParsing.TrackSizes,
            (v, ctx) => ((TrackSizesValue)v).Sizes.Select(t => GridParsing.Compute(t, ctx)).ToList(),
            get, set);

    private static Property<GridLine> Placement(PropertyId id, string name, Func<ComputedStyle, GridLine> get, Action<StyleBuilder, GridLine> set) =>
        new(id, name, false, "auto", GridParsing.Line, (v, _) => ((GridLineValue)v).Line, get, set);

    // row-gap and column-gap: normal (zero in flex and grid) or a non-negative length-percentage.
    private static Property<LengthPercentage> Gap(PropertyId id, string name, Func<ComputedStyle, LengthPercentage> get, Action<StyleBuilder, LengthPercentage> set) =>
        new(id, name, false, "normal",
            r => r.Keyword("normal") is not null ? new KeywordValue("normal") : r.LengthPercentage(nonNegative: true),
            (v, ctx) => v is KeywordValue ? default : ctx.LengthPercentage(v, nonNegative: true),
            get, set);

    private static readonly Dictionary<string, VerticalAlignKind> VerticalAlignKeywords = new()
    {
        ["baseline"] = VerticalAlignKind.Baseline, ["sub"] = VerticalAlignKind.Sub, ["super"] = VerticalAlignKind.Super,
        ["text-top"] = VerticalAlignKind.TextTop, ["text-bottom"] = VerticalAlignKind.TextBottom, ["middle"] = VerticalAlignKind.Middle,
        ["top"] = VerticalAlignKind.Top, ["bottom"] = VerticalAlignKind.Bottom,
    };

    private static readonly Dictionary<string, TextDecorationLine> DecorationLineKeywords = new()
    {
        ["underline"] = TextDecorationLine.Underline, ["overline"] = TextDecorationLine.Overline,
        ["line-through"] = TextDecorationLine.LineThrough, ["blink"] = TextDecorationLine.Blink,
    };

    // text-decoration-line: none | [ underline || overline || line-through || blink ], as its keywords in order.
    private static CssValue? DecorationLine(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new KeywordValue("none");
        var seen = new List<string>();
        while (r.Copy().Keyword([.. DecorationLineKeywords.Keys]) is { } k && !seen.Contains(k))
            seen.Add(r.Keyword(k)!);
        return seen.Count == 0 ? null : new KeywordValue(string.Join(' ', seen));
    }
    private static readonly Dictionary<string, Style.TextAlign> TextAlignKeywords = Enum<Style.TextAlign>("start", "end", "left", "right", "center", "justify");

    // letter-spacing and word-spacing: normal or a length-percentage of 1em, computed to px.
    private static Property<float> TextSpacingLength(PropertyId id, string name, Func<ComputedStyle, float> get, Action<StyleBuilder, float> set) =>
        new(id, name, true, "normal",
            r => r.Keyword("normal") is not null ? new KeywordValue("normal") : r.LengthPercentage(),
            (v, ctx) => v is KeywordValue ? 0 : ctx.LengthPercentage(v).Resolve(ctx.FontSize),
            get, set);

    // The keywords of font-variant-numeric, at most one from each group, kept in the order written.
    private static CssValue? VariantNumeric(ValueReader r)
    {
        if (r.Keyword("normal") is not null)
            return new KeywordValue("normal");
        string[][] groups = [["lining-nums", "oldstyle-nums"], ["proportional-nums", "tabular-nums"], ["diagonal-fractions", "stacked-fractions"], ["ordinal"], ["slashed-zero"]];
        var seen = new List<string>();
        while (r.Keyword([.. groups.SelectMany(g => g)]) is { } keyword)
        {
            if (seen.Any(s => groups.First(g => g.Contains(keyword)).Contains(s)))
                return null;
            seen.Add(keyword);
        }
        return seen.Count == 0 ? null : new KeywordValue(string.Join(' ', seen));
    }

    // font-feature-settings, normalised to "tag value" pairs ("tag" alone means 1, off 0, on 1).
    private static CssValue? FeatureSettings(ValueReader r)
    {
        if (r.Keyword("normal") is not null)
            return new KeywordValue("normal");
        var features = new List<string>();
        while (true)
        {
            if (r.String() is not { Length: 4 } tag || tag.Any(c => c is < ' ' or > '~'))
                return null;
            var value = r.Keyword("on", "off") is { } k ? (k == "on" ? 1 : 0) : r.Integer() is { } i && i >= 0 ? i : 1;
            features.Add($"\"{tag}\" {value}");
            if (r.AtEnd)
                return new KeywordValue(string.Join(", ", features));
            if (!r.Comma())
                return null;
        }
    }
    private static readonly string[] CursorKeywords =
    [
        "auto", "default", "none", "context-menu", "help", "pointer", "progress", "wait", "cell", "crosshair", "text", "vertical-text",
        "alias", "copy", "move", "no-drop", "not-allowed", "grab", "grabbing", "e-resize", "n-resize", "ne-resize", "nw-resize",
        "s-resize", "se-resize", "sw-resize", "w-resize", "ew-resize", "ns-resize", "nesw-resize", "nwse-resize", "col-resize",
        "row-resize", "all-scroll", "zoom-in", "zoom-out",
    ];

    // none | <shadow>#, a shadow being [ inset? && <length>{2,4} && <color>? ] (text shadows: no inset, at most three
    // lengths). Blur radii and text shadows' lengths are never negative where the grammar says so.
    private static CssValue? ShadowList(ValueReader r, bool box)
    {
        if (r.Keyword("none") is not null)
            return new ShadowListValue([]);
        var shadows = new List<ShadowSpecified>();
        while (true)
        {
            var lengths = new List<CssValue>();
            CssValue? color = null;
            var inset = false;
            while (!r.AtEnd && !r.PeekComma())
            {
                if (box && !inset && r.Keyword("inset") is not null)
                    inset = true;
                else if (lengths.Count == 0 && r.LengthPercentage(allowPercent: false) is { } first)
                {
                    lengths.Add(first);
                    while (lengths.Count < (box ? 4 : 3) && r.LengthPercentage(allowPercent: false, nonNegative: lengths.Count == 2) is { } more)
                        lengths.Add(more);
                }
                else if (color is null && r.ColorSpecified() is { } c)
                    color = c;
                else
                    return null;
            }
            if (lengths.Count < 2)
                return null;
            shadows.Add(new ShadowSpecified(lengths, color, inset));
            if (r.AtEnd)
                return new ShadowListValue(shadows);
            r.Comma();
        }
    }

    private static IReadOnlyList<Shadow> ComputeShadows(ShadowListValue value, ComputeContext ctx) =>
        value.Shadows.Select(s =>
        {
            float L(int i) => i < s.Lengths.Count ? ctx.LengthPercentage(s.Lengths[i]).Resolve(0) : 0;
            return new Shadow(L(0), L(1), Math.Max(0, L(2)), L(3), s.Color is null ? CssColor.CurrentColor : ctx.Color(s.Color, ctx.CurrentColor), s.Inset);
        }).ToList();

    private static readonly Dictionary<string, Style.BlendMode> BlendKeywords = Enum<Style.BlendMode>("normal", "multiply", "screen", "overlay", "darken",
        "lighten", "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "exclusion", "hue", "saturation", "color", "luminosity", "plus-lighter");

    private static readonly Dictionary<string, Overflow> OverflowKeywords = Enum<Overflow>("visible", "hidden", "clip", "scroll", "auto");

    private static readonly Dictionary<string, BorderStyle> BorderStyleKeywords =
        Enum<BorderStyle>("none", "hidden", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset");

    // https://www.w3.org/TR/css-fonts-4/#absolute-size-mapping (medium = 16px)
    private static readonly Dictionary<string, float> FontSizeKeywords = new()
    {
        ["xx-small"] = 9, ["x-small"] = 10, ["small"] = 13, ["medium"] = 16, ["large"] = 18, ["x-large"] = 24,
        ["xx-large"] = 32, ["xxx-large"] = 48,
    };

    // Keywords listed in the enum's declaration order.
    private static Dictionary<string, T> Enum<T>(params string[] keywords) where T : struct, Enum =>
        keywords.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => System.Enum.GetValues<T>()[x.i]);

    private static Property<T> Keywords<T>(PropertyId id, string name, bool inherited, string initial, Dictionary<string, T> keywords,
                                           Func<ComputedStyle, T> get, Action<StyleBuilder, T> set) =>
        new(id, name, inherited, initial,
            r => r.Keyword(keywords.Keys.ToArray()) is { } k ? new KeywordValue(k) : null,
            (v, _) => keywords[((KeywordValue)v).Keyword],
            get, set);

    // width/height/min-*/max-*: a non-negative length-percentage or the listed keywords.
    private static Property<SizeValue> Size(PropertyId id, string name, string initial, string keyword,
                                            Func<ComputedStyle, SizeValue> get, Action<StyleBuilder, SizeValue> set) =>
        new(id, name, false, initial,
            r => r.Keyword(keyword, "min-content", "max-content", "fit-content") is { } k ? new KeywordValue(k) : r.LengthPercentage(nonNegative: true),
            (v, ctx) => v switch
            {
                KeywordValue { Keyword: "auto" } => SizeValue.Auto,
                KeywordValue { Keyword: "none" } => SizeValue.None,
                KeywordValue { Keyword: "min-content" } => new SizeValue(SizeKind.MinContent),
                KeywordValue { Keyword: "max-content" } => new SizeValue(SizeKind.MaxContent),
                KeywordValue { Keyword: "fit-content" } => new SizeValue(SizeKind.FitContent),
                _ => SizeValue.Of(ctx.LengthPercentage(v, nonNegative: true)),
            },
            get, set);

    // Margins and insets: auto or any length-percentage.
    private static Property<SizeValue> Offset(PropertyId id, string name, string initial,
                                              Func<ComputedStyle, SizeValue> get, Action<StyleBuilder, SizeValue> set) =>
        new(id, name, false, initial,
            r => r.Keyword("auto") is not null ? new KeywordValue("auto") : r.LengthPercentage(),
            (v, ctx) => v is KeywordValue ? SizeValue.Auto : SizeValue.Of(ctx.LengthPercentage(v)),
            get, set);

    // border-*-radius: one or two non-negative length-percentages, horizontal then vertical.
    private static Property<CornerRadius> Radius(PropertyId id, string name, Func<ComputedStyle, CornerRadius> get, Action<StyleBuilder, CornerRadius> set) =>
        new(id, name, false, "0",
            r => r.LengthPercentage(nonNegative: true) is { } x ? new RadiusValue(x, r.LengthPercentage(nonNegative: true) ?? x) : null,
            (v, ctx) => new CornerRadius(ctx.LengthPercentage(((RadiusValue)v).X, nonNegative: true), ctx.LengthPercentage(((RadiusValue)v).Y, nonNegative: true)),
            get, set);

    private static Property<LengthPercentage> Padding(PropertyId id, string name,
                                                      Func<ComputedStyle, LengthPercentage> get, Action<StyleBuilder, LengthPercentage> set) =>
        new(id, name, false, "0",
            r => r.LengthPercentage(nonNegative: true),
            (v, ctx) => ctx.LengthPercentage(v, nonNegative: true),
            get, set);

    // Border widths are lengths (no percentages); thin/medium/thick are 1/3/5px. A none or hidden style makes the
    // computed width 0 (BorderGroup's accessors).
    private static Property<float> BorderWidth(PropertyId id, string name, Func<ComputedStyle, float> get, Action<StyleBuilder, float> set) =>
        new(id, name, false, "medium",
            r => r.Keyword("thin", "medium", "thick") is { } k ? new KeywordValue(k) : r.LengthPercentage(allowPercent: false, nonNegative: true),
            (v, ctx) => v switch
            {
                KeywordValue { Keyword: "thin" } => 1,
                KeywordValue { Keyword: "medium" } => 3,
                KeywordValue => 5,
                _ => Math.Max(0, ctx.LengthPercentage(v).Resolve(0)),
            },
            get, set);

    // A background longhand: a comma-separated list, one value per layer.
    private static Property<IReadOnlyList<TComputed>> Layers<TSpecified, TComputed>(PropertyId id, string name, string initial,
        Func<ValueReader, TSpecified?> item, Func<TSpecified, ComputeContext, TComputed> compute,
        Func<ComputedStyle, IReadOnlyList<TComputed>> get, Action<StyleBuilder, IReadOnlyList<TComputed>> set) where TSpecified : struct =>
        new(id, name, false, initial,
            r => BackgroundParsing.List(r, item),
            (v, ctx) => ((LayerListValue<TSpecified>)v).Items.Select(x => compute(x, ctx)).ToList(),
            get, set);

    /// <summary>
    /// The border-radius shorthand's value: the four corners' radii from the top left, clockwise. Also the radii after
    /// <c>round</c> in basic shapes.
    /// </summary>
    internal static RadiusValue[]? BorderRadii(ValueReader r)
    {
        List<CssValue>? Read()
        {
            var list = new List<CssValue>();
            while (list.Count < 4 && r.LengthPercentage(nonNegative: true) is { } value)
                list.Add(value);
            return list.Count == 0 ? null : list;
        }
        if (Read() is not { } horizontal || (r.Delim('/') ? Read() : horizontal) is not { } vertical)
            return null;
        // Missing corners copy the opposite one: top-right for bottom-left, top-left for the rest.
        static CssValue Corner(List<CssValue> l, int i) => i < l.Count ? l[i] : i == 3 && l.Count > 1 ? l[1] : l[0];
        return [.. Enumerable.Range(0, 4).Select(i => new RadiusValue(Corner(horizontal, i), Corner(vertical, i)))];
    }

    /// <summary>A computed position: offsets from the left and top edges.</summary>
    internal static Style.BackgroundPosition ComputePosition(PositionSpecified p, ComputeContext ctx) =>
        new(FromEdge(ctx.LengthPercentage(p.X), p.XFromEnd), FromEdge(ctx.LengthPercentage(p.Y), p.YFromEnd));

    // An offset from the right or bottom edge is 100% minus the offset.
    internal static LengthPercentage FromEdge(LengthPercentage value, bool fromEnd) =>
        !fromEnd ? value
        : value.Calc is null ? new LengthPercentage(-value.Px, 100 - value.Percent)
        : new LengthPercentage(0, 0, new CalcSum(new CalcPercent(100), new CalcProduct(new CalcNumber(-1), value.Calc)));

    // https://www.w3.org/TR/css-fonts-4/#font-stretch-prop
    private static readonly Dictionary<string, float> FontStretchKeywords = new()
    {
        ["ultra-condensed"] = 50, ["extra-condensed"] = 62.5f, ["condensed"] = 75, ["semi-condensed"] = 87.5f, ["normal"] = 100,
        ["semi-expanded"] = 112.5f, ["expanded"] = 125, ["extra-expanded"] = 150, ["ultra-expanded"] = 200,
    };

    private static Property<IReadOnlyList<CounterChange>> Counters(PropertyId id, string name, int defaultValue, bool allowReversed,
        Func<ComputedStyle, IReadOnlyList<CounterChange>> get, Action<StyleBuilder, IReadOnlyList<CounterChange>> set) =>
        new(id, name, false, "none",
            r => GeneratedContentParsing.Counters(r, defaultValue, allowReversed),
            (v, _) => ((CounterListValue)v).Changes,
            get, set);

    private static Property<CssColor> Color(PropertyId id, string name, string initial, Func<ComputedStyle, CssColor> get, Action<StyleBuilder, CssColor> set) =>
        new(id, name, false, initial, r => r.ColorSpecified(), (v, ctx) => ctx.Color(v, ctx.CurrentColor), get, set);

    private static float ComputeFontSize(CssValue value, ComputeContext context)
    {
        var parent = context.Parent.Font.Size;
        return value switch
        {
            KeywordValue { Keyword: "smaller" } => parent / 1.2f,
            KeywordValue { Keyword: "larger" } => parent * 1.2f,
            KeywordValue k => FontSizeKeywords[k.Keyword],
            _ => Math.Max(0, context.LengthPercentage(value, forFontSize: true).Resolve(parent)),
        };
    }

    // https://www.w3.org/TR/css-fonts-4/#relative-weights
    private static int ComputeFontWeight(CssValue value, ComputeContext context)
    {
        var parent = context.Parent.Font.Weight;
        return value switch
        {
            KeywordValue { Keyword: "normal" } => 400,
            KeywordValue { Keyword: "bold" } => 700,
            KeywordValue { Keyword: "bolder" } => parent < 350 ? 400 : parent < 550 ? 700 : 900,
            KeywordValue { Keyword: "lighter" } => parent < 100 ? parent : parent < 550 ? 100 : parent < 750 ? 400 : 700,
            NumberValue n => (int)Math.Round(n.Number),
            _ => 400,
        };
    }

    // ---------------------------------------------------------------- shorthands

    private sealed record Shorthand(PropertyId[] Longhands, Func<ValueReader, List<(PropertyId, CssValue)>?> Expand);

    private static readonly Dictionary<string, Shorthand> Shorthands = new(StringComparer.Ordinal)
    {
        ["margin"] = Sides([PropertyId.MarginTop, PropertyId.MarginRight, PropertyId.MarginBottom, PropertyId.MarginLeft]),
        ["padding"] = Sides([PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft]),
        ["inset"] = Sides([PropertyId.Top, PropertyId.Right, PropertyId.Bottom, PropertyId.Left]),
        ["border-width"] = Sides([PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth]),
        ["border-style"] = Sides([PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle]),
        ["border-color"] = Sides([PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor]),
        ["border-top"] = Border(Side.Top),
        ["border-right"] = Border(Side.Right),
        ["border-bottom"] = Border(Side.Bottom),
        ["border-left"] = Border(Side.Left),
        ["border"] = Border(Side.Top, Side.Right, Side.Bottom, Side.Left),
        // https://www.w3.org/TR/css-backgrounds-3/#border-radius: 1-4 horizontal radii, optionally "/" and 1-4 vertical.
        ["border-radius"] = new([PropertyId.BorderTopLeftRadius, PropertyId.BorderTopRightRadius, PropertyId.BorderBottomRightRadius, PropertyId.BorderBottomLeftRadius],
            r => BorderRadii(r) is { } radii ? radii.Select((radius, i) => (PropertyId.BorderTopLeftRadius + i, (CssValue)radius)).ToList() : null),
        // https://www.w3.org/TR/css-text-4/#white-space-property (the single keywords)
        ["white-space"] = new([PropertyId.WhiteSpaceCollapse, PropertyId.TextWrapMode], r =>
        {
            (string Collapse, string Wrap)? parts = r.Keyword("normal", "pre", "nowrap", "pre-wrap", "pre-line", "break-spaces") switch
            {
                "normal" => ("collapse", "wrap"),
                "pre" => ("preserve", "nowrap"),
                "nowrap" => ("collapse", "nowrap"),
                "pre-wrap" => ("preserve", "wrap"),
                "pre-line" => ("preserve-breaks", "wrap"),
                "break-spaces" => ("break-spaces", "wrap"),
                _ => null,
            };
            return parts is { } p
                ? [(PropertyId.WhiteSpaceCollapse, new KeywordValue(p.Collapse)), (PropertyId.TextWrapMode, new KeywordValue(p.Wrap))]
                : null;
        }),
        // list-style: type || position || image. A none goes to whichever of type and image is not otherwise given.
        ["list-style"] = new([PropertyId.ListStyleType, PropertyId.ListStylePosition, PropertyId.ListStyleImage], r =>
        {
            CssValue? type = null, position = null, image = null;
            var nones = 0;
            while (!r.AtEnd)
            {
                var one = r.OneValue();
                if (one.Copy().Keyword("none") is not null)
                    nones++;
                else if (position is null && Get(PropertyId.ListStylePosition).Parse(one.Copy()) is { } p)
                    position = p;
                else if (type is null && Get(PropertyId.ListStyleType).Parse(one.Copy()) is { } t)
                    type = t;
                else if (image is null && Get(PropertyId.ListStyleImage).Parse(one.Copy()) is { } i)
                    image = i;
                else
                    return null;
            }
            if (nones > 2 || nones == 2 && (type is not null || image is not null) || nones == 1 && type is not null && image is not null)
                return null;
            if (nones > 0 && type is null)
                type = new ListStyleTypeValue(Css.ListStyleType.None);
            return
            [
                (PropertyId.ListStyleType, type ?? Get(PropertyId.ListStyleType).Initial),
                (PropertyId.ListStylePosition, position ?? Get(PropertyId.ListStylePosition).Initial),
                (PropertyId.ListStyleImage, image ?? Get(PropertyId.ListStyleImage).Initial),
            ];
        }),
        ["background"] = new([PropertyId.BackgroundColor, PropertyId.BackgroundImage, PropertyId.BackgroundPosition, PropertyId.BackgroundSize,
            PropertyId.BackgroundRepeat, PropertyId.BackgroundAttachment, PropertyId.BackgroundOrigin, PropertyId.BackgroundClip],
            BackgroundParsing.Shorthand),
        ["font"] = new([PropertyId.FontStyle, PropertyId.FontVariantCaps, PropertyId.FontWeight, PropertyId.FontStretch,
            PropertyId.FontSize, PropertyId.LineHeight, PropertyId.FontFamily], FontShorthand),
        // https://www.w3.org/TR/css-flexbox-1/#flex-property: none | [ <grow> <shrink>? || <basis> ]
        ["flex"] = new([PropertyId.FlexGrow, PropertyId.FlexShrink, PropertyId.FlexBasis], r =>
        {
            if (r.Keyword("none") is not null)
                return [(PropertyId.FlexGrow, new NumberValue(0)), (PropertyId.FlexShrink, new NumberValue(0)), (PropertyId.FlexBasis, new KeywordValue("auto"))];
            CssValue? grow = null, shrink = null, basis = null;
            while (!r.AtEnd)
            {
                if (grow is null && r.Number(nonNegative: true) is { } g)
                {
                    grow = new NumberValue(g);
                    if (r.Number(nonNegative: true) is { } s)
                        shrink = new NumberValue(s);
                }
                else if (basis is null && Get(PropertyId.FlexBasis).Parse(r.OneValue()) is { } b)
                {
                    basis = b;
                }
                else
                {
                    return null;
                }
            }
            if (grow is null && basis is null)
                return null;
            // Omitted grow and shrink are 1; an omitted basis is 0.
            return
            [
                (PropertyId.FlexGrow, grow ?? new NumberValue(1)),
                (PropertyId.FlexShrink, shrink ?? new NumberValue(1)),
                (PropertyId.FlexBasis, basis ?? new PercentageValue(0)),
            ];
        }),
        ["flex-flow"] = new([PropertyId.FlexDirection, PropertyId.FlexWrap], r =>
        {
            CssValue? direction = null, wrap = null;
            while (!r.AtEnd)
            {
                var one = r.OneValue();
                if (direction is null && Get(PropertyId.FlexDirection).Parse(one.Copy()) is { } d)
                    direction = d;
                else if (wrap is null && Get(PropertyId.FlexWrap).Parse(one.Copy()) is { } w)
                    wrap = w;
                else
                    return null;
            }
            return direction is null && wrap is null ? null
                : [(PropertyId.FlexDirection, direction ?? Get(PropertyId.FlexDirection).Initial), (PropertyId.FlexWrap, wrap ?? Get(PropertyId.FlexWrap).Initial)];
        }),
        // https://www.w3.org/TR/css-grid-1/#placement-shorthands
        ["grid-row"] = GridLines(PropertyId.GridRowStart, PropertyId.GridRowEnd),
        ["grid-column"] = GridLines(PropertyId.GridColumnStart, PropertyId.GridColumnEnd),
        ["grid-area"] = new([PropertyId.GridRowStart, PropertyId.GridColumnStart, PropertyId.GridRowEnd, PropertyId.GridColumnEnd], r =>
        {
            var parts = SlashSeparated(r, 4, GridParsing.Line);
            if (parts is null)
                return null;
            var rowStart = parts[0];
            var columnStart = parts.Count > 1 ? parts[1] : GridParsing.Omitted(rowStart);
            var rowEnd = parts.Count > 2 ? parts[2] : GridParsing.Omitted(rowStart);
            var columnEnd = parts.Count > 3 ? parts[3] : GridParsing.Omitted(columnStart);
            return [(PropertyId.GridRowStart, rowStart), (PropertyId.GridColumnStart, columnStart), (PropertyId.GridRowEnd, rowEnd), (PropertyId.GridColumnEnd, columnEnd)];
        }),
        // grid-template: none | <rows> / <columns> | [ <line-names>? <string> <track-size>? <line-names>? ]+ [ / <columns> ]?
        ["grid-template"] = new([PropertyId.GridTemplateRows, PropertyId.GridTemplateColumns, PropertyId.GridTemplateAreas], r =>
        {
            var none = new TrackListValue([], [[]]);
            if (r.Copy().Keyword("none") is not null && r.Keyword("none") is not null && r.AtEnd)
                return [(PropertyId.GridTemplateRows, none), (PropertyId.GridTemplateColumns, none), (PropertyId.GridTemplateAreas, new GridAreasValue(GridAreas.None))];
            var values = r.Rest();
            var slash = values.FindIndex(v => v is PreservedToken t && t.Token.IsDelim('/'));
            var head = slash < 0 ? values : values[..slash];
            if (head.Any(v => v is PreservedToken { Token.Kind: CssTokenKind.String }))
            {
                if (TemplateAreas(new ValueReader(r.Source, head)) is not var (rows, areas))
                    return null;
                CssValue columns = none;
                if (slash >= 0)
                {
                    var tail = new ValueReader(r.Source, values[(slash + 1)..]);
                    if (GridParsing.TrackList(tail) is not TrackListValue { Repeat: null } list || !tail.AtEnd)
                        return null;
                    columns = list;
                }
                return [(PropertyId.GridTemplateRows, rows), (PropertyId.GridTemplateColumns, columns), (PropertyId.GridTemplateAreas, new GridAreasValue(areas))];
            }
            var parts = SlashSeparated(new ValueReader(r.Source, values), 2, GridParsing.TrackList);
            return parts is [var rowList, var columnList]
                ? [(PropertyId.GridTemplateRows, rowList), (PropertyId.GridTemplateColumns, columnList), (PropertyId.GridTemplateAreas, new GridAreasValue(GridAreas.None))]
                : null;
        }),
        ["place-items"] = PlacePair(PropertyId.AlignItems, PropertyId.JustifyItems),
        ["place-self"] = PlacePair(PropertyId.AlignSelf, PropertyId.JustifySelf),
        ["place-content"] = PlacePair(PropertyId.AlignContent, PropertyId.JustifyContent),
        ["gap"] = new([PropertyId.RowGap, PropertyId.ColumnGap], r =>
        {
            var row = Get(PropertyId.RowGap).Parse(r.OneValue());
            if (row is null)
                return null;
            var column = r.AtEnd ? row : Get(PropertyId.ColumnGap).Parse(r.OneValue());
            return column is null ? null : [(PropertyId.RowGap, row), (PropertyId.ColumnGap, column)];
        }),
        // https://www.w3.org/TR/css-text-decor-4/#text-decoration-property: line || style || color || thickness
        ["text-decoration"] = new([PropertyId.TextDecorationLine, PropertyId.TextDecorationStyle, PropertyId.TextDecorationColor, PropertyId.TextDecorationThickness], r =>
        {
            CssValue? line = null, style = null, color = null, thickness = null;
            while (!r.AtEnd)
            {
                if (line is null && DecorationLine(r) is { } l)
                {
                    line = l;
                    continue;
                }
                var one = r.OneValue();
                if (style is null && Get(PropertyId.TextDecorationStyle).Parse(one.Copy()) is { } s)
                    style = s;
                else if (thickness is null && Get(PropertyId.TextDecorationThickness).Parse(one.Copy()) is { } t)
                    thickness = t;
                else if (color is null && Get(PropertyId.TextDecorationColor).Parse(one.Copy()) is { } c)
                    color = c;
                else
                    return null;
            }
            return
            [
                (PropertyId.TextDecorationLine, line ?? Get(PropertyId.TextDecorationLine).Initial),
                (PropertyId.TextDecorationStyle, style ?? Get(PropertyId.TextDecorationStyle).Initial),
                (PropertyId.TextDecorationColor, color ?? Get(PropertyId.TextDecorationColor).Initial),
                (PropertyId.TextDecorationThickness, thickness ?? Get(PropertyId.TextDecorationThickness).Initial),
            ];
        }),
        // https://www.w3.org/TR/css-text-3/#overflow-wrap-property: word-wrap is a legacy name for overflow-wrap.
        ["word-wrap"] = new([PropertyId.OverflowWrap], r => Get(PropertyId.OverflowWrap).Parse(r) is { } wrap ? [(PropertyId.OverflowWrap, wrap)] : null),
        // https://www.w3.org/TR/css-ui-4/#outline: width || style || color
        ["outline"] = new([PropertyId.OutlineWidth, PropertyId.OutlineStyle, PropertyId.OutlineColor], r =>
        {
            CssValue? width = null, style = null, color = null;
            while (!r.AtEnd)
            {
                var one = r.OneValue();
                if (width is null && Get(PropertyId.OutlineWidth).Parse(one.Copy()) is { } w)
                    width = w;
                else if (style is null && Get(PropertyId.OutlineStyle).Parse(one.Copy()) is { } s)
                    style = s;
                else if (color is null && Get(PropertyId.OutlineColor).Parse(one.Copy()) is { } c)
                    color = c;
                else
                    return null;
            }
            return width is null && style is null && color is null ? null
                :
                [
                    (PropertyId.OutlineWidth, width ?? Get(PropertyId.OutlineWidth).Initial),
                    (PropertyId.OutlineStyle, style ?? Get(PropertyId.OutlineStyle).Initial),
                    (PropertyId.OutlineColor, color ?? Get(PropertyId.OutlineColor).Initial),
                ];
        }),
        // The prefixed name artifacts use with display: -webkit-box.
        ["-webkit-line-clamp"] = new([PropertyId.LineClamp], r => Get(PropertyId.LineClamp).Parse(r) is { } clamp ? [(PropertyId.LineClamp, clamp)] : null),
        // The prefixed name artifacts write for older engines.
        ["-webkit-backdrop-filter"] = new([PropertyId.BackdropFilter], r => Get(PropertyId.BackdropFilter).Parse(r) is { } filter ? [(PropertyId.BackdropFilter, filter)] : null),
        ["overflow"] = new([PropertyId.OverflowX, PropertyId.OverflowY], r =>
        {
            var x = Get(PropertyId.OverflowX).Parse(r.OneValue());
            if (x is null)
                return null;
            var y = r.AtEnd ? x : Get(PropertyId.OverflowY).Parse(r.OneValue());
            return y is null ? null : [(PropertyId.OverflowX, x), (PropertyId.OverflowY, y)];
        }),
    };

    // Values separated by "/": each part parsed by the property grammar.
    private static List<CssValue>? SlashSeparated(ValueReader r, int most, Func<ValueReader, CssValue?> parse)
    {
        var groups = new List<List<ComponentValue>> { new() };
        foreach (var value in r.Rest())
        {
            if (value is PreservedToken t && t.Token.IsDelim('/'))
                groups.Add([]);
            else
                groups[^1].Add(value);
        }
        if (groups.Count > most)
            return null;
        var parts = new List<CssValue>();
        foreach (var group in groups)
        {
            var reader = new ValueReader(r.Source, group);
            if (parse(reader) is not { } value || !reader.AtEnd)
                return null;
            parts.Add(value);
        }
        return parts;
    }

    // The areas form of grid-template: each string a row, with an optional size and line names around it.
    private static (TrackListValue Rows, GridAreas Areas)? TemplateAreas(ValueReader r)
    {
        var strings = new List<string>();
        var tracks = new List<TrackSizeSpecified>();
        var names = new List<IReadOnlyList<string>>();
        var pending = new List<string>();
        var auto = new TrackSizeSpecified(new TrackBreadthSpecified(TrackKind.Auto), new TrackBreadthSpecified(TrackKind.Auto));
        while (!r.AtEnd)
        {
            if (r.LineNames() is { } before)
                pending.AddRange(before);
            if (r.String() is not { } row)
                return null;
            strings.Add(row);
            names.Add([.. pending]);
            pending.Clear();
            var size = GridParsing.OneTrackSize(r);
            tracks.Add(size ?? auto);
            if (r.LineNames() is { } after)
                pending.AddRange(after);
        }
        names.Add(pending);
        return GridParsing.BuildAreas(strings) is { } areas && areas.Rows == tracks.Count ? (new TrackListValue(tracks, names), areas) : null;
    }

    private static Shorthand GridLines(PropertyId start, PropertyId end) => new([start, end], r =>
        SlashSeparated(r, 2, GridParsing.Line) is { } parts
            ? [(start, parts[0]), (end, parts.Count > 1 ? parts[1] : GridParsing.Omitted(parts[0]))]
            : null);

    // place-*: <align> [ <justify> ]? (the justify value repeats the align value when omitted).
    private static Shorthand PlacePair(PropertyId align, PropertyId justify) => new([align, justify], r =>
    {
        var first = r.OneValue();
        var a = Get(align).Parse(first.Copy());
        if (a is null)
            return null;
        if (r.AtEnd)
            return Get(justify).Parse(first) is { } same ? [(align, a), (justify, same)] : null;
        return Get(justify).Parse(r.OneValue()) is { } j ? [(align, a), (justify, j)] : null;
    });

    // margin/padding/inset/border-*: 1–4 values for top, right, bottom, left.
    private static Shorthand Sides(PropertyId[] sides) => new(sides, r =>
    {
        var values = new List<CssValue>();
        while (!r.AtEnd && values.Count < 4)
        {
            var value = Get(sides[0]).Parse(r.OneValue());
            if (value is null)
                return null;
            values.Add(value);
        }
        if (values.Count == 0)
            return null;
        CssValue top = values[0], right = values.Count > 1 ? values[1] : top, bottom = values.Count > 2 ? values[2] : top,
            left = values.Count > 3 ? values[3] : right;
        return [(sides[0], top), (sides[1], right), (sides[2], bottom), (sides[3], left)];
    });

    private enum Side { Top, Right, Bottom, Left }

    // border and border-<side>: <line-width> || <line-style> || <color>; omitted parts reset to initial.
    private static Shorthand Border(params Side[] sides)
    {
        PropertyId WidthOf(Side s) => PropertyId.BorderTopWidth + (int)s;
        PropertyId StyleOf(Side s) => PropertyId.BorderTopStyle + (int)s;
        PropertyId ColorOf(Side s) => PropertyId.BorderTopColor + (int)s;
        var longhands = sides.SelectMany(s => new[] { WidthOf(s), StyleOf(s), ColorOf(s) }).ToArray();

        return new(longhands, r =>
        {
            CssValue? width = null, style = null, color = null;
            while (!r.AtEnd)
            {
                var one = r.OneValue();
                if (width is null && Get(PropertyId.BorderTopWidth).Parse(one.Copy()) is { } w)
                    width = w;
                else if (style is null && Get(PropertyId.BorderTopStyle).Parse(one.Copy()) is { } s)
                    style = s;
                else if (color is null && Get(PropertyId.BorderTopColor).Parse(one.Copy()) is { } c)
                    color = c;
                else
                    return null;
            }
            if (width is null && style is null && color is null)
                return null;
            var result = new List<(PropertyId, CssValue)>();
            foreach (var side in sides)
            {
                result.Add((WidthOf(side), width ?? Get(WidthOf(side)).Initial));
                result.Add((StyleOf(side), style ?? Get(StyleOf(side)).Initial));
                result.Add((ColorOf(side), color ?? Get(ColorOf(side)).Initial));
            }
            return result;
        });
    }

    // https://www.w3.org/TR/css-fonts-4/#font-prop: [ style || variant || weight || stretch ]? size [ / line-height ]? family,
    // or a system font keyword. Longhands not given are reset to their initial values.
    private static List<(PropertyId, CssValue)>? FontShorthand(ValueReader r)
    {
        CssValue Initial(PropertyId id) => Get(id).Initial;

        // ponytail: system font keywords map to system-ui at 13px until the host reports its system fonts.
        if (r.Keyword("caption", "icon", "menu", "message-box", "small-caption", "status-bar") is not null)
        {
            return r.AtEnd
                ?
                [
                    (PropertyId.FontStyle, Initial(PropertyId.FontStyle)), (PropertyId.FontVariantCaps, Initial(PropertyId.FontVariantCaps)),
                    (PropertyId.FontWeight, Initial(PropertyId.FontWeight)), (PropertyId.FontStretch, Initial(PropertyId.FontStretch)),
                    (PropertyId.FontSize, new LengthValue(new Length(13, LengthUnit.Px))), (PropertyId.LineHeight, Initial(PropertyId.LineHeight)),
                    (PropertyId.FontFamily, new FontFamilyValue(["system-ui"])),
                ]
                : null;
        }

        CssValue? style = null, variant = null, weight = null, stretch = null;
        for (var i = 0; i < 4; i++)
        {
            var mark = r.Mark;
            if (r.Keyword("normal") is not null)
                continue;
            if (style is null && Get(PropertyId.FontStyle).Parse(r.OneValue()) is { } s)
            {
                style = s;
                continue;
            }
            r.Reset(mark);
            if (variant is null && r.Keyword("small-caps") is not null)
            {
                variant = new KeywordValue("small-caps");
                continue;
            }
            if (weight is null && (r.Keyword("bold", "bolder", "lighter") is { } wk ? new KeywordValue(wk) : r.Number() is { } n && n is >= 1 and <= 1000 ? (CssValue)new NumberValue(n) : null) is { } w)
            {
                weight = w;
                continue;
            }
            r.Reset(mark);
            if (stretch is null && r.Keyword(FontStretchKeywords.Keys.ToArray()) is { } sk)
            {
                stretch = new PercentageValue(FontStretchKeywords[sk]);
                continue;
            }
            r.Reset(mark);
            break;
        }

        var size = Get(PropertyId.FontSize).Parse(r.OneValue());
        if (size is null)
            return null;
        CssValue? lineHeight = null;
        if (r.Delim('/'))
        {
            lineHeight = Get(PropertyId.LineHeight).Parse(r.OneValue());
            if (lineHeight is null)
                return null;
        }
        if (r.FontFamily() is not { } family)
            return null;

        return
        [
            (PropertyId.FontStyle, style ?? Initial(PropertyId.FontStyle)),
            (PropertyId.FontVariantCaps, variant ?? Initial(PropertyId.FontVariantCaps)),
            (PropertyId.FontWeight, weight ?? Initial(PropertyId.FontWeight)),
            (PropertyId.FontStretch, stretch ?? Initial(PropertyId.FontStretch)),
            (PropertyId.FontSize, size),
            (PropertyId.LineHeight, lineHeight ?? Initial(PropertyId.LineHeight)),
            (PropertyId.FontFamily, family),
        ];
    }

    /// <summary>The style every property's initial value computes to (the root's parent).</summary>
    public static ComputedStyle InitialStyle() => new()
    {
        Font = new FontGroup(["serif"], 16, 400, Style.FontStyle.Normal, LineHeight.Normal, 100, "normal"),
        Inherited = new InheritedGroup(CssColor.Black, Visibility.Visible, ColorSchemeValue.Normal),
        Box = new BoxGroup(Display.Inline, Position.Static, FloatSide.None, Clear.None, BoxSizing.ContentBox, Overflow.Visible, Overflow.Visible, null, 1),
        Size = new SizeGroup(SizeValue.Auto, SizeValue.Auto, SizeValue.Auto, SizeValue.Auto, SizeValue.None, SizeValue.None),
        Spacing = new SpacingGroup(
            SizeValue.Of(default), SizeValue.Of(default), SizeValue.Of(default), SizeValue.Of(default),
            default, default, default, default,
            SizeValue.Auto, SizeValue.Auto, SizeValue.Auto, SizeValue.Auto),
        Border = new BorderGroup(3, 3, 3, 3, BorderStyle.None, BorderStyle.None, BorderStyle.None, BorderStyle.None,
            CssColor.CurrentColor, CssColor.CurrentColor, CssColor.CurrentColor, CssColor.CurrentColor),
        Background = new BackgroundGroup(CssColor.Transparent, [NoImage.Instance],
            [new Style.BackgroundPosition(new LengthPercentage(0, 0), new LengthPercentage(0, 0))],
            [new BackgroundSize(BackgroundSizeKind.Explicit, SizeValue.Auto, SizeValue.Auto)],
            [new RepeatStyle(Css.BackgroundRepeat.Repeat, Css.BackgroundRepeat.Repeat)],
            [Css.BackgroundAttachment.Scroll], [BackgroundBox.PaddingBox], [BackgroundBox.BorderBox]),
        Text = new TextGroup(Style.WhiteSpaceCollapse.Collapse, Style.TextWrapMode.Wrap, new ListStyleType("disc", null), Style.ListStylePosition.Outside),
        Generated = new GeneratedGroup(ContentValue.Normal, [], [], []),
    };
}

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
        Enum e => string.Concat(e.ToString().Select((c, i) => char.IsUpper(c) ? (i > 0 ? "-" : "") + char.ToLowerInvariant(c) : c.ToString())),
        IReadOnlyList<string> list => string.Join(", ", list),
        IReadOnlyList<CounterChange> counters => counters.Count == 0 ? "none" : string.Join(" ", counters),
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
                r => r.Color() is { } c ? new ColorValue(c) : null,
                (v, ctx) => ((ColorValue)v).Color.Resolve(ctx.Parent.Inherited.Color),
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
        };

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

    private static Property<IReadOnlyList<CounterChange>> Counters(PropertyId id, string name, int defaultValue, bool allowReversed,
        Func<ComputedStyle, IReadOnlyList<CounterChange>> get, Action<StyleBuilder, IReadOnlyList<CounterChange>> set) =>
        new(id, name, false, "none",
            r => GeneratedContentParsing.Counters(r, defaultValue, allowReversed),
            (v, _) => ((CounterListValue)v).Changes,
            get, set);

    private static Property<CssColor> Color(PropertyId id, string name, string initial, Func<ComputedStyle, CssColor> get, Action<StyleBuilder, CssColor> set) =>
        new(id, name, false, initial, r => r.Color() is { } c ? new ColorValue(c) : null, (v, _) => ((ColorValue)v).Color, get, set);

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
        // list-style: type || position || image. Images arrive with the image loader, so only none is accepted there.
        ["list-style"] = new([PropertyId.ListStyleType, PropertyId.ListStylePosition], r =>
        {
            CssValue? type = null, position = null;
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
                else
                    return null;
            }
            if (nones > 2 || (nones == 2 && type is not null))
                return null;
            if (nones > 0 && type is null)
                type = new ListStyleTypeValue(Css.ListStyleType.None);
            return
            [
                (PropertyId.ListStyleType, type ?? Get(PropertyId.ListStyleType).Initial),
                (PropertyId.ListStylePosition, position ?? Get(PropertyId.ListStylePosition).Initial),
            ];
        }),
        ["overflow"] = new([PropertyId.OverflowX, PropertyId.OverflowY], r =>
        {
            var x = Get(PropertyId.OverflowX).Parse(r.OneValue());
            if (x is null)
                return null;
            var y = r.AtEnd ? x : Get(PropertyId.OverflowY).Parse(r.OneValue());
            return y is null ? null : [(PropertyId.OverflowX, x), (PropertyId.OverflowY, y)];
        }),
    };

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

    /// <summary>The style every property's initial value computes to (the root's parent).</summary>
    public static ComputedStyle InitialStyle() => new()
    {
        Font = new FontGroup(["serif"], 16, 400, Style.FontStyle.Normal, LineHeight.Normal),
        Inherited = new InheritedGroup(CssColor.Black, Visibility.Visible),
        Box = new BoxGroup(Display.Inline, Position.Static, FloatSide.None, Clear.None, BoxSizing.ContentBox, Overflow.Visible, Overflow.Visible, null, 1),
        Size = new SizeGroup(SizeValue.Auto, SizeValue.Auto, SizeValue.Auto, SizeValue.Auto, SizeValue.None, SizeValue.None),
        Spacing = new SpacingGroup(
            SizeValue.Of(default), SizeValue.Of(default), SizeValue.Of(default), SizeValue.Of(default),
            default, default, default, default,
            SizeValue.Auto, SizeValue.Auto, SizeValue.Auto, SizeValue.Auto),
        Border = new BorderGroup(3, 3, 3, 3, BorderStyle.None, BorderStyle.None, BorderStyle.None, BorderStyle.None,
            CssColor.CurrentColor, CssColor.CurrentColor, CssColor.CurrentColor, CssColor.CurrentColor),
        Background = new BackgroundGroup(CssColor.Transparent),
        Text = new TextGroup(Style.WhiteSpaceCollapse.Collapse, Style.TextWrapMode.Wrap, new ListStyleType("disc", null), Style.ListStylePosition.Outside),
        Generated = new GeneratedGroup(ContentValue.Normal, [], [], []),
    };
}

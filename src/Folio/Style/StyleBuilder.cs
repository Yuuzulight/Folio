using Folio.Css;

namespace Folio.Style;

/// <summary>
/// Builds one element's <see cref="ComputedStyle"/> from its cascaded values: inherited groups start as the parent's,
/// the others as the initial ones, and a group is only replaced when a value in it changes.
/// </summary>
internal sealed class StyleBuilder
{
    private readonly ComputedStyle _parent;
    private System.Collections.Immutable.ImmutableDictionary<string, string> _custom;

    public StyleBuilder(ComputedStyle parent)
    {
        _parent = parent;
        _custom = parent.Custom;
        var initial = ComputedStyle.Initial;
        Font = parent.Font;
        Inherited = parent.Inherited;
        Box = initial.Box;
        Size = initial.Size;
        Spacing = initial.Spacing;
        Border = initial.Border;
        Background = initial.Background;
        Text = parent.Text;
        Generated = initial.Generated;
        Flex = initial.Flex;
        Grid = initial.Grid;
        Shadows = initial.Shadows;
        TextSpacing = parent.TextSpacing;
        Quotes = parent.Quotes;
        Outline = initial.Outline;
        Ui = parent.Ui;
        Decoration = initial.Decoration;
        Transform = initial.Transform;
        Replaced = initial.Replaced;
        Effects = initial.Effects;
        Mask = initial.Mask;
        BorderImage = initial.BorderImage;
        Animation = initial.Animation;
        Svg = parent.Svg;
        SvgStop = initial.SvgStop;
    }

    public FontGroup Font { get; set; }
    public InheritedGroup Inherited { get; set; }
    public BoxGroup Box { get; set; }
    public SizeGroup Size { get; set; }
    public SpacingGroup Spacing { get; set; }
    public BorderGroup Border { get; set; }
    public BackgroundGroup Background { get; set; }
    public TextGroup Text { get; set; }
    public GeneratedGroup Generated { get; set; }
    public FlexGroup Flex { get; set; }
    public GridGroup Grid { get; set; }
    public ShadowGroup Shadows { get; set; }
    public SpacingTextGroup TextSpacing { get; set; }
    public QuotesGroup Quotes { get; set; }
    public OutlineGroup Outline { get; set; }
    public UiGroup Ui { get; set; }
    public DecorationGroup Decoration { get; set; }
    public TransformGroup Transform { get; set; }
    public ReplacedGroup Replaced { get; set; }
    public EffectsGroup Effects { get; set; }
    public MaskGroup Mask { get; set; }
    public BorderImageGroup BorderImage { get; set; }
    public AnimationGroup Animation { get; set; }
    public SvgGroup Svg { get; set; }
    public SvgStopGroup SvgStop { get; set; }

    /// <summary>
    /// Computes an element's style from its cascaded value per property (properties absent from
    /// <paramref name="cascaded"/> inherit or take their initial value). <c>revert</c> and <c>revert-layer</c>
    /// must already be resolved by the cascade; left here they act as <c>unset</c>.
    /// </summary>
    /// <param name="groups">Equal groups built during one style pass, so siblings share instances too.</param>
    public static ComputedStyle Compute(IReadOnlyDictionary<PropertyId, CssValue> cascaded, ComputeContext context, Dictionary<object, object>? groups = null)
    {
        var builder = new StyleBuilder(context.Parent) { _custom = context.Custom };

        // Properties others depend on come first: color-scheme (light-dark()), font-size (em), color (currentcolor).
        if (cascaded.TryGetValue(PropertyId.ColorScheme, out var scheme))
            builder.Apply(Properties.Get(PropertyId.ColorScheme), scheme, context);
        context.UsesDark = builder.Inherited.ColorScheme.UsesDark(context.PrefersDark);
        if (cascaded.TryGetValue(PropertyId.FontSize, out var fontSize))
            builder.Apply(Properties.Get(PropertyId.FontSize), fontSize, context);
        context.FontSize = builder.Font.Size;
        // The rest of the font too, so ex and ch in other properties measure the element's own first available font.
        foreach (var id in (ReadOnlySpan<PropertyId>)[PropertyId.FontFamily, PropertyId.FontWeight, PropertyId.FontStyle, PropertyId.FontStretch])
        {
            if (cascaded.TryGetValue(id, out var value))
                builder.Apply(Properties.Get(id), value, context);
        }
        context.Font = builder.Font;
        if (cascaded.TryGetValue(PropertyId.Color, out var color))
            builder.Apply(Properties.Get(PropertyId.Color), color, context);
        context.CurrentColor = builder.Inherited.Color;
        if (context.Registered is { Count: > 0 } registered)
            context.Custom = builder._custom = CustomProperties.ApplySyntax(context.Custom, context.Parent.Custom, registered, context);

        foreach (var (id, value) in cascaded)
        {
            if (id is not (PropertyId.FontSize or PropertyId.Color or PropertyId.ColorScheme or PropertyId.FontFamily or PropertyId.FontWeight
                    or PropertyId.FontStyle or PropertyId.FontStretch))
                builder.Apply(Properties.Get(id), value, context);
        }
        builder.PropagateDecorations();
        return builder.Build(groups);
    }

    private void Apply(Property property, CssValue value, ComputeContext context)
    {
        if (value is UnparsedValue pending)
            value = CustomProperties.Resolve(pending, property, context.Custom) ?? new CssWideValue(CssWideKeyword.Unset);
        if (value is CssWideValue wide)
        {
            var inherit = wide.Keyword == CssWideKeyword.Inherit || (wide.Keyword != CssWideKeyword.Initial && property.Inherited);
            if (inherit)
                property.Inherit(this, _parent);
            else
                property.Apply(this, property.Initial, context);
            return;
        }
        property.Apply(this, value, context);
    }

    // Decorations reach the text of in-flow descendants, but not floats, absolutely positioned boxes or the contents of
    // atomic inlines (https://www.w3.org/TR/css-text-decor-3/#line-decoration); the element's own go inside its parent's.
    private void PropagateDecorations()
    {
        var applied = Box.Float != FloatSide.None || Box.Position is Position.Absolute or Position.Fixed
            || Box.Display is Display.InlineBlock or Display.InlineTable or Display.InlineFlex or Display.InlineGrid
            ? null : _parent.Inherited.Decorations;
        var own = Decoration;
        if (own.Line != TextDecorationLine.None)
            applied = new AppliedDecoration(own.Line, own.Style, own.Color.Resolve(Inherited.Color), own.Thickness, Text.UnderlineOffset, applied);
        if (!Equals(applied, Inherited.Decorations))
            Inherited = Inherited with { Decorations = applied };
    }

    public ComputedStyle Build(Dictionary<object, object>? groups = null)
    {
        var initial = ComputedStyle.Initial;
        return new ComputedStyle
        {
            Font = Share(Font, _parent.Font, groups),
            Inherited = Share(Inherited, _parent.Inherited, groups),
            Box = Share(Box, initial.Box, groups),
            Size = Share(Size, initial.Size, groups),
            Spacing = Share(Spacing, initial.Spacing, groups),
            Border = Share(Border, initial.Border, groups),
            Background = Share(Background, initial.Background, groups),
            Text = Share(Text, _parent.Text, groups),
            Generated = Share(Generated, initial.Generated, groups),
            Flex = Share(Flex, initial.Flex, groups),
            Grid = Share(Grid, initial.Grid, groups),
            Shadows = Share(Shadows, initial.Shadows, groups),
            TextSpacing = Share(TextSpacing, _parent.TextSpacing, groups),
            Quotes = Share(Quotes, _parent.Quotes, groups),
            Outline = Share(Outline, initial.Outline, groups),
            Ui = Share(Ui, _parent.Ui, groups),
            Decoration = Share(Decoration, initial.Decoration, groups),
            Transform = Share(Transform, initial.Transform, groups),
            Replaced = Share(Replaced, initial.Replaced, groups),
            Effects = Share(Effects, initial.Effects, groups),
            Mask = Share(Mask, initial.Mask, groups),
            BorderImage = Share(BorderImage, initial.BorderImage, groups),
            Animation = Share(Animation, initial.Animation, groups),
            Svg = Share(Svg, _parent.Svg, groups),
            SvgStop = Share(SvgStop, initial.SvgStop, groups),
            Custom = _custom,
        };
    }

    // Reuses an equal instance (the parent's or initial group, or one built earlier in the pass).
    private static T Share<T>(T built, T existing, Dictionary<object, object>? groups) where T : class
    {
        if (ReferenceEquals(built, existing) || built.Equals(existing))
            return existing;
        if (groups is null)
            return built;
        if (groups.TryGetValue(built, out var shared))
            return (T)shared;
        groups[built] = built;
        return built;
    }
}

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

    /// <summary>
    /// Computes an element's style from its cascaded value per property (properties absent from
    /// <paramref name="cascaded"/> inherit or take their initial value). <c>revert</c> and <c>revert-layer</c>
    /// must already be resolved by the cascade; left here they act as <c>unset</c>.
    /// </summary>
    /// <param name="groups">Equal groups built during one style pass, so siblings share instances too.</param>
    public static ComputedStyle Compute(IReadOnlyDictionary<PropertyId, CssValue> cascaded, ComputeContext context, Dictionary<object, object>? groups = null)
    {
        var builder = new StyleBuilder(context.Parent) { _custom = context.Custom };

        // font-size first: em units in every other property refer to it.
        if (cascaded.TryGetValue(PropertyId.FontSize, out var fontSize))
            builder.Apply(Properties.Get(PropertyId.FontSize), fontSize, context);
        context.FontSize = builder.Font.Size;

        foreach (var (id, value) in cascaded)
        {
            if (id != PropertyId.FontSize)
                builder.Apply(Properties.Get(id), value, context);
        }
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

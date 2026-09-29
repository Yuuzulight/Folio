using Folio.Css;

namespace Folio.Style;

/// <summary>
/// Builds one element's <see cref="ComputedStyle"/> from its cascaded values: inherited groups start as the parent's,
/// the others as the initial ones, and a group is only replaced when a value in it changes.
/// </summary>
internal sealed class StyleBuilder
{
    private readonly ComputedStyle _parent;

    public StyleBuilder(ComputedStyle parent)
    {
        _parent = parent;
        var initial = ComputedStyle.Initial;
        Font = parent.Font;
        Inherited = parent.Inherited;
        Box = initial.Box;
        Size = initial.Size;
        Spacing = initial.Spacing;
        Border = initial.Border;
        Background = initial.Background;
    }

    public FontGroup Font { get; set; }
    public InheritedGroup Inherited { get; set; }
    public BoxGroup Box { get; set; }
    public SizeGroup Size { get; set; }
    public SpacingGroup Spacing { get; set; }
    public BorderGroup Border { get; set; }
    public BackgroundGroup Background { get; set; }

    /// <summary>
    /// Computes an element's style from its cascaded value per property (properties absent from
    /// <paramref name="cascaded"/> inherit or take their initial value). <c>revert</c> and <c>revert-layer</c>
    /// must already be resolved by the cascade; left here they act as <c>unset</c>.
    /// </summary>
    public static ComputedStyle Compute(IReadOnlyDictionary<PropertyId, CssValue> cascaded, ComputeContext context)
    {
        var builder = new StyleBuilder(context.Parent);

        // font-size first: em units in every other property refer to it.
        if (cascaded.TryGetValue(PropertyId.FontSize, out var fontSize))
            builder.Apply(Properties.Get(PropertyId.FontSize), fontSize, context);
        context.FontSize = builder.Font.Size;

        foreach (var (id, value) in cascaded)
        {
            if (id != PropertyId.FontSize)
                builder.Apply(Properties.Get(id), value, context);
        }
        return builder.Build();
    }

    private void Apply(Property property, CssValue value, ComputeContext context)
    {
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

    public ComputedStyle Build()
    {
        var initial = ComputedStyle.Initial;
        return new ComputedStyle
        {
            Font = Share(Font, _parent.Font),
            Inherited = Share(Inherited, _parent.Inherited),
            Box = Share(Box, initial.Box),
            Size = Share(Size, initial.Size),
            Spacing = Share(Spacing, initial.Spacing),
            Border = Share(Border, initial.Border),
            Background = Share(Background, initial.Background),
        };
    }

    // Reuses the existing instance when the values are equal, so unchanged groups stay shared.
    private static T Share<T>(T built, T existing) where T : class => built.Equals(existing) ? existing : built;
}

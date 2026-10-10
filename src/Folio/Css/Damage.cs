namespace Folio.Css;

/// <summary>
/// What a change to a property's computed value invalidates (docs/study/14-invalidation.md), from least to most:
/// the display list, the layout, or the box tree. Each includes the ones before it.
/// </summary>
internal enum Damage
{
    /// <summary>Only painting changes: the last layout is painted again.</summary>
    Paint,

    /// <summary>Sizes or positions may change: the boxes are laid out again.</summary>
    Layout,

    /// <summary>Which boxes exist, or their inline content, may change: the box subtree is built again.</summary>
    Boxes,
}

/// <summary>The damage tag of every property in the table (<see cref="PropertyId"/>); a unit test fails on an untagged one.</summary>
internal static class PropertyDamage
{
    // No box, line or position depends on these. The stacking tree is built again with every display list, so z-index,
    // opacity and the like are here; animation and transition properties change nothing until their effects restyle.
    private static readonly HashSet<PropertyId> Paint =
    [
        PropertyId.Opacity, PropertyId.ZIndex, PropertyId.Isolation, PropertyId.MixBlendMode, PropertyId.BackgroundBlendMode,
        PropertyId.Color, PropertyId.ColorScheme, PropertyId.AccentColor, PropertyId.ScrollbarColor, PropertyId.Cursor, PropertyId.PointerEvents, PropertyId.UserSelect,
        PropertyId.BackgroundColor, PropertyId.BackgroundImage, PropertyId.BackgroundPosition, PropertyId.BackgroundSize, PropertyId.BackgroundRepeat,
        PropertyId.BackgroundAttachment, PropertyId.BackgroundOrigin, PropertyId.BackgroundClip,
        PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor,
        PropertyId.BorderBlockStartColor, PropertyId.BorderBlockEndColor, PropertyId.BorderInlineStartColor, PropertyId.BorderInlineEndColor,
        PropertyId.BorderTopLeftRadius, PropertyId.BorderTopRightRadius, PropertyId.BorderBottomRightRadius, PropertyId.BorderBottomLeftRadius,
        PropertyId.BorderImageSource, PropertyId.BorderImageSlice, PropertyId.BorderImageWidth, PropertyId.BorderImageOutset, PropertyId.BorderImageRepeat,
        PropertyId.BoxShadow, PropertyId.TextShadow,
        PropertyId.TextDecorationLine, PropertyId.TextDecorationStyle, PropertyId.TextDecorationColor, PropertyId.TextDecorationThickness,
        PropertyId.TextUnderlineOffset, PropertyId.TextDecorationSkipInk,
        PropertyId.OutlineWidth, PropertyId.OutlineStyle, PropertyId.OutlineColor, PropertyId.OutlineOffset,
        PropertyId.ColumnRuleWidth, PropertyId.ColumnRuleStyle, PropertyId.ColumnRuleColor,
        PropertyId.ObjectFit, PropertyId.ObjectPosition, PropertyId.ImageRendering,
        PropertyId.MaskMode, PropertyId.MaskRepeat, PropertyId.MaskPosition, PropertyId.MaskSize, PropertyId.MaskOrigin, PropertyId.MaskClip,
        PropertyId.MaskComposite, PropertyId.MaskBorderSource, PropertyId.MaskBorderSlice, PropertyId.MaskBorderWidth, PropertyId.MaskBorderOutset,
        PropertyId.MaskBorderRepeat, PropertyId.MaskBorderMode,
        PropertyId.AnimationName, PropertyId.AnimationIterationCount, PropertyId.AnimationDirection, PropertyId.AnimationFillMode,
        PropertyId.AnimationDuration, PropertyId.AnimationDelay, PropertyId.AnimationTimingFunction, PropertyId.AnimationPlayState,
        PropertyId.TransitionProperty, PropertyId.TransitionDuration, PropertyId.TransitionTimingFunction, PropertyId.TransitionDelay,
        PropertyId.TransitionBehavior,
    ];

    // These decide which boxes the box tree builder makes (blockification, generated content, markers, anonymous boxes,
    // column spanners) or the inline content it collects (white space processing, text transforms, bidi controls).
    private static readonly HashSet<PropertyId> Boxes =
    [
        PropertyId.Display, PropertyId.Position, PropertyId.Float, PropertyId.Content, PropertyId.Quotes,
        PropertyId.CounterReset, PropertyId.CounterIncrement, PropertyId.CounterSet,
        PropertyId.ListStyleType, PropertyId.ListStylePosition, PropertyId.ListStyleImage,
        PropertyId.WhiteSpaceCollapse, PropertyId.TextTransform, PropertyId.Direction, PropertyId.UnicodeBidi, PropertyId.WritingMode,
        PropertyId.ColumnCount, PropertyId.ColumnWidth, PropertyId.ColumnSpan,
    ];

    // Everything else changes sizes or positions. Among them: visibility (collapse removes table rows and columns);
    // transforms, perspective, filters and backdrop filters (they make containing blocks for fixed boxes); and SVG
    // properties, clip paths and mask images (SVG content and url() references are laid out with the box).
    private static readonly HashSet<PropertyId> Layout =
    [
        PropertyId.Clear, PropertyId.BoxSizing, PropertyId.Visibility, PropertyId.OverflowX, PropertyId.OverflowY,
        PropertyId.Width, PropertyId.Height, PropertyId.MinWidth, PropertyId.MinHeight, PropertyId.MaxWidth, PropertyId.MaxHeight,
        PropertyId.MarginTop, PropertyId.MarginRight, PropertyId.MarginBottom, PropertyId.MarginLeft,
        PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft,
        PropertyId.Top, PropertyId.Right, PropertyId.Bottom, PropertyId.Left,
        PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth,
        PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle,
        PropertyId.FontFamily, PropertyId.FontSize, PropertyId.FontWeight, PropertyId.FontStyle, PropertyId.FontStretch, PropertyId.FontVariantCaps,
        PropertyId.FontVariantNumeric, PropertyId.FontFeatureSettings, PropertyId.LineHeight, PropertyId.TextWrapMode,
        PropertyId.TextAlign, PropertyId.TextAlignLast, PropertyId.VerticalAlign, PropertyId.TextIndent, PropertyId.Hyphens,
        PropertyId.LetterSpacing, PropertyId.WordSpacing, PropertyId.TabSize, PropertyId.WordBreak, PropertyId.OverflowWrap,
        PropertyId.TextOverflow, PropertyId.LineClamp, PropertyId.AspectRatio, PropertyId.ScrollbarGutter, PropertyId.ScrollbarWidth,
        PropertyId.FlexDirection, PropertyId.FlexWrap, PropertyId.JustifyContent, PropertyId.AlignItems, PropertyId.AlignSelf,
        PropertyId.AlignContent, PropertyId.FlexGrow, PropertyId.FlexShrink, PropertyId.FlexBasis, PropertyId.Order,
        PropertyId.RowGap, PropertyId.ColumnGap, PropertyId.GridTemplateColumns, PropertyId.GridTemplateRows, PropertyId.GridAutoColumns,
        PropertyId.GridAutoRows, PropertyId.GridAutoFlow, PropertyId.GridRowStart, PropertyId.GridRowEnd, PropertyId.GridColumnStart,
        PropertyId.GridColumnEnd, PropertyId.JustifyItems, PropertyId.JustifySelf, PropertyId.GridTemplateAreas,
        PropertyId.TableLayout, PropertyId.BorderCollapse, PropertyId.BorderSpacing, PropertyId.CaptionSide, PropertyId.EmptyCells,
        PropertyId.ColumnFill, PropertyId.BreakInside,
        PropertyId.Transform, PropertyId.Translate, PropertyId.Rotate, PropertyId.Scale, PropertyId.TransformOrigin,
        PropertyId.Perspective, PropertyId.PerspectiveOrigin, PropertyId.Filter, PropertyId.BackdropFilter, PropertyId.ClipPath,
        PropertyId.MaskImage, PropertyId.MaskType,
        PropertyId.Fill, PropertyId.FillOpacity, PropertyId.FillRule, PropertyId.Stroke, PropertyId.StrokeOpacity, PropertyId.StrokeWidth,
        PropertyId.StrokeLinecap, PropertyId.StrokeLinejoin, PropertyId.StrokeMiterlimit, PropertyId.StrokeDasharray, PropertyId.StrokeDashoffset,
        PropertyId.PaintOrder, PropertyId.TextAnchor, PropertyId.DominantBaseline, PropertyId.StopColor, PropertyId.StopOpacity,
        PropertyId.FloodColor, PropertyId.FloodOpacity, PropertyId.LightingColor, PropertyId.ColorInterpolationFilters, PropertyId.ClipRule,
        PropertyId.MarkerStart, PropertyId.MarkerMid, PropertyId.MarkerEnd,
        PropertyId.MarginBlockStart, PropertyId.MarginBlockEnd, PropertyId.MarginInlineStart, PropertyId.MarginInlineEnd,
        PropertyId.PaddingBlockStart, PropertyId.PaddingBlockEnd, PropertyId.PaddingInlineStart, PropertyId.PaddingInlineEnd,
        PropertyId.InsetBlockStart, PropertyId.InsetBlockEnd, PropertyId.InsetInlineStart, PropertyId.InsetInlineEnd,
        PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockEndWidth, PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineEndWidth,
        PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockEndStyle, PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineEndStyle,
        PropertyId.InlineSize, PropertyId.BlockSize, PropertyId.MinInlineSize, PropertyId.MinBlockSize, PropertyId.MaxInlineSize, PropertyId.MaxBlockSize,
    ];

    /// <summary>The property's tag, or null when it has none (which the table test reports).</summary>
    public static Damage? Of(PropertyId id) =>
        Paint.Contains(id) ? Damage.Paint : Boxes.Contains(id) ? Damage.Boxes : Layout.Contains(id) ? Damage.Layout : null;

    /// <summary>How many tags a property has; exactly one each, checked by a unit test.</summary>
    internal static int TagCount(PropertyId id) => (Paint.Contains(id) ? 1 : 0) + (Boxes.Contains(id) ? 1 : 0) + (Layout.Contains(id) ? 1 : 0);
}

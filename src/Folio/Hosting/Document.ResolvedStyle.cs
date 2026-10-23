using System.Globalization;
using Folio.Dom;
using Folio.Painting;
using Folio.Style;

namespace Folio;

// The resolved values CSSOM asks for (https://www.w3.org/TR/cssom-1/#resolved-values): for the properties that are measured in
// layout, the used value of the last layout instead of the computed one.
public sealed partial class Document
{
    private static readonly HashSet<string> ReplacedTags = new(StringComparer.Ordinal)
    {
        "img", "video", "canvas", "svg", "iframe", "object", "embed", "input", "textarea", "select", "button", "audio", "meter", "progress",
    };

    internal static string PxText(float value)
    {
        var rounded = (float)Math.Round(value, 4);
        return (rounded == 0 ? 0 : rounded).ToString("0.####", CultureInfo.InvariantCulture) + "px";
    }

    /// <summary>
    /// The used value of a property whose resolved value is measured in layout: <c>width</c> and <c>height</c> (as box-sizing reads
    /// them), the margins and paddings, and the offsets of a positioned element. Null when the computed value is the answer: the
    /// element has no box, the property does not apply, or it is not one of these.
    /// </summary>
    internal string? UsedValue(ElementNode element, string property)
    {
        property = Physical(element, property);
        if (!UsedProperties.Contains(property))
            return null;
        FlushLayout();
        if (FindFragment(element)?.Box?.Style is not { } style || LayoutBox(element) is not { } own)
            return null;
        var (width, height) = (own.Rect.Width, own.Rect.Height);
        var (cbWidth, cbHeight) = (ContainingSize(element, horizontal: true), ContainingSize(element, horizontal: false));
        var spacing = style.Spacing;
        float Pad(LengthPercentage p) => p.Resolve(cbWidth);
        switch (property)
        {
            case "width" or "height":
            {
                if (style.Box.Display == Display.Inline && !ReplacedTags.Contains(element.LocalName))
                    return null;
                var horizontal = property == "width";
                var border = style.Border;
                var size = horizontal ? width : height;
                if (style.Box.BoxSizing == BoxSizing.ContentBox)
                {
                    size -= horizontal
                        ? border.LeftWidth + border.RightWidth + Pad(spacing.PaddingLeft) + Pad(spacing.PaddingRight)
                        : border.TopWidth + border.BottomWidth + Pad(spacing.PaddingTop) + Pad(spacing.PaddingBottom);
                }
                return PxText(Math.Max(0, size));
            }
            case "padding-top": return PxText(Pad(spacing.PaddingTop));
            case "padding-right": return PxText(Pad(spacing.PaddingRight));
            case "padding-bottom": return PxText(Pad(spacing.PaddingBottom));
            case "padding-left": return PxText(Pad(spacing.PaddingLeft));
            case "margin-top" or "margin-bottom":
                return spacing.MarginTop.Kind is SizeKind.Auto or SizeKind.Length && spacing.MarginBottom.Kind is SizeKind.Auto or SizeKind.Length
                    ? PxText(MarginUsed(property == "margin-top" ? spacing.MarginTop : spacing.MarginBottom, cbWidth)) : null;
            case "margin-left" or "margin-right":
            {
                var (left, right) = (spacing.MarginLeft, spacing.MarginRight);
                if (left.Kind is not (SizeKind.Auto or SizeKind.Length) || right.Kind is not (SizeKind.Auto or SizeKind.Length))
                    return null;
                var value = property == "margin-left" ? left : right;
                if (value.Kind == SizeKind.Length)
                    return PxText(value.Length.Resolve(cbWidth));
                // An auto margin takes what the line leaves, shared by the autos on it (block-level boxes in normal flow).
                var inFlow = style.Box.Position is Position.Static or Position.Relative && style.Box.Float == FloatSide.None && style.Box.Display != Display.Inline;
                if (!inFlow)
                    return PxText(0);
                var fixedTotal = (left.Kind == SizeKind.Length ? left.Length.Resolve(cbWidth) : 0) + (right.Kind == SizeKind.Length ? right.Length.Resolve(cbWidth) : 0);
                var autos = (left.Kind == SizeKind.Auto ? 1 : 0) + (right.Kind == SizeKind.Auto ? 1 : 0);
                return PxText(Math.Max(0, (cbWidth - width - fixedTotal) / autos));
            }
            case "top" or "right" or "bottom" or "left":
                return OffsetUsed(element, style, own.Rect, property, cbWidth, cbHeight);
        }
        return null;
    }

    private static readonly HashSet<string> UsedProperties = new(StringComparer.Ordinal)
    {
        "width", "height", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "margin-top", "margin-right", "margin-bottom", "margin-left", "top", "right", "bottom", "left",
    };

    private static float MarginUsed(SizeValue margin, float cbWidth) => margin.Kind == SizeKind.Length ? margin.Length.Resolve(cbWidth) : 0;

    private string? OffsetUsed(ElementNode element, ComputedStyle style, RectF rect, string property, float cbWidth, float cbHeight)
    {
        var position = style.Box.Position;
        if (position is Position.Static or Position.Sticky)
            return null;
        var spacing = style.Spacing;
        var (top, right, bottom, left) = (spacing.Top, spacing.Right, spacing.Bottom, spacing.Left);
        var horizontal = property is "left" or "right";
        var own = property switch { "top" => top, "right" => right, "bottom" => bottom, _ => left };
        var opposite = property switch { "top" => bottom, "right" => left, "bottom" => top, _ => right };
        if (own.Kind is not (SizeKind.Auto or SizeKind.Length))
            return null;
        if (own.Kind == SizeKind.Length)
            return PxText(own.Length.Resolve(horizontal ? cbWidth : cbHeight));
        if (position == Position.Relative)
            return PxText(opposite.Kind == SizeKind.Length ? -opposite.Length.Resolve(horizontal ? cbWidth : cbHeight) : 0);
        // An absolutely positioned box with an auto offset sits where the flow put it: the distance from the containing block's edge.
        var (originX, originY, blockWidth, blockHeight) = PaddingBoxOf(element, style);
        var margin = property switch
        {
            "top" => MarginUsed(spacing.MarginTop, cbWidth),
            "right" => MarginUsed(spacing.MarginRight, cbWidth),
            "bottom" => MarginUsed(spacing.MarginBottom, cbWidth),
            _ => MarginUsed(spacing.MarginLeft, cbWidth),
        };
        return PxText(property switch
        {
            "left" => rect.X - originX - margin,
            "top" => rect.Y - originY - margin,
            "right" => blockWidth - (rect.X - originX) - rect.Width - margin,
            _ => blockHeight - (rect.Y - originY) - rect.Height - margin,
        });
    }

    /// <summary>The padding box of an absolutely positioned (or fixed) element's containing block: origin and size, in layout coordinates.</summary>
    private (float X, float Y, float Width, float Height) PaddingBoxOf(ElementNode element, ComputedStyle style)
    {
        if (style.Box.Position != Position.Fixed)
        {
            for (var cursor = element.Parent as ElementNode; cursor is not null; cursor = cursor.Parent as ElementNode)
            {
                if (FindFragment(cursor)?.Box?.Style is { } s && s.Box.Position != Position.Static && LayoutBox(cursor) is { } box)
                    return (box.Rect.X + s.Border.LeftWidth, box.Rect.Y + s.Border.TopWidth,
                        box.Rect.Width - s.Border.LeftWidth - s.Border.RightWidth, box.Rect.Height - s.Border.TopWidth - s.Border.BottomWidth);
            }
        }
        return (0, 0, _page?.Width ?? 0, _page?.Height ?? 0);
    }

    /// <summary>
    /// The size of the containing block a percentage in this element's style is a percentage of: the parent's content box, or for an
    /// absolutely positioned element the padding box of the positioned ancestor, or for a fixed one the viewport.
    /// </summary>
    internal float ContainingSize(ElementNode element, bool horizontal)
    {
        var position = FindFragment(element)?.Box?.Style.Box.Position ?? Position.Static;
        if (position is Position.Absolute or Position.Fixed && FindFragment(element)?.Box?.Style is { } style)
        {
            var (_, _, w, h) = PaddingBoxOf(element, style);
            return horizontal ? w : h;
        }
        if (element.Parent is ElementNode parent)
            return ContentSize(parent, horizontal);
        return horizontal ? _page?.Width ?? 0 : _page?.Height ?? 0;
    }

    private float ContentSize(ElementNode element, bool horizontal)
    {
        if (LayoutBox(element) is not { } own)
            return 0;
        if (FindFragment(element)?.Box?.Style is not { } style)
            return horizontal ? own.Rect.Width : own.Rect.Height;
        var cbWidth = ContainingSize(element, horizontal: true);
        var border = style.Border;
        var spacing = style.Spacing;
        return horizontal
            ? Math.Max(0, own.Rect.Width - border.LeftWidth - border.RightWidth - spacing.PaddingLeft.Resolve(cbWidth) - spacing.PaddingRight.Resolve(cbWidth))
            : Math.Max(0, own.Rect.Height - border.TopWidth - border.BottomWidth - spacing.PaddingTop.Resolve(cbWidth) - spacing.PaddingBottom.Resolve(cbWidth));
    }

    /// <summary>The physical property a logical one stands for in the element's writing mode (horizontal ones only); the name itself otherwise.</summary>
    internal string Physical(ElementNode element, string property)
    {
        if (!property.Contains("-inline") && !property.Contains("-block") && property is not ("inline-size" or "block-size" or "min-inline-size" or "min-block-size" or "max-inline-size" or "max-block-size")
            && !property.StartsWith("inset-", StringComparison.Ordinal))
            return property;
        var style = FindFragment(element)?.Box?.Style ?? element.ComputedStyle();
        if (style is null || style.Text.IsVertical)
            return property;
        var rtl = style.Text.Direction == Direction.Rtl;
        var (start, end) = rtl ? ("right", "left") : ("left", "right");
        return property switch
        {
            "inline-size" => "width",
            "block-size" => "height",
            "min-inline-size" => "min-width",
            "min-block-size" => "min-height",
            "max-inline-size" => "max-width",
            "max-block-size" => "max-height",
            _ => Rewrite(property, start, end),
        };

        static string Rewrite(string name, string start, string end)
        {
            switch (name)
            {
                case "inset-inline-start": return start;
                case "inset-inline-end": return end;
                case "inset-block-start": return "top";
                case "inset-block-end": return "bottom";
            }
            foreach (var (logical, physical) in new[] { ("-inline-start", "-" + start), ("-inline-end", "-" + end), ("-block-start", "-top"), ("-block-end", "-bottom") })
            {
                if (name.EndsWith(logical, StringComparison.Ordinal))
                    return name[..^logical.Length] + physical;
            }
            return name;
        }
    }
}

using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

internal readonly record struct DomRect(float X, float Y, float Width, float Height)
{
    public float Top => Y;
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float Left => X;
}

internal static class DomGeometry
{
    public static DomRect Rect(ElementNode element, Func<ElementNode, (float X, float Y, float Width, float Height)?>? layout = null)
    {
        if (layout?.Invoke(element) is { } bounds)
            return new DomRect(bounds.X, bounds.Y, Math.Max(0, bounds.Width), Math.Max(0, bounds.Height));

        var style = element.ComputedStyle();
        var width = style is not null && style.Size.Width.Kind == SizeKind.Length ? style.Size.Width.Length.Px : ParseInlinePixels(element, "width");
        var height = style is not null && style.Size.Height.Kind == SizeKind.Length ? style.Size.Height.Length.Px : ParseInlinePixels(element, "height");
        return new DomRect(0, 0, Math.Max(0, width), Math.Max(0, height));
    }

    private static float ParseInlinePixels(ElementNode element, string name)
    {
        var value = new LiveStyleDeclaration(element).GetValue(name);
        return value.EndsWith("px", StringComparison.OrdinalIgnoreCase) && float.TryParse(value[..^2], out var pixels) ? pixels : 0;
    }
}

using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

internal sealed class LiveComputedStyle(ElementNode element)
{
    public ElementNode Element { get; } = element;
    public IReadOnlyList<Property> Properties => Folio.Css.Properties.All;

    public string Get(string name)
    {
        if (name.StartsWith("--", StringComparison.Ordinal))
            return Element.ComputedStyle()?.Custom.GetValueOrDefault(name) ?? "";
        name = LiveStyleDeclaration.NormalizeName(name);
        var property = Folio.Css.Properties.Find(name);
        return property is null ? "" : property.Describe(Element.ComputedStyle() ?? Folio.Style.ComputedStyle.Initial);
    }
}

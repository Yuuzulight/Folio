using System.Text;
using Folio.Dom;
using Folio.Style;

namespace Folio.Scripting.Dom;

internal static class DomText
{
    public static string InnerText(ElementNode element)
    {
        var text = new StringBuilder();
        Append(element, text);
        return text.ToString().TrimEnd('\n');
    }

    private static void Append(Node node, StringBuilder text)
    {
        if (node is ElementNode element)
        {
            if (element.ComputedStyle()?.Box.Display == Folio.Style.Display.None)
                return;
            if (element.Name.Namespace == Namespaces.Html && element.LocalName == "br")
            {
                text.Append('\n');
                return;
            }
            var block = element.ComputedStyle()?.Box.Display is Folio.Style.Display.Block or Folio.Style.Display.FlowRoot or Folio.Style.Display.ListItem
                or Folio.Style.Display.Table or Folio.Style.Display.Flex or Folio.Style.Display.Grid;
            if (block && text.Length > 0 && text[^1] != '\n')
                text.Append('\n');
            foreach (var child in element.Children)
                Append(child, text);
            if (block && text.Length > 0 && text[^1] != '\n')
                text.Append('\n');
            return;
        }
        if (node is Text t)
            text.Append(t.Data);
    }
}

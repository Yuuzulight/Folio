using Folio.Css;
using Folio.Dom;

namespace Folio.Style;

/// <summary>An element's computed style and those of its ::before, ::after and ::marker pseudo-elements.</summary>
internal sealed class ElementStyles(ComputedStyle style)
{
    public ComputedStyle Style { get; } = style;
    public ComputedStyle? Before { get; set; }
    public ComputedStyle? After { get; set; }
    public ComputedStyle? Marker { get; set; }
}

internal static class ElementStyleExtensions
{
    /// <summary>The element's computed style, once <see cref="StyleResolver"/> has run.</summary>
    public static ComputedStyle? ComputedStyle(this Element element) => (element.StyleData as ElementStyles)?.Style;

    /// <summary>A pseudo-element's style: set for ::before/::after when rules target them, and for ::marker on list items.</summary>
    public static ComputedStyle? PseudoStyle(this Element element, PseudoElement pseudoElement) => element.StyleData is ElementStyles styles
        ? pseudoElement switch
        {
            PseudoElement.Before => styles.Before,
            PseudoElement.After => styles.After,
            PseudoElement.Marker => styles.Marker,
            _ => styles.Style,
        }
        : null;
}

/// <summary>
/// Computes the style of every element in a document: collects the user-agent, user and author stylesheets,
/// then cascades and computes element by element in tree order, keeping the ancestor filter current.
/// </summary>
internal static class StyleResolver
{
    private static readonly Lazy<CascadeData> UserAgent = new(() =>
    {
        using var stream = typeof(StyleResolver).Assembly.GetManifestResourceStream("Folio.UserAgent.css")!;
        using var reader = new StreamReader(stream);
        var data = new CascadeData(Origin.UserAgent, Shared, new MediaContext(800, 600));
        data.Add(CssParser.ParseStyleSheet(reader.ReadToEnd()));
        return data;
    });

    // The UA sheet is shared by every document, so its names are interned in the process-wide table.
    private static Atom Shared(string name) => AtomTable.Shared.TryIntern(name, out var atom) ? atom : Atom.None;

    /// <param name="userStyleSheet">The host's user stylesheet (FolioOptions.UserStyleSheet), if any.</param>
    public static void Resolve(DocumentNode document, MediaContext media, string? userStyleSheet = null)
    {
        var origins = new List<CascadeData> { UserAgent.Value };
        if (userStyleSheet is not null)
        {
            var user = new CascadeData(Origin.User, document.Intern, media);
            user.Add(CssParser.ParseStyleSheet(userStyleSheet));
            origins.Add(user);
        }
        var author = new CascadeData(Origin.Author, document.Intern, media);
        foreach (var style in StyleElements(document))
        {
            var mediaAttribute = style.GetAttribute("media");
            if (mediaAttribute is null || Conditions.MediaMatches(mediaAttribute, media))
                author.Add(CssParser.ParseStyleSheet(style.TextContent ?? ""));
        }
        origins.Add(author);

        var registered = origins.SelectMany(o => o.Registered).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        var filter = new AncestorFilter();
        var context = new MatchContext { Filter = filter };
        var rootFontSize = Style.ComputedStyle.Initial.Font.Size;
        var groups = new Dictionary<object, object>();

        // Iterative pre-order walk: (element, parent style); a null element marks leaving an element.
        var stack = new Stack<(Element? Element, Element? Leaving, ComputedStyle Parent)>();
        if (document.DocumentElement is { } root)
            stack.Push((root, null, Style.ComputedStyle.Initial));
        while (stack.TryPop(out var item))
        {
            if (item.Element is null)
            {
                filter.Pop(item.Leaving!);
                continue;
            }
            var element = item.Element;

            List<CascadeDeclaration>? inline = null;
            if (element.GetAttribute("style") is { } styleAttribute)
            {
                var (source, block) = CssParser.ParseBlockContents(styleAttribute);
                inline = CascadeData.Parse(source, block.Declarations);
            }
            var (values, custom) = Cascade.Compute(element, origins, inline, int.MaxValue, context);

            var computeContext = new ComputeContext(item.Parent, rootFontSize, media.Width, media.Height)
            {
                PrefersDark = media.DarkColorScheme,
                Custom = CustomProperties.Compute(item.Parent.Custom, custom, registered),
            };
            var style = StyleBuilder.Compute(values, computeContext, groups);
            var styles = new ElementStyles(style);
            element.StyleData = styles;
            if (style.Box.Display != Display.None)
            {
                ComputedStyle? Pseudo(PseudoElement pe, bool always = false)
                {
                    if (!always && !origins.Any(o => o.Rules.HasRulesFor(pe)))
                        return null;
                    var (pseudoValues, pseudoCustom) = Cascade.Compute(element, origins, null, 0, context, pe);
                    var pseudoContext = new ComputeContext(style, rootFontSize, media.Width, media.Height)
                    {
                        PrefersDark = media.DarkColorScheme,
                        Custom = CustomProperties.Compute(style.Custom, pseudoCustom, registered),
                    };
                    return StyleBuilder.Compute(pseudoValues, pseudoContext, groups);
                }
                styles.Before = Pseudo(PseudoElement.Before);
                styles.After = Pseudo(PseudoElement.After);
                if (style.Box.Display == Display.ListItem)
                    styles.Marker = Pseudo(PseudoElement.Marker, always: true);
            }
            if (element.Parent is DocumentNode)
                rootFontSize = style.Font.Size;

            filter.Push(element);
            stack.Push((null, element, style));
            for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
            {
                if (child is Element e)
                    stack.Push((e, null, style));
            }
        }
    }

    // <style> elements in tree order whose type is CSS (https://html.spec.whatwg.org/multipage/semantics.html#the-style-element).
    private static IEnumerable<Element> StyleElements(DocumentNode document)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is Element { LocalName: "style" } style && style.Name.Namespace == Namespaces.Html
                && (style.GetAttribute("type") is not { Length: > 0 } type || type.Equals("text/css", StringComparison.OrdinalIgnoreCase)))
                yield return style;
        }
    }
}

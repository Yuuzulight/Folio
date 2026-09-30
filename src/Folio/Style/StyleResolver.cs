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
    /// <param name="sources">
    /// Where link elements and @import load from; by default only data: URLs, with no base URL.
    /// </param>
    public static void Resolve(DocumentNode document, MediaContext media, string? userStyleSheet = null, StyleSources? sources = null)
    {
        sources ??= new StyleSources(Resources.ResourceLoader.DataUrlsOnly, null);
        sources = sources with
        {
            BaseUrl = BaseUrl(document, sources.BaseUrl),
            RequireCssType = sources.RequireCssType && document.Mode != DocumentMode.Quirks,
        };

        var origins = new List<CascadeData> { UserAgent.Value };
        if (userStyleSheet is not null)
        {
            var user = new CascadeData(Origin.User, document.Intern, media, sources);
            user.Add(CssParser.ParseStyleSheet(userStyleSheet));
            origins.Add(user);
        }
        var author = new CascadeData(Origin.Author, document.Intern, media, sources);
        foreach (var element in StyleSheetElements(document))
        {
            var mediaAttribute = element.GetAttribute("media");
            if (mediaAttribute is not null && !Conditions.MediaMatches(mediaAttribute, media))
                continue;
            if (element.LocalName == "style")
            {
                author.Add(CssParser.ParseStyleSheet(element.TextContent ?? ""));
                continue;
            }
            // https://html.spec.whatwg.org/multipage/links.html#link-type-stylesheet
            var href = element.GetAttribute("href") ?? "";
            if (Resources.ResourceLoader.Resolve(sources.BaseUrl, href) is not { } url)
            {
                sources.Report?.Invoke($"Stylesheet \"{href}\" has no base URL to resolve against.");
                continue;
            }
            if (sources.LoadStyleSheet(url) is { } css)
                author.Add(CssParser.ParseStyleSheet(css), url);
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
            var (values, custom) = Cascade.Compute(element, origins, inline, int.MaxValue, context, hints: PresentationalHints.For(element));

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

    // style elements and link rel=stylesheet elements in tree order whose type is CSS
    // (https://html.spec.whatwg.org/multipage/semantics.html#the-style-element, #the-link-element). Alternate
    // stylesheets and disabled links are not applied.
    private static IEnumerable<Element> StyleSheetElements(DocumentNode document)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is not Element element || element.Name.Namespace != Namespaces.Html
                || element.GetAttribute("type") is { Length: > 0 } type && !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
                continue;
            if (element.LocalName == "style")
            {
                yield return element;
            }
            else if (element.LocalName == "link" && element.GetAttribute("disabled") is null && element.GetAttribute("href") is { Length: > 0 })
            {
                var rel = (element.GetAttribute("rel") ?? "").Split(Element.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries);
                if (rel.Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && !rel.Contains("alternate", StringComparer.OrdinalIgnoreCase))
                    yield return element;
            }
        }
    }

    // https://html.spec.whatwg.org/multipage/semantics.html#the-base-element: the first base element with href.
    private static string? BaseUrl(DocumentNode document, string? documentUrl)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is Element { LocalName: "base" } element && element.Name.Namespace == Namespaces.Html && element.GetAttribute("href") is { } href)
                return Resources.ResourceLoader.Resolve(documentUrl, href) ?? documentUrl;
        }
        return documentUrl;
    }
}

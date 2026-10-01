using Folio.Css;
using Folio.Dom;

namespace Folio.Style;

/// <summary>An element's computed style and those of its ::before, ::after and ::marker pseudo-elements.</summary>
internal sealed class ElementStyles(ComputedStyle style)
{
    public ComputedStyle Style { get; set; } = style;
    public ComputedStyle? Before { get; set; }
    public ComputedStyle? After { get; set; }
    public ComputedStyle? Marker { get; set; }
    public ComputedStyle? FirstLetter { get; set; }
}

internal static class ElementStyleExtensions
{
    /// <summary>The element's computed style, once <see cref="StyleResolver"/> has run.</summary>
    public static ComputedStyle? ComputedStyle(this ElementNode element) => (element.StyleData as ElementStyles)?.Style;

    /// <summary>A pseudo-element's style: set for ::before/::after when rules target them, and for ::marker on list items.</summary>
    public static ComputedStyle? PseudoStyle(this ElementNode element, PseudoElement pseudoElement) => element.StyleData is ElementStyles styles
        ? pseudoElement switch
        {
            PseudoElement.Before => styles.Before,
            PseudoElement.After => styles.After,
            PseudoElement.Marker => styles.Marker,
            PseudoElement.FirstLetter => styles.FirstLetter,
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
    /// <param name="measure">Measures fonts for ex and ch; without it they are 0.5em.</param>
    /// <param name="animationTime">Seconds on the document timeline animations are sampled at; null for the settled document.</param>
    /// <returns>The <c>@font-face</c> rules of the user and author stylesheets, in that order.</returns>
    public static List<FontFaceRule> Resolve(DocumentNode document, MediaContext media, string? userStyleSheet = null, StyleSources? sources = null,
                                             FontMeasure? measure = null, double? animationTime = null)
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
        var keyframes = new Dictionary<string, List<Keyframe>>(StringComparer.Ordinal);
        foreach (var (name, blocks) in origins.SelectMany(o => o.Keyframes))
            keyframes[name] = blocks;
        var animations = new List<AnimatedElement>();
        var filter = new AncestorFilter();
        var context = new MatchContext { Filter = filter };
        var rootFontSize = Style.ComputedStyle.Initial.Font.Size;
        var groups = new Dictionary<object, object>();
        var shared = new SharingCache();
        var (matched, pseudoMatched) = (new List<RuleIndex<CascadeRule>.Entry>(), new List<RuleIndex<CascadeRule>.Entry>());
        var (hasBefore, hasAfter) = (origins.Any(o => o.Rules.HasRulesFor(PseudoElement.Before)), origins.Any(o => o.Rules.HasRulesFor(PseudoElement.After)));
        var hasFirstLetter = origins.Any(o => o.Rules.HasRulesFor(PseudoElement.FirstLetter));

        // Iterative pre-order walk: (element, parent style); a null element marks leaving an element.
        var stack = new Stack<(ElementNode? ElementNode, ElementNode? Leaving, ComputedStyle Parent)>();
        if (document.DocumentElement is { } root)
            stack.Push((root, null, Style.ComputedStyle.Initial));
        while (stack.TryPop(out var item))
        {
            if (item.ElementNode is null)
            {
                filter.Pop(item.Leaving!);
                continue;
            }
            var element = item.ElementNode;

            List<CascadeDeclaration>? inline = null;
            if (element.GetAttribute("style") is { } styleAttribute)
            {
                var (source, block) = CssParser.ParseBlockContents(styleAttribute);
                inline = CascadeData.Parse(source, block.Declarations, sources.BaseUrl);
            }
            matched.Clear();
            Cascade.Match(element, origins, context, PseudoElement.None, matched);
            var hints = PresentationalHints.For(element);
            // Only the matched rules and the parent's style decide the style of an element without its own declarations.
            var sharable = inline is null && hints is null;
            if (!sharable || shared.Find(item.Parent, rootFontSize, matched) is not { } style)
            {
                var (values, custom) = Cascade.Compute(matched, inline, int.MaxValue, hints, parent: item.Parent);
                style = StyleBuilder.Compute(values, Context(item.Parent, custom), groups);
                // Animations at the document time: each keyframe's declarations join the cascade's animation origin and
                // the values are interpolated between them.
                if (!ReferenceEquals(style.Animation, AnimationGroup.Initial) && keyframes.Count > 0)
                {
                    var (parent, rules, elementInline, elementHints) = (item.Parent, matched.ToList(), inline, hints);
                    var animated = new AnimatedElement(element, style, parent, keyframes, declarations =>
                    {
                        var (keyed, keyedCustom) = Cascade.Compute(rules, elementInline, int.MaxValue, elementHints, declarations, parent);
                        return StyleBuilder.Compute(keyed, Context(parent, keyedCustom), groups);
                    }, registered);
                    animations.Add(animated);
                    style = animated.Sample(animationTime, groups);
                    // Each animated element keeps its own style, so a frame can change it alone.
                    sharable = false;
                }
                if (sharable)
                    shared.Add(item.Parent, rootFontSize, matched, style);
            }
            var styles = new ElementStyles(style);
            element.StyleData = styles;
            if (style.Box.Display != Display.None)
            {
                ComputedStyle? Pseudo(PseudoElement pe, bool always = false)
                {
                    if (!always && !(pe switch { PseudoElement.Before => hasBefore, PseudoElement.After => hasAfter, _ => hasFirstLetter }))
                        return null;
                    // With no rules for it, ::before or ::after would have content: normal and generate no box.
                    pseudoMatched.Clear();
                    Cascade.Match(element, origins, context, pe, pseudoMatched);
                    if (!always && pseudoMatched.Count == 0)
                        return null;
                    var (pseudoValues, pseudoCustom) = Cascade.Compute(pseudoMatched, null, 0, null, parent: style);
                    var pseudoStyle = StyleBuilder.Compute(pseudoValues, Context(style, pseudoCustom), groups);
                    if (ReferenceEquals(pseudoStyle.Animation, AnimationGroup.Initial) || keyframes.Count == 0)
                        return pseudoStyle;
                    // Generated content animates like an element, its keyframes computed in its own context.
                    var (originating, rules) = (style, pseudoMatched.ToList());
                    var animated = new AnimatedElement(element, pseudoStyle, originating, keyframes, declarations =>
                    {
                        var (keyed, keyedCustom) = Cascade.Compute(rules, null, 0, null, declarations, originating);
                        return StyleBuilder.Compute(keyed, Context(originating, keyedCustom), groups);
                    }) { PseudoElement = pe };
                    animations.Add(animated);
                    return animated.Sample(animationTime, groups);
                }
                styles.Before = Pseudo(PseudoElement.Before);
                styles.After = Pseudo(PseudoElement.After);
                styles.FirstLetter = Pseudo(PseudoElement.FirstLetter);
                if (style.Box.Display == Display.ListItem)
                    styles.Marker = Pseudo(PseudoElement.Marker, always: true);
            }
            if (element.Parent is DocumentNode)
                rootFontSize = style.Font.Size;

            filter.Push(element);
            stack.Push((null, element, style));
            for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
            {
                if (child is ElementNode e)
                    stack.Push((e, null, style));
            }
        }
        document.StyleState = new Restyler(origins, media, measure, registered, sources.BaseUrl, rootFontSize, groups) { Animations = animations };
        return [.. origins.Skip(1).SelectMany(o => o.FontFaces)];

        ComputeContext Context(ComputedStyle parent, Dictionary<string, CustomProperties.Declared> custom) =>
            new(parent, rootFontSize, media.Width, media.Height)
            {
                PrefersDark = media.DarkColorScheme,
                Measure = measure,
                Custom = CustomProperties.Compute(parent.Custom, custom, registered),
                Registered = registered,
            };
    }

    /// <summary>
    /// An element's style computed again under another parent style, with the rules and settings of the document's last
    /// style resolution: what SVG <c>use</c> instances need, which match style rules where the original element is but
    /// inherit from the <c>use</c> element (https://www.w3.org/TR/SVG2/struct.html#UseStyleInheritance). Null before
    /// the document has been styled.
    /// </summary>
    public static ComputedStyle? Restyle(ElementNode element, ComputedStyle parent) =>
        (element.OwnerDocument.StyleState as Restyler)?.Style(element, parent);

    /// <summary>The elements with animations in the document's last style resolution, in tree order.</summary>
    public static IReadOnlyList<AnimatedElement> Animated(DocumentNode document) => (document.StyleState as Restyler)?.Animations ?? [];

    private sealed class Restyler(List<CascadeData> origins, MediaContext media, FontMeasure? measure,
                                  Dictionary<string, RegisteredProperty> registered, string? baseUrl, float rootFontSize, Dictionary<object, object> groups)
    {
        public List<AnimatedElement> Animations { get; init; } = [];

        private readonly MatchContext _context = new();
        private readonly List<RuleIndex<CascadeRule>.Entry> _matched = [];

        public ComputedStyle Style(ElementNode element, ComputedStyle parent)
        {
            List<CascadeDeclaration>? inline = null;
            if (element.GetAttribute("style") is { } styleAttribute)
            {
                var (source, block) = CssParser.ParseBlockContents(styleAttribute);
                inline = CascadeData.Parse(source, block.Declarations, baseUrl);
            }
            _matched.Clear();
            Cascade.Match(element, origins, _context, PseudoElement.None, _matched);
            var (values, custom) = Cascade.Compute(_matched, inline, int.MaxValue, PresentationalHints.For(element), parent: parent);
            return StyleBuilder.Compute(values, new ComputeContext(parent, rootFontSize, media.Width, media.Height)
            {
                PrefersDark = media.DarkColorScheme,
                Measure = measure,
                Custom = CustomProperties.Compute(parent.Custom, custom, registered),
                Registered = registered,
            }, groups);
        }
    }

    /// <summary>
    /// The style-sharing cache (docs/study/04-cascade-and-computed-values.md): elements that match the same rules, in
    /// the same order, under the same parent style object, and have no style attribute or presentational hints,
    /// compute the same style, so they share one <see cref="ComputedStyle"/>. Rows of a table and items of a list then
    /// cost one selector-matching pass each. Because children of shared parents see the same parent object, sharing
    /// carries down the tree.
    /// </summary>
    private sealed class SharingCache
    {
        private readonly Dictionary<int, List<(ComputedStyle Parent, float RootFontSize, RuleIndex<CascadeRule>.Entry[] Rules, ComputedStyle Style)>> _buckets = [];

        public ComputedStyle? Find(ComputedStyle parent, float rootFontSize, List<RuleIndex<CascadeRule>.Entry> rules)
        {
            if (!_buckets.TryGetValue(Hash(parent, rules), out var bucket))
                return null;
            foreach (var entry in bucket)
            {
                if (ReferenceEquals(entry.Parent, parent) && entry.RootFontSize == rootFontSize && Same(entry.Rules, rules))
                    return entry.Style;
            }
            return null;
        }

        public void Add(ComputedStyle parent, float rootFontSize, List<RuleIndex<CascadeRule>.Entry> rules, ComputedStyle style)
        {
            var hash = Hash(parent, rules);
            if (!_buckets.TryGetValue(hash, out var bucket))
                _buckets[hash] = bucket = [];
            bucket.Add((parent, rootFontSize, [.. rules], style));
        }

        private static bool Same(RuleIndex<CascadeRule>.Entry[] a, List<RuleIndex<CascadeRule>.Entry> b)
        {
            if (a.Length != b.Count)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (!ReferenceEquals(a[i], b[i]))
                    return false;
            }
            return true;
        }

        private static int Hash(ComputedStyle parent, List<RuleIndex<CascadeRule>.Entry> rules)
        {
            var hash = new HashCode();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(parent));
            foreach (var rule in rules)
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(rule));
            return hash.ToHashCode();
        }
    }

    // style elements and link rel=stylesheet elements in tree order whose type is CSS
    // (https://html.spec.whatwg.org/multipage/semantics.html#the-style-element, #the-link-element), and SVG style
    // elements (https://www.w3.org/TR/SVG2/styling.html#StyleElement). Alternate stylesheets and disabled links are
    // not applied.
    private static IEnumerable<ElementNode> StyleSheetElements(DocumentNode document)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is not ElementNode element
                || element.Name.Namespace != Namespaces.Html && !(element.Name.Namespace == Namespaces.Svg && element.LocalName == "style")
                || element.GetAttribute("type") is { Length: > 0 } type && !type.Equals("text/css", StringComparison.OrdinalIgnoreCase))
                continue;
            if (element.LocalName == "style")
            {
                yield return element;
            }
            else if (element.LocalName == "link" && element.GetAttribute("disabled") is null && element.GetAttribute("href") is { Length: > 0 })
            {
                var rel = (element.GetAttribute("rel") ?? "").Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries);
                if (rel.Contains("stylesheet", StringComparer.OrdinalIgnoreCase) && !rel.Contains("alternate", StringComparer.OrdinalIgnoreCase))
                    yield return element;
            }
        }
    }

    // https://html.spec.whatwg.org/multipage/semantics.html#the-base-element: the first base element with href.
    internal static string? BaseUrl(DocumentNode document, string? documentUrl)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is ElementNode { LocalName: "base" } element && element.Name.Namespace == Namespaces.Html && element.GetAttribute("href") is { } href)
                return Resources.ResourceLoader.Resolve(documentUrl, href) ?? documentUrl;
        }
        return documentUrl;
    }
}

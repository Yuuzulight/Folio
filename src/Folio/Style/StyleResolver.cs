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
        var restyler = new Restyler(origins, media, measure, registered, sources.BaseUrl, keyframes, animationTime);
        document.StyleState = restyler;
        if (document.DocumentElement is { } root)
            restyler.Walk(root, Folio.Style.ComputedStyle.Initial);
        return [.. origins.Skip(1).SelectMany(o => o.FontFaces)];
    }

    /// <summary>
    /// Restyles each of <paramref name="roots"/> and its subtree with the rules and settings of the document's last
    /// style resolution, after a state change (#417). Returns how many elements were restyled, or null when the
    /// document needs a full <see cref="Resolve"/> instead: it hasn't been styled, or it has animations.
    /// </summary>
    // ponytail: animated documents restyle in full, since a subtree walk would add its animations a second time.
    public static int? RestyleSubtrees(DocumentNode document, IEnumerable<ElementNode> roots) =>
        document.StyleState is Restyler { Animations.Count: 0 } restyler ? restyler.RestyleSubtrees(roots) : null;

    /// <summary>Where the document's state pseudo-classes appear, from its last style resolution.</summary>
    public static StateInvalidation Invalidation(DocumentNode document) =>
        (document.StyleState as Restyler)?.Invalidation ?? StateInvalidation.Empty;

    /// <summary>
    /// An element's style computed again under another parent style, with the rules and settings of the document's last
    /// style resolution: what SVG <c>use</c> instances need, which match style rules where the original element is but
    /// inherit from the <c>use</c> element (https://www.w3.org/TR/SVG2/struct.html#UseStyleInheritance). Null before
    /// the document has been styled.
    /// </summary>
    public static ComputedStyle? Restyle(ElementNode element, ComputedStyle parent) =>
        (element.OwnerDocument.StyleState as Restyler)?.Style(element, parent);

    /// <summary>
    /// An element's ::first-letter style under another parent style: the letter inherits from the inline element it
    /// sits in (https://www.w3.org/TR/css-pseudo-4/#first-letter-pseudo). Null before the document has been styled.
    /// </summary>
    // ponytail: a first letter restyled this way does not animate.
    public static ComputedStyle? RestyleFirstLetter(ElementNode element, ComputedStyle parent) =>
        (element.OwnerDocument.StyleState as Restyler)?.FirstLetter(element, parent);

    /// <summary>The elements with animations in the document's last style resolution, in tree order.</summary>
    public static IReadOnlyList<AnimatedElement> Animated(DocumentNode document) => (document.StyleState as Restyler)?.Animations ?? [];

    /// <summary>
    /// The document's rules and settings from its last style resolution, and the walk that computes styles in tree
    /// order with the ancestor filter kept current: over the whole document, or over the subtrees a state change
    /// marked.
    /// </summary>
    private sealed class Restyler
    {
        private readonly List<CascadeData> _origins;
        private readonly MediaContext _media;
        private readonly FontMeasure? _measure;
        private readonly Dictionary<string, RegisteredProperty> _registered;
        private readonly string? _baseUrl;
        private readonly Dictionary<string, List<Keyframe>> _keyframes;
        private readonly double? _animationTime;
        private readonly Dictionary<object, object> _groups = [];
        private readonly AncestorFilter _filter = new();
        private readonly MatchContext _walkContext;
        private readonly MatchContext _context = new();
        private readonly List<RuleIndex<CascadeRule>.Entry> _matched = [];
        private readonly List<RuleIndex<CascadeRule>.Entry> _pseudoMatched = [];
        private readonly bool _hasBefore, _hasAfter, _hasFirstLetter;
        private float _rootFontSize = Folio.Style.ComputedStyle.Initial.Font.Size;

        public Restyler(List<CascadeData> origins, MediaContext media, FontMeasure? measure, Dictionary<string, RegisteredProperty> registered,
                        string? baseUrl, Dictionary<string, List<Keyframe>> keyframes, double? animationTime)
        {
            (_origins, _media, _measure, _registered, _baseUrl, _keyframes, _animationTime) = (origins, media, measure, registered, baseUrl, keyframes, animationTime);
            _walkContext = new MatchContext { Filter = _filter };
            (_hasBefore, _hasAfter) = (origins.Any(o => o.Rules.HasRulesFor(PseudoElement.Before)), origins.Any(o => o.Rules.HasRulesFor(PseudoElement.After)));
            _hasFirstLetter = origins.Any(o => o.Rules.HasRulesFor(PseudoElement.FirstLetter));
            Invalidation = StateInvalidation.Build(origins.SelectMany(o => o.Rules.StateSelectors));
        }

        public List<AnimatedElement> Animations { get; } = [];

        public StateInvalidation Invalidation { get; }

        public int RestyleSubtrees(IEnumerable<ElementNode> roots)
        {
            var count = 0;
            // A root inside another root's subtree is restyled with it.
            var set = roots.ToHashSet();
            foreach (var root in set)
            {
                var covered = false;
                for (var ancestor = root.Parent; ancestor is not null && !covered; ancestor = ancestor.Parent)
                    covered = ancestor is ElementNode e && set.Contains(e);
                if (covered || root.Parent is null)
                    continue;
                var ancestors = new List<ElementNode>();
                for (var ancestor = root.Parent; ancestor is ElementNode e; ancestor = ancestor.Parent)
                    ancestors.Add(e);
                ancestors.Reverse();
                foreach (var ancestor in ancestors)
                    _filter.Push(ancestor);
                var parent = root.Parent is ElementNode p ? p.ComputedStyle() ?? Folio.Style.ComputedStyle.Initial : Folio.Style.ComputedStyle.Initial;
                count += Walk(root, parent);
                for (var i = ancestors.Count - 1; i >= 0; i--)
                    _filter.Pop(ancestors[i]);
            }
            return count;
        }

        /// <summary>Computes the styles of <paramref name="start"/> and its descendants; returns how many elements.</summary>
        public int Walk(ElementNode start, ComputedStyle startParent)
        {
            var count = 0;
            var shared = new SharingCache();
            // Iterative pre-order walk: (element, parent style); a null element marks leaving an element.
            var stack = new Stack<(ElementNode? ElementNode, ElementNode? Leaving, ComputedStyle Parent)>();
            stack.Push((start, null, startParent));
            while (stack.TryPop(out var item))
            {
                if (item.ElementNode is null)
                {
                    _filter.Pop(item.Leaving!);
                    continue;
                }
                var element = item.ElementNode;
                count++;

                List<CascadeDeclaration>? inline = null;
                if (element.GetAttribute("style") is { } styleAttribute)
                {
                    var (source, block) = CssParser.ParseBlockContents(styleAttribute);
                    inline = CascadeData.Parse(source, block.Declarations, _baseUrl);
                }
                _matched.Clear();
                Cascade.Match(element, _origins, _walkContext, PseudoElement.None, _matched);
                var hints = PresentationalHints.For(element);
                // Only the matched rules and the parent's style decide the style of an element without its own declarations.
                var sharable = inline is null && hints is null;
                if (!sharable || shared.Find(item.Parent, _rootFontSize, _matched) is not { } style)
                {
                    var (values, custom) = Cascade.Compute(_matched, inline, int.MaxValue, hints, parent: item.Parent);
                    style = StyleBuilder.Compute(values, Context(item.Parent, custom), _groups);
                    // Animations at the document time: each keyframe's declarations join the cascade's animation origin and
                    // the values are interpolated between them.
                    if (!ReferenceEquals(style.Animation, AnimationGroup.Initial) && _keyframes.Count > 0)
                    {
                        var (parent, rules, elementInline, elementHints) = (item.Parent, _matched.ToList(), inline, hints);
                        var animated = new AnimatedElement(element, style, parent, _keyframes, declarations =>
                        {
                            var (keyed, keyedCustom) = Cascade.Compute(rules, elementInline, int.MaxValue, elementHints, declarations, parent);
                            return StyleBuilder.Compute(keyed, Context(parent, keyedCustom), _groups);
                        }, _registered);
                        Animations.Add(animated);
                        style = animated.Sample(_animationTime, _groups);
                        // Each animated element keeps its own style, so a frame can change it alone.
                        sharable = false;
                    }
                    if (sharable)
                        shared.Add(item.Parent, _rootFontSize, _matched, style);
                }
                var styles = new ElementStyles(style);
                element.StyleData = styles;
                if (style.Box.Display != Display.None)
                {
                    styles.Before = Pseudo(element, style, PseudoElement.Before);
                    styles.After = Pseudo(element, style, PseudoElement.After);
                    styles.FirstLetter = Pseudo(element, style, PseudoElement.FirstLetter);
                    if (style.Box.Display == Display.ListItem)
                        styles.Marker = Pseudo(element, style, PseudoElement.Marker, always: true);
                }
                if (element.Parent is DocumentNode)
                    _rootFontSize = style.Font.Size;

                _filter.Push(element);
                stack.Push((null, element, style));
                for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
                {
                    if (child is ElementNode e)
                        stack.Push((e, null, style));
                }
            }
            return count;
        }

        private ComputedStyle? Pseudo(ElementNode element, ComputedStyle style, PseudoElement pe, bool always = false)
        {
            if (!always && !(pe switch { PseudoElement.Before => _hasBefore, PseudoElement.After => _hasAfter, _ => _hasFirstLetter }))
                return null;
            // With no rules for it, ::before or ::after would have content: normal and generate no box.
            _pseudoMatched.Clear();
            Cascade.Match(element, _origins, _walkContext, pe, _pseudoMatched);
            if (!always && _pseudoMatched.Count == 0)
                return null;
            var (pseudoValues, pseudoCustom) = Cascade.Compute(_pseudoMatched, null, 0, null, parent: style);
            if (pe == PseudoElement.FirstLetter)
                pseudoValues = FirstLetterProperties(pseudoValues);
            var pseudoStyle = StyleBuilder.Compute(pseudoValues, Context(style, pseudoCustom), _groups);
            if (ReferenceEquals(pseudoStyle.Animation, AnimationGroup.Initial) || _keyframes.Count == 0)
                return pseudoStyle;
            // Generated content animates like an element, its keyframes computed in its own context.
            var (originating, rules) = (style, _pseudoMatched.ToList());
            var animated = new AnimatedElement(element, pseudoStyle, originating, _keyframes, declarations =>
            {
                var (keyed, keyedCustom) = Cascade.Compute(rules, null, 0, null, declarations, originating);
                return StyleBuilder.Compute(keyed, Context(originating, keyedCustom), _groups);
            }) { PseudoElement = pe };
            Animations.Add(animated);
            return animated.Sample(_animationTime, _groups);
        }

        public ComputedStyle Style(ElementNode element, ComputedStyle parent)
        {
            List<CascadeDeclaration>? inline = null;
            if (element.GetAttribute("style") is { } styleAttribute)
            {
                var (source, block) = CssParser.ParseBlockContents(styleAttribute);
                inline = CascadeData.Parse(source, block.Declarations, _baseUrl);
            }
            _matched.Clear();
            Cascade.Match(element, _origins, _context, PseudoElement.None, _matched);
            var (values, custom) = Cascade.Compute(_matched, inline, int.MaxValue, PresentationalHints.For(element), parent: parent);
            return StyleBuilder.Compute(values, Context(parent, custom), _groups);
        }

        public ComputedStyle FirstLetter(ElementNode element, ComputedStyle parent)
        {
            _matched.Clear();
            Cascade.Match(element, _origins, _context, PseudoElement.FirstLetter, _matched);
            var (values, custom) = Cascade.Compute(_matched, null, 0, null, parent: parent);
            return StyleBuilder.Compute(FirstLetterProperties(values), Context(parent, custom), _groups);
        }

        private ComputeContext Context(ComputedStyle parent, Dictionary<string, CustomProperties.Declared> custom) =>
            new(parent, _rootFontSize, _media.Width, _media.Height)
            {
                PrefersDark = _media.DarkColorScheme,
                Measure = _measure,
                Custom = CustomProperties.Compute(parent.Custom, custom, _registered),
                Registered = _registered,
            };
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

    // The properties ::first-letter takes (https://www.w3.org/TR/css-pseudo-4/#first-letter-styling): fonts, colour,
    // backgrounds, text decoration and shadows, case, spacing, line height, vertical-align, margins, padding, borders,
    // box shadows and float. The rest are ignored.
    private static readonly string[] FirstLetterPrefixes =
    [
        "font", "color", "background", "text-decoration", "text-shadow", "text-transform", "letter-spacing", "word-spacing",
        "line-height", "vertical-align", "margin", "padding", "border", "box-shadow", "float",
    ];

    private static Dictionary<PropertyId, CssValue> FirstLetterProperties(Dictionary<PropertyId, CssValue> values)
    {
        foreach (var id in values.Keys.ToList())
        {
            var name = Properties.Get(id).Name;
            if (!FirstLetterPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
                values.Remove(id);
        }
        return values;
    }
}

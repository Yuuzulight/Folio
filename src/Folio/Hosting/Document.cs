using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Interaction;
using Folio.Layout;
using Folio.Painting;
using Folio.Resources;
using Folio.Style;
using Folio.Typography;

namespace Folio;

/// <summary>
/// A parsed HTML document, or a standalone SVG document when the text is one (an <c>svg</c> root element declared
/// as XML or in the SVG namespace), parsed as XML (docs/architecture.md, public API sketch).
/// </summary>
public sealed class Document : IDisposable
{
    private readonly List<Diagnostic> _diagnostics;

    private Document(DocumentNode node, FolioOptions options, List<Diagnostic> diagnostics)
    {
        Node = node;
        Options = options;
        _diagnostics = diagnostics;
    }

    internal DocumentNode Node { get; }

    internal FolioOptions Options { get; }

    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>All allowed loads finished or failed. Nothing is loaded yet, so it is always complete.</summary>
    public Task ResourcesSettled => Task.CompletedTask;

    /// <summary>https://html.spec.whatwg.org/multipage/dom.html#document.title: the first title element's text (for an
    /// SVG document, the root's first title child), ASCII whitespace stripped and collapsed.</summary>
    public string Title
    {
        get
        {
            if (Node.DocumentElement is { LocalName: "svg" } svg && svg.Name.Namespace == Namespaces.Svg)
            {
                var first = svg.Children.OfType<ElementNode>().FirstOrDefault(e => e.LocalName == "title" && e.Name.Namespace == Namespaces.Svg);
                return Collapse(first?.TextContent);
            }
            for (Node? node = Node; node is not null; node = node.NextInTree(Node))
            {
                if (node is ElementNode { LocalName: "title" } title && title.Name.Namespace == Namespaces.Html)
                    return Collapse(title.TextContent);
            }
            return "";

            static string Collapse(string? text) => string.Join(' ', (text ?? "").Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>
    /// The accessibility tree of the last painted layout (roles, names, values, states and bounds), or null before the
    /// first paint, since it reads computed styles and fragments.
    /// </summary>
    internal Interaction.AccessibleNode? AccessibilityTree() =>
        _page is { } page ? Interaction.AccessibilityTree.Build(Node, page, Title) : null;

    public static Document Parse(string html, FolioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        options ??= new FolioOptions();
        return Build(html, options, []);
    }

    /// <summary>Parses bytes, choosing the encoding from a byte order mark or a meta element, else UTF-8.</summary>
    public static Document Parse(Stream bytes, FolioOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        options ??= new FolioOptions();
        var diagnostics = new List<Diagnostic>();

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = bytes.Read(chunk, 0, chunk.Length)) > 0)
        {
            var room = options.Limits.MaxInputSize - (int)buffer.Length;
            buffer.Write(chunk, 0, Math.Min(read, room));
            if (read > room)
            {
                diagnostics.Add(Limit($"The input is larger than {options.Limits.MaxInputSize} bytes; the rest was not read."));
                break;
            }
        }

        var data = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var (encoding, bom, unsupported) = EncodingSniffer.Sniff(data);
        if (unsupported is not null)
            diagnostics.Add(new Diagnostic(DiagnosticCode.UnsupportedEncoding, Severity.Warning,
                $"The encoding \"{unsupported}\" is not supported; the document was read as UTF-8.", null, "encoding"));
        return Build(EncodingSniffer.Decode(data[bom..], encoding), options, diagnostics);
    }

    private static Document Build(string html, FolioOptions options, List<Diagnostic> diagnostics)
    {
        if (html.Length > options.Limits.MaxInputSize)
        {
            html = html[..options.Limits.MaxInputSize];
            diagnostics.Add(Limit($"The input is longer than {options.Limits.MaxInputSize} characters; the rest was not parsed."));
        }

        var lines = new LineMap(html);
        var limits = new ParserLimits(options.Limits.MaxNestingDepth, options.Limits.MaxNodes);
        // A standalone SVG file goes through the XML parser; everything else is HTML.
        var svg = Xml.XmlParser.IsSvgDocument(html);
        var node = svg ? Xml.XmlParser.Parse(html, limits, Report) : TreeBuilder.Parse(html, limits, Report);
        return new Document(node, options, diagnostics);

        void Report(string code, int offset)
        {
            switch (code)
            {
                case "nesting-depth-limit":
                    diagnostics.Add(Limit($"Elements nest deeper than {limits.MaxDepth}; deeper content was attached higher.", lines.At(offset)));
                    break;
                case "node-count-limit":
                    diagnostics.Add(Limit($"The document has more than {limits.MaxNodes} nodes; parsing stopped.", lines.At(offset)));
                    break;
                default:
                    if (options.CollectDiagnostics)
                    {
                        diagnostics.Add(svg
                            ? new Diagnostic(DiagnosticCode.ParseError, Severity.Info, $"XML parse error: {code}", lines.At(offset), "xml-parsing")
                            : new Diagnostic(DiagnosticCode.ParseError, Severity.Info, $"HTML parse error: {code}", lines.At(offset), "html-parsing"));
                    }
                    break;
            }
        }
    }

    private static Diagnostic Limit(string message, SourceLocation? at = null) =>
        new(DiagnosticCode.LimitExceeded, Severity.Warning, message, at, null);

    private FontCollection? _fonts;
    private Imaging.ImageLoader? _images;
    private bool _webFontsLoaded;
    private readonly Dictionary<ElementNode, Element> _elements = [];
    private InputRouter? _inputRouter;

    /// <summary>The input router for handling pointer, keyboard, and focus events on this document.</summary>
    public InputRouter Input => _inputRouter ??= new InputRouter(this);

    /// <summary>The root element, or null for an empty document.</summary>
    public Element? DocumentElement => Node.DocumentElement is { } root ? Wrap(root) : null;

    /// <summary>The first element in tree order with this id (https://dom.spec.whatwg.org/#dom-nonelementparentnode-getelementbyid).</summary>
    public Element? GetElementById(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length == 0)
            return null;
        for (Node? node = Node; node is not null; node = node.NextInTree(Node))
        {
            if (node is ElementNode element && element.GetAttribute("id") == id)
                return Wrap(element);
        }
        return null;
    }

    /// <summary>The first element matching a selector list.</summary>
    /// <exception cref="ArgumentException">The selector list is not valid.</exception>
    public Element? QuerySelector(string selectors) => Query(Node, selectors).FirstOrDefault();

    /// <summary>Every element matching a selector list, in tree order.</summary>
    /// <exception cref="ArgumentException">The selector list is not valid.</exception>
    public IReadOnlyList<Element> QuerySelectorAll(string selectors) => Query(Node, selectors).ToList();

    /// <summary>Creates a new element in the HTML namespace.</summary>
    public Element CreateElement(string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        var node = Node.CreateElement(Namespaces.Html, localName.ToLowerInvariant());
        return Wrap(node);
    }

    internal Element Wrap(ElementNode node)
    {
        if (!_elements.TryGetValue(node, out var element))
            _elements[node] = element = new Element(this, node);
        return element;
    }

    // The descendants of a node that match a selector list, in tree order.
    internal IEnumerable<Element> Query(ContainerNode scope, string selectors)
    {
        ArgumentNullException.ThrowIfNull(selectors);
        var (source, values) = CssParser.ParseComponentValues(selectors);
        var list = SelectorParser.Parse(source, values, Node.Intern)
            ?? throw new ArgumentException($"\"{selectors}\" is not a valid selector list.", nameof(selectors));
        return Matches(scope, list);

        IEnumerable<Element> Matches(ContainerNode root, SelectorList selectorList)
        {
            var context = new MatchContext();
            for (var node = root.NextInTree(root); node is not null; node = node.NextInTree(root))
            {
                if (node is ElementNode element && SelectorMatcher.Matches(selectorList, element, context))
                    yield return Wrap(element);
            }
        }
    }
    private Fragment? _page;

    /// <summary>
    /// The element under a point of the last painted layout (page coordinates in CSS pixels): the one painted on top
    /// there, by hit testing in reverse paint order. Null outside every box or before painting.
    /// </summary>
    internal ElementNode? ElementAt(float x, float y) =>
        _page is { } page ? HitTester.Hit(page, new(x, y), _frame?.Scale ?? 1)?.Node : null;

    /// <summary>The link under a point: the nearest a or area element with an href, resolved against the base URL.</summary>
    internal Uri? LinkAt(float x, float y)
    {
        for (Node? node = ElementAt(x, y); node is not null; node = node.Parent)
        {
            if (node is ElementNode { LocalName: "a" or "area" } link && link.Name.Namespace == Namespaces.Html && link.GetAttribute("href") is { } href)
            {
                href = href.Trim();
                return Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute
                    : Options.BaseUri is { } baseUri && Uri.TryCreate(baseUri, href, out var resolved) ? resolved
                    : null;
            }
        }
        return null;
    }

    // The last full paint's box tree and layout, which a paint-only animation frame paints again.
    private (Box Root, Fragment Page, float Scale)? _frame;
    private Dictionary<(ElementNode, PseudoElement), List<Box>>? _animatedBoxes;
    private (float Width, float Height, float Scale, ITextShaper? Shaper, double? AnimationTime)? _lastPaint;

    /// <summary>The last built display list, or null before painting.</summary>
    internal DisplayList? DisplayList { get; private set; }

    /// <summary>How many times the document has been laid out (for tests of the paint-only path).</summary>
    internal int LayoutCount { get; private set; }

    /// <summary>How many elements the last <see cref="Update"/> styled again (#417).</summary>
    internal int RestyleCount { get; private set; }

    /// <summary>
    /// Sets or clears a user-action state (<see cref="NodeFlags.Hover"/>, <see cref="NodeFlags.Active"/>,
    /// <see cref="NodeFlags.Focus"/>, <see cref="NodeFlags.FocusVisible"/>) on an element and marks the elements whose
    /// style that can change, from the stylesheets' state invalidation sets (#417). <see cref="Update"/> styles them
    /// again. With no rule mentioning the state, nothing is marked.
    /// </summary>
    internal void SetState(ElementNode element, NodeFlags state, bool on)
    {
        var flags = on ? element.Flags | state : element.Flags & ~state;
        if (flags == element.Flags)
            return;
        element.Flags = flags;
        var sets = StyleResolver.Invalidation(Node);
        foreach (var (flag, kind) in StateKinds)
        {
            if ((state & flag) == 0 || !sets.Uses(kind))
                continue;
            if (sets.Everywhere(kind))
            {
                Node.StyleMutated = true;
                return;
            }
            sets.Collect(element, kind, Node.StyleRoots);
        }
        // Focus moving in or out also changes :focus-within on the element and every ancestor.
        if ((state & NodeFlags.Focus) != 0 && sets.Uses(PseudoClass.FocusWithin))
        {
            if (sets.Everywhere(PseudoClass.FocusWithin))
            {
                Node.StyleMutated = true;
                return;
            }
            for (Node? node = element; node is ElementNode e; node = node.Parent)
                sets.Collect(e, PseudoClass.FocusWithin, Node.StyleRoots);
        }
    }

    private static readonly (NodeFlags Flag, PseudoClass Kind)[] StateKinds =
    [
        (NodeFlags.Hover, PseudoClass.Hover),
        (NodeFlags.Active, PseudoClass.Active),
        (NodeFlags.Focus, PseudoClass.Focus),
        (NodeFlags.FocusVisible, PseudoClass.FocusVisible),
    ];

    /// <summary>
    /// Styles, lays out and paints the document in a viewport (the pipeline in docs/architecture.md): its display list
    /// and the height of its content, from the canvas origin.
    /// </summary>
    /// <param name="animationTime">Seconds on the document timeline to sample animations at; null for the settled document.</param>
    internal (DisplayList List, float Height) Paint(float viewportWidth, float viewportHeight, float deviceScale = 1, ITextShaper? shaper = null,
                                                   double? animationTime = null)
    {
        (_frame, _animatedBoxes) = (null, null);
        _lastPaint = (viewportWidth, viewportHeight, deviceScale, shaper, animationTime);
        var media = new MediaContext(viewportWidth, viewportHeight, deviceScale, Options.ColorScheme == ColorScheme.Dark) { ReducedMotion = Options.ReducedMotion };
        _fonts ??= FontCollection.For(Options.Fonts);
        var sources = new StyleSources(Options.ResourceLoader is { } host ? new ResourceLoader(host: host) : ResourceLoader.DataUrlsOnly, Options.BaseUri?.AbsoluteUri);
        var fontFaces = StyleResolver.Resolve(Node, media, Options.UserStyleSheet, sources, InlineLayout.MeasureWith(_fonts), animationTime);
        if (!_webFontsLoaded)
        {
            // Web fonts load once, synchronously, before the first layout, so font-display never has a swap to do.
            // Styles are resolved again when some loaded, since ex and ch measure the first available font.
            _webFontsLoaded = true;
            if (WebFonts.Load(fontFaces, _fonts, sources.Loader,
                    message => _diagnostics.Add(new Diagnostic(DiagnosticCode.ResourceNotLoaded, Severity.Warning, message, null, "@font-face"))) > 0)
                StyleResolver.Resolve(Node, media, Options.UserStyleSheet, sources, InlineLayout.MeasureWith(_fonts), animationTime);
        }
        // Images load once per document (docs/study/16-resources-and-security.md: data: URLs only by default).
        _images ??= new Imaging.ImageLoader(sources.Loader, StyleResolver.BaseUrl(Node, Options.BaseUri?.AbsoluteUri),
            (message, feature) => _diagnostics.Add(new Diagnostic(DiagnosticCode.ResourceNotLoaded, Severity.Warning, message, null, feature)));
        if (BoxTreeBuilder.Build(Node, _images, deviceScale) is not { } root)
            return (DisplayList = new DisplayList(), 0);
        var page = LayoutEngine.LayoutDocument(root, viewportWidth, viewportHeight, _fonts, shaper, _images);
        _page = page;
        LayoutCount++;
        _frame = (root, page, deviceScale);
        Node.StructureMutated = false;
        Node.StyleMutated = false;
        Node.StyleRoots.Clear();
        // The content reaches down to the root's bottom margin edge, or further for positioned boxes.
        var height = page.Children.Select((c, i) => c.Y + c.Fragment.Height + (i == 0 ? c.Fragment.BottomMargins.Resolve() : 0)).DefaultIfEmpty(0).Max();
        DisplayList = DisplayListBuilder.Build(page, _images, deviceScale);
        return (DisplayList, height);
    }

    /// <summary>Whether an animation still changes after <paramref name="time"/> (seconds on the document timeline).</summary>
    internal bool AnimationsRunning(double time) => StyleResolver.Animated(Node).Any(a => a.IsRunning(time));

    /// <summary>
    /// The display list at another animation time when every animation is paint-only (docs/study/14-invalidation.md):
    /// only the animated elements are styled again, and the last layout is painted with their new styles. Null when
    /// the document needs a full <see cref="Paint"/> instead: nothing is laid out yet, an animation changes layout, or
    /// a box stops or starts being a containing block for fixed descendants.
    /// </summary>
    internal DisplayList? PaintFrame(double time)
    {
        if (_frame is not { } frame || _images is null)
            return null;
        var animated = StyleResolver.Animated(Node);
        if (animated.Any(a => !a.PaintOnly))
            return null;
        _animatedBoxes ??= BoxesOf(frame.Root, animated.Select(a => (a.Element, a.PseudoElement)).ToHashSet());
        foreach (var element in animated)
        {
            var style = element.Sample(time);
            if (element.Element.StyleData is ElementStyles styles)
            {
                switch (element.PseudoElement)
                {
                    case PseudoElement.Before: styles.Before = style; break;
                    case PseudoElement.After: styles.After = style; break;
                    case PseudoElement.Marker: styles.Marker = style; break;
                    case PseudoElement.FirstLetter: styles.FirstLetter = style; break;
                    default: styles.Style = style; break;
                }
            }
            foreach (var box in _animatedBoxes.GetValueOrDefault((element.Element, element.PseudoElement)) ?? [])
            {
                var containsFixed = box.ContainsFixed;
                box.Style = style;
                if (box.ContainsFixed != containsFixed)
                {
                    _frame = null;
                    return null;
                }
            }
        }
        return DisplayList = DisplayListBuilder.Build(frame.Page, _images, frame.Scale);

        static Dictionary<(ElementNode, PseudoElement), List<Box>> BoxesOf(Box root, HashSet<(ElementNode, PseudoElement)> elements)
        {
            var boxes = new Dictionary<(ElementNode, PseudoElement), List<Box>>();
            var stack = new Stack<Box>([root]);
            while (stack.TryPop(out var box))
            {
                if (box.Node is ElementNode element && elements.Contains((element, box.PseudoElement)))
                    (boxes.TryGetValue((element, box.PseudoElement), out var list) ? list : boxes[(element, box.PseudoElement)] = []).Add(box);
                foreach (var child in box.Children)
                    stack.Push(child);
            }
            return boxes;
        }
    }

    /// <summary>
    /// Restyles the document after DOM or attribute mutations, applying the minimum required damage level
    /// (<see cref="Damage.Paint"/>, <see cref="Damage.Layout"/>, or <see cref="Damage.Boxes"/>) and updating the display list.
    /// </summary>
    internal Damage Update()
    {
        if (_lastPaint is not { } last || _images is null)
            throw new InvalidOperationException("The document has not been painted yet.");

        if (Node.StructureMutated)
        {
            Node.StructureMutated = false;
            return RebuildBoxes(last);
        }

        // #417: after state changes alone, only the subtrees they marked are styled again.
        var partial = !Node.StyleMutated;
        var roots = Node.StyleRoots.ToList();
        Node.StyleMutated = false;
        Node.StyleRoots.Clear();

        var oldStyles = new Dictionary<ElementNode, (ComputedStyle? Style, ComputedStyle? Before, ComputedStyle? After, ComputedStyle? Marker)>();
        void Remember(Node scope)
        {
            for (Node? node = scope; node is not null; node = node.NextInTree(scope))
            {
                if (node is ElementNode element && !oldStyles.ContainsKey(element))
                {
                    oldStyles[element] = (
                        element.ComputedStyle(),
                        element.PseudoStyle(PseudoElement.Before),
                        element.PseudoStyle(PseudoElement.After),
                        element.PseudoStyle(PseudoElement.Marker));
                }
            }
        }

        if (partial)
        {
            foreach (var root in roots)
                Remember(root);
            if (StyleResolver.RestyleSubtrees(Node, roots) is { } restyled)
                RestyleCount = restyled;
            else
                partial = false;
        }
        if (!partial)
        {
            Remember(Node);
            var media = new MediaContext(last.Width, last.Height, last.Scale, Options.ColorScheme == ColorScheme.Dark) { ReducedMotion = Options.ReducedMotion };
            _fonts ??= FontCollection.For(Options.Fonts);
            var sources = new StyleSources(Options.ResourceLoader is { } host ? new ResourceLoader(host: host) : ResourceLoader.DataUrlsOnly, Options.BaseUri?.AbsoluteUri);
            StyleResolver.Resolve(Node, media, Options.UserStyleSheet, sources, InlineLayout.MeasureWith(_fonts), last.AnimationTime);
            RestyleCount = oldStyles.Count;
        }

        var maxDamage = Damage.Paint;
        foreach (var (element, (oldStyle, oldBefore, oldAfter, oldMarker)) in oldStyles)
        {
            var newStyle = element.ComputedStyle();
            CompareStyles(oldStyle, newStyle, ref maxDamage);
            if (maxDamage == Damage.Boxes) break;

            ComparePseudo(oldBefore, element.PseudoStyle(PseudoElement.Before), ref maxDamage);
            if (maxDamage == Damage.Boxes) break;

            ComparePseudo(oldAfter, element.PseudoStyle(PseudoElement.After), ref maxDamage);
            if (maxDamage == Damage.Boxes) break;

            ComparePseudo(oldMarker, element.PseudoStyle(PseudoElement.Marker), ref maxDamage);
            if (maxDamage == Damage.Boxes) break;
        }

        for (Node? node = Node; !partial && node is not null && maxDamage != Damage.Boxes; node = node.NextInTree(Node))
        {
            if (node is ElementNode element && !oldStyles.ContainsKey(element))
                maxDamage = Damage.Boxes;
        }

        if (_frame is not { } frame)
            maxDamage = Damage.Boxes;

        if (maxDamage == Damage.Paint)
        {
            var frameVal = _frame!.Value;
            var boxes = AllBoxes(frameVal.Root);
            var needsLayout = false;
            foreach (var box in boxes)
            {
                if (box.Node is ElementNode el)
                {
                    var newStyle = box.PseudoElement switch
                    {
                        PseudoElement.Before => el.PseudoStyle(PseudoElement.Before),
                        PseudoElement.After => el.PseudoStyle(PseudoElement.After),
                        PseudoElement.Marker => el.PseudoStyle(PseudoElement.Marker),
                        _ => el.ComputedStyle(),
                    };
                    if (newStyle is not null)
                    {
                        var containsFixed = box.ContainsFixed;
                        box.Style = newStyle;
                        if (box.ContainsFixed != containsFixed)
                        {
                            needsLayout = true;
                            break;
                        }
                    }
                }
            }

            if (needsLayout)
            {
                maxDamage = Damage.Layout;
            }
            else
            {
                DisplayList = DisplayListBuilder.Build(frameVal.Page, _images, frameVal.Scale);
                return Damage.Paint;
            }
        }

        if (maxDamage == Damage.Layout)
        {
            var frameVal = _frame!.Value;
            var boxes = AllBoxes(frameVal.Root);
            foreach (var box in boxes)
            {
                box.LayoutCache = null;
                if (box.Node is ElementNode el)
                {
                    var newStyle = box.PseudoElement switch
                    {
                        PseudoElement.Before => el.PseudoStyle(PseudoElement.Before),
                        PseudoElement.After => el.PseudoStyle(PseudoElement.After),
                        PseudoElement.Marker => el.PseudoStyle(PseudoElement.Marker),
                        _ => el.ComputedStyle(),
                    };
                    if (newStyle is not null)
                        box.Style = newStyle;
                }
            }

            var page = LayoutEngine.LayoutDocument(frameVal.Root, last.Width, last.Height, _fonts, last.Shaper, _images);
            _page = page;
            LayoutCount++;
            _frame = (frameVal.Root, page, frameVal.Scale);
            DisplayList = DisplayListBuilder.Build(page, _images, frameVal.Scale);
            return Damage.Layout;
        }

        return RebuildBoxes(last);

        Damage RebuildBoxes((float Width, float Height, float Scale, ITextShaper? Shaper, double? AnimationTime) l)
        {
            if (BoxTreeBuilder.Build(Node, _images, l.Scale) is not { } root)
            {
                _page = null;
                _frame = null;
                DisplayList = new DisplayList();
                return Damage.Boxes;
            }

            var page = LayoutEngine.LayoutDocument(root, l.Width, l.Height, _fonts, l.Shaper, _images);
            _page = page;
            LayoutCount++;
            _frame = (root, page, l.Scale);
            DisplayList = DisplayListBuilder.Build(page, _images, l.Scale);
            return Damage.Boxes;
        }

        static void ComparePseudo(ComputedStyle? oldPseudo, ComputedStyle? newPseudo, ref Damage max)
        {
            if ((oldPseudo is null) != (newPseudo is null))
            {
                max = Damage.Boxes;
                return;
            }
            if (oldPseudo is not null && newPseudo is not null)
                CompareStyles(oldPseudo, newPseudo, ref max);
        }

        static void CompareStyles(ComputedStyle? oldStyle, ComputedStyle? newStyle, ref Damage max)
        {
            if (oldStyle is null || newStyle is null)
            {
                max = Damage.Boxes;
                return;
            }
            if (ReferenceEquals(oldStyle, newStyle) || oldStyle.Equals(newStyle))
                return;

            foreach (var id in Enum.GetValues<PropertyId>())
            {
                var damage = PropertyDamage.Of(id) ?? Damage.Boxes;
                if (damage <= max)
                    continue;

                var prop = Properties.Get(id);
                if (prop.Describe(oldStyle) != prop.Describe(newStyle))
                {
                    max = damage;
                    if (max == Damage.Boxes)
                        return;
                }
            }
        }

        static List<Box> AllBoxes(Box root)
        {
            var boxes = new List<Box>();
            var stack = new Stack<Box>([root]);
            while (stack.TryPop(out var box))
            {
                boxes.Add(box);
                foreach (var child in box.Children)
                    stack.Push(child);
            }
            return boxes;
        }
    }

    public void Dispose()
    {
        // Nothing is held yet; decoded images, fonts and the content process will be released here.
    }

    // Maps parser offsets (in newline-normalised input) to line and column.
    private sealed class LineMap
    {
        private readonly List<int> _starts = [0];

        public LineMap(string html)
        {
            var offset = 0;
            for (var i = 0; i < html.Length; i++, offset++)
            {
                if (html[i] == '\r' && i + 1 < html.Length && html[i + 1] == '\n')
                    i++;
                if (html[i] is '\n' or '\r')
                    _starts.Add(offset + 1);
            }
        }

        public SourceLocation At(int offset)
        {
            var line = _starts.BinarySearch(offset);
            if (line < 0)
                line = ~line - 1;
            return new SourceLocation(line + 1, offset - _starts[line] + 1);
        }
    }
}

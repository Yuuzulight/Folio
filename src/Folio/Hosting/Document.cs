using Folio.Css;
using Folio.Dom;
using Folio.Html;
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
    /// The element under a point of the last painted layout (page coordinates in CSS pixels): the deepest box whose
    /// border box holds it, later boxes (painted on top) first. Null outside every box or before painting.
    /// </summary>
    // ponytail: hit testing follows fragment order, not the stacking tree, and ignores clipping (study 15 comes in M3).
    internal ElementNode? ElementAt(float x, float y)
    {
        return _page is null ? null : Find(_page, 0, 0);

        ElementNode? Find(Fragment fragment, float left, float top)
        {
            for (var i = fragment.Children.Count - 1; i >= 0; i--)
            {
                var (child, cx, cy) = (fragment.Children[i].Fragment, left + fragment.Children[i].X, top + fragment.Children[i].Y);
                if (child.Kind == FragmentKind.Line)
                {
                    if (Find(child, cx, cy) is { } inLine)
                        return inLine;
                    continue;
                }
                if (child.Kind == FragmentKind.Text || x < cx || y < cy || x >= cx + child.Width || y >= cy + child.Height)
                    continue;
                return Find(child, cx, cy) ?? (child.Box?.Node as ElementNode);
            }
            return null;
        }
    }

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

    /// <summary>
    /// Styles, lays out and paints the document in a viewport (the pipeline in docs/architecture.md): its display list
    /// and the height of its content, from the canvas origin.
    /// </summary>
    internal (DisplayList List, float Height) Paint(float viewportWidth, float viewportHeight, float deviceScale = 1, ITextShaper? shaper = null)
    {
        var media = new MediaContext(viewportWidth, viewportHeight, deviceScale, Options.ColorScheme == ColorScheme.Dark);
        _fonts ??= FontCollection.For(Options.Fonts);
        var sources = new StyleSources(ResourceLoader.DataUrlsOnly, Options.BaseUri?.AbsoluteUri);
        var fontFaces = StyleResolver.Resolve(Node, media, Options.UserStyleSheet, sources, InlineLayout.MeasureWith(_fonts));
        if (!_webFontsLoaded)
        {
            // Web fonts load once, synchronously, before the first layout, so font-display never has a swap to do.
            // Styles are resolved again when some loaded, since ex and ch measure the first available font.
            _webFontsLoaded = true;
            if (WebFonts.Load(fontFaces, _fonts, sources.Loader) > 0)
                StyleResolver.Resolve(Node, media, Options.UserStyleSheet, sources, InlineLayout.MeasureWith(_fonts));
        }
        // Images load once per document (docs/study/16-resources-and-security.md: data: URLs only by default).
        _images ??= new Imaging.ImageLoader(sources.Loader, StyleResolver.BaseUrl(Node, Options.BaseUri?.AbsoluteUri),
            (message, feature) => _diagnostics.Add(new Diagnostic(DiagnosticCode.ResourceNotLoaded, Severity.Warning, message, null, feature)));
        if (BoxTreeBuilder.Build(Node, _images, deviceScale) is not { } root)
            return (new DisplayList(), 0);
        var page = _page = LayoutEngine.LayoutDocument(root, viewportWidth, viewportHeight, _fonts, shaper);
        // The content reaches down to the root's bottom margin edge, or further for positioned boxes.
        var height = page.Children.Select((c, i) => c.Y + c.Fragment.Height + (i == 0 ? c.Fragment.BottomMargins.Resolve() : 0)).DefaultIfEmpty(0).Max();
        return (DisplayListBuilder.Build(page, _images), height);
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

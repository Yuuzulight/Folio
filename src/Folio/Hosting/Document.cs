using System.Numerics;
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

    /// <summary>Stage-6 capabilities granted by the host for this document.</summary>
    public FeatureCapabilities Capabilities => Options.Capabilities;
    public int MaxGpuBytes => Options.Limits.MaxGpuBytes;
    public int MaxMediaBytes => Options.Limits.MaxMediaBytes;
    public int MaxMediaDecoders => Options.Limits.MaxMediaDecoders;
    public int MaxWorkerMessageBytes => Options.Limits.MaxWorkerMessageBytes;
    public int MaxWorkers => Options.Limits.MaxWorkers;
    public long MaxWorkerMemoryBytes => Options.Limits.MaxWorkerMemoryBytes;
    public int MaxWebAssemblyBytes => Options.Limits.MaxWebAssemblyBytes;

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
    public Interaction.AccessibleNode? AccessibilityTree() =>
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
        var limits = new ParserLimits(options.Limits.MaxNestingDepth, options.Limits.MaxNodes, ScriptingEnabled: options.Scripting != ScriptingPolicy.Disabled);
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

    // Canvas text, shaped with this document's fonts (and web fonts) by the raster backend's shaper.
    private Hosting.CanvasStyleEngine? _canvasText;

    internal Hosting.CanvasStyleEngine? CanvasText => Options.Raster is { } raster
        ? _canvasText ??= new Hosting.CanvasStyleEngine(_fonts ??= FontCollection.For(Options.Fonts), raster.TextShaper)
        : null;
    private Imaging.ImageLoader? _images;
    private bool _webFontsLoaded;
    private readonly Dictionary<ElementNode, Element> _elements = [];
    private readonly Dictionary<ElementNode, TextControlState> _textControlStates = new(ReferenceEqualityComparer.Instance);
    private InputRouter? _inputRouter;

    internal TextControlState? GetExistingTextControlState(ElementNode element) =>
        _textControlStates.GetValueOrDefault(element);

    internal TextControlState? GetOrCreateTextControlState(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html)
            return null;

        if (element.LocalName == "textarea")
        {
            if (!_textControlStates.TryGetValue(element, out var state))
                _textControlStates[element] = state = new TextControlState(element);
            return state;
        }

        if (element.LocalName == "input")
        {
            var type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
            if (type is "text" or "email" or "search" or "url" or "tel" or "password" or "number")
            {
                if (!_textControlStates.TryGetValue(element, out var state))
                    _textControlStates[element] = state = new TextControlState(element);
                return state;
            }
        }

        return null;
    }

    internal string GetControlValue(ElementNode element)
    {
        if (_textControlStates.TryGetValue(element, out var state))
            return state.Value;
        return element.Value ?? element.GetAttribute("value") ?? (element.LocalName == "textarea" ? element.TextContent : null) ?? "";
    }

    internal void SetControlValue(ElementNode element, string value)
    {
        var state = GetOrCreateTextControlState(element);
        if (state is not null)
        {
            state.Value = value;
            element.Value = state.Value;
            element.DisplayValue = state.DisplayText;
            Update();
        }
        else
        {
            element.Value = value;
            Update();
        }
    }

    internal int GetControlSelectionStart(ElementNode element) =>
        _textControlStates.TryGetValue(element, out var state) ? state.SelectionStart : 0;

    internal void SetControlSelectionStart(ElementNode element, int value)
    {
        if (GetOrCreateTextControlState(element) is { } state)
        {
            state.SelectionStart = value;
            UpdateControlSelection(element, state);
        }
    }

    internal int GetControlSelectionEnd(ElementNode element) =>
        _textControlStates.TryGetValue(element, out var state) ? state.SelectionEnd : 0;

    internal void SetControlSelectionEnd(ElementNode element, int value)
    {
        if (GetOrCreateTextControlState(element) is { } state)
        {
            state.SelectionEnd = value;
            UpdateControlSelection(element, state);
        }
    }

    internal void SetControlSelectionRange(ElementNode element, int start, int end, string? direction = null)
    {
        if (GetOrCreateTextControlState(element) is { } state)
        {
            state.SetSelectionRange(start, end, backwards: direction == "backward");
            UpdateControlSelection(element, state);
        }
    }

    internal void SelectControl(ElementNode element)
    {
        if (GetOrCreateTextControlState(element) is { } state)
        {
            state.SelectAll();
            UpdateControlSelection(element, state);
        }
    }

    internal void UpdateControlSelection(ElementNode element, TextControlState state)
    {
        Input.Select(new RangePoint(element, state.SelectionStart), new RangePoint(element, state.SelectionEnd));
        RepaintDisplayList();
    }

    internal void ToggleCheckbox(ElementNode element)
    {
        if (ElementNode.IsDisabledControl(element))
            return;
        if (element.Indeterminate)
            element.Indeterminate = false;
        var next = !element.IsChecked;
        element.Checked = next;
        SetState(element, NodeFlags.Checked, next);
        Update();
    }

    internal void CheckRadio(ElementNode element)
    {
        if (ElementNode.IsDisabledControl(element) || element.IsChecked)
            return;

        // Radios are grouped by name within parent form (or entire document if no form)
        var group = GetRadioGroup(element);
        foreach (var radio in group)
        {
            if (radio != element && radio.IsChecked)
            {
                radio.Checked = false;
                SetState(radio, NodeFlags.Checked, false);
            }
        }

        element.Checked = true;
        SetState(element, NodeFlags.Checked, true);
        Update();
    }

    internal void SelectAdjacentRadio(ElementNode current, int offset)
    {
        if (ElementNode.IsDisabledControl(current))
            return;
        var group = GetRadioGroup(current).Where(r => !ElementNode.IsDisabledControl(r)).ToList();
        if (group.Count <= 1)
            return;
        var idx = group.IndexOf(current);
        if (idx < 0)
            idx = 0;
        var nextIdx = (idx + offset + group.Count) % group.Count;
        var nextRadio = group[nextIdx];
        CheckRadio(nextRadio);
        Input.Focus(Wrap(nextRadio));
    }

    private List<ElementNode> GetRadioGroup(ElementNode radio)
    {
        var name = radio.GetAttribute("name") ?? "";
        var form = FindParentForm(radio);
        var root = (ContainerNode?)form ?? Node;
        return root.Descendants
            .OfType<ElementNode>()
            .Where(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "input"
                && string.Equals(e.GetAttribute("type"), "radio", StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.GetAttribute("name") ?? "", name, StringComparison.OrdinalIgnoreCase)
                && FindParentForm(e) == form)
            .ToList();
    }

    internal void SelectAdjacentOption(ElementNode select, int offset)
    {
        if (ElementNode.IsDisabledControl(select))
            return;
        var options = select.Descendants.OfType<ElementNode>()
            .Where(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "option" && !ElementNode.IsDisabledControl(e))
            .ToList();
        if (options.Count == 0)
            return;
        var curIdx = options.FindLastIndex(o => o.IsSelected);
        if (curIdx < 0)
            curIdx = 0;
        var nextIdx = Math.Clamp(curIdx + offset, 0, options.Count - 1);
        if (nextIdx != curIdx)
        {
            foreach (var opt in options)
                opt.Selected = false;
            options[nextIdx].Selected = true;
            Update();
        }
    }

    internal void StepRange(ElementNode range, int stepDirection)
    {
        if (ElementNode.IsDisabledControl(range))
            return;
        var (min, max, step, val) = GetRangeMetrics(range);
        var nextVal = Math.Clamp(val + stepDirection * step, min, max);
        range.Value = nextVal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Update();
    }

    internal void SetRangeExtreme(ElementNode range, bool maxExtreme)
    {
        if (ElementNode.IsDisabledControl(range))
            return;
        var (min, max, _, _) = GetRangeMetrics(range);
        range.Value = (maxExtreme ? max : min).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Update();
    }

    internal void SetRangeFraction(ElementNode range, float fraction)
    {
        if (ElementNode.IsDisabledControl(range))
            return;
        var (min, max, step, _) = GetRangeMetrics(range);
        fraction = Math.Clamp(fraction, 0f, 1f);
        var rawVal = min + (max - min) * fraction;
        var steps = Math.Round((rawVal - min) / step);
        var snapped = Math.Clamp(min + steps * step, min, max);
        range.Value = snapped.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Update();
    }

    private static (double Min, double Max, double Step, double Val) GetRangeMetrics(ElementNode element)
    {
        var min = double.TryParse(element.GetAttribute("min"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pMin) ? pMin : 0.0;
        var max = double.TryParse(element.GetAttribute("max"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pMax) ? pMax : 100.0;
        if (max < min)
            max = min;
        var step = double.TryParse(element.GetAttribute("step"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pStep) && pStep > 0 ? pStep : 1.0;
        var defaultVal = (min + max) / 2.0;
        var rawVal = element.Value ?? element.GetAttribute("value");
        var val = double.TryParse(rawVal, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pVal) ? pVal : defaultVal;
        return (min, max, step, Math.Clamp(val, min, max));
    }

    internal static ElementNode? FindParentForm(ElementNode? element)
    {
        for (var cur = element?.Parent as ElementNode; cur is not null; cur = cur.Parent as ElementNode)
        {
            if (cur.Name.Namespace == Namespaces.Html && cur.LocalName == "form")
                return cur;
        }
        return null;
    }

    internal void SubmitForm(ElementNode form, ElementNode? submitter = null)
    {
        if (form.Name.Namespace != Namespaces.Html || form.LocalName != "form")
            return;

        var action = form.GetAttribute("action") ?? "";
        var method = (form.GetAttribute("method") ?? "GET").ToUpperInvariant();
        var data = new List<KeyValuePair<string, string>>();

        var controls = form.Descendants.OfType<ElementNode>().Where(e => e.Name.Namespace == Namespaces.Html).ToList();
        foreach (var control in controls)
        {
            if (ElementNode.IsDisabledControl(control))
                continue;
            var name = control.GetAttribute("name");
            if (string.IsNullOrEmpty(name))
                continue;

            var tag = control.LocalName;
            if (tag == "input")
            {
                var type = (control.GetAttribute("type") ?? "text").ToLowerInvariant();
                if (type is "submit" or "button" or "reset" or "image")
                {
                    if (control == submitter)
                        data.Add(new(name, control.Value ?? control.GetAttribute("value") ?? ""));
                }
                else if (type is "checkbox" or "radio")
                {
                    if (control.IsChecked)
                        data.Add(new(name, control.Value ?? control.GetAttribute("value") ?? "on"));
                }
                else if (type != "file")
                {
                    data.Add(new(name, GetControlValue(control)));
                }
            }
            else if (tag == "textarea")
            {
                data.Add(new(name, GetControlValue(control)));
            }
            else if (tag == "select")
            {
                var options = control.Descendants.OfType<ElementNode>()
                    .Where(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "option" && !ElementNode.IsDisabledControl(e))
                    .ToList();
                var selected = options.Where(o => o.IsSelected).ToList();
                if (selected.Count == 0 && options.Count > 0 && control.GetAttribute("multiple") is null)
                    selected.Add(options[0]);
                foreach (var opt in selected)
                    data.Add(new(name, opt.Value ?? opt.GetAttribute("value") ?? opt.TextContent ?? ""));
            }
            else if (tag == "button")
            {
                if (control == submitter)
                    data.Add(new(name, control.Value ?? control.GetAttribute("value") ?? ""));
            }
        }

        Input.OnFormSubmitted(new FormSubmittedEventArgs(Wrap(form), action, method, data, submitter is not null ? Wrap(submitter) : null));
    }

    internal void ResetForm(ElementNode form)
    {
        if (form.Name.Namespace != Namespaces.Html || form.LocalName != "form")
            return;

        var controls = form.Descendants.OfType<ElementNode>().Where(e => e.Name.Namespace == Namespaces.Html).ToList();
        foreach (var control in controls)
        {
            var tag = control.LocalName;
            if (tag == "input")
            {
                var type = (control.GetAttribute("type") ?? "text").ToLowerInvariant();
                if (type is "checkbox" or "radio")
                {
                    var initChecked = control.GetAttribute("checked") is not null;
                    control.Checked = initChecked;
                    control.Indeterminate = false;
                    SetState(control, NodeFlags.Checked, initChecked);
                }
                else
                {
                    var initVal = control.GetAttribute("value") ?? "";
                    SetControlValue(control, initVal);
                }
            }
            else if (tag == "textarea")
            {
                SetControlValue(control, control.TextContent ?? "");
            }
            else if (tag == "select")
            {
                var options = control.Descendants.OfType<ElementNode>()
                    .Where(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "option")
                    .ToList();
                foreach (var opt in options)
                    opt.Selected = opt.GetAttribute("selected") is not null;
            }
        }
        Update();
    }

    internal void ToggleDetails(ElementNode details)
    {
        if (details.Name.Namespace != Namespaces.Html || details.LocalName != "details")
            return;
        if (details.GetAttribute("open") is not null)
            details.RemoveAttribute("open");
        else
            details.SetAttribute("open", "");
        Update();
    }

    private CaretInfo? GetActiveCaretInfo(double time)
    {
        if (_inputRouter?.FocusedNode is not { } focused)
            return null;

        if (!InputRouter.IsTextControl(focused))
            return null;

        var state = GetExistingTextControlState(focused);
        if (state is null || state.HasSelection)
            return null;

        var visible = (int)Math.Floor(time * 2.0) % 2 == 0;
        return new CaretInfo(focused, state.CaretIndex, visible);
    }

    /// <summary>The input router for handling pointer, keyboard, and focus events on this document.</summary>
    public InputRouter Input => _inputRouter ??= new InputRouter(this);

    /// <summary>The plain text of the active selection, or empty.</summary>
    public string SelectedText => _inputRouter?.SelectedText ?? "";

    /// <summary>The HTML fragment of the active selection, or empty.</summary>
    public string SelectedHtml => _inputRouter?.SelectedHtml ?? "";

    /// <summary>Selects all selectable content across the document.</summary>
    public void SelectAll() => Input.SelectAll();

    /// <summary>Clears the current selection.</summary>
    public void ClearSelection() => Input.ClearSelection();

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

    private readonly Dictionary<ElementNode, Vector2> _scrollOffsets = new(ReferenceEqualityComparer.Instance);
    private Fragment? _page;
    internal Fragment? Page => _page;

    /// <summary>Finds the fragment laid out for an element node in the last layout, if any.</summary>
    internal Fragment? FindFragment(ElementNode element)
    {
        if (_page is not { } page)
            return null;
        var stack = new Stack<Fragment>();
        stack.Push(page);
        while (stack.TryPop(out var frag))
        {
            if (frag.Box?.Node == element && frag.Kind == FragmentKind.Box)
                return frag;
            foreach (var child in frag.Children)
                stack.Push(child.Fragment);
        }
        return null;
    }

    /// <summary>Computes the border-box bounds of an element on the canvas in CSS pixels.</summary>
    internal RectF? GetElementBounds(ElementNode element)
    {
        if (element == Node.DocumentElement)
            return _page is { } icb ? new RectF(0, 0, icb.Width, icb.Height) : null;

        if (_page is not { } page)
            return null;

        RectF? found = null;
        WalkFragment(page, 0, 0);
        return found;

        bool WalkFragment(Fragment frag, float currentX, float currentY)
        {
            if (frag.Box?.Node == element && frag.Kind == FragmentKind.Box)
            {
                found = new RectF(currentX, currentY, frag.Width, frag.Height);
                return true;
            }

            var isScroller = frag.Box?.Node is ElementNode node && IsScrollContainer(node);
            var scrollOffset = isScroller && frag.Box?.Node is ElementNode scrollerEl
                && _scrollOffsets.TryGetValue(scrollerEl, out var offset) ? offset : Vector2.Zero;

            foreach (var child in frag.Children)
            {
                var cx = currentX + child.X - (isScroller ? scrollOffset.X : 0);
                var cy = currentY + child.Y - (isScroller ? scrollOffset.Y : 0);
                if (WalkFragment(child.Fragment, cx, cy))
                    return true;
            }
            return false;
        }
    }

    private (float ClientWidth, float ClientHeight, float ScrollWidth, float ScrollHeight) GetScrollMetrics(ElementNode element)
    {
        if (element == Node.DocumentElement && _page is { } icb)
        {
            var cw = icb.Width;
            var ch = icb.Height;
            var sw = Math.Max(cw, icb.Children.Select(c => c.X + c.Fragment.Width).DefaultIfEmpty(0).Max());
            var sh = Math.Max(ch, icb.Children.Select(c => c.Y + c.Fragment.Height).DefaultIfEmpty(0).Max());
            return (cw, ch, sw, sh);
        }

        if (FindFragment(element) is not { } frag)
            return (0, 0, 0, 0);

        var border = frag.Box?.Style.Border;
        var clientWidth = Math.Max(0, frag.Width - (border?.LeftWidth ?? 0) - (border?.RightWidth ?? 0));
        var clientHeight = Math.Max(0, frag.Height - (border?.TopWidth ?? 0) - (border?.BottomWidth ?? 0));

        // Content extents: maximum reached by child fragments (measured from padding box origin)
        var maxRight = clientWidth;
        var maxBottom = clientHeight;
        var bl = border?.LeftWidth ?? 0;
        var bt = border?.TopWidth ?? 0;
        foreach (var child in frag.Children)
        {
            var r = child.X - bl + child.Fragment.Width;
            var b = child.Y - bt + child.Fragment.Height;
            if (r > maxRight) maxRight = r;
            if (b > maxBottom) maxBottom = b;
        }

        return (clientWidth, clientHeight, maxRight, maxBottom);
    }

    internal (float ClientWidth, float ClientHeight, float ScrollWidth, float ScrollHeight) GetScrollMetricsForNode(ElementNode element) =>
        GetScrollMetrics(element);

    internal bool IsScrollContainer(ElementNode element)
    {
        if (element == Node.DocumentElement)
            return true;
        if (FindFragment(element)?.Box is { } box)
        {
            var (ox, oy) = (box.Style.Box.OverflowX, box.Style.Box.OverflowY);
            return ox is Overflow.Scroll or Overflow.Auto or Overflow.Hidden
                || oy is Overflow.Scroll or Overflow.Auto or Overflow.Hidden;
        }
        return false;
    }

    private readonly record struct SmoothScrollAnimation(ElementNode Node, Vector2 Start, Vector2 Target, double StartTime, double Duration);
    private readonly Dictionary<ElementNode, SmoothScrollAnimation> _smoothScrolls = new(ReferenceEqualityComparer.Instance);

    private void CancelSmoothScroll(ElementNode node) => _smoothScrolls.Remove(node);

    internal void CancelSmoothScrollAnimations() => _smoothScrolls.Clear();

    internal Vector2 ScrollBy(ElementNode element, Vector2 delta)
    {
        CancelSmoothScroll(element);
        var (cw, ch, sw, sh) = GetScrollMetrics(element);
        var maxX = Math.Max(0, sw - cw);
        var maxY = Math.Max(0, sh - ch);

        var current = _scrollOffsets.TryGetValue(element, out var v) ? v : Vector2.Zero;
        var targetX = current.X + delta.X;
        var targetY = current.Y + delta.Y;

        var clampedX = Math.Clamp(targetX, 0, maxX);
        var clampedY = Math.Clamp(targetY, 0, maxY);

        var consumedX = clampedX - current.X;
        var consumedY = clampedY - current.Y;

        if (clampedX != current.X || clampedY != current.Y)
        {
            _scrollOffsets[element] = new Vector2(clampedX, clampedY);
            if (_frame is { } frame && _images is not null)
                DisplayList = DisplayListBuilder.Build(frame.Page, _images, frame.Scale, _scrollOffsets, _inputRouter?.Selection);
        }

        return new Vector2(consumedX, consumedY);
    }

    internal float GetScrollLeft(ElementNode element) =>
        _scrollOffsets.TryGetValue(element, out var v) ? v.X : 0;

    internal float GetScrollTop(ElementNode element) =>
        _scrollOffsets.TryGetValue(element, out var v) ? v.Y : 0;

    internal float GetClientWidth(ElementNode element) => GetScrollMetrics(element).ClientWidth;
    internal float GetClientHeight(ElementNode element) => GetScrollMetrics(element).ClientHeight;
    internal float GetScrollWidth(ElementNode element) => GetScrollMetrics(element).ScrollWidth;
    internal float GetScrollHeight(ElementNode element) => GetScrollMetrics(element).ScrollHeight;

    /// <summary>Scrolls an element to the given position, clamped to its scroll range.</summary>
    public void ScrollTo(Element element, float x, float y, string? behavior = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        CancelSmoothScroll(element.Node);
        var (cw, ch, sw, sh) = GetScrollMetrics(element.Node);
        var maxX = Math.Max(0, sw - cw);
        var maxY = Math.Max(0, sh - ch);
        var clamped = new Vector2(Math.Clamp(x, 0, maxX), Math.Clamp(y, 0, maxY));

        var current = _scrollOffsets.TryGetValue(element.Node, out var v) ? v : Vector2.Zero;
        var computedBehavior = FindFragment(element.Node)?.Box?.Style.Box.ScrollBehavior ?? ScrollBehavior.Auto;
        var isSmooth = string.Equals(behavior, "smooth", StringComparison.OrdinalIgnoreCase)
            || (behavior is null && computedBehavior == ScrollBehavior.Smooth);

        if (isSmooth && clamped != current)
        {
            var startTime = _lastPaint?.AnimationTime ?? 0;
            _smoothScrolls[element.Node] = new SmoothScrollAnimation(element.Node, current, clamped, startTime, 0.3);
        }
        else
        {
            _scrollOffsets[element.Node] = clamped;
            if (_frame is { } frame && _images is not null)
                DisplayList = DisplayListBuilder.Build(frame.Page, _images, frame.Scale, _scrollOffsets, _inputRouter?.Selection);
        }
    }

    /// <summary>Scrolls the root document / viewport to the given coordinates.</summary>
    public void ScrollTo(float x, float y, string? behavior = null)
    {
        if (DocumentElement is { } root)
            ScrollTo(root, x, y, behavior);
    }

    /// <summary>Scrolls an element into view across all ancestor scroll containers.</summary>
    public void ScrollIntoView(Element element, string? behavior = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ScrollIntoView(element.Node, behavior);
    }

    internal void ScrollIntoView(ElementNode targetNode, string? behavior = null)
    {
        // CSSOM View §7: walk from target upward through ancestor scroll containers
        for (Node? curr = targetNode.Parent; curr is not null; curr = curr.Parent)
        {
            if (curr is not ElementNode scroller || !IsScrollContainer(scroller))
                continue;

            var targetRect = GetRelativeBorderBox(targetNode, scroller);
            if (targetRect is null)
                continue;

            // scroll-margin expands target bounds
            var targetMargin = GetScrollMargin(targetNode);
            var mTop = targetMargin.Top.Resolve(targetRect.Value.Height);
            var mRight = targetMargin.Right.Resolve(targetRect.Value.Width);
            var mBottom = targetMargin.Bottom.Resolve(targetRect.Value.Height);
            var mLeft = targetMargin.Left.Resolve(targetRect.Value.Width);
            var expandedTarget = new RectF(
                targetRect.Value.X - mLeft,
                targetRect.Value.Y - mTop,
                targetRect.Value.Width + mLeft + mRight,
                targetRect.Value.Height + mTop + mBottom);

            // scroll-padding insets scroller's scrollport (padding box)
            var (cw, ch, sw, sh) = GetScrollMetrics(scroller);
            var scrollerPadding = GetScrollPadding(scroller);
            var pTop = scrollerPadding.Top.Resolve(ch);
            var pRight = scrollerPadding.Right.Resolve(cw);
            var pBottom = scrollerPadding.Bottom.Resolve(ch);
            var pLeft = scrollerPadding.Left.Resolve(cw);

            var scrollport = new RectF(pLeft, pTop, Math.Max(0, cw - pLeft - pRight), Math.Max(0, ch - pTop - pBottom));

            var currentScroll = _scrollOffsets.TryGetValue(scroller, out var cur) ? cur : Vector2.Zero;
            var targetScrollX = currentScroll.X;
            var targetScrollY = currentScroll.Y;

            // Vertical alignment
            if (expandedTarget.Y < scrollport.Y)
                targetScrollY = currentScroll.Y + (expandedTarget.Y - scrollport.Y);
            else if (expandedTarget.Bottom > scrollport.Bottom)
                targetScrollY = currentScroll.Y + (expandedTarget.Bottom - scrollport.Bottom);

            // Horizontal alignment
            if (expandedTarget.X < scrollport.X)
                targetScrollX = currentScroll.X + (expandedTarget.X - scrollport.X);
            else if (expandedTarget.Right > scrollport.Right)
                targetScrollX = currentScroll.X + (expandedTarget.Right - scrollport.Right);

            var maxX = Math.Max(0, sw - cw);
            var maxY = Math.Max(0, sh - ch);
            targetScrollX = Math.Clamp(targetScrollX, 0, maxX);
            targetScrollY = Math.Clamp(targetScrollY, 0, maxY);

            var scrollerBehavior = behavior;
            if (scrollerBehavior is null && FindFragment(scroller)?.Box?.Style.Box.ScrollBehavior == ScrollBehavior.Smooth)
                scrollerBehavior = "smooth";

            ScrollTo(Wrap(scroller), targetScrollX, targetScrollY, scrollerBehavior);
        }
    }

    private (LengthPercentage Top, LengthPercentage Right, LengthPercentage Bottom, LengthPercentage Left) GetScrollMargin(ElementNode node)
    {
        var style = FindFragment(node)?.Box?.Style;
        return (style?.Spacing.ScrollMarginTop ?? default,
                style?.Spacing.ScrollMarginRight ?? default,
                style?.Spacing.ScrollMarginBottom ?? default,
                style?.Spacing.ScrollMarginLeft ?? default);
    }

    private (LengthPercentage Top, LengthPercentage Right, LengthPercentage Bottom, LengthPercentage Left) GetScrollPadding(ElementNode node)
    {
        var style = FindFragment(node)?.Box?.Style;
        return (style?.Spacing.ScrollPaddingTop ?? default,
                style?.Spacing.ScrollPaddingRight ?? default,
                style?.Spacing.ScrollPaddingBottom ?? default,
                style?.Spacing.ScrollPaddingLeft ?? default);
    }

    private RectF? GetRelativeBorderBox(ElementNode target, ElementNode scroller)
    {
        if (scroller == Node.DocumentElement)
            return GetElementBounds(target);

        if (FindFragment(scroller) is not { } scrollerFrag || FindFragment(target) is not { } targetFrag)
            return null;

        var border = scrollerFrag.Box?.Style.Border;
        var bLeft = border?.LeftWidth ?? 0;
        var bTop = border?.TopWidth ?? 0;

        RectF? found = null;
        Walk(scrollerFrag, -bLeft, -bTop);
        return found;

        bool Walk(Fragment frag, float currentX, float currentY)
        {
            if (frag.Box?.Node == target && frag.Kind == FragmentKind.Box)
            {
                found = new RectF(currentX, currentY, frag.Width, frag.Height);
                return true;
            }

            var isInnerScroller = frag != scrollerFrag && frag.Box?.Node is ElementNode node && IsScrollContainer(node);
            var scrollOffset = isInnerScroller && frag.Box?.Node is ElementNode scrollerEl
                && _scrollOffsets.TryGetValue(scrollerEl, out var offset) ? offset : Vector2.Zero;

            foreach (var child in frag.Children)
            {
                var cx = currentX + child.X - (isInnerScroller ? scrollOffset.X : 0);
                var cy = currentY + child.Y - (isInnerScroller ? scrollOffset.Y : 0);
                if (Walk(child.Fragment, cx, cy))
                    return true;
            }
            return false;
        }
    }

    private ElementNode? _targetElement;

    /// <summary>The currently targeted element matching the URL fragment (:target), or null.</summary>
    public Element? TargetElement => _targetElement is not null ? Wrap(_targetElement) : null;

    /// <summary>Navigates to the given URL fragment identifier (e.g. "#section" or "section").</summary>
    public bool NavigateToFragment(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        if (fragment.StartsWith('#'))
            fragment = fragment[1..];

        var oldTarget = _targetElement;
        var newTarget = string.IsNullOrEmpty(fragment) ? null : GetElementById(fragment)?.Node;

        if (oldTarget == newTarget)
        {
            if (newTarget is not null)
                ScrollIntoView(newTarget);
            return newTarget is not null;
        }

        if (oldTarget is not null)
            SetState(oldTarget, NodeFlags.Target, false);

        _targetElement = newTarget;

        if (newTarget is not null)
            SetState(newTarget, NodeFlags.Target, true);

        Update();

        if (newTarget is not null)
            ScrollIntoView(newTarget);

        return newTarget is not null;
    }

    /// <summary>The horizontal scroll offset of the root document.</summary>
    public float ScrollLeft => DocumentElement is { } root ? root.ScrollLeft : 0;

    /// <summary>The vertical scroll offset of the root document.</summary>
    public float ScrollTop => DocumentElement is { } root ? root.ScrollTop : 0;

    /// <summary>The scroll width of the root document.</summary>
    public float ScrollWidth => DocumentElement is { } root ? root.ScrollWidth : (_page?.Width ?? 0);

    /// <summary>The scroll height of the root document.</summary>
    public float ScrollHeight => DocumentElement is { } root ? root.ScrollHeight : (_page?.Height ?? 0);

    /// <summary>The client width of the root document.</summary>
    public float ClientWidth => _page?.Width ?? 0;

    /// <summary>The client height of the root document.</summary>
    public float ClientHeight => _page?.Height ?? 0;

    internal HitResult? HitAt(float x, float y) =>
        _page is { } page ? HitTester.Hit(page, new(x, y), _frame?.Scale ?? 1, _scrollOffsets) : null;

    /// <summary>
    /// The element under a point of the last painted layout (page coordinates in CSS pixels): the one painted on top
    /// there, by hit testing in reverse paint order. Null outside every box or before painting.
    /// </summary>
    internal ElementNode? ElementAt(float x, float y) => HitAt(x, y)?.Node;

    /// <summary>
    /// Computes the CSS cursor keyword active at the given point (page coordinates in CSS pixels).
    /// Defaults to "default", resolves "auto" over selectable text to "text", and respects "user-select: none".
    /// </summary>
    public string CursorAt(float x, float y)
    {
        var hit = HitAt(x, y);
        if (hit is null)
            return "default";

        var style = hit.Style ?? hit.Node.ComputedStyle();
        var cursor = style?.Ui.Cursor ?? "auto";
        if (cursor != "auto")
            return cursor;

        // "auto" resolution: text under the point resolves to "text", unless user-select is "none".
        if (hit.Fragment.Kind == FragmentKind.Text)
        {
            var userSelect = style?.Ui.UserSelect ?? UserSelect.Auto;
            return userSelect == UserSelect.None ? "default" : "text";
        }

        return "default";
    }

    /// <summary>
    /// Returns the tooltip text from the nearest ancestor element with a non-empty title attribute under the point.
    /// </summary>
    public string? TitleAt(float x, float y)
    {
        for (Node? node = ElementAt(x, y); node is not null; node = node.Parent)
        {
            if (node is ElementNode el && el.GetAttribute("title") is { Length: > 0 } title)
                return title;
        }
        return null;
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
        (NodeFlags.Target, PseudoClass.Target),
        (NodeFlags.Checked, PseudoClass.Checked),
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
        // The content reaches down to the root's bottom margin edge, or further for positioned boxes.
        var height = page.Children.Select((c, i) => c.Y + c.Fragment.Height + (i == 0 ? c.Fragment.BottomMargins.Resolve() : 0)).DefaultIfEmpty(0).Max();
        if (animationTime.HasValue)
            AdvanceSmoothScrolls(animationTime.Value);
        DisplayList = DisplayListBuilder.Build(page, _images, deviceScale, _scrollOffsets, _inputRouter?.Selection, GetActiveCaretInfo(animationTime ?? 0));
        return (DisplayList, height);
    }

    /// <summary>Rebuilds the display list from the current layout frame when paint state (such as selection) changes.</summary>
    internal void RepaintDisplayList()
    {
        if (_frame is { } frame && _images is not null)
            DisplayList = DisplayListBuilder.Build(frame.Page, _images, frame.Scale, _scrollOffsets, _inputRouter?.Selection, GetActiveCaretInfo(0));
    }

    private void AdvanceSmoothScrolls(double time)
    {
        if (_smoothScrolls.Count == 0)
            return;

        var completed = new List<ElementNode>();
        foreach (var (node, anim) in _smoothScrolls)
        {
            var t = anim.Duration > 0 ? (time - anim.StartTime) / anim.Duration : 1.0;
            if (t >= 1.0)
            {
                _scrollOffsets[node] = anim.Target;
                completed.Add(node);
            }
            else if (t <= 0.0)
            {
                _scrollOffsets[node] = anim.Start;
            }
            else
            {
                var progress = (float)Easing.Ease.Apply(t);
                _scrollOffsets[node] = Vector2.Lerp(anim.Start, anim.Target, progress);
            }
        }

        foreach (var node in completed)
            _smoothScrolls.Remove(node);
    }

    /// <summary>Whether an animation still changes after <paramref name="time"/> (seconds on the document timeline).</summary>
    internal bool AnimationsRunning(double time) =>
        _smoothScrolls.Values.Any(s => time < s.StartTime + s.Duration)
        || StyleResolver.Animated(Node).Any(a => a.IsRunning(time))
        || (_inputRouter?.HasFocusedTextControl == true);

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
        AdvanceSmoothScrolls(time);
        return DisplayList = DisplayListBuilder.Build(frame.Page, _images, frame.Scale, _scrollOffsets, _inputRouter?.Selection, GetActiveCaretInfo(time));

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
            // A style or link element came, went or changed its text: the styles are resolved again before the boxes are built.
            if (Node.SheetsMutated)
            {
                Node.SheetsMutated = false;
                Node.StyleMutated = false;
                var media = new MediaContext(last.Width, last.Height, last.Scale, Options.ColorScheme == ColorScheme.Dark) { ReducedMotion = Options.ReducedMotion };
                _fonts ??= FontCollection.For(Options.Fonts);
                var sources = new StyleSources(Options.ResourceLoader is { } host ? new ResourceLoader(host: host) : ResourceLoader.DataUrlsOnly, Options.BaseUri?.AbsoluteUri);
                StyleResolver.Resolve(Node, media, Options.UserStyleSheet, sources, InlineLayout.MeasureWith(_fonts), last.AnimationTime);
            }
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
        var changed = new List<ElementNode>();
        foreach (var (element, (oldStyle, oldBefore, oldAfter, oldMarker)) in oldStyles)
        {
            var newStyle = element.ComputedStyle();
            if (!ReferenceEquals(oldStyle, newStyle) && !Equals(oldStyle, newStyle))
                changed.Add(element);
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
                var newStyle = box.Node is ElementNode el
                    ? box.PseudoElement switch
                    {
                        PseudoElement.Before => el.PseudoStyle(PseudoElement.Before),
                        PseudoElement.After => el.PseudoStyle(PseudoElement.After),
                        PseudoElement.Marker => el.PseudoStyle(PseudoElement.Marker) ?? el.ComputedStyle(),
                        _ => el.ComputedStyle(),
                    }
                    : box is MarkerBox && box.Parent?.Node is ElementNode pEl
                        ? (pEl.PseudoStyle(PseudoElement.Marker) ?? pEl.ComputedStyle())
                        : box.Parent is { } parent && parent.Node is ElementNode { LocalName: "select" }
                            ? BoxTreeBuilder.SelectInnerStyle(parent.Style)
                            : box.Parent is { } p
                                ? BoxTreeBuilder.AnonymousStyle(p.Style, box.Style.Box.Display)
                                : null;

                if (newStyle is not null)
                {
                    var (containsFixed, previous) = (box.ContainsFixed, box.Style);
                    box.Style = newStyle;
                    // Collapsed table borders are resolved against the neighbouring cells in layout and kept in the fragments.
                    var collapsedBorder = box is TablePartBox && newStyle.Text.BorderCollapse == BorderCollapse.Collapse && !previous.Equals(newStyle);
                    if (box.ContainsFixed != containsFixed || collapsedBorder)
                    {
                        needsLayout = true;
                        break;
                    }
                }
            }

            // An inline SVG's render tree has its paints (currentColor, inherited fill and stroke) resolved when it is built in
            // layout: a style change on the SVG, inside it or above it needs that tree built again, not just a repaint.
            if (!needsLayout && changed.Count > 0)
            {
                foreach (var box in boxes)
                {
                    if (box is ReplacedBox { SvgRoot: { } svgRoot } && changed.Any(c => IsAncestorOrSelf(c, svgRoot) || IsAncestorOrSelf(svgRoot, c)))
                    {
                        needsLayout = true;
                        break;
                    }
                }
            }

            if (!needsLayout)
                UpdateFragmentTextStyles(frameVal.Page);

            if (needsLayout)
            {
                maxDamage = Damage.Layout;
            }
            else
            {
                DisplayList = DisplayListBuilder.Build(frameVal.Page, _images, frameVal.Scale, _scrollOffsets, _inputRouter?.Selection);
                return Damage.Paint;
            }
        }

        if (maxDamage == Damage.Layout)
        {
            // Text and anonymous boxes carry styles inherited from their parents when the box tree is built, and the
            // boxes would keep the old ones: the tree is built again, as for Boxes damage (docs/study/14-invalidation.md,
            // scoped rebuilds come with #418).
            RebuildBoxes(last);
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
            DisplayList = DisplayListBuilder.Build(page, _images, l.Scale, _scrollOffsets, _inputRouter?.Selection);
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

        static bool IsAncestorOrSelf(Node ancestor, Node node)
        {
            for (Node? current = node; current is not null; current = current.Parent)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }
            return false;
        }

        static List<Box> AllBoxes(Box root)
        {
            var boxes = new List<Box>();
            var seen = new HashSet<Box>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<Box>([root]);
            while (stack.TryPop(out var box))
            {
                if (!seen.Add(box))
                    continue;
                boxes.Add(box);
                if (box is BlockContainerBox { Marker: { } marker })
                    stack.Push(marker);
                // Boxes inside a paragraph (an inline disclosure marker, an atomic inline) are items of its inline
                // formatting context, not children, and take a new style like any other box.
                if (box is BlockContainerBox { Inline: { } inline })
                {
                    foreach (var item in inline.Items)
                    {
                        if (item.Box is { } inlineBox)
                            stack.Push(inlineBox);
                    }
                }
                foreach (var child in box.Children)
                    stack.Push(child);
            }
            return boxes;
        }

        static void UpdateFragmentTextStyles(Fragment fragment)
        {
            var stack = new Stack<Fragment>([fragment]);
            while (stack.TryPop(out var f))
            {
                if (f.Text is { } text && text.Inline is { } inline)
                    f.Text = text with { Style = inline.Style };
                else if (f.Text is { } t && f.Box is { } b)
                    f.Text = t with { Style = b.Style };

                foreach (var child in f.Children)
                    stack.Push(child.Fragment);
            }
        }
    }

    public void Dispose()
    {
        _textControlStates.Clear();
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

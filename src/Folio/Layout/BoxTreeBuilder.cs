using System.Globalization;
using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Style;

namespace Folio.Layout;

/// <summary>
/// Builds the box tree from a styled DOM (docs/study/05-box-tree.md): display types and blockification, anonymous
/// block and flex/grid items, block-in-inline splitting, inline formatting contexts with white space processed,
/// ::before/::after/::marker, counters and quotes, table fix-up. The DOM is walked with an explicit stack.
/// </summary>
internal sealed class BoxTreeBuilder
{
    private readonly CounterState _counters = new();
    private readonly Stack<Frame> _frames = new();
    private readonly Stack<Frame> _containers = new(); // the frames that collect children, innermost on top
    private int _quoteDepth;
    private Imaging.ImageLoader? _images;
    private float _deviceScale = 1;

    /// <summary>The root element's box, or null when the root generates none (display: none).</summary>
    /// <param name="images">Loads img sources; without one, images have no content.</param>
    /// <param name="deviceScale">Device pixels per CSS pixel, for choosing among srcset candidates.</param>
    public static Box? Build(DocumentNode document, Imaging.ImageLoader? images = null, float deviceScale = 1)
    {
        if (document.DocumentElement is not { } root || root.ComputedStyle() is null)
            return null;
        var builder = new BoxTreeBuilder { _images = images, _deviceScale = deviceScale };
        var top = new Frame(FrameKind.Block, null, root.ComputedStyle()!);
        builder.Push(top);
        builder.Walk(root);
        builder.Finish(top);
        return top.Segments.OfType<Box>().FirstOrDefault();
    }

    // ---------------------------------------------------------------- frames

    private enum FrameKind
    {
        Block,     // block container: block-level children or inline content
        Flex,      // flex or grid container: children are items
        Table,     // table grid box and other table parts
        Inline,    // an open inline box: content goes to the enclosing container's run
        Contents,  // display: contents: no box
    }

    private sealed class Frame(FrameKind kind, Box? box, ComputedStyle style)
    {
        public FrameKind Kind { get; } = kind;
        public Box? Box { get; } = box;
        public ComputedStyle Style { get; } = style;

        /// <summary>For container frames: boxes and inline runs in order.</summary>
        public List<object> Segments { get; } = [];

        public InlineRun? Run { get; set; }

        /// <summary>For Inline frames: the element whose box it is (closes it on leave).</summary>
        public Node? ElementNode { get; init; }

        public bool CollectsChildren => Kind is FrameKind.Block or FrameKind.Flex or FrameKind.Table;

        /// <summary>For a ruby's Inline frame: the base and annotation pair being filled, if any.</summary>
        public RubyColumnBox? RubyColumn { get; set; }
        public bool IsRuby { get; init; }

        /// <summary>For a ruby base's or annotation's frame: the ruby's frame.</summary>
        public Frame? RubyBaseOf { get; init; }
        public Frame? RubyAnnotationOf { get; init; }
    }

    private Frame Container => _containers.Peek();

    // A block container whose ::first-letter is still to come: the first letter of its first line of text.
    // ponytail: only text in the container's own inline content counts; a first line inside a child block, and the
    // properties ::first-letter may not set, are not handled yet.
    private (ElementNode Element, Frame Frame, ComputedStyle Style)? _firstLetter;

    private void Push(Frame frame)
    {
        _frames.Push(frame);
        if (frame.CollectsChildren)
            _containers.Push(frame);
    }

    private Frame Pop()
    {
        var frame = _frames.Pop();
        if (frame.CollectsChildren)
            _containers.Pop();
        return frame;
    }

    private InlineRun RunOf(Frame container) => container.Run ??= new InlineRun(this, container);

    // ---------------------------------------------------------------- walk

    // Pre-order walk with enter/leave events and no recursion.
    private void Walk(ElementNode root)
    {
        var stack = new Stack<(Node Node, bool Leaving)>();
        stack.Push((root, false));
        while (stack.TryPop(out var item))
        {
            if (item.Node is not ElementNode element)
            {
                if (item.Node is Text text)
                    AddText(text.Data, text.Parent is ElementNode p ? p.ComputedStyle() : null);
                continue;
            }
            if (item.Leaving)
            {
                Leave(element);
                continue;
            }
            if (!Enter(element))
                continue;
            stack.Push((element, true));
            for (var child = element.LastChild; child is not null; child = child.PreviousSibling)
            {
                if (child is ElementNode or Text)
                    stack.Push((child, false));
            }
        }
    }

    /// <returns>Whether the element's children should be visited.</returns>
    private bool Enter(ElementNode element)
    {
        var style = element.ComputedStyle();
        if (style is null)
            return false;
        var replaced = ReplacedKindOf(element);
        var display = style.Box.Display;
        if (display == Display.None || (display == Display.Contents && replaced is not null))
            return false;

        var (reset, set) = ListAttributes(element, style);
        _counters.Apply(element, reset, style.Generated.CounterIncrement, set, display == Display.ListItem, name => ReversedStart(element, name));

        if (IsHtml(element, "br"))
        {
            RunOf(Container).AddForcedBreak(style);
            return false;
        }
        if (IsHtml(element, "wbr"))
        {
            RunOf(Container).AddBreakOpportunity(style);
            return false;
        }

        if (display == Display.Contents)
        {
            Push(new Frame(FrameKind.Contents, null, style) { ElementNode = element });
            AddPseudo(element, PseudoElement.Before);
            return true;
        }

        var parent = _frames.Peek();
        if (display == Display.RubyText && (parent.IsRuby || parent.RubyBaseOf is not null))
        {
            OpenAnnotation(element, style, parent);
            return true;
        }
        if (parent.IsRuby)
            OpenRubyBase(parent);
        parent = _frames.Peek();
        var blockify = Container.Kind == FrameKind.Flex && parent.Kind != FrameKind.Inline
                       || style.Box.Float != FloatSide.None || style.Box.Position is Position.Absolute or Position.Fixed
                       || element.Parent is DocumentNode;
        if (blockify)
            display = Blockified(display);

        // Text-like controls show their value as text in a box of their own (study 15: static appearance).
        if (ControlText(element) is { } controlText)
        {
            var control = OpenBox(element, style, IsInlineLevel(display) ? Display.InlineBlock : Blockified(display), PseudoElement.None);
            Push(control);
            // A drop-down select's text sits in an inner box padded inside the author's padding, with room for the arrow.
            var inner = control.Box is BlockContainerBox && element.LocalName == "select";
            if (inner)
            {
                var box = new BlockContainerBox(SelectInnerStyle(style), null);
                Place(box, inlineLevel: false);
                Push(new Frame(FrameKind.Block, box, box.Style));
            }
            AddText(controlText, style);
            if (inner)
                Finish(Pop());
            Finish(Pop());
            return false;
        }
        if (replaced is { } kind)
        {
            var (source, density) = kind == ReplacedKind.Image ? ImageSource(element, _deviceScale) : (null, 1);
            var image = source is null ? null : _images?.LoadAny(source);
            var svg = kind == ReplacedKind.Svg ? element : image as ElementNode;
            var box = new ReplacedBox(style, element, kind)
            {
                IsAtomicInline = IsInlineLevel(display),
                Image = image as Imaging.DecodedImage,
                SvgImage = image as ElementNode,
                Density = density,
                SvgNatural = svg is null ? null : Svg.SvgRenderTree.NaturalSize(svg, style.Font.Size),
            };
            Place(box, IsInlineLevel(display));
            return false;
        }

        var frame = OpenBox(element, style, display, PseudoElement.None);
        if (display == Display.Ruby)
            frame = new Frame(FrameKind.Inline, frame.Box, style) { ElementNode = element, IsRuby = true };
        Push(frame);
        if (frame is { Kind: FrameKind.Block, Box: BlockContainerBox } && element.PseudoStyle(PseudoElement.FirstLetter) is { } firstLetter
            && firstLetter.Box.Display != Display.None)
            _firstLetter = (element, frame, firstLetter);
        if (display == Display.ListItem)
            AddMarker(element);
        AddPseudo(element, PseudoElement.Before);
        return true;
    }

    private void Leave(ElementNode element)
    {
        if (_frames.Peek().RubyBaseOf is { } ruby && ruby.ElementNode == element)
            Finish(Pop());
        AddPseudo(element, PseudoElement.After);
        var frame = Pop();
        Finish(frame);
        if (frame.RubyAnnotationOf is { } annotated)
            annotated.RubyColumn = null;
        _counters.Leave(element);
    }

    // Creates the box for an element or pseudo-element and places it in its parent; returns its frame.
    private Frame OpenBox(Node node, ComputedStyle style, Display display, PseudoElement pseudo)
    {
        switch (display)
        {
            case Display.Inline or Display.Contents or Display.Ruby or Display.RubyText:
                var inline = new InlineBox(style, node, pseudo);
                RunOf(Container).Open(inline);
                return new Frame(FrameKind.Inline, inline, style) { ElementNode = node };
            case Display.InlineBlock or Display.Block or Display.FlowRoot or Display.ListItem:
                var block = new BlockContainerBox(style, node, pseudo) { IsAtomicInline = display == Display.InlineBlock };
                Place(block, display == Display.InlineBlock);
                return new Frame(FrameKind.Block, block, style);
            case Display.Flex or Display.InlineFlex:
                var flex = new FlexContainerBox(style, node, pseudo) { IsAtomicInline = display == Display.InlineFlex };
                Place(flex, flex.IsAtomicInline);
                return new Frame(FrameKind.Flex, flex, style);
            case Display.Grid or Display.InlineGrid:
                var grid = new GridContainerBox(style, node, pseudo) { IsAtomicInline = display == Display.InlineGrid };
                Place(grid, grid.IsAtomicInline);
                return new Frame(FrameKind.Flex, grid, style);
            case Display.Table or Display.InlineTable:
                var wrapper = new TableWrapperBox(style, node) { IsAtomicInline = display == Display.InlineTable };
                Place(wrapper, wrapper.IsAtomicInline);
                var table = new TablePartBox(style, node, TablePart.Table, pseudo);
                wrapper.Add(table);
                return new Frame(FrameKind.Table, table, style);
            default:
                var part = new TablePartBox(style, node, PartOf(display), pseudo);
                Place(part, inlineLevel: false);
                return new Frame(part.Part is TablePart.Cell or TablePart.Caption ? FrameKind.Block : FrameKind.Table, part, style);
        }
    }

    // Ruby (css-ruby-1 §2): each base and the annotation after it become one RubyColumnBox, an atomic inline in the
    // ruby's inline box. Content in a ruby opens a column and its base; an rt closes the base and fills the annotation.
    // ponytail: no rtc or rb pairing across levels; a ruby nested in a base is laid out inside that base.
    private void OpenRubyBase(Frame ruby)
    {
        if (ruby.RubyColumn is not null)
            return;
        var column = new RubyColumnBox(AnonymousStyle(ruby.Style, Display.Block)) { IsAtomicInline = true };
        Place(column, inlineLevel: true);
        var rubyBase = new BlockContainerBox(AnonymousStyle(ruby.Style, Display.Block), null);
        column.Add(rubyBase);
        ruby.RubyColumn = column;
        Push(new Frame(FrameKind.Block, rubyBase, rubyBase.Style) { RubyBaseOf = ruby });
    }

    private void OpenAnnotation(ElementNode rt, ComputedStyle style, Frame parent)
    {
        var ruby = parent.RubyBaseOf ?? parent;
        if (parent.RubyBaseOf is null)
            OpenRubyBase(ruby); // an annotation with no base before it
        Finish(Pop());
        var annotation = new BlockContainerBox(style, rt);
        ruby.RubyColumn!.Add(annotation);
        Push(new Frame(FrameKind.Block, annotation, style) { RubyAnnotationOf = ruby });
        AddPseudo(rt, PseudoElement.Before);
    }

    // Adds a new child box: inline-level boxes (and floats/abspos inside inline content) become items of the run.
    private void Place(Box box, bool inlineLevel)
    {
        var container = Container;
        // Anything but text before the first letter (a block, an image, an inline block) means there is none.
        if (_firstLetter?.Frame == container)
            _firstLetter = null;
        var insideInline = _frames.Peek().Kind == FrameKind.Inline;
        if (inlineLevel && container.Kind == FrameKind.Block)
        {
            RunOf(container).AddAtomic(box);
            return;
        }
        if ((box.IsFloat || box.IsAbsolutelyPositioned) && container.Kind == FrameKind.Block
            && (insideInline || container.Run is { IsEmpty: false }))
        {
            RunOf(container).AddOutOfFlow(box);
            return;
        }
        if (insideInline && container.Kind == FrameKind.Block)
        {
            // A block inside an inline: end the paragraph here and continue the inline boxes after the block.
            container.Run!.SplitAround(box);
            return;
        }
        container.Run?.EndSegment();
        container.Segments.Add(box);
    }

    private void Finish(Frame frame)
    {
        if (frame.Kind == FrameKind.Inline)
        {
            RunOf(Container).Close((InlineBox)frame.Box!);
            return;
        }
        if (!frame.CollectsChildren)
            return;

        if (_firstLetter?.Frame == frame)
            _firstLetter = null;
        frame.Run?.EndSegment();
        var runs = frame.Segments.OfType<InlineRun>().ToList();
        var hasBoxes = frame.Segments.Any(s => s is Box);
        if (frame.Kind == FrameKind.Block && frame.Box is BlockContainerBox block && !hasBoxes)
        {
            // Only inline content: this container holds the inline formatting context itself.
            if (runs.Count > 0 && !runs[0].IsWhitespaceOnly)
                block.Inline = runs[0].Context;
            return;
        }

        var children = new List<Box>();
        foreach (var segment in frame.Segments)
        {
            if (segment is Box box)
            {
                children.Add(box);
            }
            else if (segment is InlineRun run && !run.IsWhitespaceOnly && frame.Box is not null)
            {
                var anonymous = new BlockContainerBox(AnonymousStyle(frame.Box.Style, Display.Block), null) { Inline = run.Context };
                children.Add(anonymous);
            }
        }

        if (frame.Box is null)
        {
            // The document's top frame: the root box.
            frame.Segments.Clear();
            frame.Segments.AddRange(children);
            return;
        }
        children = TableFixUp(frame.Box, children);
        if (frame.Box is TablePartBox { Part: TablePart.Table, Parent: TableWrapperBox wrapper })
        {
            // Captions belong to the table wrapper, before the table grid (caption-side: top).
            var captions = children.Where(c => IsPart(c, TablePart.Caption)).ToList();
            wrapper.Children.InsertRange(0, captions);
            foreach (var caption in captions)
                caption.Parent = wrapper;
            children = children.Except(captions).ToList();
        }
        foreach (var child in children)
            frame.Box.Add(child);
    }

    // ---------------------------------------------------------------- text, pseudo-elements, markers

    private void AddText(string text, ComputedStyle? style)
    {
        if (style is null || text.Length == 0)
            return;
        if (_frames.Peek() is { IsRuby: true } ruby)
        {
            // White space between a ruby's pairs is not content (css-ruby-1 §2.3).
            if (string.IsNullOrWhiteSpace(text))
                return;
            OpenRubyBase(ruby);
        }
        if (_firstLetter is { } pending && pending.Frame == Container && FirstLetter(text) is var (start, length))
        {
            // https://www.w3.org/TR/css-pseudo-4/#first-letter-pseudo: the first letter (with the punctuation around
            // it) gets its own box, inline or floated, styled by ::first-letter.
            _firstLetter = null;
            if (start > 0)
                RunOf(Container).AddText(text[..start], style);
            var display = pending.Style.Box.Float != FloatSide.None ? Display.Block : Display.Inline;
            var frame = OpenBox(pending.Element, pending.Style, display, PseudoElement.FirstLetter);
            Push(frame);
            RunOf(Container).AddText(text.Substring(start, length), pending.Style);
            Finish(Pop());
            if (start + length < text.Length)
                RunOf(Container).AddText(text[(start + length)..], style);
            return;
        }
        RunOf(Container).AddText(text, style);
    }

    /// <summary>
    /// Where the first letter is in a text: after any leading white space, punctuation, then one typographic letter
    /// unit (a grapheme cluster), then punctuation that follows it; null when the text is only white space.
    /// </summary>
    internal static (int Start, int Length)? FirstLetter(string text)
    {
        var start = 0;
        while (start < text.Length && char.IsWhiteSpace(text[start]))
            start++;
        if (start == text.Length)
            return null;
        var end = start;
        while (end < text.Length && IsPunctuation(text, end))
            end += StringInfo.GetNextTextElementLength(text, end);
        if (end < text.Length && !char.IsWhiteSpace(text[end]))
            end += StringInfo.GetNextTextElementLength(text, end);
        while (end < text.Length && IsPunctuation(text, end))
            end += StringInfo.GetNextTextElementLength(text, end);
        return (start, end - start);

        static bool IsPunctuation(string s, int i) => char.GetUnicodeCategory(s, i) is UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation
            or UnicodeCategory.OtherPunctuation;
    }

    private void AddPseudo(ElementNode element, PseudoElement pseudo)
    {
        var style = element.PseudoStyle(pseudo);
        if (style is null || style.Box.Display == Display.None || style.Generated.Content.Kind != ContentKind.Items)
            return;

        _counters.Apply(element, style.Generated.CounterReset, style.Generated.CounterIncrement, style.Generated.CounterSet, false);
        var text = ContentText(element, style);
        var display = style.Box.Display;
        if (Container.Kind == FrameKind.Flex || style.Box.Float != FloatSide.None || style.Box.Position is Position.Absolute or Position.Fixed)
            display = Blockified(display);
        var frame = OpenBox(element, style, display == Display.Contents ? Display.Inline : display, pseudo);
        Push(frame);
        AddText(text, style);
        Finish(Pop());
    }

    // https://www.w3.org/TR/css-content-3/#content-property: strings, attr(), counters and quotes.
    private string ContentText(ElementNode element, ComputedStyle style)
    {
        var text = new StringBuilder();
        // quotes: auto is English: “ ” then ‘ ’ for nested quotations; deeper ones repeat the last pair.
        var pairs = style.Quotes.Pairs ?? [("\u201C", "\u201D"), ("\u2018", "\u2019")];
        foreach (var item in style.Generated.Content.Items)
        {
            switch (item)
            {
                case ContentString s:
                    text.Append(s.Text);
                    break;
                case ContentAttr a:
                    text.Append(element.GetAttribute(a.Name) ?? "");
                    break;
                case ContentCounter c:
                    text.Append(CounterStyles.Format(_counters.Value(c.Name, element), c.Style));
                    break;
                case ContentCounters c:
                    text.Append(string.Join(c.Separator, _counters.Values(c.Name, element).Select(v => CounterStyles.Format(v, c.Style))));
                    break;
                case ContentQuote q:
                    if (q.Open)
                    {
                        if (q.Emit && pairs.Count > 0)
                            text.Append(pairs[Math.Min(_quoteDepth, pairs.Count - 1)].Open);
                        _quoteDepth++;
                    }
                    else if (_quoteDepth > 0)
                    {
                        _quoteDepth--;
                        if (q.Emit && pairs.Count > 0)
                            text.Append(pairs[Math.Min(_quoteDepth, pairs.Count - 1)].Close);
                    }
                    break;
            }
        }
        return text.ToString();
    }

    // https://www.w3.org/TR/css-lists-3/#marker-pseudo: outside markers are boxes of their own, inside ones inline text.
    // A list-style-image that loads is the marker instead of the list-style-type text, followed by a space like the
    // symbolic markers (https://www.w3.org/TR/css-lists-3/#image-markers).
    // ponytail: gradient list-style-images fall back to the list-style-type marker.
    private void AddMarker(ElementNode element)
    {
        var style = element.PseudoStyle(PseudoElement.Marker) ?? element.ComputedStyle()!;
        var image = style.Generated.Content.Kind != ContentKind.Items && style.Text.ListStyleImage is UrlImage url
            ? _images?.Load(url.Url, "list-style-image") : null;
        var imageBox = image is null ? null : new ReplacedBox(style, element, ReplacedKind.Image) { IsAtomicInline = true, Image = image };
        var text = imageBox is not null ? " "
            : style.Generated.Content.Kind == ContentKind.Items ? ContentText(element, style)
            : CounterStyles.Marker(_counters.Value("list-item", element), style.Text.ListStyleType);
        if (text.Length == 0)
            return;

        var container = Container;
        if (style.Text.ListStylePosition == ListStylePosition.Inside && imageBox is null && style.Generated.Content.Kind != ContentKind.Items
            && style.Text.ListStyleType.CounterStyle is "disclosure-closed" or "disclosure-open")
        {
            // A disclosure triangle is a shape of its own, sized by the font (InlineLayout.SymbolFragment).
            var symbol = style.Text.ListStyleType.CounterStyle == "disclosure-open" ? ListSymbol.DisclosureOpen : ListSymbol.DisclosureClosed;
            RunOf(container).AddAtomic(new MarkerBox(style, element, text) { Symbol = symbol });
        }
        else if (style.Text.ListStylePosition == ListStylePosition.Inside)
        {
            var marker = new InlineBox(style, element, PseudoElement.Marker);
            var run = RunOf(container);
            run.Open(marker);
            if (imageBox is not null)
                run.AddAtomic(imageBox);
            run.AddText(text, style, preserve: true);
            run.Close(marker);
        }
        else if (container.Box is BlockContainerBox block)
        {
            var symbol = imageBox is null && style.Generated.Content.Kind != ContentKind.Items
                ? style.Text.ListStyleType.CounterStyle switch { "disc" => ListSymbol.Disc, "circle" => ListSymbol.Circle, "square" => ListSymbol.Square, _ => (ListSymbol?)null }
                : null;
            block.Marker = new MarkerBox(style, element, text) { Parent = block, Image = imageBox, Symbol = symbol };
            if (imageBox is not null)
                imageBox.Parent = block.Marker;
        }
    }

    // HTML list attributes (https://html.spec.whatwg.org/multipage/grouping-content.html#the-ol-element): ol start sets
    // where list-item counting begins, li value sets the item's number.
    private static (IReadOnlyList<CounterChange> Reset, IReadOnlyList<CounterChange> Set) ListAttributes(ElementNode element, ComputedStyle style)
    {
        var reset = style.Generated.CounterReset;
        var set = style.Generated.CounterSet;
        if (IsHtml(element, "ol") && int.TryParse(element.GetAttribute("start"), out var start))
        {
            reset = reset.Select(c => c.Name == "list-item" ? c with { Value = c.Reversed ? start + 1 : start - 1 } : c).ToList();
        }
        if (IsHtml(element, "li") && element.Parent is ElementNode { LocalName: "ol" } && int.TryParse(element.GetAttribute("value"), out var value)
            && !set.Any(c => c.Name == "list-item"))
        {
            set = [.. set, new CounterChange("list-item", value)];
        }
        return (reset, set);
    }

    // HTML: ol reversed without start counts down from its number of list items (https://html.spec.whatwg.org/multipage/grouping-content.html#the-ol-element).
    private static int ReversedStart(ElementNode element, string counter) =>
        counter == "list-item"
            ? element.Children.OfType<ElementNode>().Count(e => e.ComputedStyle()?.Box.Display == Display.ListItem) + 1
            : 0;

    // ---------------------------------------------------------------- helpers

    private static bool IsHtml(ElementNode element, string name) => element.Name.Namespace == Namespaces.Html && element.LocalName == name;

    /// <summary>
    /// The image an img element shows (https://html.spec.whatwg.org/multipage/images.html#select-an-image-source):
    /// among srcset's density candidates and src as 1x, the smallest density at least the device's, else the largest.
    /// </summary>
    // ponytail: width descriptors (with sizes) and picture's source elements are not chosen from; src is used then.
    private static (string? Url, float Density) ImageSource(ElementNode img, float deviceScale)
    {
        var candidates = new List<(string Url, float Density)>();
        if (img.GetAttribute("srcset") is { } srcset)
            candidates.AddRange(SrcsetCandidates(srcset));
        if (img.GetAttribute("src") is { Length: > 0 } src && !candidates.Any(c => c.Density == 1))
            candidates.Add((src, 1));
        if (candidates.Count == 0)
            return (null, 1);
        var enough = candidates.Where(c => c.Density >= deviceScale).OrderBy(c => c.Density).ToList();
        return enough.Count > 0 ? enough[0] : candidates.MaxBy(c => c.Density);
    }

    // https://html.spec.whatwg.org/multipage/images.html#parse-a-srcset-attribute: a URL runs to the next whitespace
    // (so data: URLs keep their commas); descriptors run to the next comma. Only x descriptors are kept.
    private static IEnumerable<(string Url, float Density)> SrcsetCandidates(string srcset)
    {
        var i = 0;
        while (true)
        {
            while (i < srcset.Length && (char.IsWhiteSpace(srcset[i]) || srcset[i] == ','))
                i++;
            if (i >= srcset.Length)
                yield break;
            var start = i;
            while (i < srcset.Length && !char.IsWhiteSpace(srcset[i]))
                i++;
            var url = srcset[start..i];
            var descriptors = "";
            if (url.EndsWith(','))
            {
                url = url.TrimEnd(',');
            }
            else
            {
                start = i;
                while (i < srcset.Length && srcset[i] != ',')
                    i++;
                descriptors = srcset[start..i].Trim();
            }
            if (descriptors.Length == 0)
                yield return (url, 1);
            else if (descriptors.EndsWith('x') && !descriptors.Contains(' ')
                     && float.TryParse(descriptors[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var density)
                     && density > 0)
                yield return (url, density);
        }
    }

    // Elements whose content comes from outside CSS (docs/study/05-box-tree.md, replaced elements).
    private static ReplacedKind? ReplacedKindOf(ElementNode element)
    {
        // The outermost svg element is a replaced box; SVG rendering itself arrives in M2 (study 13).
        if (element.Name.Namespace == Namespaces.Svg)
            return element.LocalName == "svg" && (element.Parent as ElementNode)?.Name.Namespace != Namespaces.Svg ? ReplacedKind.Svg : null;
        if (element.Name.Namespace != Namespaces.Html)
            return null;
        return element.LocalName switch
        {
            "img" => ReplacedKind.Image,
            "canvas" => ReplacedKind.Canvas,
            "video" or "audio" => ReplacedKind.Media,
            "iframe" or "embed" or "object" => ReplacedKind.Frame,
            "input" or "select" or "textarea" or "progress" or "meter" => ReplacedKind.FormControl,
            _ => null,
        };
    }

    /// <summary>
    /// The text a text-like form control shows in its static state: a text input's value (its placeholder when empty,
    /// a bullet per character for passwords), a textarea's text, a single-line select's selected option (else its
    /// first); null for other elements, which keep their replaced box.
    /// </summary>
    // ponytail: placeholders are drawn in the control's own colour.
    private static string? ControlText(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html)
            return null;
        switch (element.LocalName)
        {
            case "input":
                var type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
                if (type is not ("text" or "email" or "search" or "url" or "tel" or "password" or "number"))
                    return null;
                var value = element.GetAttribute("value") ?? "";
                if (value.Length == 0)
                    return element.GetAttribute("placeholder") ?? "";
                return type == "password" ? new string('•', value.Length) : value;
            case "textarea":
                return element.TextContent ?? "";
            case "select" when element.GetAttribute("multiple") is null && (!int.TryParse(element.GetAttribute("size"), out var size) || size <= 1):
                var options = Descendants(element).Where(e => e.LocalName == "option").ToList();
                var chosen = options.LastOrDefault(o => o.GetAttribute("selected") is not null) ?? options.FirstOrDefault();
                return chosen is null ? "" : string.Join(" ", (chosen.GetAttribute("label") ?? chosen.TextContent ?? "").Split(ElementNode.AsciiWhitespace, StringSplitOptions.RemoveEmptyEntries));
            default:
                return null;
        }

        static IEnumerable<ElementNode> Descendants(ElementNode root)
        {
            for (Node? node = root.FirstChild; node is not null && node != root; node = node.NextInTree(root))
            {
                if (node is ElementNode e)
                    yield return e;
            }
        }
    }

    private static bool IsInlineLevel(Display display) =>
        display is Display.Inline or Display.InlineBlock or Display.InlineFlex or Display.InlineGrid or Display.InlineTable;

    // https://www.w3.org/TR/css-display-3/#blockify
    private static Display Blockified(Display display) => display switch
    {
        Display.Inline or Display.InlineBlock or Display.Ruby or Display.RubyText => Display.Block,
        Display.InlineFlex => Display.Flex,
        Display.InlineGrid => Display.Grid,
        Display.InlineTable => Display.Table,
        Display.TableRowGroup or Display.TableHeaderGroup or Display.TableFooterGroup or Display.TableRow or Display.TableCell
            or Display.TableColumnGroup or Display.TableColumn or Display.TableCaption => Display.Block,
        _ => display,
    };

    private static TablePart PartOf(Display display) => display switch
    {
        Display.TableRowGroup => TablePart.RowGroup,
        Display.TableHeaderGroup => TablePart.HeaderGroup,
        Display.TableFooterGroup => TablePart.FooterGroup,
        Display.TableRow => TablePart.Row,
        Display.TableCell => TablePart.Cell,
        Display.TableColumnGroup => TablePart.ColumnGroup,
        Display.TableColumn => TablePart.Column,
        _ => TablePart.Caption,
    };

    /// <summary>An anonymous box's style: inherited from its parent box, initial otherwise, with the given display.</summary>
    private static ComputedStyle AnonymousStyle(ComputedStyle parent, Display display)
    {
        var keyword = display switch
        {
            Display.TableRow => "table-row",
            Display.TableCell => "table-cell",
            Display.Table => "table",
            _ => "block",
        };
        return StyleBuilder.Compute(new Dictionary<PropertyId, CssValue> { [PropertyId.Display] = new KeywordValue(keyword) },
            new ComputeContext(parent, parent.Font.Size, 0, 0));
    }

    /// <summary>The width at the end of a drop-down select that its arrow is drawn in (study 15).</summary>
    internal const float SelectArrowWidth = 16;

    // The inner box of a drop-down select: 1px above and below the text, 4px before it, and the arrow after it.
    private static ComputedStyle SelectInnerStyle(ComputedStyle select)
    {
        static LengthValue Px(float px) => new(new Length(px, LengthUnit.Px));
        var (start, end) = (Px(4), Px(1 + SelectArrowWidth));
        var rtl = select.Text.Direction == Direction.Rtl;
        return StyleBuilder.Compute(new Dictionary<PropertyId, CssValue>
        {
            [PropertyId.Display] = new KeywordValue("block"),
            [PropertyId.PaddingTop] = Px(1),
            [PropertyId.PaddingBottom] = Px(1),
            [PropertyId.PaddingLeft] = rtl ? end : start,
            [PropertyId.PaddingRight] = rtl ? start : end,
        }, new ComputeContext(select, select.Font.Size, 0, 0));
    }

    // ---------------------------------------------------------------- table fix-up

    private static bool IsPart(Box box, params TablePart[] parts) => box is TablePartBox p && parts.Contains(p.Part);

    private static bool IsRowGroup(Box box) => IsPart(box, TablePart.RowGroup, TablePart.HeaderGroup, TablePart.FooterGroup);

    private static bool IsProperTableChild(Box box) =>
        IsRowGroup(box) || IsPart(box, TablePart.Row, TablePart.Caption, TablePart.ColumnGroup, TablePart.Column);

    // https://www.w3.org/TR/css-tables-3/#fixup-algorithm (steps 1–3, simplified to the cases box building produces).
    private static List<Box> TableFixUp(Box parent, List<Box> children)
    {
        var part = (parent as TablePartBox)?.Part;
        switch (part)
        {
            case TablePart.Column:
                return []; // columns have no content
            case TablePart.ColumnGroup:
                return children.Where(c => IsPart(c, TablePart.Column)).ToList();
            case TablePart.Table:
                children = Wrap(parent, children, c => !IsProperTableChild(c), Display.TableRow);
                break;
            case TablePart.RowGroup or TablePart.HeaderGroup or TablePart.FooterGroup:
                children = Wrap(parent, children, c => !IsPart(c, TablePart.Row), Display.TableRow);
                break;
            case TablePart.Row:
                children = Wrap(parent, children, c => !IsPart(c, TablePart.Cell), Display.TableCell);
                break;
        }

        // Missing parents: cells outside rows get a row; rows, row groups, captions and columns outside a table get a table.
        if (part != TablePart.Row)
            children = Wrap(parent, children, c => IsPart(c, TablePart.Cell), Display.TableRow);
        if (part != TablePart.Table && !(part is TablePart.RowGroup or TablePart.HeaderGroup or TablePart.FooterGroup))
            children = Wrap(parent, children, IsProperTableChild, Display.Table);
        return children;
    }

    private static List<Box> Wrap(Box parent, List<Box> children, Func<Box, bool> needsWrapping, Display wrapper)
    {
        if (!children.Any(needsWrapping))
            return children;
        var result = new List<Box>();
        List<Box>? run = null;
        foreach (var child in children)
        {
            if (!needsWrapping(child))
            {
                Flush();
                result.Add(child);
                continue;
            }
            (run ??= []).Add(child);
        }
        Flush();
        return result;

        void Flush()
        {
            if (run is null)
                return;
            var style = AnonymousStyle(parent.Style, wrapper);
            Box outer;
            TablePartBox inner;
            if (wrapper == Display.Table)
            {
                outer = new TableWrapperBox(style, null);
                inner = new TablePartBox(style, null, TablePart.Table);
                outer.Add(inner);
            }
            else
            {
                inner = new TablePartBox(style, null, wrapper == Display.TableRow ? TablePart.Row : TablePart.Cell);
                outer = inner;
            }
            foreach (var child in TableFixUp(inner, run))
                inner.Add(child);
            result.Add(outer);
            run = null;
        }
    }

    // ---------------------------------------------------------------- inline runs

    /// <summary>
    /// Builds one inline formatting context: items over a text buffer, with white space processed (CSS Text 3 §4.1.1)
    /// across element boundaries.
    /// </summary>
    private sealed class InlineRun(BoxTreeBuilder builder, Frame container)
    {
        private readonly StringBuilder _text = new();
        private readonly List<InlineBox> _open = [];
        private bool _afterCollapsibleSpace = true; // a space at the start of a paragraph collapses away (phase II)
        private bool _hasContent;

        public InlineFormattingContext Context { get; } = new();

        public bool IsEmpty => Context.Items.Count == 0;

        /// <summary>Only collapsible white space and nothing else: generates no boxes between blocks.</summary>
        public bool IsWhitespaceOnly => !_hasContent && Context.Items.All(i => i.Kind == InlineItemKind.Text);

        private bool _inSegments;

        private void EnsureSegment()
        {
            if (_inSegments)
                return;
            _inSegments = true;
            container.Segments.Add(this);
        }

        /// <summary>Finishes this run; later inline content starts a new one.</summary>
        public void EndSegment()
        {
            if (_inSegments || !IsEmpty)
            {
                EnsureSegment();
                Context.Text = _text.ToString();
            }
            container.Run = null;
        }

        public void Open(InlineBox box)
        {
            EnsureSegment();
            _open.Add(box);
            Context.Items.Add(new InlineItem(InlineItemKind.OpenBox, _text.Length, 0, box, box.Style));
            _hasContent = true;
        }

        public void Close(InlineBox box)
        {
            var index = _open.LastIndexOf(box);
            if (index >= 0)
                _open.RemoveAt(index);
            Context.Items.Add(new InlineItem(InlineItemKind.CloseBox, _text.Length, 0, box, box.Style));
        }

        public void AddAtomic(Box box)
        {
            EnsureSegment();
            Context.Items.Add(new InlineItem(InlineItemKind.Atomic, _text.Length, 0, box, box.Style));
            _afterCollapsibleSpace = false;
            _hasContent = true;
        }

        public void AddOutOfFlow(Box box)
        {
            EnsureSegment();
            Context.Items.Add(new InlineItem(box.IsFloat ? InlineItemKind.Float : InlineItemKind.OutOfFlow, _text.Length, 0, box, box.Style));
            _hasContent = true;
        }

        public void AddForcedBreak(ComputedStyle style)
        {
            EnsureSegment();
            TrimTrailingCollapsibleSpace();
            Context.Items.Add(new InlineItem(InlineItemKind.ForcedBreak, _text.Length, 0, null, style));
            _afterCollapsibleSpace = true;
            _hasContent = true;
        }

        public void AddBreakOpportunity(ComputedStyle style)
        {
            EnsureSegment();
            Context.Items.Add(new InlineItem(InlineItemKind.BreakOpportunity, _text.Length, 0, null, style));
        }

        /// <summary>
        /// Appends text with phase I white space processing for its style. Collapsible runs of spaces, tabs and
        /// segment breaks become one space (also across element boundaries); preserved segment breaks become
        /// forced breaks.
        /// </summary>
        // ponytail: segment breaks between East Asian characters should vanish instead of becoming a space
        // (CSS Text 3 §4.1.3); that needs the Unicode width tables from the typography stage.
        public void AddText(string text, ComputedStyle style, bool preserve = false)
        {
            EnsureSegment();
            var mode = preserve ? WhiteSpaceCollapse.Preserve : style.Text.WhiteSpaceCollapse;
            var start = _text.Length;
            foreach (var raw in text)
            {
                var c = raw == '\r' ? '\n' : raw;
                switch (mode)
                {
                    case WhiteSpaceCollapse.Collapse:
                        if (c is ' ' or '\t' or '\n')
                        {
                            if (!_afterCollapsibleSpace)
                                _text.Append(' ');
                            _afterCollapsibleSpace = true;
                            continue;
                        }
                        break;
                    case WhiteSpaceCollapse.PreserveBreaks:
                        if (c == '\n')
                        {
                            Flush(start, style);
                            AddForcedBreak(style);
                            start = _text.Length;
                            continue;
                        }
                        if (c is ' ' or '\t')
                        {
                            if (!_afterCollapsibleSpace)
                                _text.Append(' ');
                            _afterCollapsibleSpace = true;
                            continue;
                        }
                        break;
                    case WhiteSpaceCollapse.PreserveSpaces:
                        if (c is '\n' or '\t')
                            c = ' ';
                        break;
                    default:
                        if (c == '\n')
                        {
                            Flush(start, style);
                            Context.Items.Add(new InlineItem(InlineItemKind.ForcedBreak, _text.Length, 0, null, style));
                            _hasContent = true;
                            start = _text.Length;
                            continue;
                        }
                        break;
                }
                _text.Append(style.TextSpacing.Transform == TextTransform.None ? c : Transform(c, style.TextSpacing.Transform, _text.Length > 0 ? _text[^1] : ' '));
                _afterCollapsibleSpace = false;
                _hasContent = true;
            }
            Flush(start, style);
        }

        /// <summary>text-transform on one character, after white space processing (https://www.w3.org/TR/css-text-3/#text-transform-property).</summary>
        // ponytail: one-to-one case mapping, and a word starts after anything but a letter, digit, mark or apostrophe;
        // full case mapping (SpecialCasing) and UAX #29 word boundaries arrive with the generated case tables.
        private static char Transform(char c, TextTransform transform, char previous) => transform switch
        {
            TextTransform.Uppercase => char.ToUpperInvariant(c),
            TextTransform.Lowercase => char.ToLowerInvariant(c),
            TextTransform.Capitalize when !(char.IsLetterOrDigit(previous) || previous is '\'' or '\u2019'
                                             || char.GetUnicodeCategory(previous) is System.Globalization.UnicodeCategory.NonSpacingMark
                                                 or System.Globalization.UnicodeCategory.SpacingCombiningMark) => char.ToUpperInvariant(c),
            // Printable ASCII to its full-width form (U+FF01 to U+FF5E).
            TextTransform.FullWidth when c is >= '!' and <= '~' => (char)(c - '!' + 0xFF01),
            _ => c,
        };

        private void Flush(int start, ComputedStyle style)
        {
            if (_text.Length > start)
                Context.Items.Add(new InlineItem(InlineItemKind.Text, start, _text.Length - start, null, style));
        }

        // A collapsible space before a preserved break is removed.
        private void TrimTrailingCollapsibleSpace()
        {
            if (_text.Length == 0 || _text[^1] != ' ' || Context.Items.Count == 0 || Context.Items[^1] is not { Kind: InlineItemKind.Text } last
                || last.Style.Text.WhiteSpaceCollapse is not (WhiteSpaceCollapse.Collapse or WhiteSpaceCollapse.PreserveBreaks)
                || last.Start + last.Length != _text.Length)
                return;
            _text.Length--;
            Context.Items[^1] = last with { Length = last.Length - 1 };
            if (Context.Items[^1].Length == 0)
                Context.Items.RemoveAt(Context.Items.Count - 1);
        }

        /// <summary>A block inside inline boxes: close them here (as continuations), place the block, reopen them after.</summary>
        public void SplitAround(Box block)
        {
            var open = _open.ToList();
            foreach (var box in Enumerable.Reverse(open))
                Context.Items.Add(new InlineItem(InlineItemKind.CloseBox, _text.Length, 0, box, box.Style, Continuation: true));
            EndSegment();
            container.Segments.Add(block);
            var next = builder.RunOf(container);
            foreach (var box in open)
            {
                next.EnsureSegment();
                next._open.Add(box);
                next.Context.Items.Add(new InlineItem(InlineItemKind.OpenBox, 0, 0, box, box.Style, Continuation: true));
            }
        }
    }
}

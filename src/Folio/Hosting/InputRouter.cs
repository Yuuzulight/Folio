using System.Numerics;
using Folio.Dom;
using Folio.Painting;

namespace Folio;

/// <summary>
/// Routes host-agnostic input events to the document: tracks hover, active and focus states, and executes default
/// actions (link activation, keyboard navigation, scrolling). In M4, DOM events will be dispatched to scripts first.
/// </summary>
public sealed class InputRouter
{
    private readonly Document _document;
    private ElementNode? _hovered;
    private ElementNode? _active;
    private ElementNode? _focused = null;

    internal InputRouter(Document document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    private string _currentCursor = "default";
    private string? _currentTooltip = null;

    /// <summary>The element currently hovered under the pointer, or null.</summary>
    public Element? HoveredElement => _hovered is not null ? _document.Wrap(_hovered) : null;

    /// <summary>The element currently pressed/active, or null.</summary>
    public Element? ActiveElement => _active is not null ? _document.Wrap(_active) : null;

    /// <summary>The element currently focused, or null.</summary>
    public Element? FocusedElement => _focused is not null ? _document.Wrap(_focused) : null;

    /// <summary>Sets the focused element, updating :focus state.</summary>
    public void Focus(Element? element)
    {
        var targetNode = element?.Node;
        if (_focused == targetNode)
            return;

        if (_focused is not null)
            SetElementAndAncestorsState(_focused, NodeFlags.Focus | NodeFlags.FocusVisible, false);

        _focused = targetNode;

        if (_focused is not null)
            SetElementAndAncestorsState(_focused, NodeFlags.Focus | NodeFlags.FocusVisible, true);

        _document.Update();
    }

    /// <summary>The current CSS cursor keyword active under the pointer.</summary>
    public string CurrentCursor => _currentCursor;

    /// <summary>The current tooltip text active under the pointer, if any.</summary>
    public string? CurrentTooltip => _currentTooltip;

    internal ElementNode? HoveredNode => _hovered;
    internal ElementNode? ActiveNode => _active;
    internal ElementNode? FocusedNode => _focused;

    /// <summary>A link was activated by mouse click or keyboard.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>The active CSS cursor changed under the pointer.</summary>
    public event EventHandler<CursorChangedEventArgs>? CursorChanged;

    /// <summary>The active tooltip text changed under the pointer.</summary>
    public event EventHandler<TooltipChangedEventArgs>? TooltipChanged;

    private sealed record ScrollbarDragState(
        ElementNode Scroller,
        bool Vertical,
        bool IsThumb,
        float InitialPointerPos,
        float InitialScrollOffset,
        float TrackStart,
        float TrackLength,
        float ThumbLength,
        float MaxScroll);

    private ScrollbarDragState? _scrollbarDrag;

    /// <summary>A pointer moved over the document.</summary>
    public void HandlePointerMove(PointerEvent e)
    {
        if (_scrollbarDrag is { } drag)
        {
            if (drag.IsThumb && drag.TrackLength > drag.ThumbLength && drag.MaxScroll > 0)
            {
                var currentPos = drag.Vertical ? e.Position.Y : e.Position.X;
                var deltaPointer = currentPos - drag.InitialPointerPos;
                var availableTrack = drag.TrackLength - drag.ThumbLength;
                var scrollDelta = deltaPointer / availableTrack * drag.MaxScroll;
                var targetOffset = drag.InitialScrollOffset + scrollDelta;

                if (drag.Vertical)
                    _document.ScrollTo(drag.Scroller == _document.Node.DocumentElement ? _document.DocumentElement! : _document.Wrap(drag.Scroller), _document.GetScrollLeft(drag.Scroller), targetOffset);
                else
                    _document.ScrollTo(drag.Scroller == _document.Node.DocumentElement ? _document.DocumentElement! : _document.Wrap(drag.Scroller), targetOffset, _document.GetScrollTop(drag.Scroller));
            }
            return;
        }

        var target = _document.ElementAt(e.Position.X, e.Position.Y);
        if (UpdateHover(target))
            _document.Update();

        UpdateCursorAndTooltip(e.Position.X, e.Position.Y);
    }

    /// <summary>A pointer button was pressed down.</summary>
    public void HandlePointerDown(PointerEvent e)
    {
        // 1. Check if clicking on a scrollbar track or thumb of any scroll container under pointer
        if (e.Button == PointerButton.Primary && HitTestScrollbar(e.Position) is { } hitScrollbar)
        {
            if (hitScrollbar.IsThumb)
            {
                _scrollbarDrag = hitScrollbar;
                return;
            }
            else
            {
                // Track click: page in direction of click relative to thumb
                var (cw, ch, sw, sh) = _document.GetScrollMetricsForNode(hitScrollbar.Scroller);
                var pageStep = hitScrollbar.Vertical ? Math.Max(1, ch - 40f) : Math.Max(1, cw - 40f);
                var clickPos = hitScrollbar.Vertical ? e.Position.Y : e.Position.X;
                var currentThumbPos = hitScrollbar.TrackStart + (hitScrollbar.MaxScroll > 0 && hitScrollbar.TrackLength > hitScrollbar.ThumbLength
                    ? hitScrollbar.InitialScrollOffset / hitScrollbar.MaxScroll * (hitScrollbar.TrackLength - hitScrollbar.ThumbLength) : 0);

                var step = clickPos < currentThumbPos ? -pageStep : pageStep;
                var delta = hitScrollbar.Vertical ? new Vector2(0, step) : new Vector2(step, 0);
                _document.ScrollBy(hitScrollbar.Scroller, delta);
                return;
            }
        }

        var target = _document.ElementAt(e.Position.X, e.Position.Y);
        var changed = UpdateHover(target);
        if (e.Button == PointerButton.Primary && target is not null)
        {
            _active = target;
            SetElementAndAncestorsState(_active, NodeFlags.Active, true);
            changed = true;
        }

        if (changed)
            _document.Update();
    }

    /// <summary>A pointer button was released.</summary>
    public void HandlePointerUp(PointerEvent e)
    {
        if (_scrollbarDrag is not null)
        {
            _scrollbarDrag = null;
            return;
        }

        var target = _document.ElementAt(e.Position.X, e.Position.Y);
        var activated = _active;
        var changed = false;
        if (_active is not null)
        {
            SetElementAndAncestorsState(_active, NodeFlags.Active, false);
            _active = null;
            changed = true;
        }

        if (changed)
            _document.Update();

        if (e.Button == PointerButton.Primary && activated is not null && target is not null)
        {
            // If mouse down and up both happen within the same link element (or child), activate it.
            if (FindLink(activated) is { } startLink && FindLink(target) is { } endLink && startLink == endLink)
            {
                ActivateLink(startLink, e.Modifiers);
            }
        }
    }

    /// <summary>The pointer left the document view.</summary>
    public void HandlePointerLeave()
    {
        _scrollbarDrag = null;
        var changed = UpdateHover(null);
        if (_active is not null)
        {
            SetElementAndAncestorsState(_active, NodeFlags.Active, false);
            _active = null;
            changed = true;
        }

        if (changed)
            _document.Update();

        UpdateCursorAndTooltip(null, null);
    }

    /// <summary>Mouse wheel or touchpad scroll event.</summary>
    public void HandleWheel(WheelEvent e)
    {
        // 1. Convert delta according to DeltaMode
        var delta = e.Delta;
        switch (e.DeltaMode)
        {
            case WheelDeltaMode.Line:
                // Standard default line height is ~16-20px (CSSOM / UI Events standard recommendation is 16px or line height)
                delta *= 16f;
                break;
            case WheelDeltaMode.Page:
                delta = new Vector2(delta.X * _document.ClientWidth, delta.Y * _document.ClientHeight);
                break;
        }

        // 2. Shift+wheel scrolls horizontally
        if ((e.Modifiers & KeyModifiers.Shift) != 0)
        {
            // If delta was vertical, move it to horizontal
            if (delta.X == 0 && delta.Y != 0)
                delta = new Vector2(delta.Y, 0);
        }

        if (delta == Vector2.Zero)
            return;

        // 3. Find hit element under pointer position
        var hitTarget = _document.ElementAt(e.Position.X, e.Position.Y);
        // Fall back to document root element if hitting empty canvas or nothing
        hitTarget ??= _document.Node.DocumentElement;

        // 4. Route delta from innermost container outward, chaining remaining delta
        RouteWheelDelta(hitTarget, delta);
    }

    private void RouteWheelDelta(ElementNode? start, Vector2 initialDelta)
    {
        var remainingX = initialDelta.X;
        var remainingY = initialDelta.Y;

        for (Node? curr = start; curr is not null; curr = curr.Parent)
        {
            if (curr is not ElementNode el)
                continue;

            if (!_document.IsScrollContainer(el))
                continue;

            // Scroll container encountered.
            // Check overscroll-behavior
            var style = _document.FindFragment(el)?.Box?.Style;
            var obx = style?.Box.OverscrollBehaviorX ?? Style.OverscrollBehavior.Auto;
            var oby = style?.Box.OverscrollBehaviorY ?? Style.OverscrollBehavior.Auto;

            // Attempt to consume remaining delta
            var toConsume = new Vector2(remainingX, remainingY);
            if (toConsume != Vector2.Zero)
            {
                var consumed = _document.ScrollBy(el, toConsume);
                remainingX -= consumed.X;
                remainingY -= consumed.Y;
            }

            // Check if overscroll-behavior prevents chaining on each axis
            if (obx is Style.OverscrollBehavior.Contain or Style.OverscrollBehavior.None)
                remainingX = 0;

            if (oby is Style.OverscrollBehavior.Contain or Style.OverscrollBehavior.None)
                remainingY = 0;

            if (remainingX == 0 && remainingY == 0)
                break;
        }
    }

    /// <summary>Key press.</summary>
    public void HandleKeyDown(KeyEvent e)
    {
        // Keyboard scrolling:
        // Target is the nearest scroll container of the focused element,
        // else the hovered element's nearest scroll container,
        // else the root document element.
        var targetElement = FindTargetScroller(_focused)
            ?? FindTargetScroller(_hovered)
            ?? _document.Node.DocumentElement;

        if (targetElement is null)
            return;

        var (cw, ch, sw, sh) = _document.GetScrollMetricsForNode(targetElement);
        // Default line distance: 40px (common browser keyboard scroll distance for arrows)
        const float LineStep = 40f;
        // Page step: page height minus an overlap of ~40px so user keeps context, or at least 1px
        var pageStepY = Math.Max(1, ch - 40f);
        var pageStepX = Math.Max(1, cw - 40f);

        switch (e.Key)
        {
            case "ArrowDown":
                _document.ScrollBy(targetElement, new Vector2(0, LineStep));
                break;
            case "ArrowUp":
                _document.ScrollBy(targetElement, new Vector2(0, -LineStep));
                break;
            case "ArrowRight":
                _document.ScrollBy(targetElement, new Vector2(LineStep, 0));
                break;
            case "ArrowLeft":
                _document.ScrollBy(targetElement, new Vector2(-LineStep, 0));
                break;
            case "PageDown":
                _document.ScrollBy(targetElement, new Vector2(0, pageStepY));
                break;
            case "PageUp":
                _document.ScrollBy(targetElement, new Vector2(0, -pageStepY));
                break;
            case "Home":
                if ((e.Modifiers & KeyModifiers.Control) != 0 || targetElement == _document.Node.DocumentElement)
                    _document.ScrollTo(targetElement == _document.Node.DocumentElement ? _document.DocumentElement! : _document.Wrap(targetElement), 0, 0);
                else
                    _document.ScrollBy(targetElement, new Vector2(-sw, 0));
                break;
            case "End":
                if ((e.Modifiers & KeyModifiers.Control) != 0 || targetElement == _document.Node.DocumentElement)
                    _document.ScrollTo(targetElement == _document.Node.DocumentElement ? _document.DocumentElement! : _document.Wrap(targetElement), 0, sh);
                else
                    _document.ScrollBy(targetElement, new Vector2(sw, 0));
                break;
            case " " or "Space":
                var spaceDelta = (e.Modifiers & KeyModifiers.Shift) != 0 ? -pageStepY : pageStepY;
                _document.ScrollBy(targetElement, new Vector2(0, spaceDelta));
                break;
        }
    }

    private ElementNode? FindTargetScroller(ElementNode? start)
    {
        for (Node? curr = start; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el && _document.IsScrollContainer(el))
                return el;
        }
        return null;
    }

    public void HandleKeyUp(KeyEvent e) { }

    public void HandleTextInput(TextInputEvent e) { }

    private bool UpdateHover(ElementNode? newHovered)
    {
        if (_hovered == newHovered)
            return false;

        if (_hovered is not null)
            SetElementAndAncestorsState(_hovered, NodeFlags.Hover, false);

        _hovered = newHovered;

        if (_hovered is not null)
            SetElementAndAncestorsState(_hovered, NodeFlags.Hover, true);

        return true;
    }

    private void SetElementAndAncestorsState(ElementNode element, NodeFlags flag, bool on)
    {
        for (Node? node = element; node is not null; node = node.Parent)
        {
            if (node is ElementNode el)
                _document.SetState(el, flag, on);
        }
    }

    private static ElementNode? FindLink(ElementNode element)
    {
        for (Node? node = element; node is not null; node = node.Parent)
        {
            if (node is ElementNode { LocalName: "a" or "area" } link
                && link.Name.Namespace == Namespaces.Html
                && link.GetAttribute("href") is not null)
            {
                return link;
            }
        }
        return null;
    }

    private void ActivateLink(ElementNode link, KeyModifiers modifiers)
    {
        if (link.GetAttribute("href") is not { } href)
            return;
        href = href.Trim();
        Uri? resolved = Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs
            : _document.Options.BaseUri is { } baseUri && Uri.TryCreate(baseUri, href, out var rel) ? rel
            : null;

        if (resolved is not null)
        {
            var args = new LinkActivatedEventArgs(resolved, modifiers);
            LinkActivated?.Invoke(this, args);
        }
    }

    private void UpdateCursorAndTooltip(float? x, float? y)
    {
        var newCursor = x.HasValue && y.HasValue ? _document.CursorAt(x.Value, y.Value) : "default";
        var newTooltip = x.HasValue && y.HasValue ? _document.TitleAt(x.Value, y.Value) : null;

        if (_currentCursor != newCursor)
        {
            _currentCursor = newCursor;
            CursorChanged?.Invoke(this, new CursorChangedEventArgs(newCursor));
        }

        if (_currentTooltip != newTooltip)
        {
            _currentTooltip = newTooltip;
            TooltipChanged?.Invoke(this, new TooltipChangedEventArgs(newTooltip));
        }
    }

    private ScrollbarDragState? HitTestScrollbar(Vector2 point)
    {
        // Check scroll containers under point starting from element at point up through parents,
        // plus document root.
        var target = _document.ElementAt(point.X, point.Y) ?? _document.Node.DocumentElement;
        for (Node? curr = target; curr is not null; curr = curr.Parent)
        {
            if (curr is not ElementNode el || !_document.IsScrollContainer(el))
                continue;

            if (_document.GetElementBounds(el) is not { } bounds)
                continue;

            var (cw, ch, sw, sh) = _document.GetScrollMetricsForNode(el);
            var maxScrollX = Math.Max(0, sw - cw);
            var maxScrollY = Math.Max(0, sh - ch);

            const float BarThickness = 12f;

            // 1. Vertical scrollbar (right side of bounds)
            if (maxScrollY > 0)
            {
                var trackX = bounds.Right - BarThickness;
                if (point.X >= trackX && point.X <= bounds.Right && point.Y >= bounds.Y && point.Y <= bounds.Bottom)
                {
                    var scrollY = _document.GetScrollTop(el);
                    var thumbHeight = Math.Max(20f, bounds.Height * (ch / sh));
                    var trackLength = bounds.Height;
                    var available = Math.Max(0, trackLength - thumbHeight);
                    var thumbTop = bounds.Y + (maxScrollY > 0 ? scrollY / maxScrollY * available : 0);

                    var isThumb = point.Y >= thumbTop && point.Y <= thumbTop + thumbHeight;
                    return new ScrollbarDragState(el, Vertical: true, IsThumb: isThumb,
                        InitialPointerPos: point.Y, InitialScrollOffset: scrollY,
                        TrackStart: bounds.Y, TrackLength: trackLength, ThumbLength: thumbHeight,
                        MaxScroll: maxScrollY);
                }
            }

            // 2. Horizontal scrollbar (bottom side of bounds)
            if (maxScrollX > 0)
            {
                var trackY = bounds.Bottom - BarThickness;
                if (point.X >= bounds.X && point.X <= bounds.Right && point.Y >= trackY && point.Y <= bounds.Bottom)
                {
                    var scrollX = _document.GetScrollLeft(el);
                    var thumbWidth = Math.Max(20f, bounds.Width * (cw / sw));
                    var trackLength = bounds.Width;
                    var available = Math.Max(0, trackLength - thumbWidth);
                    var thumbLeft = bounds.X + (maxScrollX > 0 ? scrollX / maxScrollX * available : 0);

                    var isThumb = point.X >= thumbLeft && point.X <= thumbLeft + thumbWidth;
                    return new ScrollbarDragState(el, Vertical: false, IsThumb: isThumb,
                        InitialPointerPos: point.X, InitialScrollOffset: scrollX,
                        TrackStart: bounds.X, TrackLength: trackLength, ThumbLength: thumbWidth,
                        MaxScroll: maxScrollX);
                }
            }
        }
        return null;
    }
}


using System.Numerics;
using Folio.Dom;

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
    private readonly ElementNode? _focused = null;

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

    /// <summary>A pointer moved over the document.</summary>
    public void HandlePointerMove(PointerEvent e)
    {
        var target = _document.ElementAt(e.Position.X, e.Position.Y);
        if (UpdateHover(target))
            _document.Update();

        UpdateCursorAndTooltip(e.Position.X, e.Position.Y);
    }

    /// <summary>A pointer button was pressed down.</summary>
    public void HandlePointerDown(PointerEvent e)
    {
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
        // Focus navigation and keyboard scrolling arrive with #423/#425.
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
}

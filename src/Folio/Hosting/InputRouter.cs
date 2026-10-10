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

    /// <summary>The element currently hovered under the pointer, or null.</summary>
    public Element? HoveredElement => _hovered is not null ? _document.Wrap(_hovered) : null;

    /// <summary>The element currently pressed/active, or null.</summary>
    public Element? ActiveElement => _active is not null ? _document.Wrap(_active) : null;

    /// <summary>The element currently focused, or null.</summary>
    public Element? FocusedElement => _focused is not null ? _document.Wrap(_focused) : null;

    internal ElementNode? HoveredNode => _hovered;
    internal ElementNode? ActiveNode => _active;
    internal ElementNode? FocusedNode => _focused;

    /// <summary>A link was activated by mouse click or keyboard.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>A pointer moved over the document.</summary>
    public void HandlePointerMove(PointerEvent e)
    {
        var target = _document.ElementAt(e.Position.X, e.Position.Y);
        if (UpdateHover(target))
            _document.Update();
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
    }

    /// <summary>Mouse wheel or touchpad scroll event.</summary>
    public void HandleWheel(WheelEvent e)
    {
        // Handled by scroll containers in #421/#422.
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
}

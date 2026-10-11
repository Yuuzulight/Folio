using System.Numerics;
using Folio.Dom;
using Folio.Interaction;
using Folio.Painting;
using Folio.Style;
using Folio.Typography;

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
    private Selection? _selection;
    private bool _isSelecting;
    private RangePoint? _selectionPivot;

    internal InputRouter(Document document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    private string _currentCursor = "default";
    private string? _currentTooltip = null;

    /// <summary>The current document selection, or null.</summary>
    internal Selection? Selection => _selection;

    /// <summary>The plain text of the active selection, or empty.</summary>
    public string SelectedText => _selection is not null ? RangeSerializer.ToPlainText(_selection.Range) : "";

    /// <summary>The HTML fragment of the active selection, or empty.</summary>
    public string SelectedHtml => _selection is not null ? RangeSerializer.ToHtml(_selection.Range) : "";

    /// <summary>The element currently hovered under the pointer, or null.</summary>
    public Element? HoveredElement => _hovered is not null ? _document.Wrap(_hovered) : null;

    /// <summary>The element currently pressed/active, or null.</summary>
    public Element? ActiveElement => _active is not null ? _document.Wrap(_active) : null;

    /// <summary>The element currently focused, or null.</summary>
    public Element? FocusedElement => _focused is not null ? _document.Wrap(_focused) : null;

    /// <summary>Sets the focused element, updating :focus state.</summary>
    public void Focus(Element? element) => Focus(element?.Node, visible: true);

    internal void Focus(ElementNode? targetNode, bool visible)
    {
        if (targetNode is not null && IsDisabled(targetNode))
            targetNode = null;

        if (_focused == targetNode)
        {
            if (targetNode is not null && visible && (targetNode.Flags & NodeFlags.FocusVisible) == 0)
            {
                SetElementAndAncestorsState(targetNode, NodeFlags.FocusVisible, true);
                _document.Update();
            }
            return;
        }

        if (_focused is not null)
            SetElementAndAncestorsState(_focused, NodeFlags.Focus | NodeFlags.FocusVisible, false);

        _focused = targetNode;

        if (_focused is not null)
        {
            var flags = visible ? (NodeFlags.Focus | NodeFlags.FocusVisible) : NodeFlags.Focus;
            SetElementAndAncestorsState(_focused, flags, true);
            _document.ScrollIntoView(_focused);

            if (IsTextControl(_focused))
            {
                var state = _document.GetOrCreateTextControlState(_focused);
                if (state is not null)
                {
                    _focused.Value = state.Value;
                    _focused.DisplayValue = state.DisplayText;
                    _selection = new Selection(new RangePoint(_focused, state.SelectionStart), new RangePoint(_focused, state.SelectionEnd));
                    EnsureCaretVisible(_focused, state);
                }
            }
        }

        _document.Update();
    }

    /// <summary>The current CSS cursor keyword active under the pointer.</summary>
    public string CurrentCursor => _currentCursor;

    /// <summary>The current tooltip text active under the pointer, if any.</summary>
    public string? CurrentTooltip => _currentTooltip;

    internal ElementNode? HoveredNode => _hovered;
    internal ElementNode? ActiveNode => _active;
    internal ElementNode? FocusedNode => _focused;

    internal bool HasFocusedTextControl => _focused is not null && IsTextControl(_focused);

    internal TextControlState? FocusedControlState => _focused is not null ? _document.GetOrCreateTextControlState(_focused) : null;

    internal static bool IsTextControl(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html)
            return false;
        if (element.LocalName == "textarea")
            return true;
        if (element.LocalName == "input")
        {
            var type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
            return type is "text" or "email" or "search" or "url" or "tel" or "password" or "number";
        }
        return false;
    }

    /// <summary>A link was activated by mouse click or keyboard.</summary>
    public event EventHandler<LinkActivatedEventArgs>? LinkActivated;

    /// <summary>The active CSS cursor changed under the pointer.</summary>
    public event EventHandler<CursorChangedEventArgs>? CursorChanged;

    /// <summary>The active tooltip text changed under the pointer.</summary>
    public event EventHandler<TooltipChangedEventArgs>? TooltipChanged;

    /// <summary>A copy command was requested (e.g. Ctrl+C / Cmd+C).</summary>
    public event EventHandler? CopyRequested;

    /// <summary>A cut command was requested (e.g. Ctrl+X / Cmd+X).</summary>
    public event EventHandler? CutRequested;

    /// <summary>A paste command was requested (e.g. Ctrl+V / Cmd+V).</summary>
    public event EventHandler<PasteEventArgs>? PasteRequested;

    /// <summary>The document selection changed.</summary>
    internal event EventHandler<SelectionChangedEventArgs>? SelectionChanged;

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

        if (_isSelecting && _selectionPivot is { } pivot)
        {
            var hit = _document.HitAt(e.Position.X, e.Position.Y);
            if (hit?.Position is { } pos && IsSelectable(pos.Node))
            {
                if (_focused is not null && IsTextControl(_focused) && (pos.Node == _focused || pos.Node.Parent == _focused))
                {
                    var state = FocusedControlState;
                    if (state is not null)
                    {
                        var anchor = pivot.Node == _focused ? pivot.Offset : state.CaretIndex;
                        state.SetSelectionRange(anchor, pos.Offset, backwards: pos.Offset < anchor);
                        SetSelection(new Selection(new RangePoint(_focused, state.SelectionStart), new RangePoint(_focused, state.SelectionEnd)));
                        EnsureCaretVisible(_focused, state);
                        return;
                    }
                }
                var newFocus = new RangePoint(pos.Node, pos.Offset);
                SetSelection(new Selection(pivot, newFocus));
            }
        }
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

        var hit = _document.HitAt(e.Position.X, e.Position.Y);
        var target = hit?.Node ?? _document.ElementAt(e.Position.X, e.Position.Y);
        var changed = UpdateHover(target);
        if (e.Button == PointerButton.Primary)
        {
            var focusTarget = FindFocusable(target);
            Focus(focusTarget, visible: false);

            if (target is not null)
            {
                _active = target;
                SetElementAndAncestorsState(_active, NodeFlags.Active, true);
                changed = true;
            }

            HandleSelectionPointerDown(e, hit);
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

        _isSelecting = false;

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
        if (_focused is not null && IsTextControl(_focused))
        {
            if (HandleControlKeyDown(e, _focused))
                return;
        }

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

        if (e.Key is "a" or "A" && (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            SelectAll();
            return;
        }

        if (e.Key is "c" or "C" && (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (_selection is not null && !_selection.IsCollapsed)
            {
                CopyRequested?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        if (e.Key is "Tab")
        {
            NavigateFocus((e.Modifiers & KeyModifiers.Shift) != 0);
            return;
        }

        if (e.Key is "Enter" && _focused is not null)
        {
            if (FindLink(_focused) is { } focusedLink)
            {
                ActivateLink(focusedLink, e.Modifiers);
                return;
            }
        }

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

    private void NavigateFocus(bool reverse)
    {
        var elements = GetSequentialFocusableElements();
        if (elements.Count == 0)
            return;

        int currentIndex = _focused is not null ? elements.IndexOf(_focused) : -1;
        int nextIndex;
        if (currentIndex < 0)
        {
            nextIndex = reverse ? elements.Count - 1 : 0;
        }
        else
        {
            nextIndex = reverse ? currentIndex - 1 : currentIndex + 1;
            if (nextIndex < 0)
                nextIndex = elements.Count - 1;
            else if (nextIndex >= elements.Count)
                nextIndex = 0;
        }

        Focus(elements[nextIndex], visible: true);
    }

    internal List<ElementNode> GetSequentialFocusableElements()
    {
        var positive = new List<(ElementNode Node, int Order, int TabIndex)>();
        var zero = new List<ElementNode>();
        var order = 0;

        for (Node? curr = _document.Node; curr is not null; curr = curr.NextInTree(_document.Node))
        {
            if (curr is not ElementNode el)
                continue;

            order++;
            var ti = GetTabIndex(el);
            if (ti is null || ti.Value < 0)
                continue;

            if (ti.Value > 0)
                positive.Add((el, order, ti.Value));
            else
                zero.Add(el);
        }

        positive.Sort((a, b) =>
        {
            var cmp = a.TabIndex.CompareTo(b.TabIndex);
            return cmp != 0 ? cmp : a.Order.CompareTo(b.Order);
        });

        var result = new List<ElementNode>(positive.Count + zero.Count);
        foreach (var (n, _, _) in positive)
            result.Add(n);
        result.AddRange(zero);
        return result;
    }

    internal static ElementNode? FindFocusable(ElementNode? element)
    {
        for (Node? node = element; node is not null; node = node.Parent)
        {
            if (node is ElementNode el && GetTabIndex(el) is not null)
                return el;
        }
        return null;
    }

    internal static int? GetTabIndex(ElementNode element)
    {
        if (IsDisabled(element))
            return null;

        var tabIndexAttr = element.GetAttribute("tabindex");
        if (tabIndexAttr is not null)
        {
            if (int.TryParse(tabIndexAttr.Trim(), out var parsed))
                return parsed;
        }

        // Default focusable HTML elements
        if (element.Name.Namespace == Namespaces.Html)
        {
            var tag = element.LocalName;
            if (tag is "a" or "area" && element.GetAttribute("href") is not null)
                return 0;

            if (tag is "button" or "select" or "textarea")
                return 0;

            if (tag is "input")
            {
                var type = element.GetAttribute("type")?.ToLowerInvariant();
                if (type is not "hidden")
                    return 0;
            }
        }

        return null;
    }

    private static bool IsDisabled(ElementNode element)
    {
        if (element.Name.Namespace != Namespaces.Html)
            return false;

        var isControl = element.LocalName is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset";
        if (!isControl)
            return false;

        if (element.GetAttribute("disabled") is not null)
            return true;

        if (element.LocalName == "option" && element.Parent is ElementNode { LocalName: "optgroup" } group && group.GetAttribute("disabled") is not null)
            return true;

        if (element.LocalName is "optgroup" or "option")
            return false;

        Node child = element;
        for (var ancestor = element.Parent as ElementNode; ancestor is not null; child = ancestor, ancestor = ancestor.Parent as ElementNode)
        {
            if (ancestor.Name.Namespace == Namespaces.Html && ancestor.LocalName == "fieldset" && ancestor.GetAttribute("disabled") is not null)
            {
                var firstLegend = ancestor.Children.OfType<ElementNode>().FirstOrDefault(e => e.Name.Namespace == Namespaces.Html && e.LocalName == "legend");
                if (child != firstLegend)
                    return true;
            }
        }
        return false;
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

    /// <summary>Text input event (e.g. typed character, IME committed string).</summary>
    public void HandleTextInput(TextInputEvent e)
    {
        if (string.IsNullOrEmpty(e.Text) || _focused is null || !IsTextControl(_focused))
            return;

        var state = FocusedControlState;
        if (state is null || state.IsReadOnly || state.IsDisabled)
            return;

        if (state.Insert(e.Text))
        {
            _focused.Value = state.Value;
            _focused.DisplayValue = state.DisplayText;
            SetSelection(new Selection(new RangePoint(_focused, state.SelectionStart), new RangePoint(_focused, state.SelectionEnd)));
            EnsureCaretVisible(_focused, state);
            _document.Update();
        }
    }

    /// <summary>Pastes text into the currently focused text control, or replaces active selection.</summary>
    public bool Paste(string text)
    {
        if (string.IsNullOrEmpty(text) || _focused is null || !IsTextControl(_focused))
            return false;

        var state = FocusedControlState;
        if (state is null || state.IsReadOnly || state.IsDisabled)
            return false;

        if (state.Insert(text))
        {
            _focused.Value = state.Value;
            _focused.DisplayValue = state.DisplayText;
            SetSelection(new Selection(new RangePoint(_focused, state.SelectionStart), new RangePoint(_focused, state.SelectionEnd)));
            EnsureCaretVisible(_focused, state);
            _document.Update();
            return true;
        }

        return false;
    }

    private bool HandleControlKeyDown(KeyEvent e, ElementNode focused)
    {
        var state = FocusedControlState;
        if (state is null)
            return false;

        var ctrl = (e.Modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (e.Modifiers & KeyModifiers.Shift) != 0;

        // Select All inside control
        if (ctrl && e.Key is "a" or "A")
        {
            state.SelectAll();
            SetSelection(new Selection(new RangePoint(focused, 0), new RangePoint(focused, state.Value.Length)));
            EnsureCaretVisible(focused, state);
            return true;
        }

        // Copy inside control
        if (ctrl && e.Key is "c" or "C")
        {
            if (state.HasSelection)
            {
                CopyRequested?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return false;
        }

        // Cut inside control
        if (ctrl && e.Key is "x" or "X")
        {
            if (state.HasSelection && !state.IsReadOnly && !state.IsDisabled)
            {
                CopyRequested?.Invoke(this, EventArgs.Empty);
                CutRequested?.Invoke(this, EventArgs.Empty);
                state.DeleteBackward();
                focused.Value = state.Value;
                focused.DisplayValue = state.DisplayText;
                SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                EnsureCaretVisible(focused, state);
                _document.Update();
                return true;
            }
            return false;
        }

        // Paste inside control
        if (ctrl && e.Key is "v" or "V")
        {
            if (!state.IsReadOnly && !state.IsDisabled)
            {
                var args = new PasteEventArgs();
                PasteRequested?.Invoke(this, args);
                if (!string.IsNullOrEmpty(args.Text))
                {
                    Paste(args.Text);
                    return true;
                }
            }
            return false;
        }

        // Undo / Redo
        if (ctrl && (e.Key is "z" or "Z"))
        {
            if (shift)
            {
                if (state.Redo())
                {
                    focused.Value = state.Value;
                    focused.DisplayValue = state.DisplayText;
                    SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                    EnsureCaretVisible(focused, state);
                    _document.Update();
                    return true;
                }
            }
            else
            {
                if (state.Undo())
                {
                    focused.Value = state.Value;
                    focused.DisplayValue = state.DisplayText;
                    SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                    EnsureCaretVisible(focused, state);
                    _document.Update();
                    return true;
                }
            }
            return true;
        }

        if (ctrl && (e.Key is "y" or "Y"))
        {
            if (state.Redo())
            {
                focused.Value = state.Value;
                focused.DisplayValue = state.DisplayText;
                SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                EnsureCaretVisible(focused, state);
                _document.Update();
                return true;
            }
            return true;
        }

        // Backspace
        if (e.Key is "Backspace")
        {
            if (state.DeleteBackward(byWord: ctrl))
            {
                focused.Value = state.Value;
                focused.DisplayValue = state.DisplayText;
                SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                EnsureCaretVisible(focused, state);
                _document.Update();
            }
            return true;
        }

        // Delete
        if (e.Key is "Delete")
        {
            if (state.DeleteForward(byWord: ctrl))
            {
                focused.Value = state.Value;
                focused.DisplayValue = state.DisplayText;
                SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                EnsureCaretVisible(focused, state);
                _document.Update();
            }
            return true;
        }

        // Enter in textarea
        if (e.Key is "Enter")
        {
            if (state.IsTextArea)
            {
                if (state.Insert("\n"))
                {
                    focused.Value = state.Value;
                    focused.DisplayValue = state.DisplayText;
                    SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                    EnsureCaretVisible(focused, state);
                    _document.Update();
                }
                return true;
            }
            return false;
        }

        // Arrows & Navigation
        if (e.Key is "ArrowLeft")
        {
            state.MoveLeft(extendSelection: shift, byWord: ctrl);
            SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
            EnsureCaretVisible(focused, state);
            return true;
        }

        if (e.Key is "ArrowRight")
        {
            state.MoveRight(extendSelection: shift, byWord: ctrl);
            SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
            EnsureCaretVisible(focused, state);
            return true;
        }

        if (e.Key is "ArrowUp")
        {
            state.MoveUp(extendSelection: shift);
            if (focused.Value != state.Value)
            {
                focused.Value = state.Value;
                focused.DisplayValue = state.DisplayText;
                _document.Update();
            }
            else
            {
                SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                EnsureCaretVisible(focused, state);
            }
            return true;
        }

        if (e.Key is "ArrowDown")
        {
            state.MoveDown(extendSelection: shift);
            if (focused.Value != state.Value)
            {
                focused.Value = state.Value;
                focused.DisplayValue = state.DisplayText;
                _document.Update();
            }
            else
            {
                SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
                EnsureCaretVisible(focused, state);
            }
            return true;
        }

        if (e.Key is "Home")
        {
            state.MoveHome(extendSelection: shift, toDocumentStart: ctrl);
            SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
            EnsureCaretVisible(focused, state);
            return true;
        }

        if (e.Key is "End")
        {
            state.MoveEnd(extendSelection: shift, toDocumentEnd: ctrl);
            SetSelection(new Selection(new RangePoint(focused, state.SelectionStart), new RangePoint(focused, state.SelectionEnd)));
            EnsureCaretVisible(focused, state);
            return true;
        }

        return false;
    }

    internal void EnsureCaretVisible(ElementNode element, TextControlState state)
    {
        if (_document.Page is not { } page)
            return;

        var caretRect = HitTester.CaretRect(page, new TextPosition(element, state.CaretIndex));
        if (caretRect == default)
            return;

        if (_document.GetElementBounds(element) is not { } bounds)
            return;

        if (state.IsTextArea)
        {
            var style = _document.FindFragment(element)?.Box?.Style;
            var padTop = style?.Spacing.PaddingTop.Resolve(bounds.Height) ?? 0;
            var padBottom = style?.Spacing.PaddingBottom.Resolve(bounds.Height) ?? 0;
            var visibleTop = bounds.Y + padTop;
            var visibleBottom = bounds.Bottom - padBottom;

            if (caretRect.Bottom > visibleBottom)
                _document.ScrollBy(element, new Vector2(0, caretRect.Bottom - visibleBottom));
            else if (caretRect.Y < visibleTop)
                _document.ScrollBy(element, new Vector2(0, caretRect.Y - visibleTop));
        }
        else
        {
            var style = _document.FindFragment(element)?.Box?.Style;
            var padLeft = style?.Spacing.PaddingLeft.Resolve(bounds.Width) ?? 0;
            var padRight = style?.Spacing.PaddingRight.Resolve(bounds.Width) ?? 0;
            var visibleLeft = bounds.X + padLeft;
            var visibleRight = bounds.Right - padRight;

            if (caretRect.Right > visibleRight)
                _document.ScrollBy(element, new Vector2(caretRect.Right - visibleRight, 0));
            else if (caretRect.X < visibleLeft)
                _document.ScrollBy(element, new Vector2(caretRect.X - visibleLeft, 0));
        }
    }

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

        // 1. Check if href is an in-document fragment reference
        string? fragment = null;
        if (href.StartsWith('#'))
        {
            fragment = href;
        }

        Uri? resolved = Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs
            : _document.Options.BaseUri is { } baseUri && Uri.TryCreate(baseUri, href, out var rel) ? rel
            : null;

        if (fragment is null && resolved is not null && _document.Options.BaseUri is { } docBase)
        {
            // If resolved URI matches base URI ignoring fragment, it's a same-document navigation
            if (Uri.Compare(resolved, docBase, UriComponents.HttpRequestUrl, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
                && !string.IsNullOrEmpty(resolved.Fragment))
            {
                fragment = resolved.Fragment;
            }
        }

        var handled = false;
        if (resolved is not null)
        {
            var args = new LinkActivatedEventArgs(resolved, modifiers);
            LinkActivated?.Invoke(this, args);
            handled = args.Handled;
        }

        if (!handled && fragment is not null)
        {
            _document.NavigateToFragment(fragment);
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

    private void HandleSelectionPointerDown(PointerEvent e, HitResult? hit)
    {
        if (hit?.Position is not { } pos)
        {
            if ((e.Modifiers & KeyModifiers.Shift) == 0 && e.ClickCount <= 1)
            {
                ClearSelection();
            }
            return;
        }

        if (!IsSelectable(pos.Node))
            return;

        if (_focused is not null && IsTextControl(_focused) && (pos.Node == _focused || pos.Node.Parent == _focused))
        {
            var state = FocusedControlState;
            if (state is not null)
            {
                var offset = Math.Clamp(pos.Offset, 0, state.Value.Length);
                if (e.ClickCount == 2)
                {
                    var wordRange = ExpandToWord(new TextPosition(_focused, offset));
                    state.SetSelectionRange(wordRange.Range.Start.Offset, wordRange.Range.End.Offset);
                }
                else if (e.ClickCount >= 3)
                {
                    state.SelectAll();
                }
                else if ((e.Modifiers & KeyModifiers.Shift) != 0)
                {
                    var anchor = _selectionPivot?.Node == _focused ? _selectionPivot.Value.Offset : state.SelectionStart;
                    state.SetSelectionRange(anchor, offset, backwards: offset < anchor);
                }
                else
                {
                    state.SetSelectionRange(offset, offset);
                    _selectionPivot = new RangePoint(_focused, offset);
                }

                SetSelection(new Selection(new RangePoint(_focused, state.SelectionStart), new RangePoint(_focused, state.SelectionEnd)));
                _isSelecting = true;
                EnsureCaretVisible(_focused, state);
                return;
            }
        }

        var targetPoint = new RangePoint(pos.Node, pos.Offset);

        // Check user-select: all on hit node
        if (GetUserSelect(pos.Node) == UserSelect.All)
        {
            var atomicRange = GetAtomicUnitRange(pos.Node);
            _selectionPivot = atomicRange.Anchor;
            SetSelection(atomicRange);
            _isSelecting = true;
            return;
        }

        // Shift+click extends current selection
        if ((e.Modifiers & KeyModifiers.Shift) != 0 && _selection is not null)
        {
            var anchor = _selectionPivot ?? _selection.Anchor;
            SetSelection(new Selection(anchor, targetPoint));
            _isSelecting = true;
            return;
        }

        // Multi-click handling
        if (e.ClickCount == 2)
        {
            var wordRange = ExpandToWord(pos);
            _selectionPivot = wordRange.Anchor;
            SetSelection(wordRange);
            _isSelecting = true;
            return;
        }

        if (e.ClickCount >= 3)
        {
            var paraRange = ExpandToParagraph(pos);
            _selectionPivot = paraRange.Anchor;
            SetSelection(paraRange);
            _isSelecting = true;
            return;
        }

        // Single click: start drag
        _selectionPivot = targetPoint;
        SetSelection(new Selection(targetPoint, targetPoint));
        _isSelecting = true;
    }

    /// <summary>Sets the selection to the specified range.</summary>
    internal void Select(RangePoint anchor, RangePoint focus)
    {
        _selectionPivot = anchor;
        SetSelection(new Selection(anchor, focus));
    }

    /// <summary>Clears the current selection.</summary>
    public void ClearSelection()
    {
        _selectionPivot = null;
        SetSelection(null);
    }

    /// <summary>Selects all selectable content across the document.</summary>
    public void SelectAll()
    {
        var first = FindFirstSelectablePoint();
        var last = FindLastSelectablePoint();
        if (first is not null && last is not null)
        {
            _selectionPivot = first.Value;
            SetSelection(new Selection(first.Value, last.Value));
        }
    }

    private void SetSelection(Selection? selection)
    {
        if (SelectionEquals(_selection, selection))
            return;

        _selection = selection;
        _document.RepaintDisplayList();
        SelectionChanged?.Invoke(this, new SelectionChangedEventArgs(_selection));
    }

    private static bool SelectionEquals(Selection? a, Selection? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.Anchor.Node == b.Anchor.Node && a.Anchor.Offset == b.Anchor.Offset &&
               a.Focus.Node == b.Focus.Node && a.Focus.Offset == b.Focus.Offset;
    }

    private static bool IsSelectable(Node node)
    {
        for (Node? curr = node; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el)
            {
                var style = el.ComputedStyle();
                if (style is null || style.Box.Display == Display.None)
                    return false;
                if (style.Ui.UserSelect == UserSelect.None)
                    return false;
            }
        }
        return true;
    }

    private static UserSelect GetUserSelect(Node node)
    {
        for (Node? curr = node; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el)
            {
                var style = el.ComputedStyle();
                if (style is not null)
                    return style.Ui.UserSelect;
            }
        }
        return UserSelect.Auto;
    }

    private static Selection GetAtomicUnitRange(Node node)
    {
        ElementNode? container = null;
        for (Node? curr = node; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el && el.ComputedStyle()?.Ui.UserSelect == UserSelect.All)
            {
                container = el;
                break;
            }
        }

        if (container is not null)
        {
            Text? first = null;
            Text? last = null;
            for (Node? n = container; n is not null; n = n.NextInTree(container))
            {
                if (n is Text t && t.Data.Length > 0 && IsSelectable(t))
                {
                    first ??= t;
                    last = t;
                }
            }
            if (first is not null && last is not null)
                return new Selection(new RangePoint(first, 0), new RangePoint(last, last.Data.Length));
        }

        if (node is Text text)
            return new Selection(new RangePoint(text, 0), new RangePoint(text, text.Data.Length));

        return new Selection(new RangePoint(node, 0), new RangePoint(node, 0));
    }

    private static Selection ExpandToWord(TextPosition pos)
    {
        string data;
        if (pos.Node is Text text)
            data = text.Data;
        else if (pos.Node is ElementNode el)
            data = el.DisplayValue ?? el.Value ?? el.GetAttribute("value") ?? "";
        else
            return new Selection(new RangePoint(pos.Node, pos.Offset), new RangePoint(pos.Node, pos.Offset));

        if (data.Length == 0)
            return new Selection(new RangePoint(pos.Node, 0), new RangePoint(pos.Node, 0));

        var boundaries = WordBoundaries.Find(data);
        var offset = Math.Clamp(pos.Offset, 0, data.Length);

        // Find boundary interval [start, end] enclosing offset
        var start = 0;
        var end = data.Length;
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var bStart = boundaries[i];
            var bEnd = boundaries[i + 1];
            if (offset >= bStart && offset <= bEnd)
            {
                start = bStart;
                end = bEnd;
                break;
            }
        }

        return new Selection(new RangePoint(pos.Node, start), new RangePoint(pos.Node, end));
    }

    private static Selection ExpandToParagraph(TextPosition pos)
    {
        if (pos.Node is ElementNode elNode && IsTextControl(elNode))
        {
            var len = (elNode.DisplayValue ?? elNode.Value ?? "").Length;
            return new Selection(new RangePoint(elNode, 0), new RangePoint(elNode, len));
        }

        // Containing block element or nearest enclosing block-level element
        ElementNode? block = null;
        for (Node? curr = pos.Node.Parent; curr is not null; curr = curr.Parent)
        {
            if (curr is ElementNode el && IsBlockFormattingContextOrBlock(el))
            {
                block = el;
                break;
            }
        }

        if (block is not null)
        {
            Text? first = null;
            Text? last = null;
            for (Node? n = block; n is not null; n = n.NextInTree(block))
            {
                if (n is Text t && t.Data.Length > 0 && IsSelectable(t))
                {
                    first ??= t;
                    last = t;
                }
            }
            if (first is not null && last is not null)
                return new Selection(new RangePoint(first, 0), new RangePoint(last, last.Data.Length));
        }

        if (pos.Node is Text text)
            return new Selection(new RangePoint(text, 0), new RangePoint(text, text.Data.Length));

        return new Selection(new RangePoint(pos.Node, 0), new RangePoint(pos.Node, 0));
    }

    private static bool IsBlockFormattingContextOrBlock(ElementNode element)
    {
        var display = element.ComputedStyle()?.Box.Display ?? Display.Inline;
        return display is Display.Block or Display.FlowRoot or Display.ListItem
            or Display.Flex or Display.Grid or Display.Table or Display.TableCell;
    }

    private RangePoint? FindFirstSelectablePoint()
    {
        for (Node? curr = _document.Node; curr is not null; curr = curr.NextInTree(_document.Node))
        {
            if (curr is Text t && t.Data.Length > 0 && IsSelectable(t))
                return new RangePoint(t, 0);
        }
        return null;
    }

    private RangePoint? FindLastSelectablePoint()
    {
        Text? last = null;
        for (Node? curr = _document.Node; curr is not null; curr = curr.NextInTree(_document.Node))
        {
            if (curr is Text t && t.Data.Length > 0 && IsSelectable(t))
                last = t;
        }
        return last is not null ? new RangePoint(last, last.Data.Length) : null;
    }
}


using Folio.Dom;
using Folio.Interaction;

namespace Folio.WinForms;

/// <summary>
/// Exposes the accessibility tree of a <see cref="FolioView"/> document through WinForms <see cref="AccessibleObject"/>s
/// backed by UI Automation.
/// </summary>
public sealed class FolioViewAccessibleObject : Control.ControlAccessibleObject
{
    private readonly FolioView _view;
    private AccessibleNode? _rootNode;
    private readonly List<FolioNodeAccessibleObject> _children = [];

    public FolioViewAccessibleObject(FolioView ownerControl) : base(ownerControl)
    {
        _view = ownerControl;
    }

    public override AccessibleRole Role => AccessibleRole.Document;

    public override string? Name => _rootNode?.Name is { Length: > 0 } name ? name : _view.Document?.Title ?? base.Name;

    public override System.Windows.Forms.AccessibleStates State => System.Windows.Forms.AccessibleStates.Focusable;

    internal void SyncTree(AccessibleNode? root)
    {
        _rootNode = root;
        _children.Clear();
        if (root is null)
            return;

        foreach (var child in root.Children)
            _children.Add(new FolioNodeAccessibleObject(_view, this, child));
    }

    public override int GetChildCount() => _children.Count;

    public override AccessibleObject? GetChild(int index) =>
        index >= 0 && index < _children.Count ? _children[index] : null;

    internal int GetChildIndex(FolioNodeAccessibleObject child) => _children.IndexOf(child);


    public override AccessibleObject? HitTest(int x, int y)
    {
        // Screen coordinates to client coordinates
        var clientPoint = _view.PointToClient(new Point(x, y));
        foreach (var child in _children)
        {
            var hit = child.HitTest(clientPoint.X, clientPoint.Y);
            if (hit is not null)
                return hit;
        }
        return this;
    }

    public override AccessibleObject? GetFocused()
    {
        var focused = _view.Document?.Input.FocusedNode;
        if (focused is null)
            return null;

        return FindObjectForNode(focused);
    }

    public override AccessibleObject? GetSelected() => GetFocused();

    internal AccessibleObject? FindObjectForNode(Node node)
    {
        foreach (var child in _children)
        {
            var found = child.FindObjectForNode(node);
            if (found is not null)
                return found;
        }
        return null;
    }
}

/// <summary>
/// An accessible object representing one node in the Folio accessibility tree.
/// </summary>
public sealed class FolioNodeAccessibleObject : AccessibleObject
{
    private readonly FolioView _view;
    private readonly AccessibleObject _parent;
    private readonly AccessibleNode _node;
    private readonly List<FolioNodeAccessibleObject> _children = [];

    internal FolioNodeAccessibleObject(FolioView view, AccessibleObject parent, AccessibleNode node)
    {
        _view = view;
        _parent = parent;
        _node = node;

        foreach (var child in node.Children)
            _children.Add(new FolioNodeAccessibleObject(_view, this, child));
    }

    public AccessibleNode Node => _node;

    public override AccessibleObject Parent => _parent;

    public override string? Name => _node.Name.Length > 0 ? _node.Name : null;

    public override string? Value
    {
        get => _node.Value;
        set { }
    }

    public override string? Description => _node.Level > 0 ? $"Heading level {_node.Level}" : null;

    public override AccessibleRole Role => MapRole(_node.Role);

    public override System.Windows.Forms.AccessibleStates State
    {
        get
        {
            var state = System.Windows.Forms.AccessibleStates.None;

            if ((_node.States & Folio.Interaction.AccessibleStates.Checked) != 0)
                state |= System.Windows.Forms.AccessibleStates.Checked;
            if ((_node.States & Folio.Interaction.AccessibleStates.Mixed) != 0)
                state |= System.Windows.Forms.AccessibleStates.Mixed;
            if ((_node.States & Folio.Interaction.AccessibleStates.Selected) != 0)
                state |= System.Windows.Forms.AccessibleStates.Selected;
            if ((_node.States & Folio.Interaction.AccessibleStates.Disabled) != 0)
                state |= System.Windows.Forms.AccessibleStates.Unavailable;
            if ((_node.States & Folio.Interaction.AccessibleStates.ReadOnly) != 0)
                state |= System.Windows.Forms.AccessibleStates.ReadOnly;
            if ((_node.States & Folio.Interaction.AccessibleStates.Expanded) != 0)
                state |= System.Windows.Forms.AccessibleStates.Expanded;
            if ((_node.States & Folio.Interaction.AccessibleStates.Collapsed) != 0)
                state |= System.Windows.Forms.AccessibleStates.Collapsed;

            if (_node.Node is ElementNode elem)
            {
                var input = _view.Document?.Input;
                if (input?.FocusedNode == elem)
                    state |= System.Windows.Forms.AccessibleStates.Focused;

                // Focusable check
                if (IsFocusable(elem))
                    state |= System.Windows.Forms.AccessibleStates.Focusable;
            }

            return state;
        }
    }

    public override Rectangle Bounds
    {
        get
        {
            if (_node.Bounds is not { } b)
                return Rectangle.Empty;

            var pixelScale = _view.DeviceDpi / 96f * _view.ZoomFactor;
            var scrollTop = _view.ScrollTop;

            var clientX = (int)Math.Round(b.X * pixelScale);
            var clientY = (int)Math.Round((b.Y - scrollTop) * pixelScale);
            var clientW = (int)Math.Round(b.Width * pixelScale);
            var clientH = (int)Math.Round(b.Height * pixelScale);

            var screenPoint = _view.PointToScreen(new Point(clientX, clientY));
            return new Rectangle(screenPoint.X, screenPoint.Y, clientW, clientH);
        }
    }

    public override int GetChildCount() => _children.Count;

    public override AccessibleObject? GetChild(int index) =>
        index >= 0 && index < _children.Count ? _children[index] : null;

    public override AccessibleObject? HitTest(int x, int y)
    {
        // x and y are client coordinates here
        if (_node.Bounds is { } b)
        {
            var pixelScale = _view.DeviceDpi / 96f * _view.ZoomFactor;
            var scrollTop = _view.ScrollTop;
            var rect = new Rectangle(
                (int)Math.Round(b.X * pixelScale),
                (int)Math.Round((b.Y - scrollTop) * pixelScale),
                (int)Math.Round(b.Width * pixelScale),
                (int)Math.Round(b.Height * pixelScale));

            if (rect.Contains(x, y))
            {
                for (var i = _children.Count - 1; i >= 0; i--)
                {
                    var hit = _children[i].HitTest(x, y);
                    if (hit is not null)
                        return hit;
                }
                return this;
            }
        }

        return null;
    }

    public override AccessibleObject? Navigate(AccessibleNavigation navdir)
    {
        switch (navdir)
        {
            case AccessibleNavigation.FirstChild:
                return _children.Count > 0 ? _children[0] : null;
            case AccessibleNavigation.LastChild:
                return _children.Count > 0 ? _children[^1] : null;
            case AccessibleNavigation.Next:
                if (_parent is FolioNodeAccessibleObject parentNode)
                {
                    var index = parentNode._children.IndexOf(this);
                    if (index >= 0 && index < parentNode._children.Count - 1)
                        return parentNode._children[index + 1];
                }
                else if (_parent is FolioViewAccessibleObject root)
                {
                    var index = root.GetChildIndex(this);
                    if (index >= 0 && index < root.GetChildCount() - 1)
                        return root.GetChild(index + 1);
                }
                return null;
            case AccessibleNavigation.Previous:
                if (_parent is FolioNodeAccessibleObject parentNodePrev)
                {
                    var index = parentNodePrev._children.IndexOf(this);
                    if (index > 0)
                        return parentNodePrev._children[index - 1];
                }
                else if (_parent is FolioViewAccessibleObject rootPrev)
                {
                    var index = rootPrev.GetChildIndex(this);
                    if (index > 0)
                        return rootPrev.GetChild(index - 1);
                }
                return null;
            case AccessibleNavigation.Up:
                return _parent;
            default:
                return base.Navigate(navdir);
        }
    }

    public override void DoDefaultAction()
    {
        if (_node.Node is ElementNode elem)
            _view.Document?.Wrap(elem).Click();
    }

    public override string? DefaultAction => _node.Role switch
    {
        "button" => "Press",
        "link" => "Jump",
        "checkbox" => (_node.States & Folio.Interaction.AccessibleStates.Checked) != 0 ? "Uncheck" : "Check",
        "radio" => "Select",
        _ => null,
    };

    public override void Select(AccessibleSelection flags)
    {
        if ((flags & AccessibleSelection.TakeFocus) != 0 && _node.Node is ElementNode elem)
            _view.Document?.Wrap(elem).Focus();
    }

    internal AccessibleObject? FindObjectForNode(Node node)
    {
        if (_node.Node == node)
            return this;

        foreach (var child in _children)
        {
            var found = child.FindObjectForNode(node);
            if (found is not null)
                return found;
        }
        return null;
    }

    private static AccessibleRole MapRole(string role) => role switch
    {
        "document" => AccessibleRole.Document,
        "heading" => AccessibleRole.StaticText,
        "link" => AccessibleRole.Link,
        "list" => AccessibleRole.List,
        "listitem" => AccessibleRole.ListItem,
        "table" => AccessibleRole.Table,
        "caption" => AccessibleRole.StaticText,
        "row" => AccessibleRole.Row,
        "cell" => AccessibleRole.Cell,
        "rowheader" => AccessibleRole.RowHeader,
        "columnheader" => AccessibleRole.ColumnHeader,
        "paragraph" => AccessibleRole.StaticText,
        "text" => AccessibleRole.StaticText,
        "image" => AccessibleRole.Graphic,
        "button" => AccessibleRole.PushButton,
        "checkbox" => AccessibleRole.CheckButton,
        "radio" => AccessibleRole.RadioButton,
        "slider" => AccessibleRole.Slider,
        "spinbutton" => AccessibleRole.SpinButton,
        "searchbox" => AccessibleRole.Text,
        "textbox" => AccessibleRole.Text,
        "combobox" => AccessibleRole.ComboBox,
        "listbox" => AccessibleRole.List,
        "option" => AccessibleRole.ListItem,
        "group" => AccessibleRole.Grouping,
        "progressbar" => AccessibleRole.ProgressBar,
        "meter" => AccessibleRole.ProgressBar,
        _ => AccessibleRole.Client,
    };

    private static bool IsFocusable(ElementNode elem)
    {
        if (elem.GetAttribute("tabindex") is not null)
            return true;
        if (elem.Name.Namespace != Namespaces.Html)
            return false;
        return elem.LocalName switch
        {
            "a" or "area" => elem.GetAttribute("href") is not null,
            "button" or "select" or "textarea" => true,
            "input" => (elem.GetAttribute("type") ?? "text").ToLowerInvariant() != "hidden",
            _ => false,
        };
    }
}

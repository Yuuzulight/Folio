using System.Numerics;
using Folio.Dom;

namespace Folio;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1 << 0,
    Control = 1 << 1,
    Shift = 1 << 2,
    Meta = 1 << 3,
}

[Flags]
public enum PointerButtons
{
    None = 0,
    Primary = 1 << 0,
    Secondary = 1 << 1,
    Middle = 1 << 2,
    Back = 1 << 3,
    Forward = 1 << 4,
}

public enum PointerButton
{
    None = -1,
    Primary = 0,
    Middle = 1,
    Secondary = 2,
    Back = 3,
    Forward = 4,
}

public readonly record struct PointerEvent(
    Vector2 Position,
    PointerButton Button = PointerButton.None,
    PointerButtons Buttons = PointerButtons.None,
    int ClickCount = 0,
    KeyModifiers Modifiers = KeyModifiers.None);

public enum WheelDeltaMode
{
    Pixel = 0,
    Line = 1,
    Page = 2,
}

public readonly record struct WheelEvent(
    Vector2 Position,
    Vector2 Delta,
    KeyModifiers Modifiers = KeyModifiers.None,
    WheelDeltaMode DeltaMode = WheelDeltaMode.Pixel);

public readonly record struct KeyEvent(
    string Key,
    string Code,
    KeyModifiers Modifiers = KeyModifiers.None);

public readonly record struct TextInputEvent(
    string Text,
    KeyModifiers Modifiers = KeyModifiers.None);

public readonly record struct CompositionEvent(
    string Data);

public sealed class LinkActivatedEventArgs(Uri uri, KeyModifiers modifiers = KeyModifiers.None) : EventArgs
{
    public Uri Uri { get; } = uri;
    public KeyModifiers Modifiers { get; } = modifiers;
    public bool Handled { get; set; }
}

public sealed class CursorChangedEventArgs(string cursor) : EventArgs
{
    public string Cursor { get; } = cursor;
}

public sealed class TooltipChangedEventArgs(string? tooltip) : EventArgs
{
    public string? Tooltip { get; } = tooltip;
}

public sealed class SelectionChangedEventArgs : EventArgs
{
    internal Folio.Dom.Selection? Selection { get; }

    internal SelectionChangedEventArgs(Folio.Dom.Selection? selection)
    {
        Selection = selection;
    }
}

public sealed class FocusChangedEventArgs(Element? focusedElement) : EventArgs
{
    public Element? FocusedElement { get; } = focusedElement;
}


public sealed class PasteEventArgs : EventArgs
{
    public string? Text { get; set; }
    public bool Handled { get; set; }
}

public sealed class ButtonClickedEventArgs(Element button) : EventArgs
{
    public Element Button { get; } = button;
}

public sealed class FormSubmittedEventArgs(
    Element form,
    string action,
    string method,
    IReadOnlyList<KeyValuePair<string, string>> data,
    Element? submitter = null) : EventArgs
{
    public Element Form { get; } = form;
    public string Action { get; } = action;
    public string Method { get; } = method;
    public IReadOnlyList<KeyValuePair<string, string>> Data { get; } = data;
    public Element? Submitter { get; } = submitter;
}

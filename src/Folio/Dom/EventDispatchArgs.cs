namespace Folio.Dom;

/// <summary>
/// Parameters for dispatching an event from the host (such as <see cref="Folio.InputRouter"/>)
/// to the DOM event system in script.
/// </summary>
internal sealed class EventDispatchArgs
{
    public float ScreenX { get; init; }
    public float ScreenY { get; init; }
    public float ClientX { get; init; }
    public float ClientY { get; init; }
    public float PageX { get; init; }
    public float PageY { get; init; }
    public bool CtrlKey { get; init; }
    public bool ShiftKey { get; init; }
    public bool AltKey { get; init; }
    public bool MetaKey { get; init; }
    public short Button { get; init; }
    public ushort Buttons { get; init; }
    public Node? RelatedTarget { get; init; }

    // PointerEvent specific
    public int PointerId { get; init; }
    public float Width { get; init; } = 1f;
    public float Height { get; init; } = 1f;
    public float Pressure { get; init; }
    public float TangentialPressure { get; init; }
    public int TiltX { get; init; }
    public int TiltY { get; init; }
    public int Twist { get; init; }
    public string PointerType { get; init; } = "mouse";
    public bool IsPrimary { get; init; } = true;

    // WheelEvent specific
    public double DeltaX { get; init; }
    public double DeltaY { get; init; }
    public double DeltaZ { get; init; }
    public uint DeltaMode { get; init; }

    // KeyboardEvent specific
    public string Key { get; init; } = "";
    public string Code { get; init; } = "";
    public uint Location { get; init; }
    public bool Repeat { get; init; }
    public bool IsComposing { get; init; }

    // InputEvent specific
    public string? Data { get; init; }
    public string InputType { get; init; } = "";

    // Event flags
    public bool Bubbles { get; init; } = true;
    public bool Cancelable { get; init; } = true;
    public bool Composed { get; init; } = true;
    public int Detail { get; init; }
}

/// <summary>
/// Delegate invoked to dispatch a trusted DOM event on a target node.
/// Returns <c>true</c> if default actions should proceed (<c>!event.defaultPrevented</c>),
/// or <c>false</c> if the default action was prevented.
/// </summary>
internal delegate bool DocumentEventDispatchDelegate(Node target, string type, EventDispatchArgs args);

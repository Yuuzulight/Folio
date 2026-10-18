namespace Folio.Dom;

/// <summary>Nested iframe content supplied by the scripting layer to the host renderer.</summary>
internal interface IFramePresentation
{
    object? DisplayList { get; }
    object? Accessibility { get; }
    object? ChildDocument { get; }
    bool HasFocusableContent { get; }
    bool FocusFirst(bool reverse);
    string? FallbackText { get; }
    void Render(float width, float height);
}

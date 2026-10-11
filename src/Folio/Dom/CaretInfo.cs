namespace Folio.Dom;

/// <summary>
/// Active text caret state for rendering in the display list.
/// </summary>
internal readonly record struct CaretInfo(ElementNode Element, int Offset, bool Visible);

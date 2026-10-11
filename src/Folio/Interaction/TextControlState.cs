using System.Globalization;
using Folio.Dom;
using Folio.Typography;

namespace Folio.Interaction;

/// <summary>
/// State and editing model for an HTML &lt;input&gt; or &lt;textarea&gt; form control.
/// Manages value, selection, caret, undo/redo history, and editing commands.
/// </summary>
internal sealed class TextControlState
{
    private string _value;
    private int _selectionStart;
    private int _selectionEnd;
    private bool _selectionIsBackwards;
    private readonly List<(string Value, int Start, int End)> _undoStack = [];
    private readonly List<(string Value, int Start, int End)> _redoStack = [];

    public ElementNode Element { get; }

    public TextControlState(ElementNode element)
    {
        Element = element ?? throw new ArgumentNullException(nameof(element));
        _value = element.Value ?? InitialValue(element);
        element.Value = _value;
        element.DisplayValue = DisplayText;
        _selectionStart = _value.Length;
        _selectionEnd = _value.Length;
    }

    private static string InitialValue(ElementNode element)
    {
        if (element.LocalName == "textarea")
            return element.TextContent ?? "";

        return element.GetAttribute("value") ?? "";
    }

    /// <summary>The current text value of the control.</summary>
    public string Value
    {
        get => _value;
        set
        {
            value ??= "";
            if (_value == value)
                return;

            PushUndo();
            _value = value;
            _selectionStart = Math.Clamp(_selectionStart, 0, _value.Length);
            _selectionEnd = Math.Clamp(_selectionEnd, 0, _value.Length);
            _redoStack.Clear();
            NotifyChanged();
        }
    }

    /// <summary>The start of the active selection within the control (0 to Value.Length).</summary>
    public int SelectionStart
    {
        get => _selectionStart;
        set => SetSelectionRange(value, _selectionEnd, _selectionIsBackwards);
    }

    /// <summary>The end of the active selection within the control (0 to Value.Length).</summary>
    public int SelectionEnd
    {
        get => _selectionEnd;
        set => SetSelectionRange(_selectionStart, value, _selectionIsBackwards);
    }

    /// <summary>Whether the selection was created backwards (focus precedes anchor).</summary>
    public bool SelectionIsBackwards => _selectionIsBackwards;

    public bool HasSelection => _selectionStart != _selectionEnd;

    public int CaretIndex => _selectionIsBackwards ? _selectionStart : _selectionEnd;

    public bool IsPassword => Element.LocalName == "input"
        && (Element.GetAttribute("type") ?? "text").Equals("password", StringComparison.OrdinalIgnoreCase);

    public bool IsReadOnly => Element.GetAttribute("readonly") is not null;

    public bool IsDisabled => Element.IsDisabled;

    public bool IsTextArea => Element.LocalName == "textarea";

    public int? MaxLength
    {
        get
        {
            var attr = Element.GetAttribute("maxlength");
            return attr is not null && int.TryParse(attr.Trim(), out var max) && max >= 0 ? max : null;
        }
    }

    /// <summary>The text shown on screen (bullets for passwords, placeholder when empty and unfocused, else Value).</summary>
    public string DisplayText => IsPassword ? new string('•', _value.Length) : _value;

    /// <summary>The selected text substring within the control.</summary>
    public string SelectedText
    {
        get
        {
            if (_selectionStart >= _selectionEnd)
                return "";
            return _value.Substring(_selectionStart, _selectionEnd - _selectionStart);
        }
    }

    public void SetSelectionRange(int start, int end, bool backwards = false)
    {
        var s = Math.Clamp(Math.Min(start, end), 0, _value.Length);
        var e = Math.Clamp(Math.Max(start, end), 0, _value.Length);
        _selectionStart = s;
        _selectionEnd = e;
        _selectionIsBackwards = backwards && s != e;
    }

    public void SelectAll()
    {
        SetSelectionRange(0, _value.Length);
    }

    /// <summary>Inserts text at the current caret or replaces the active selection.</summary>
    public bool Insert(string text)
    {
        if (IsReadOnly || IsDisabled || string.IsNullOrEmpty(text))
            return false;

        // In single-line inputs, replace newlines with space or strip them
        if (!IsTextArea && (text.Contains('\r') || text.Contains('\n')))
            text = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        var currentLen = _value.Length - (_selectionEnd - _selectionStart);
        if (MaxLength is { } max)
        {
            var allowed = Math.Max(0, max - currentLen);
            if (allowed <= 0)
                return false;
            if (text.Length > allowed)
                text = text.Substring(0, allowed);
        }

        PushUndo();

        var prefix = _value.Substring(0, _selectionStart);
        var suffix = _value.Substring(_selectionEnd);
        _value = prefix + text + suffix;

        var newCaret = prefix.Length + text.Length;
        _selectionStart = newCaret;
        _selectionEnd = newCaret;
        _selectionIsBackwards = false;
        _redoStack.Clear();
        NotifyChanged();
        return true;
    }

    /// <summary>Deletes the character or grapheme cluster before the caret, or the selection.</summary>
    public bool DeleteBackward(bool byWord = false)
    {
        if (IsReadOnly || IsDisabled)
            return false;

        if (HasSelection)
        {
            PushUndo();
            var prefix = _value.Substring(0, _selectionStart);
            var suffix = _value.Substring(_selectionEnd);
            _value = prefix + suffix;
            _selectionEnd = _selectionStart;
            _selectionIsBackwards = false;
            _redoStack.Clear();
            NotifyChanged();
            return true;
        }

        var caret = CaretIndex;
        if (caret <= 0)
            return false;

        PushUndo();

        int deleteStart;
        if (byWord)
        {
            deleteStart = FindPreviousWordBoundary(caret);
        }
        else
        {
            var clusterLen = GetPreviousGraphemeClusterLength(_value, caret);
            deleteStart = caret - clusterLen;
        }

        var left = _value.Substring(0, deleteStart);
        var right = _value.Substring(caret);
        _value = left + right;
        _selectionStart = deleteStart;
        _selectionEnd = deleteStart;
        _selectionIsBackwards = false;
        _redoStack.Clear();
        NotifyChanged();
        return true;
    }

    /// <summary>Deletes the character or grapheme cluster after the caret, or the selection.</summary>
    public bool DeleteForward(bool byWord = false)
    {
        if (IsReadOnly || IsDisabled)
            return false;

        if (HasSelection)
        {
            PushUndo();
            var prefix = _value.Substring(0, _selectionStart);
            var suffix = _value.Substring(_selectionEnd);
            _value = prefix + suffix;
            _selectionEnd = _selectionStart;
            _selectionIsBackwards = false;
            _redoStack.Clear();
            NotifyChanged();
            return true;
        }

        var caret = CaretIndex;
        if (caret >= _value.Length)
            return false;

        PushUndo();

        int deleteEnd;
        if (byWord)
        {
            deleteEnd = FindNextWordBoundary(caret);
        }
        else
        {
            var clusterLen = GetNextGraphemeClusterLength(_value, caret);
            deleteEnd = caret + clusterLen;
        }

        var left = _value.Substring(0, caret);
        var right = _value.Substring(deleteEnd);
        _value = left + right;
        _selectionStart = caret;
        _selectionEnd = caret;
        _selectionIsBackwards = false;
        _redoStack.Clear();
        NotifyChanged();
        return true;
    }

    public void MoveLeft(bool extendSelection = false, bool byWord = false)
    {
        var caret = CaretIndex;
        int newPos;
        if (byWord)
        {
            newPos = FindPreviousWordBoundary(caret);
        }
        else
        {
            if (!extendSelection && HasSelection)
            {
                // Collapse to selection start
                SetSelectionRange(_selectionStart, _selectionStart);
                return;
            }
            var clusterLen = GetPreviousGraphemeClusterLength(_value, caret);
            newPos = Math.Max(0, caret - clusterLen);
        }

        if (extendSelection)
        {
            var anchor = _selectionIsBackwards ? _selectionEnd : _selectionStart;
            SetSelectionRange(Math.Min(anchor, newPos), Math.Max(anchor, newPos), backwards: newPos < anchor);
        }
        else
        {
            SetSelectionRange(newPos, newPos);
        }
    }

    public void MoveRight(bool extendSelection = false, bool byWord = false)
    {
        var caret = CaretIndex;
        int newPos;
        if (byWord)
        {
            newPos = FindNextWordBoundary(caret);
        }
        else
        {
            if (!extendSelection && HasSelection)
            {
                // Collapse to selection end
                SetSelectionRange(_selectionEnd, _selectionEnd);
                return;
            }
            var clusterLen = GetNextGraphemeClusterLength(_value, caret);
            newPos = Math.Min(_value.Length, caret + clusterLen);
        }

        if (extendSelection)
        {
            var anchor = _selectionIsBackwards ? _selectionEnd : _selectionStart;
            SetSelectionRange(Math.Min(anchor, newPos), Math.Max(anchor, newPos), backwards: newPos < anchor);
        }
        else
        {
            SetSelectionRange(newPos, newPos);
        }
    }

    public void MoveHome(bool extendSelection = false, bool toDocumentStart = false)
    {
        int newPos;
        if (!IsTextArea || toDocumentStart)
        {
            newPos = 0;
        }
        else
        {
            // Start of current line
            var caret = CaretIndex;
            var prevNl = _value.LastIndexOf('\n', Math.Max(0, caret - 1));
            newPos = prevNl < 0 ? 0 : prevNl + 1;
        }

        if (extendSelection)
        {
            var anchor = _selectionIsBackwards ? _selectionEnd : _selectionStart;
            SetSelectionRange(Math.Min(anchor, newPos), Math.Max(anchor, newPos), backwards: newPos < anchor);
        }
        else
        {
            SetSelectionRange(newPos, newPos);
        }
    }

    public void MoveEnd(bool extendSelection = false, bool toDocumentEnd = false)
    {
        int newPos;
        if (!IsTextArea || toDocumentEnd)
        {
            newPos = _value.Length;
        }
        else
        {
            // End of current line
            var caret = CaretIndex;
            var nextNl = _value.IndexOf('\n', caret);
            newPos = nextNl < 0 ? _value.Length : nextNl;
        }

        if (extendSelection)
        {
            var anchor = _selectionIsBackwards ? _selectionEnd : _selectionStart;
            SetSelectionRange(Math.Min(anchor, newPos), Math.Max(anchor, newPos), backwards: newPos < anchor);
        }
        else
        {
            SetSelectionRange(newPos, newPos);
        }
    }

    public void MoveUp(bool extendSelection = false)
    {
        if (!IsTextArea)
        {
            if (Element.LocalName == "input" && (Element.GetAttribute("type") ?? "").Equals("number", StringComparison.OrdinalIgnoreCase))
                StepNumber(1);
            return;
        }

        var caret = CaretIndex;
        var prevNl = _value.LastIndexOf('\n', Math.Max(0, caret - 1));
        if (prevNl < 0)
        {
            // Already on first line: move to line start
            MoveHome(extendSelection);
            return;
        }

        var currentLineStart = prevNl + 1;
        var col = caret - currentLineStart;

        var prevPrevNl = _value.LastIndexOf('\n', Math.Max(0, prevNl - 1));
        var prevLineStart = prevPrevNl < 0 ? 0 : prevPrevNl + 1;
        var prevLineLen = prevNl - prevLineStart;

        var targetCol = Math.Min(col, prevLineLen);
        var newPos = prevLineStart + targetCol;

        if (extendSelection)
        {
            var anchor = _selectionIsBackwards ? _selectionEnd : _selectionStart;
            SetSelectionRange(Math.Min(anchor, newPos), Math.Max(anchor, newPos), backwards: newPos < anchor);
        }
        else
        {
            SetSelectionRange(newPos, newPos);
        }
    }

    public void MoveDown(bool extendSelection = false)
    {
        if (!IsTextArea)
        {
            if (Element.LocalName == "input" && (Element.GetAttribute("type") ?? "").Equals("number", StringComparison.OrdinalIgnoreCase))
                StepNumber(-1);
            return;
        }

        var caret = CaretIndex;
        var nextNl = _value.IndexOf('\n', caret);
        if (nextNl < 0)
        {
            // Already on last line: move to line end
            MoveEnd(extendSelection);
            return;
        }

        var prevNl = _value.LastIndexOf('\n', Math.Max(0, caret - 1));
        var currentLineStart = prevNl < 0 ? 0 : prevNl + 1;
        var col = caret - currentLineStart;

        var nextLineStart = nextNl + 1;
        var nextNextNl = _value.IndexOf('\n', nextLineStart);
        var nextLineEnd = nextNextNl < 0 ? _value.Length : nextNextNl;
        var nextLineLen = nextLineEnd - nextLineStart;

        var targetCol = Math.Min(col, nextLineLen);
        var newPos = nextLineStart + targetCol;

        if (extendSelection)
        {
            var anchor = _selectionIsBackwards ? _selectionEnd : _selectionStart;
            SetSelectionRange(Math.Min(anchor, newPos), Math.Max(anchor, newPos), backwards: newPos < anchor);
        }
        else
        {
            SetSelectionRange(newPos, newPos);
        }
    }

    private void StepNumber(int delta)
    {
        if (IsReadOnly || IsDisabled)
            return;

        if (double.TryParse(_value, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentNum))
        {
            var step = 1.0;
            if (double.TryParse(Element.GetAttribute("step"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                step = s;

            var nextVal = currentNum + delta * step;
            if (double.TryParse(Element.GetAttribute("min"), NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && nextVal < min)
                nextVal = min;
            if (double.TryParse(Element.GetAttribute("max"), NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && nextVal > max)
                nextVal = max;

            Value = nextVal.ToString(CultureInfo.InvariantCulture);
            SelectAll();
        }
        else
        {
            Value = delta > 0 ? "1" : "0";
            SelectAll();
        }
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0 || IsReadOnly || IsDisabled)
            return false;

        _redoStack.Add((_value, _selectionStart, _selectionEnd));
        var prev = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        _value = prev.Value;
        SetSelectionRange(prev.Start, prev.End);
        NotifyChanged();
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0 || IsReadOnly || IsDisabled)
            return false;

        _undoStack.Add((_value, _selectionStart, _selectionEnd));
        var next = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);

        _value = next.Value;
        SetSelectionRange(next.Start, next.End);
        NotifyChanged();
        return true;
    }

    private void PushUndo()
    {
        _undoStack.Add((_value, _selectionStart, _selectionEnd));
        if (_undoStack.Count > 100)
            _undoStack.RemoveAt(0);
    }

    private void NotifyChanged()
    {
        Element.Value = _value;
        Element.DisplayValue = DisplayText;
        Element.OwnerDocument.StructureMutated = true;
        Element.OnMutated(MutationKind.CharacterData);
    }

    private int FindPreviousWordBoundary(int offset)
    {
        if (offset <= 0 || _value.Length == 0)
            return 0;

        var i = offset;
        while (i > 0 && char.IsWhiteSpace(_value[i - 1]))
            i--;

        if (i == 0)
            return 0;

        var boundaries = WordBoundaries.Find(_value);
        for (var b = boundaries.Count - 1; b >= 0; b--)
        {
            if (boundaries[b] < i)
                return boundaries[b];
        }
        return 0;
    }

    private int FindNextWordBoundary(int offset)
    {
        if (offset >= _value.Length || _value.Length == 0)
            return _value.Length;

        var i = offset;
        while (i < _value.Length && char.IsWhiteSpace(_value[i]))
            i++;

        if (i >= _value.Length)
            return _value.Length;

        var boundaries = WordBoundaries.Find(_value);
        for (var b = 0; b < boundaries.Count; b++)
        {
            if (boundaries[b] > i)
                return boundaries[b];
        }
        return _value.Length;
    }

    internal static int GetPreviousGraphemeClusterLength(string text, int offset)
    {
        if (offset <= 0 || offset > text.Length)
            return 0;

        if (offset >= 2 && text[offset - 2] == '\r' && text[offset - 1] == '\n')
            return 2;

        if (offset >= 2 && char.IsSurrogatePair(text[offset - 2], text[offset - 1]))
            return 2;

        var i = offset - 1;
        while (i > 0)
        {
            var cat = char.GetUnicodeCategory(text[i]);
            if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                || (text[i] >= '\uFE00' && text[i] <= '\uFE0F')
                || text[i] == '\u200D')
            {
                i--;
                if (i > 0 && char.IsSurrogatePair(text[i - 1], text[i]))
                    i--;
            }
            else
            {
                break;
            }
        }

        if (i > 0 && char.IsSurrogatePair(text[i - 1], text[i]))
            i--;

        return Math.Max(1, offset - i);
    }

    internal static int GetNextGraphemeClusterLength(string text, int offset)
    {
        if (offset >= text.Length)
            return 0;

        if (offset + 1 < text.Length && text[offset] == '\r' && text[offset + 1] == '\n')
            return 2;

        return StringInfo.GetNextTextElementLength(text, offset);
    }
}

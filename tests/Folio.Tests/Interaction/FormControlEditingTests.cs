using System.Numerics;
using Folio.Dom;
using Folio.Interaction;

namespace Folio.Tests.Interaction;

public class FormControlEditingTests
{
    private static readonly FolioOptions BoxFontOptions = new()
    {
        Fonts = new Folio.Typography.FontSettings
        {
            Source = new Folio.Typography.FontFolderSource(Path.Combine(AppContext.BaseDirectory, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>>
            {
                ["serif"] = ["Folio Box"],
                ["sans-serif"] = ["Folio Box"],
                ["monospace"] = ["Folio Box"],
            },
        },
    };

    [Fact]
    public void TypingInsertsCharactersIntoInput()
    {
        using var doc = Document.Parse("<input id=txt type=text value='abc'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Value starts as "abc" with caret at end (offset 3)
        Assert.Equal("abc", input.Value);
        Assert.Equal(3, input.SelectionStart);
        Assert.Equal(3, input.SelectionEnd);

        // Type 'd'
        doc.Input.HandleTextInput(new TextInputEvent("d"));
        Assert.Equal("abcd", input.Value);
        Assert.Equal(4, input.SelectionStart);
        Assert.Equal(4, input.SelectionEnd);

        // Type 'e'
        doc.Input.HandleTextInput(new TextInputEvent("e"));
        Assert.Equal("abcde", input.Value);
        Assert.Equal(5, input.SelectionStart);
    }

    [Fact]
    public void ValueModelIsSeparateFromValueAttribute()
    {
        using var doc = Document.Parse("<input id=txt type=text value='original'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        doc.Input.HandleTextInput(new TextInputEvent("!"));
        Assert.Equal("original!", input.Value);
        Assert.Equal("original", input.GetAttribute("value"));

        // Setting Value directly
        input.Value = "custom";
        Assert.Equal("custom", input.Value);
        Assert.Equal("original", input.GetAttribute("value"));
    }

    [Fact]
    public void BackspaceAndDeleteSingleCharacters()
    {
        using var doc = Document.Parse("<input id=txt type=text value='test'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Caret is at end (offset 4). Backspace removes 't'
        doc.Input.HandleKeyDown(new KeyEvent("Backspace", "Backspace"));
        Assert.Equal("tes", input.Value);
        Assert.Equal(3, input.SelectionStart);

        // Left arrow moves caret between 'e' and 's' (offset 2)
        doc.Input.HandleKeyDown(new KeyEvent("ArrowLeft", "ArrowLeft"));
        Assert.Equal(2, input.SelectionStart);

        // Delete removes 's' forward
        doc.Input.HandleKeyDown(new KeyEvent("Delete", "Delete"));
        Assert.Equal("te", input.Value);
        Assert.Equal(2, input.SelectionStart);

        // Delete at end of text does nothing
        doc.Input.HandleKeyDown(new KeyEvent("Delete", "Delete"));
        Assert.Equal("te", input.Value);
    }

    [Fact]
    public void BackspaceAndDeleteGraphemeClusters()
    {
        // 1. Surrogate pairs (e.g. Emoji U+1F603: \uD83D\uDE03)
        using var doc1 = Document.Parse("<input id=txt type=text value='hi\uD83D\uDE03'>", BoxFontOptions);
        doc1.Paint(800, 600);

        var input1 = doc1.GetElementById("txt")!;
        doc1.Input.Focus(input1);
        Assert.Equal(4, input1.SelectionStart); // "hi" + 2 code units for emoji

        // Single Backspace must delete both surrogate code units
        doc1.Input.HandleKeyDown(new KeyEvent("Backspace", "Backspace"));
        Assert.Equal("hi", input1.Value);
        Assert.Equal(2, input1.SelectionStart);

        // 2. Combining marks (e.g. e + acute accent U+0301)
        using var doc2 = Document.Parse("<input id=txt type=text value='cafe\u0301'>", BoxFontOptions);
        doc2.Paint(800, 600);

        var input2 = doc2.GetElementById("txt")!;
        doc2.Input.Focus(input2);
        Assert.Equal(5, input2.SelectionStart); // 'c','a','f','e','\u0301'

        // Single Backspace must delete base character 'e' and its combining mark '\u0301' together
        doc2.Input.HandleKeyDown(new KeyEvent("Backspace", "Backspace"));
        Assert.Equal("caf", input2.Value);
        Assert.Equal(3, input2.SelectionStart);
    }

    [Fact]
    public void WordNavigationAndWordDeletion()
    {
        using var doc = Document.Parse("<input id=txt type=text value='quick brown fox'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);
        Assert.Equal(15, input.SelectionStart);

        // Ctrl+Left moves to start of "fox"
        doc.Input.HandleKeyDown(new KeyEvent("ArrowLeft", "ArrowLeft", KeyModifiers.Control));
        Assert.Equal(12, input.SelectionStart);

        // Ctrl+Left moves to start of "brown"
        doc.Input.HandleKeyDown(new KeyEvent("ArrowLeft", "ArrowLeft", KeyModifiers.Control));
        Assert.Equal(6, input.SelectionStart);

        // Ctrl+Right moves to end of "brown"
        doc.Input.HandleKeyDown(new KeyEvent("ArrowRight", "ArrowRight", KeyModifiers.Control));
        Assert.Equal(11, input.SelectionStart);

        // Ctrl+Backspace deletes "brown"
        doc.Input.HandleKeyDown(new KeyEvent("Backspace", "Backspace", KeyModifiers.Control));
        Assert.Equal("quick  fox", input.Value);
        Assert.Equal(6, input.SelectionStart);

        // Ctrl+Delete deletes "fox"
        doc.Input.HandleKeyDown(new KeyEvent("Delete", "Delete", KeyModifiers.Control));
        Assert.Equal("quick ", input.Value);
    }

    [Fact]
    public void HomeAndEndNavigation()
    {
        using var doc = Document.Parse("<input id=txt type=text value='hello world'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Home moves to start (0)
        doc.Input.HandleKeyDown(new KeyEvent("Home", "Home"));
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(0, input.SelectionEnd);

        // End moves to end (11)
        doc.Input.HandleKeyDown(new KeyEvent("End", "End"));
        Assert.Equal(11, input.SelectionStart);
        Assert.Equal(11, input.SelectionEnd);
    }

    [Fact]
    public void SelectionShiftExtendAndTypingOverSelection()
    {
        using var doc = Document.Parse("<input id=txt type=text value='hello world'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Set selection range directly
        input.SetSelectionRange(0, 5);
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(5, input.SelectionEnd);

        // Shift+Right extends selection to 6
        doc.Input.HandleKeyDown(new KeyEvent("ArrowRight", "ArrowRight", KeyModifiers.Shift));
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(6, input.SelectionEnd);

        // Typing character 'X' replaces selected text "hello "
        doc.Input.HandleTextInput(new TextInputEvent("X"));
        Assert.Equal("Xworld", input.Value);
        Assert.Equal(1, input.SelectionStart);
        Assert.Equal(1, input.SelectionEnd);

        // Select All (Ctrl+A)
        doc.Input.HandleKeyDown(new KeyEvent("a", "KeyA", KeyModifiers.Control));
        Assert.Equal(0, input.SelectionStart);
        Assert.Equal(6, input.SelectionEnd);

        // Backspace clears entire selection
        doc.Input.HandleKeyDown(new KeyEvent("Backspace", "Backspace"));
        Assert.Equal("", input.Value);
        Assert.Equal(0, input.SelectionStart);
    }

    [Fact]
    public void MultiStepUndoAndRedo()
    {
        using var doc = Document.Parse("<input id=txt type=text value=''>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        doc.Input.HandleTextInput(new TextInputEvent("A"));
        Assert.Equal("A", input.Value);

        doc.Input.HandleTextInput(new TextInputEvent("B"));
        Assert.Equal("AB", input.Value);

        doc.Input.HandleTextInput(new TextInputEvent("C"));
        Assert.Equal("ABC", input.Value);

        // Undo 1 step -> "AB"
        doc.Input.HandleKeyDown(new KeyEvent("z", "KeyZ", KeyModifiers.Control));
        Assert.Equal("AB", input.Value);

        // Undo 2nd step -> "A"
        doc.Input.HandleKeyDown(new KeyEvent("z", "KeyZ", KeyModifiers.Control));
        Assert.Equal("A", input.Value);

        // Redo with Ctrl+Y -> "AB"
        doc.Input.HandleKeyDown(new KeyEvent("y", "KeyY", KeyModifiers.Control));
        Assert.Equal("AB", input.Value);

        // Redo with Ctrl+Shift+Z -> "ABC"
        doc.Input.HandleKeyDown(new KeyEvent("z", "KeyZ", KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal("ABC", input.Value);
    }

    [Fact]
    public void MaxLengthEnforcement()
    {
        using var doc = Document.Parse("<input id=txt type=text maxlength='4' value='abc'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Insert 1 char -> allowed (reaches length 4)
        doc.Input.HandleTextInput(new TextInputEvent("d"));
        Assert.Equal("abcd", input.Value);

        // Insert another char -> rejected because length would exceed 4
        doc.Input.HandleTextInput(new TextInputEvent("e"));
        Assert.Equal("abcd", input.Value);

        // Pasting multiple chars only inserts up to allowed room or rejects if no room
        doc.Input.Paste("xyz");
        Assert.Equal("abcd", input.Value);
    }

    [Fact]
    public void ReadOnlyAndDisabledGuards()
    {
        // 1. ReadOnly input
        using var doc1 = Document.Parse("<input id=txt readonly value='immutable'>", BoxFontOptions);
        doc1.Paint(800, 600);

        var input1 = doc1.GetElementById("txt")!;
        doc1.Input.Focus(input1);

        doc1.Input.HandleTextInput(new TextInputEvent("x"));
        Assert.Equal("immutable", input1.Value);

        doc1.Input.HandleKeyDown(new KeyEvent("Backspace", "Backspace"));
        Assert.Equal("immutable", input1.Value);

        // 2. Disabled input
        using var doc2 = Document.Parse("<input id=txt disabled value='cannot focus'>", BoxFontOptions);
        doc2.Paint(800, 600);

        var input2 = doc2.GetElementById("txt")!;
        doc2.Input.Focus(input2);

        // Disabled element cannot be focused
        Assert.Null(doc2.Input.FocusedElement);
        doc2.Input.HandleTextInput(new TextInputEvent("x"));
        Assert.Equal("cannot focus", input2.Value);

        // 3. Ancestor fieldset disabled
        using var doc3 = Document.Parse("<fieldset disabled><input id=txt value='fieldset disabled'></fieldset>", BoxFontOptions);
        doc3.Paint(800, 600);

        var input3 = doc3.GetElementById("txt")!;
        doc3.Input.Focus(input3);
        Assert.Null(doc3.Input.FocusedElement);
    }

    [Fact]
    public void PasswordMaskingRendersBulletPoints()
    {
        using var doc = Document.Parse("<input id=pwd type=password value='secret'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("pwd")!;
        Assert.Equal("secret", input.Value);
        Assert.Equal("••••••", input.DisplayValue);

        doc.Input.Focus(input);
        doc.Input.HandleTextInput(new TextInputEvent("1"));
        Assert.Equal("secret1", input.Value);
        Assert.Equal("•••••••", input.DisplayValue);
    }

    [Fact]
    public void NumberInputSteppingWithArrowKeys()
    {
        using var doc = Document.Parse("<input id=num type=number value='5' min='0' max='10' step='2'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("num")!;
        doc.Input.Focus(input);

        // ArrowUp steps by +2 -> 7
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.Equal("7", input.Value);

        // ArrowUp steps by +2 -> 9
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.Equal("9", input.Value);

        // ArrowUp steps by +2 -> clamped to max 10
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.Equal("10", input.Value);

        // ArrowDown steps by -2 -> 8
        doc.Input.HandleKeyDown(new KeyEvent("ArrowDown", "ArrowDown"));
        Assert.Equal("8", input.Value);
    }

    [Fact]
    public void TextareaMultiLineEditingAndNavigation()
    {
        using var doc = Document.Parse("<textarea id=ta>line1\nline2</textarea>", BoxFontOptions);
        doc.Paint(800, 600);

        var textarea = doc.GetElementById("ta")!;
        doc.Input.Focus(textarea);
        Assert.Equal("line1\nline2", textarea.Value);

        // Caret is at end (offset 11). Enter inserts newline
        doc.Input.HandleKeyDown(new KeyEvent("Enter", "Enter"));
        Assert.Equal("line1\nline2\n", textarea.Value);
        Assert.Equal(12, textarea.SelectionStart);

        doc.Input.HandleTextInput(new TextInputEvent("line3"));
        Assert.Equal("line1\nline2\nline3", textarea.Value);

        // ArrowUp moves to previous line
        doc.Input.HandleKeyDown(new KeyEvent("ArrowUp", "ArrowUp"));
        Assert.True(textarea.SelectionStart < 12);
    }

    [Fact]
    public void CutCopyAndPasteInControl()
    {
        using var doc = Document.Parse("<input id=txt type=text value='hello world'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Select "world" (offset 6 to 11)
        input.SetSelectionRange(6, 11);
        Assert.Equal("world", doc.SelectedText);

        var copyCalled = false;
        var cutCalled = false;
        doc.Input.CopyRequested += (_, _) => copyCalled = true;
        doc.Input.CutRequested += (_, _) => cutCalled = true;

        // Ctrl+C raises CopyRequested
        doc.Input.HandleKeyDown(new KeyEvent("c", "KeyC", KeyModifiers.Control));
        Assert.True(copyCalled);

        // Ctrl+X raises CutRequested and deletes selected text
        doc.Input.HandleKeyDown(new KeyEvent("x", "KeyX", KeyModifiers.Control));
        Assert.True(cutCalled);
        Assert.Equal("hello ", input.Value);
        Assert.Equal(6, input.SelectionStart);

        // Paste "Folio"
        doc.Input.PasteRequested += (_, e) =>
        {
            e.Text = "Folio";
            e.Handled = true;
        };
        doc.Input.HandleKeyDown(new KeyEvent("v", "KeyV", KeyModifiers.Control));
        Assert.Equal("hello Folio", input.Value);
        Assert.Equal(11, input.SelectionStart);
    }

    [Fact]
    public void CaretColorCssPropertyApplied()
    {
        using var doc = Document.Parse("<input id=txt type=text style='caret-color: red'>", BoxFontOptions);
        doc.Paint(800, 600);

        var input = doc.GetElementById("txt")!;
        var style = doc.FindFragment(input.Node)?.Box?.Style;
        Assert.NotNull(style);
        Assert.True(style.Ui.CaretColor.HasValue);
        Assert.Equal(1f, style.Ui.CaretColor.Value.R);
        Assert.Equal(0f, style.Ui.CaretColor.Value.G);
        Assert.Equal(0f, style.Ui.CaretColor.Value.B);
    }

    [Fact]
    public void AnimationClockRunsWhileTextControlIsFocused()
    {
        using var doc = Document.Parse("<input id=txt type=text value='test'>", BoxFontOptions);
        doc.Paint(800, 600);

        // Not focused initially: animations not running
        Assert.False(doc.AnimationsRunning(0.0));

        var input = doc.GetElementById("txt")!;
        doc.Input.Focus(input);

        // Focused: caret blinking keeps animation timeline running
        Assert.True(doc.AnimationsRunning(0.0));
        Assert.True(doc.AnimationsRunning(0.5));

        // Blur: animations stop
        doc.Input.Focus(null);
        Assert.False(doc.AnimationsRunning(1.0));
    }
}

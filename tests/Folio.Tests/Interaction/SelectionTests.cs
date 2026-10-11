using System.Numerics;
using Folio;
using Folio.Dom;
using Folio.Interaction;

namespace Folio.Tests.Interaction;

public class SelectionTests
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

    private const string Page = """
        <!DOCTYPE html>
        <style>
          body { margin: 0; width: 800px; height: 600px }
          p { margin: 0; font: 20px monospace }
          div { margin: 0; font: 20px monospace }
        </style>
        """;

    [Fact]
    public void DragSelectsAcrossTextRun()
    {
        // "Hello World": 11 monospace chars, 20px each (x: 0 to 220)
        using var doc = Document.Parse(Page + "<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        Selection? lastSelection = null;
        doc.Input.SelectionChanged += (_, e) => lastSelection = e.Selection;

        // Mouse down at 'H' (x=5, y=10) -> offset 0
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(5, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));
        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(0, doc.Input.Selection!.Focus.Offset);
        Assert.True(doc.Input.Selection!.IsCollapsed);

        // Drag to 'o' in Hello (x=95, y=10) -> offset 5
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(95, 10), PointerButton.Primary, PointerButtons.Primary));
        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(5, doc.Input.Selection!.Focus.Offset);
        Assert.False(doc.Input.Selection!.IsCollapsed);
        Assert.False(doc.Input.Selection!.IsBackwards);

        // Mouse up ends selecting
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(95, 10), PointerButton.Primary, PointerButtons.None));
        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(5, doc.Input.Selection!.Focus.Offset);
    }

    [Fact]
    public void DragBackwardsSetsIsBackwardsTrue()
    {
        using var doc = Document.Parse(Page + "<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Mouse down at 'W' (x=125, y=10) -> offset 6
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(125, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));
        // Drag backwards to 'H' (x=5, y=10) -> offset 0
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(5, 10), PointerButton.Primary, PointerButtons.Primary));

        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(6, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(0, doc.Input.Selection!.Focus.Offset);
        Assert.True(doc.Input.Selection!.IsBackwards);
        Assert.Equal(0, doc.Input.Selection!.Range.Start.Offset);
        Assert.Equal(6, doc.Input.Selection!.Range.End.Offset);
    }

    [Fact]
    public void DragAcrossBlocksSelectsFromFirstToSecondBlock()
    {
        using var doc = Document.Parse(Page + "<p id=p1 style='height: 30px'>First</p><p id=p2 style='height: 30px'>Second</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Click in p1 (y=10, x=5) -> p1 text offset 0
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(5, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));
        // Drag into p2 (y=40, x=45) -> p2 text offset 2 ('Se')
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(45, 40), PointerButton.Primary, PointerButtons.Primary));

        Assert.NotNull(doc.Input.Selection);
        Assert.NotEqual(doc.Input.Selection!.Anchor.Node, doc.Input.Selection!.Focus.Node);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(2, doc.Input.Selection!.Focus.Offset);
        Assert.False(doc.Input.Selection!.IsBackwards);
    }

    [Fact]
    public void DoubleClickSelectsWordLatin()
    {
        using var doc = Document.Parse(Page + "<p id=p>The quick brown fox</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Double-click inside "quick" (x: 'The ' is 80px, 'quick' is 80..180px; click at 110px)
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(110, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 2));

        Assert.NotNull(doc.Input.Selection);
        // "quick" starts at index 4 and ends at index 9
        Assert.Equal(4, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(9, doc.Input.Selection!.Focus.Offset);
        Assert.False(doc.Input.Selection!.IsCollapsed);
    }

    [Fact]
    public void DoubleClickSelectsWordCJK()
    {
        // CJK characters: each character is typically a word or cluster in UAX #29
        using var doc = Document.Parse(Page + "<p id=p>你好世界</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Click at first char '你' (x=10, y=10)
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(10, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 2));

        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(1, doc.Input.Selection!.Focus.Offset);
    }

    [Fact]
    public void TripleClickSelectsParagraphBlock()
    {
        using var doc = Document.Parse(Page + "<p id=p>First sentence. Second sentence.</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Triple-click inside paragraph
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(50, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 3));

        Assert.NotNull(doc.Input.Selection);
        var textNode = (Text)doc.Input.Selection!.Anchor.Node;
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(textNode.Data.Length, doc.Input.Selection!.Focus.Offset);
    }

    [Fact]
    public void ShiftClickExtendsSelection()
    {
        using var doc = Document.Parse(Page + "<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Single click at 'H' (offset 0)
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(5, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(5, 10), PointerButton.Primary, PointerButtons.None));

        Assert.True(doc.Input.Selection!.IsCollapsed);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);

        // Shift+click at 'World' end (x=215, y=10 -> offset 11)
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(215, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1, Modifiers: KeyModifiers.Shift));

        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        Assert.Equal(11, doc.Input.Selection!.Focus.Offset);
    }

    [Fact]
    public void CtrlASelectsAllContent()
    {
        using var doc = Document.Parse(Page + "<p id=p1>First Paragraph</p><p id=p2>Second Paragraph</p>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.Input.HandleKeyDown(new KeyEvent("a", "KeyA", KeyModifiers.Control));

        Assert.NotNull(doc.Input.Selection);
        Assert.False(doc.Input.Selection!.IsCollapsed);

        var startNode = (Text)doc.Input.Selection!.Range.Start.Node;
        var endNode = (Text)doc.Input.Selection!.Range.End.Node;
        Assert.Equal("First Paragraph", startNode.Data);
        Assert.Equal("Second Paragraph", endNode.Data);
        Assert.Equal(0, doc.Input.Selection!.Range.Start.Offset);
        Assert.Equal("Second Paragraph".Length, doc.Input.Selection!.Range.End.Offset);
    }

    [Fact]
    public void UserSelectNoneCannotBeSelected()
    {
        using var doc = Document.Parse(Page + "<p style='user-select: none'>Unselectable text</p><p id=p2>Selectable</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Click on user-select: none paragraph
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));

        Assert.Null(doc.Input.Selection);

        // Ctrl+A should select only selectable content
        doc.Input.HandleKeyDown(new KeyEvent("a", "KeyA", KeyModifiers.Control));
        Assert.NotNull(doc.Input.Selection);
        var selectedText = (Text)doc.Input.Selection!.Range.Start.Node;
        Assert.Equal("Selectable", selectedText.Data);
    }

    [Fact]
    public void UserSelectAllSelectsEntireElementAsAtomicUnit()
    {
        using var doc = Document.Parse(Page + "<p id=p style='user-select: all'>Atomic Unit Text</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Single click inside text
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(40, 10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));

        Assert.NotNull(doc.Input.Selection);
        Assert.Equal(0, doc.Input.Selection!.Anchor.Offset);
        var textNode = (Text)doc.Input.Selection!.Focus.Node;
        Assert.Equal(textNode.Data.Length, doc.Input.Selection!.Focus.Offset);
    }

    [Fact]
    public void ClickingOutsideBoxesClearsSelection()
    {
        using var doc = Document.Parse(Page + "<p style='width: 100px; height: 30px'>Text</p>", BoxFontOptions);
        doc.Paint(800, 600);

        // Select text
        doc.Input.HandleKeyDown(new KeyEvent("a", "KeyA", KeyModifiers.Control));
        Assert.NotNull(doc.Input.Selection);

        // Click outside body/viewport boxes (e.g. coordinates outside document layout)
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(-10, -10), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1));

        Assert.Null(doc.Input.Selection);
    }
}

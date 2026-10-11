using System.Numerics;
using System.Text;
using Folio.Dom;
using Folio.Interaction;

namespace Folio.Tests.Interaction;

public class SelectionCopyTests
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
    public void EmptyOrCollapsedSelectionReturnsEmptyString()
    {
        using var doc = Document.Parse("<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        Assert.Equal("", doc.SelectedText);
        Assert.Equal("", doc.SelectedHtml);

        var p = doc.GetElementById("p")!.Node;
        var text = (Text)p.FirstChild!;

        // Collapsed selection
        doc.Input.Select(new RangePoint(text, 2), new RangePoint(text, 2));
        Assert.Equal("", doc.SelectedText);
        Assert.Equal("", doc.SelectedHtml);
    }

    [Fact]
    public void PlainText_SingleTextRunPartialSelection()
    {
        using var doc = Document.Parse("<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        var p = doc.GetElementById("p")!.Node;
        var text = (Text)p.FirstChild!;

        doc.Input.Select(new RangePoint(text, 0), new RangePoint(text, 5));
        Assert.Equal("Hello", doc.SelectedText);

        doc.Input.Select(new RangePoint(text, 6), new RangePoint(text, 11));
        Assert.Equal("World", doc.SelectedText);
    }

    [Fact]
    public void PlainText_BlockBoundariesEmitNewlines()
    {
        using var doc = Document.Parse("<div>Line 1</div><div>Line 2</div><div>Line 3</div>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("Line 1\nLine 2\nLine 3", doc.SelectedText);
    }

    [Fact]
    public void PlainText_ParagraphsEmitNewlines()
    {
        using var doc = Document.Parse("<p>First paragraph</p><p>Second paragraph</p>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("First paragraph\nSecond paragraph", doc.SelectedText);
    }

    [Fact]
    public void PlainText_BrEmitsNewline()
    {
        using var doc = Document.Parse("<p>First line<br>Second line</p>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("First line\nSecond line", doc.SelectedText);
    }

    [Fact]
    public void PlainText_TableEmitsTabsBetweenCellsAndNewlinesBetweenRows()
    {
        const string html = """
            <table>
              <tr><td>A1</td><td>B1</td><td>C1</td></tr>
              <tr><td>A2</td><td>B2</td><td>C2</td></tr>
            </table>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("A1\tB1\tC1\nA2\tB2\tC2", doc.SelectedText);
    }

    [Fact]
    public void PlainText_CollapsesCollapsibleWhitespace()
    {
        using var doc = Document.Parse("<p>   Hello     World   from   Folio   </p>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("Hello World from Folio", doc.SelectedText);
    }

    [Fact]
    public void PlainText_PreservesWhitespaceInPre()
    {
        const string preContent = "Line 1\n  Line 2 with spaces\nLine 3\twith tab";
        using var doc = Document.Parse($"<pre>{preContent}</pre>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal(preContent, doc.SelectedText);
    }

    [Fact]
    public void PlainText_ExcludesDisplayNoneAndScriptAndStyle()
    {
        const string html = """
            <p>
              Visible text
              <span style="display: none">Hidden secret</span>
              <script>var x = 123;</script>
              <style>body { color: black; }</style>
              continued text
            </p>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("Visible text continued text", doc.SelectedText);
    }

    [Fact]
    public void Html_SingleTextNodeWithinInlineTag()
    {
        using var doc = Document.Parse("<p id=p><b>Hello World</b></p>", BoxFontOptions);
        doc.Paint(800, 600);

        var p = doc.GetElementById("p")!.Node;
        var b = (ElementNode)p.FirstChild!;
        var text = (Text)b.FirstChild!;

        doc.Input.Select(new RangePoint(text, 0), new RangePoint(text, 5));
        Assert.Equal("<b>Hello</b>", doc.SelectedHtml);
    }

    [Fact]
    public void Html_PartialSelectionAcrossInlineTags()
    {
        using var doc = Document.Parse("<p id=p>Hello <b>World</b> and <i>More</i></p>", BoxFontOptions);
        doc.Paint(800, 600);

        var p = doc.GetElementById("p")!.Node;
        var t1 = (Text)p.FirstChild!; // "Hello "
        var b = (ElementNode)t1.NextSibling!; // <b>World</b>
        var t2 = (Text)b.FirstChild!; // "World"

        // From "llo " in "Hello " to "Wor" in "World"
        doc.Input.Select(new RangePoint(t1, 2), new RangePoint(t2, 3));
        Assert.Equal("llo <b>Wor</b>", doc.SelectedHtml);
        Assert.Equal("llo Wor", doc.SelectedText);
    }

    [Fact]
    public void Html_PartialSelectionAcrossParagraphs()
    {
        const string html = "<div id=d><p id=p1>First paragraph</p><p id=p2>Second paragraph</p></div>";
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var p1 = (ElementNode)doc.GetElementById("p1")!.Node;
        var p2 = (ElementNode)doc.GetElementById("p2")!.Node;
        var t1 = (Text)p1.FirstChild!;
        var t2 = (Text)p2.FirstChild!;

        // From "st paragraph" in p1 to "Second" in p2
        doc.Input.Select(new RangePoint(t1, 3), new RangePoint(t2, 6));
        Assert.Equal("<p id=\"p1\">st paragraph</p><p id=\"p2\">Second</p>", doc.SelectedHtml);
        Assert.Equal("st paragraph\nSecond", doc.SelectedText);
    }

    [Fact]
    public void Html_EscapesSpecialCharacters()
    {
        using var doc = Document.Parse("<p id=p>A &amp; B &lt; C &gt; D &quot;E&quot;</p>", BoxFontOptions);
        doc.Paint(800, 600);

        var p = doc.GetElementById("p")!.Node;
        var text = (Text)p.FirstChild!;

        doc.Input.Select(new RangePoint(text, 0), new RangePoint(text, text.Data.Length));
        Assert.Equal("A &amp; B &lt; C &gt; D &quot;E&quot;", doc.SelectedHtml);
        Assert.Equal("A & B < C > D \"E\"", doc.SelectedText);
    }

    [Fact]
    public void Html_VoidElementsEmitWithoutClosingTag()
    {
        using var doc = Document.Parse("<p id=p>A<br>B<img src=\"test.png\">C</p>", BoxFontOptions);
        doc.Paint(800, 600);

        doc.SelectAll();
        Assert.Equal("A<br>B<img src=\"test.png\">C", doc.SelectedHtml);
        Assert.Equal("A\nBC", doc.SelectedText);
    }

    [Fact]
    public void InputRouter_SelectAllAndClearSelection()
    {
        using var doc = Document.Parse("<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        Assert.Equal("", doc.SelectedText);

        doc.SelectAll();
        Assert.Equal("Hello World", doc.SelectedText);
        Assert.NotNull(doc.Input.Selection);
        Assert.False(doc.Input.Selection!.IsCollapsed);

        doc.ClearSelection();
        Assert.Equal("", doc.SelectedText);
        Assert.Null(doc.Input.Selection);
    }

    [Fact]
    public void InputRouter_CtrlCTriggersCopyRequested()
    {
        using var doc = Document.Parse("<p id=p>Hello World</p>", BoxFontOptions);
        doc.Paint(800, 600);

        var copyCount = 0;
        doc.Input.CopyRequested += (_, _) => copyCount++;

        // No selection: Ctrl+C does not fire
        doc.Input.HandleKeyDown(new KeyEvent("c", "KeyC", KeyModifiers.Control));
        Assert.Equal(0, copyCount);

        // Select all: Ctrl+C fires
        doc.SelectAll();
        doc.Input.HandleKeyDown(new KeyEvent("c", "KeyC", KeyModifiers.Control));
        Assert.Equal(1, copyCount);

        // Meta+C (macOS Cmd+C) also fires
        doc.Input.HandleKeyDown(new KeyEvent("C", "KeyC", KeyModifiers.Meta));
        Assert.Equal(2, copyCount);

        // Regular 'c' key without modifier does not fire
        doc.Input.HandleKeyDown(new KeyEvent("c", "KeyC", KeyModifiers.None));
        Assert.Equal(2, copyCount);
    }

    [Fact]
    public void RangeSerializer_ToWindowsClipboardHtml_FormatsCorrectCfHtmlHeader()
    {
        const string fragment = "<b>Hello World</b>";
        var cfHtml = RangeSerializer.ToWindowsClipboardHtml(fragment);

        Assert.StartsWith("Version:0.9\r\nStartHTML:", cfHtml);
        Assert.Contains("<!--StartFragment--><b>Hello World</b><!--EndFragment-->", cfHtml);

        // Verify offsets in bytes
        var lines = cfHtml.Split("\r\n");
        var startHtmlLine = lines.First(l => l.StartsWith("StartHTML:"));
        var endHtmlLine = lines.First(l => l.StartsWith("EndHTML:"));
        var startFragLine = lines.First(l => l.StartsWith("StartFragment:"));
        var endFragLine = lines.First(l => l.StartsWith("EndFragment:"));

        var startHtml = int.Parse(startHtmlLine["StartHTML:".Length..]);
        var endHtml = int.Parse(endHtmlLine["EndHTML:".Length..]);
        var startFrag = int.Parse(startFragLine["StartFragment:".Length..]);
        var endFrag = int.Parse(endFragLine["EndFragment:".Length..]);

        var utf8Bytes = Encoding.UTF8.GetBytes(cfHtml);
        var htmlTag = Encoding.UTF8.GetBytes("<html><body>");
        var fragStartMarker = Encoding.UTF8.GetBytes("<!--StartFragment-->");
        var fragEndMarker = Encoding.UTF8.GetBytes("<!--EndFragment-->");

        // StartHTML should point to <html>
        Assert.Equal(htmlTag[0], utf8Bytes[startHtml]);
        // StartFragment should point to the byte right after <!--StartFragment-->
        var actualFragBytes = utf8Bytes[startFrag..endFrag];
        Assert.Equal(fragment, Encoding.UTF8.GetString(actualFragBytes));
    }
}

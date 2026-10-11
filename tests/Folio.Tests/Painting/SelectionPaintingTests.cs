using System.Numerics;
using Folio;
using Folio.Dom;
using Folio.Interaction;
using Folio.Painting;

namespace Folio.Tests.Painting;

public class SelectionPaintingTests
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

    private const string BaseHtml = """
        <!DOCTYPE html>
        <style>
          body { margin: 0; width: 800px; height: 600px }
          p { margin: 0; font: 20px monospace }
          div { margin: 0; font: 20px monospace }
        </style>
        """;

    [Fact]
    public void SelectionAcrossTwoLinesEmitsTwoHighlightRectangles()
    {
        // Two lines via two paragraphs
        var html = BaseHtml + "<p>AAAAA</p><p>BBBBB</p>";
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        // Select all text from start to end (across both paragraphs)
        doc.Input.HandleKeyDown(new KeyEvent("a", "KeyA", KeyModifiers.Control));
        Assert.NotNull(doc.Input.Selection);

        var displayList = doc.DisplayList;
        Assert.NotNull(displayList);

        // The default selection highlight background is rgba(51, 144, 255, 0.4)
        var defaultBg = new Folio.Css.CssColor(51 / 255f, 144 / 255f, 255 / 255f, 0.4f);
        var highlightFills = displayList.Items
            .Where(it => it.Kind == DisplayItemKind.Fill && it.Color == defaultBg)
            .ToList();

        // Must produce 2 highlight rectangles (one for each paragraph/line)
        Assert.Equal(2, highlightFills.Count);
        Assert.True(highlightFills[0].Shape.Rect.Y < highlightFills[1].Shape.Rect.Y);
    }

    [Fact]
    public void SelectionInRtlRunPositionsHighlightAccurately()
    {
        // RTL paragraph with 5 chars (each 20px wide in Folio Box monospace -> 100px total width)
        var html = BaseHtml + "<p dir='rtl' style='width: 200px'>ABCDE</p>";
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        // Select "BC" (indices 1 to 3)
        var p = doc.QuerySelector("p")!;
        var textNode = (Text)p.Node.FirstChild!;
        doc.Input.Select(new RangePoint(textNode, 1), new RangePoint(textNode, 3));
        Assert.NotNull(doc.Input.Selection);

        var displayList = doc.DisplayList;
        Assert.NotNull(displayList);

        var defaultBg = new Folio.Css.CssColor(51 / 255f, 144 / 255f, 255 / 255f, 0.4f);
        var highlightFills = displayList.Items
            .Where(it => it.Kind == DisplayItemKind.Fill && it.Color == defaultBg)
            .ToList();

        Assert.Single(highlightFills);
        var rect = highlightFills[0].Shape.Rect;
        // 2 characters = 40px wide
        Assert.Equal(40, rect.Width, tolerance: 1f);
    }

    [Fact]
    public void CustomSelectionPseudoElementOverridesColors()
    {
        var html = BaseHtml + """
            <style>
              ::selection { background-color: rgb(255, 255, 0); color: rgb(255, 0, 0); }
            </style>
            <p>Hello World</p>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        doc.Input.HandleKeyDown(new KeyEvent("a", "KeyA", KeyModifiers.Control));
        Assert.NotNull(doc.Input.Selection);

        var displayList = doc.DisplayList;
        Assert.NotNull(displayList);

        var customBg = new Folio.Css.CssColor(1, 1, 0, 1); // Yellow
        var customFg = new Folio.Css.CssColor(1, 0, 0, 1); // Red

        var highlightFills = displayList.Items
            .Where(it => it.Kind == DisplayItemKind.Fill && it.Color == customBg)
            .ToList();
        // Each text fragment ("Hello " and "World") emits a highlight rectangle
        Assert.Equal(2, highlightFills.Count);

        var selectedGlyphItems = displayList.Items
            .Where(it => it.Kind == DisplayItemKind.Glyphs && it.Color == customFg)
            .ToList();
        Assert.NotEmpty(selectedGlyphItems);
    }

    [Fact]
    public void ReplacedImageSelectionDrawsHighlightTintOverlay()
    {
        // 100x100 svg image
        var html = BaseHtml + """
            <div><svg width='100' height='100'><rect width='100' height='100' fill='blue'/></svg></div>
            """;
        using var doc = Document.Parse(html, BoxFontOptions);
        doc.Paint(800, 600);

        var div = doc.QuerySelector("div")!;
        doc.Input.Select(new RangePoint(div.Node, 0), new RangePoint(div.Node, 1));
        Assert.NotNull(doc.Input.Selection);

        var displayList = doc.DisplayList;
        Assert.NotNull(displayList);

        var tint = new Folio.Css.CssColor(51 / 255f, 144 / 255f, 255 / 255f, 0.25f);
        var tintFills = displayList.Items
            .Where(it => it.Kind == DisplayItemKind.Fill && it.Color == tint)
            .ToList();
        Assert.Single(tintFills);
        Assert.Equal(100, tintFills[0].Shape.Rect.Width, tolerance: 1f);
        Assert.Equal(100, tintFills[0].Shape.Rect.Height, tolerance: 1f);
    }
}

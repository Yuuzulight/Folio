using System.Numerics;
using Folio;

namespace Folio.Tests.Interaction;

public class CursorTooltipTests
{
    private const string Page = """
        <!DOCTYPE html>
        <style>
          body { margin: 0; width: 800px; height: 600px }
          div { width: 100px; height: 50px }
          a { display: block; width: 100px; height: 50px }
        </style>
        """;

    [Fact]
    public void PlainBoxResolvesAutoCursorToDefault()
    {
        using var doc = Document.Parse(Page + "<div id=box></div>");
        doc.Paint(800, 600);

        Assert.Equal("default", doc.CursorAt(50, 25));
    }

    [Fact]
    public void SelectableTextResolvesAutoCursorToText()
    {
        using var doc = Document.Parse(Page + "<p style='margin: 0; font-size: 16px'>Hello World</p>");
        doc.Paint(800, 600);

        // Over the text glyphs
        Assert.Equal("text", doc.CursorAt(10, 8));
    }

    [Fact]
    public void UserSelectNoneTextResolvesAutoCursorToDefault()
    {
        using var doc = Document.Parse(Page + "<p style='margin: 0; font-size: 16px; user-select: none'>Hello World</p>");
        doc.Paint(800, 600);

        // Over text with user-select: none resolves to default (arrow)
        Assert.Equal("default", doc.CursorAt(10, 8));
    }

    [Fact]
    public void UserSelectInheritsToDescendantText()
    {
        using var doc = Document.Parse(Page + "<div style='user-select: none'><p style='margin: 0; font-size: 16px'><span>Inherited None</span></p></div>");
        doc.Paint(800, 600);

        Assert.Equal("default", doc.CursorAt(10, 8));
    }

    [Fact]
    public void LinkResolvesToPointerCursor()
    {
        using var doc = Document.Parse(Page + "<a id=link href='https://example.com'>Click me</a>");
        doc.Paint(800, 600);

        // In UA stylesheet :link, :visited { cursor: pointer }
        Assert.Equal("pointer", doc.CursorAt(20, 20));
    }

    [Fact]
    public void ExplicitCursorKeywordsAreRespected()
    {
        using var doc = Document.Parse(Page + """
            <div id=mover style='cursor: move'>Move</div>
            <div id=cross style='cursor: crosshair'>Cross</div>
            <div id=grab style='cursor: grab'>Grab</div>
            """);
        doc.Paint(800, 600);

        Assert.Equal("move", doc.CursorAt(50, 25));
        Assert.Equal("crosshair", doc.CursorAt(50, 75));
        Assert.Equal("grab", doc.CursorAt(50, 125));
    }

    [Fact]
    public void UrlCursorFallsBackToTrailingKeyword()
    {
        using var doc = Document.Parse(Page + "<div id=box style='cursor: url(custom.cur) 2 2, crosshair'>Custom</div>");
        doc.Paint(800, 600);

        Assert.Equal("crosshair", doc.CursorAt(50, 25));
    }

    [Fact]
    public void TitleTooltipResolvesToNearestAncestorWithTitle()
    {
        using var doc = Document.Parse(Page + """
            <div id=outer title="Outer Tooltip">
              <div id=middle>
                <span id=inner>Hover here</span>
              </div>
            </div>
            """);
        doc.Paint(800, 600);

        Assert.Equal("Outer Tooltip", doc.TitleAt(20, 20));
    }

    [Fact]
    public void CloserAncestorTitleOverridesOuter()
    {
        using var doc = Document.Parse(Page + """
            <div id=outer title="Outer Tooltip">
              <div id=middle title="Inner Tooltip">
                <span id=inner>Hover here</span>
              </div>
            </div>
            """);
        doc.Paint(800, 600);

        Assert.Equal("Inner Tooltip", doc.TitleAt(20, 20));
    }

    [Fact]
    public void EmptyTitleDoesNotShadowAncestorTitle()
    {
        using var doc = Document.Parse(Page + """
            <div id=outer title="Valid Tooltip">
              <span id=inner title="">Hover here</span>
            </div>
            """);
        doc.Paint(800, 600);

        Assert.Equal("Valid Tooltip", doc.TitleAt(20, 20));
    }

    [Fact]
    public void InputRouterRaisesCursorAndTooltipEventsOnPointerMove()
    {
        using var doc = Document.Parse(Page + """
            <div id=b1 style='cursor: move' title='Move Box'></div>
            <div id=b2 style='cursor: wait' title='Wait Box'></div>
            """);
        doc.Paint(800, 600);

        string? cursor = null;
        string? tooltip = null;
        doc.Input.CursorChanged += (_, e) => cursor = e.Cursor;
        doc.Input.TooltipChanged += (_, e) => tooltip = e.Tooltip;

        // Move over b1 (y: 25)
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(50, 25)));
        Assert.Equal("move", cursor);
        Assert.Equal("Move Box", tooltip);
        Assert.Equal("move", doc.Input.CurrentCursor);
        Assert.Equal("Move Box", doc.Input.CurrentTooltip);

        // Move over b2 (y: 75)
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(50, 75)));
        Assert.Equal("wait", cursor);
        Assert.Equal("Wait Box", tooltip);
        Assert.Equal("wait", doc.Input.CurrentCursor);
        Assert.Equal("Wait Box", doc.Input.CurrentTooltip);

        // Leave document
        doc.Input.HandlePointerLeave();
        Assert.Equal("default", cursor);
        Assert.Null(tooltip);
        Assert.Equal("default", doc.Input.CurrentCursor);
        Assert.Null(doc.Input.CurrentTooltip);
    }
}

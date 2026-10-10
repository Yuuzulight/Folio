using System.Numerics;
using Folio.Dom;
using Folio.Style;

namespace Folio.Tests.Interaction;

public class InputRouterTests
{
    private const string Page = """
        <!DOCTYPE html>
        <style>
          body { margin: 0; width: 800px; height: 600px }
          a { display: block; width: 100px; height: 50px }
          div { width: 100px; height: 50px }
        </style>
        """;

    [Fact]
    public void ClickingLinkRaisesLinkActivatedWithResolvedUri()
    {
        using var doc = Document.Parse(Page + "<a id=link href='https://example.com/target'>Link</a>", new FolioOptions { BaseUri = new Uri("https://example.com/") });
        doc.Paint(800, 600);

        LinkActivatedEventArgs? activated = null;
        doc.Input.LinkActivated += (_, e) => activated = e;

        // Pointer down on link, pointer up on link
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary, ClickCount: 1, Modifiers: KeyModifiers.Control));
        Assert.Null(activated);

        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None, ClickCount: 1, Modifiers: KeyModifiers.Control));
        Assert.NotNull(activated);
        Assert.Equal("https://example.com/target", activated!.Uri.AbsoluteUri);
        Assert.Equal(KeyModifiers.Control, activated.Modifiers);
    }

    [Fact]
    public void RelativeHrefResolvesAgainstBaseUri()
    {
        using var doc = Document.Parse(Page + "<a id=link href='about/page'>Link</a>", new FolioOptions { BaseUri = new Uri("https://example.com/dir/") });
        doc.Paint(800, 600);

        LinkActivatedEventArgs? activated = null;
        doc.Input.LinkActivated += (_, e) => activated = e;

        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));

        Assert.NotNull(activated);
        Assert.Equal("https://example.com/dir/about/page", activated!.Uri.AbsoluteUri);
    }

    [Fact]
    public void ClickingNonLinkDoesNotRaiseLinkActivated()
    {
        using var doc = Document.Parse(Page + "<div id=box>Not a link</div>", new FolioOptions { BaseUri = new Uri("https://example.com/") });
        doc.Paint(800, 600);

        var raised = false;
        doc.Input.LinkActivated += (_, _) => raised = true;

        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));

        Assert.False(raised);
    }

    [Fact]
    public void MouseUpOutsideLinkDoesNotActivateLink()
    {
        using var doc = Document.Parse(Page + "<a id=link href='https://example.com/target'>Link</a><div id=other style='margin-top: 100px'>Other</div>", new FolioOptions { BaseUri = new Uri("https://example.com/") });
        doc.Paint(800, 600);

        var raised = false;
        doc.Input.LinkActivated += (_, _) => raised = true;

        // Down inside link (20, 20), but moved and released outside link (20, 120)
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(20, 120)));
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 120), PointerButton.Primary, PointerButtons.None));

        Assert.False(raised);
    }

    [Fact]
    public void TracksHoverAndActiveStates()
    {
        using var doc = Document.Parse(Page + "<div id=box>Box</div>");
        doc.Paint(800, 600);

        Assert.Null(doc.Input.HoveredElement);
        Assert.Null(doc.Input.ActiveElement);

        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(20, 20)));
        Assert.Equal("box", doc.Input.HoveredElement?.GetAttribute("id"));

        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        Assert.Equal("box", doc.Input.ActiveElement?.GetAttribute("id"));

        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));
        Assert.Null(doc.Input.ActiveElement);

        doc.Input.HandlePointerLeave();
        Assert.Null(doc.Input.HoveredElement);
    }

    [Fact]
    public void HoverChainSetsHoverOnElementAndAncestors()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px }
              #outer { width: 300px; height: 300px }
              #inner { width: 100px; height: 100px }
            </style>
            <div id=outer><div id=inner>Inner</div></div>
            """;
        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var outer = doc.QuerySelector("#outer")!.Node;
        var inner = doc.QuerySelector("#inner")!.Node;
        var body = doc.QuerySelector("body")!.Node;

        // Move over inner (20, 20)
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(20, 20)));
        Assert.Equal("inner", doc.Input.HoveredElement?.GetAttribute("id"));
        Assert.True((inner.Flags & NodeFlags.Hover) != 0, "inner should have Hover flag");
        Assert.True((outer.Flags & NodeFlags.Hover) != 0, "outer should have Hover flag");
        Assert.True((body.Flags & NodeFlags.Hover) != 0, "body should have Hover flag");

        // Move to outer but outside inner (200, 200)
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(200, 200)));
        Assert.Equal("outer", doc.Input.HoveredElement?.GetAttribute("id"));
        Assert.False((inner.Flags & NodeFlags.Hover) != 0, "inner should not have Hover flag");
        Assert.True((outer.Flags & NodeFlags.Hover) != 0, "outer should still have Hover flag");
        Assert.True((body.Flags & NodeFlags.Hover) != 0, "body should still have Hover flag");
    }

    [Fact]
    public void HoverMovingBetweenSiblingsClearsOldAndSetsNew()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px }
              #first { width: 100px; height: 50px }
              #second { width: 100px; height: 50px }
            </style>
            <div id=first>First</div>
            <div id=second>Second</div>
            """;
        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var first = doc.QuerySelector("#first")!.Node;
        var second = doc.QuerySelector("#second")!.Node;

        // Hover first
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(20, 20)));
        Assert.Equal("first", doc.Input.HoveredElement?.GetAttribute("id"));
        Assert.True((first.Flags & NodeFlags.Hover) != 0);
        Assert.False((second.Flags & NodeFlags.Hover) != 0);

        // Hover second (y: 50..100)
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(20, 70)));
        Assert.Equal("second", doc.Input.HoveredElement?.GetAttribute("id"));
        Assert.False((first.Flags & NodeFlags.Hover) != 0);
        Assert.True((second.Flags & NodeFlags.Hover) != 0);

        // Leave control
        doc.Input.HandlePointerLeave();
        Assert.Null(doc.Input.HoveredElement);
        Assert.False((first.Flags & NodeFlags.Hover) != 0);
        Assert.False((second.Flags & NodeFlags.Hover) != 0);
    }

    [Fact]
    public void ActiveStateAppliesOnlyWhilePressed()
    {
        using var doc = Document.Parse(Page + "<div id=box>Box</div>");
        doc.Paint(800, 600);

        var box = doc.QuerySelector("#box")!.Node;
        var body = doc.QuerySelector("body")!.Node;

        Assert.False((box.Flags & NodeFlags.Active) != 0);

        // Press down
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        Assert.Equal("box", doc.Input.ActiveElement?.GetAttribute("id"));
        Assert.True((box.Flags & NodeFlags.Active) != 0);
        Assert.True((body.Flags & NodeFlags.Active) != 0);

        // Release
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));
        Assert.Null(doc.Input.ActiveElement);
        Assert.False((box.Flags & NodeFlags.Active) != 0);
        Assert.False((body.Flags & NodeFlags.Active) != 0);
    }

    [Fact]
    public void HoverAndActiveDriveDocumentUpdate()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; width: 800px; height: 600px }
              #box { width: 100px; height: 50px; background-color: blue }
              #box:hover { background-color: red }
              #box:active { background-color: green }
            </style>
            <div id=box>Box</div>
            """;
        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        // Hover should trigger Update() and restyle #box to red
        doc.Input.HandlePointerMove(new PointerEvent(new Vector2(20, 20)));
        var box = doc.QuerySelector("#box")!.Node;
        Assert.True((box.Flags & NodeFlags.Hover) != 0);
        using (var freshHover = Document.Parse(html))
        {
            freshHover.QuerySelector("#box")!.Node.Flags |= NodeFlags.Hover;
            var (full, _) = freshHover.Paint(800, 600);
            Assert.Equal(full.Items, doc.DisplayList!.Items);
        }

        // Press down -> active should restyle #box to green
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.Primary));
        Assert.True((box.Flags & NodeFlags.Active) != 0);
        using (var freshActive = Document.Parse(html))
        {
            freshActive.QuerySelector("#box")!.Node.Flags |= NodeFlags.Hover | NodeFlags.Active;
            var (full, _) = freshActive.Paint(800, 600);
            Assert.Equal(full.Items, doc.DisplayList!.Items);
        }

        // Release -> back to hover (red)
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary, PointerButtons.None));
        Assert.False((box.Flags & NodeFlags.Active) != 0);
        Assert.True((box.Flags & NodeFlags.Hover) != 0);
        using (var freshHover = Document.Parse(html))
        {
            freshHover.QuerySelector("#box")!.Node.Flags |= NodeFlags.Hover;
            var (full, _) = freshHover.Paint(800, 600);
            Assert.Equal(full.Items, doc.DisplayList!.Items);
        }

        // Pointer leave -> back to blue
        doc.Input.HandlePointerLeave();
        Assert.False((box.Flags & NodeFlags.Hover) != 0);
        using (var freshNormal = Document.Parse(html))
        {
            var (full, _) = freshNormal.Paint(800, 600);
            Assert.Equal(full.Items, doc.DisplayList!.Items);
        }
    }
}

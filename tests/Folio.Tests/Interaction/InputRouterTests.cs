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

    [Fact]
    public void TabOrderTraversesPositiveTabIndexThenZeroAndSkipsNegativeAndDisabled()
    {
        const string html = """
            <!DOCTYPE html>
            <style>body { margin: 0; }</style>
            <a id="link1" href="https://example.com" tabindex="2">Link 1</a>
            <button id="btn1" tabindex="1">Button 1</button>
            <input id="input1" type="text" tabindex="0">
            <a id="link2" href="https://example.com">Link 2</a>
            <input id="neg" type="text" tabindex="-1">
            <button id="dis" disabled>Disabled</button>
            <fieldset disabled>
              <button id="fs-dis">FS Disabled</button>
            </fieldset>
            <button id="btn2" tabindex="2">Button 2</button>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        // Positive tabindex in ascending order, stable in document tree order for same tabindex:
        // tabindex 1: #btn1
        // tabindex 2: #link1, #btn2
        // tabindex 0: #input1, #link2
        // Skipped: #neg (tabindex -1), #dis (disabled), #fs-dis (in disabled fieldset)
        var seq = doc.Input.GetSequentialFocusableElements();
        var ids = seq.Select(e => e.GetAttribute("id")).ToList();
        Assert.Equal(["btn1", "link1", "btn2", "input1", "link2"], ids);

        // Test Tab navigation forward
        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("btn1", doc.Input.FocusedElement?.Id);
        Assert.True((doc.Input.FocusedElement!.Node.Flags & NodeFlags.FocusVisible) != 0);

        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("link1", doc.Input.FocusedElement?.Id);

        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("btn2", doc.Input.FocusedElement?.Id);

        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("input1", doc.Input.FocusedElement?.Id);

        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("link2", doc.Input.FocusedElement?.Id);

        // Wraps around to first
        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("btn1", doc.Input.FocusedElement?.Id);

        // Shift+Tab navigates in reverse
        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab", Modifiers: KeyModifiers.Shift));
        Assert.Equal("link2", doc.Input.FocusedElement?.Id);
    }

    [Fact]
    public void ClickFocusDoesNotSetFocusVisibleForLinkWhereasTabDoes()
    {
        const string html = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; }
              a { display: block; width: 100px; height: 50px; }
            </style>
            <a id="link" href="https://example.com">My Link</a>
            """;

        using var doc = Document.Parse(html);
        doc.Paint(800, 600);

        var link = doc.GetElementById("link")!;

        // 1. Click-to-focus
        doc.Input.HandlePointerDown(new PointerEvent(new Vector2(20, 20), PointerButton.Primary));
        doc.Input.HandlePointerUp(new PointerEvent(new Vector2(20, 20), PointerButton.Primary));

        Assert.Equal(link, doc.Input.FocusedElement);
        Assert.True((link.Node.Flags & NodeFlags.Focus) != 0, "Link should have :focus");
        Assert.False((link.Node.Flags & NodeFlags.FocusVisible) != 0, "Link should NOT have :focus-visible on mouse click");

        // 2. Clear focus via Blur
        link.Blur();
        Assert.Null(doc.Input.FocusedElement);
        Assert.False((link.Node.Flags & NodeFlags.Focus) != 0);

        // 3. Focus via Tab key
        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal(link, doc.Input.FocusedElement);
        Assert.True((link.Node.Flags & NodeFlags.Focus) != 0, "Link should have :focus on Tab");
        Assert.True((link.Node.Flags & NodeFlags.FocusVisible) != 0, "Link should have :focus-visible on Tab");
    }

    [Fact]
    public void EnterOnFocusedLinkRaisesLinkActivated()
    {
        const string html = """
            <!DOCTYPE html>
            <style>body { margin: 0; }</style>
            <a id="link" href="https://example.com/target">My Link</a>
            """;

        using var doc = Document.Parse(html, new FolioOptions { BaseUri = new Uri("https://example.com/") });
        doc.Paint(800, 600);

        LinkActivatedEventArgs? activated = null;
        doc.Input.LinkActivated += (_, e) => activated = e;

        // Tab to link
        doc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("link", doc.Input.FocusedElement?.Id);
        Assert.Null(activated);

        // Press Enter with modifier
        doc.Input.HandleKeyDown(new KeyEvent("Enter", "Enter", Modifiers: KeyModifiers.Control));
        Assert.NotNull(activated);
        Assert.Equal("https://example.com/target", activated!.Uri.AbsoluteUri);
        Assert.Equal(KeyModifiers.Control, activated.Modifiers);
    }

    [Fact]
    public void FocusRingDrawnForFocusedLinkEqualsAuthorOutline()
    {
        // Issue #425 acceptance: Reftest: the focus ring drawn for a focused link equals an outline drawn by author CSS.
        const string testHtml = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; }
              a { display: block; margin: 20px; width: 100px; height: 30px; }
            </style>
            <a id="link" href="https://example.com">Click me</a>
            """;

        const string refHtml = """
            <!DOCTYPE html>
            <style>
              body { margin: 0; }
              a { display: block; margin: 20px; width: 100px; height: 30px; outline: auto 5px -webkit-focus-ring-color; }
            </style>
            <a id="link" href="https://example.com">Click me</a>
            """;

        using var testDoc = Document.Parse(testHtml);
        testDoc.Paint(800, 600);

        // Focus link via keyboard (Tab) so it gets :focus-visible
        testDoc.Input.HandleKeyDown(new KeyEvent("Tab", "Tab"));
        Assert.Equal("link", testDoc.Input.FocusedElement?.Id);
        Assert.True((testDoc.Input.FocusedElement!.Node.Flags & NodeFlags.FocusVisible) != 0);

        using var refDoc = Document.Parse(refHtml);
        refDoc.Paint(800, 600);

        // Display items of testDoc (styled via UA :focus-visible { outline: auto 5px -webkit-focus-ring-color })
        // should exactly match refDoc (styled with author CSS outline: auto 5px -webkit-focus-ring-color).
        Assert.Equal(refDoc.DisplayList!.Items, testDoc.DisplayList!.Items);
    }
}


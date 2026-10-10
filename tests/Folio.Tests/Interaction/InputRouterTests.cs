using System.Numerics;
using Folio.Dom;

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
}

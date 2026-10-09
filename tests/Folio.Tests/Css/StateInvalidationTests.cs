using System.Text;
using Folio.Dom;

namespace Folio.Tests.Css;

// #417: a user-action state change restyles only the elements its rules can reach, and the result is the same as
// styling the whole document with that state set.
public class StateInvalidationTests
{
    private static string Table(int rows)
    {
        var html = new StringBuilder("<!DOCTYPE html><style>tr:hover td { color: red }</style><table>");
        for (var i = 0; i < rows; i++)
            html.Append($"<tr id=r{i}><td>a{i}</td><td>b{i}</td><td>c{i}</td></tr>");
        return html.Append("</table>").ToString();
    }

    // The display list after toggling `state` on the element `selector` and updating, and the one a fresh document
    // with that state set paints.
    private static void AssertSameAsFresh(Document document, string html, string selector, NodeFlags state)
    {
        using var fresh = Document.Parse(html);
        fresh.QuerySelector(selector)!.Node.Flags |= state;
        var (full, _) = fresh.Paint(800, 600);
        Assert.Equal(full.Items, document.DisplayList!.Items);
    }

    [Fact]
    public void HoveringARowRestylesThatRowsCellsOnly()
    {
        var html = Table(1000);
        using var document = Document.Parse(html);
        document.Paint(800, 600);
        var row = document.QuerySelector("#r500")!.Node;

        document.SetState(row, NodeFlags.Hover, true);
        document.Update();

        Assert.Equal(3, document.RestyleCount);
        AssertSameAsFresh(document, html, "#r500", NodeFlags.Hover);

        document.SetState(row, NodeFlags.Hover, false);
        document.Update();
        Assert.Equal(3, document.RestyleCount);
        using var plain = Document.Parse(html);
        Assert.Equal(plain.Paint(800, 600).Item1.Items, document.DisplayList!.Items);
    }

    [Fact]
    public void HoveringACardRestylesItsTitleOnly()
    {
        const string html = "<!DOCTYPE html><style>.card:hover .title { color: red }</style>"
            + "<div class=card id=one><h2 class=title>One</h2><p>Body</p></div>"
            + "<div class=card><h2 class=title>Two</h2><p>Body</p></div>";
        using var document = Document.Parse(html);
        document.Paint(800, 600);

        document.SetState(document.QuerySelector("#one")!.Node, NodeFlags.Hover, true);
        document.Update();

        Assert.Equal(1, document.RestyleCount);
        AssertSameAsFresh(document, html, "#one", NodeFlags.Hover);
    }

    [Fact]
    public void HoveringRestylesTheFollowingSibling()
    {
        const string html = "<!DOCTYPE html><style>.a:hover + .x { color: red }</style>"
            + "<div class=a id=a>a</div><div class=x>x</div><div class=y>y</div>";
        using var document = Document.Parse(html);
        document.Paint(800, 600);

        document.SetState(document.QuerySelector("#a")!.Node, NodeFlags.Hover, true);
        document.Update();

        Assert.Equal(1, document.RestyleCount);
        AssertSameAsFresh(document, html, "#a", NodeFlags.Hover);
    }

    [Fact]
    public void WithoutHoverRulesHoveringRestylesNothing()
    {
        const string html = "<!DOCTYPE html><style>p { color: blue }</style><p id=p>text</p>";
        using var document = Document.Parse(html);
        var (before, _) = document.Paint(800, 600);

        document.SetState(document.QuerySelector("#p")!.Node, NodeFlags.Hover, true);
        document.Update();

        Assert.Equal(0, document.RestyleCount);
        Assert.Equal(before.Items, document.DisplayList!.Items);
    }

    [Fact]
    public void FocusRestylesTheAncestorsWithFocusWithin()
    {
        const string html = "<!DOCTYPE html><style>.form:focus-within { background-color: yellow }</style>"
            + "<div class=form><span><b id=field>field</b></span></div><div>other</div>";
        using var document = Document.Parse(html);
        document.Paint(800, 600);

        document.SetState(document.QuerySelector("#field")!.Node, NodeFlags.Focus, true);
        document.Update();

        // The form and its subtree (span, b).
        Assert.Equal(3, document.RestyleCount);
        AssertSameAsFresh(document, html, "#field", NodeFlags.Focus);
    }

    [Fact]
    public void HoverInsideHasRestylesTheWholeDocument()
    {
        const string html = "<!DOCTYPE html><style>section:has(a:hover) { color: red }</style>"
            + "<section><p><a id=link href=#>link</a></p></section>";
        using var document = Document.Parse(html);
        document.Paint(800, 600);

        document.SetState(document.QuerySelector("#link")!.Node, NodeFlags.Hover, true);
        document.Update();

        // html, head, style, body, section, p, a.
        Assert.Equal(7, document.RestyleCount);
        AssertSameAsFresh(document, html, "#link", NodeFlags.Hover);
    }

    [Fact]
    public void AnAttributeChangeStillRestylesEverything()
    {
        const string html = "<!DOCTYPE html><style>tr:hover td { color: red }</style><p id=p>text</p><p>more</p>";
        using var document = Document.Parse(html);
        document.Paint(800, 600);

        document.QuerySelector("#p")!.SetAttribute("class", "x");
        document.Update();

        Assert.Equal(6, document.RestyleCount);
    }
}

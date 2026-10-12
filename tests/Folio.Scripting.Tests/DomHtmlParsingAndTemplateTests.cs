using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class DomHtmlParsingAndTemplateTests
{
    private sealed class DomSession : IDisposable
    {
        public required JintScriptEngine Engine { get; init; }
        public required DocumentNode Document { get; init; }
        public required ScriptWrapperCache Cache { get; init; }

        public void Dispose() => Engine.Dispose();
    }

    private static DomSession CreateDomSession(string html = "<!DOCTYPE html><html><head><title>Test</title></head><body><div id='container'><p id='p1'>Hello</p></div></body></html>")
    {
        var doc = TreeBuilder.Parse(html);
        var engine = new JintScriptEngine();
        var cache = DomBindings.Install(engine, doc);
        return new DomSession { Engine = engine, Document = doc, Cache = cache };
    }

    [Fact]
    public void InnerHtml_Getter_SerializesChildrenAccurately()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        var html = engine.Evaluate("document.getElementById('container').innerHTML").AsString();
        Assert.Equal("<p id=\"p1\">Hello</p>", html);
    }

    [Fact]
    public void InnerHtml_Getter_EscapesSpecialCharactersAndHandlesVoidElements()
    {
        using var session = CreateDomSession(
            "<!DOCTYPE html><html><body><div id='box'><span>a &amp; b &lt; c &gt; d</span><img src=\"foo.png\"><br></div></body></html>");
        var engine = session.Engine;

        var html = engine.Evaluate("document.getElementById('box').innerHTML").AsString();
        Assert.Equal("<span>a &amp; b &lt; c &gt; d</span><img src=\"foo.png\"><br>", html);
    }

    [Fact]
    public void InnerHtml_Getter_DoesNotEscapeInsideRawTextElements()
    {
        using var session = CreateDomSession(
            "<!DOCTYPE html><html><body><div id='box'><style>body > div { color: red & blue; }</style><script>if (a < b && c > d) {}</script></div></body></html>");
        var engine = session.Engine;

        var html = engine.Evaluate("document.getElementById('box').innerHTML").AsString();
        Assert.Equal("<style>body > div { color: red & blue; }</style><script>if (a < b && c > d) {}</script>", html);
    }

    [Fact]
    public void InnerHtml_Setter_ParsesAndReplacesChildren()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const box = document.getElementById('container'); box.innerHTML = '<span class=\"highlight\">Hi <b>there</b></span><hr>';");

        Assert.Equal(2, engine.Evaluate("box.childNodes.length").AsNumber());
        Assert.Equal("SPAN", engine.Evaluate("box.firstChild.tagName").AsString());
        Assert.Equal("highlight", engine.Evaluate("box.firstChild.className").AsString());
        Assert.Equal("Hi there", engine.Evaluate("box.firstChild.textContent").AsString());
        Assert.Equal("HR", engine.Evaluate("box.lastChild.tagName").AsString());
        Assert.Equal("<span class=\"highlight\">Hi <b>there</b></span><hr>", engine.Evaluate("box.innerHTML").AsString());
    }

    [Fact]
    public void InnerHtml_Setter_ParsesInContextOfElement()
    {
        // In context of <tr>, <td> elements parse correctly without being foster-parented
        using var session = CreateDomSession("<!DOCTYPE html><html><body><table><tr id='row'></tr></table></body></html>");
        var engine = session.Engine;

        engine.Evaluate("const tr = document.getElementById('row'); tr.innerHTML = '<td>Cell 1</td><td>Cell 2</td>';");

        Assert.Equal(2, engine.Evaluate("tr.children.length").AsNumber());
        Assert.Equal("TD", engine.Evaluate("tr.firstElementChild.tagName").AsString());
        Assert.Equal("Cell 1", engine.Evaluate("tr.firstElementChild.textContent").AsString());
        Assert.Equal("<td>Cell 1</td><td>Cell 2</td>", engine.Evaluate("tr.innerHTML").AsString());
    }

    [Fact]
    public void OuterHtml_Getter_SerializesElementAndChildren()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        var html = engine.Evaluate("document.getElementById('p1').outerHTML").AsString();
        Assert.Equal("<p id=\"p1\">Hello</p>", html);
    }

    [Fact]
    public void OuterHtml_Setter_ReplacesElementInParent()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const p = document.getElementById('p1'); p.outerHTML = '<h1 id=\"heading\">Title</h1><p>Para</p>';");

        var containerHtml = engine.Evaluate("document.getElementById('container').innerHTML").AsString();
        Assert.Equal("<h1 id=\"heading\">Title</h1><p>Para</p>", containerHtml);
        Assert.True(engine.Evaluate("p.parentNode").IsNull);
    }

    [Fact]
    public void OuterHtml_Setter_ThrowsWhenNoParentOrParentIsDocument()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // Disconnected element
        var err1 = engine.Evaluate(@"
            try {
                const div = document.createElement('div');
                div.outerHTML = '<span>fail</span>';
                'ok';
            } catch (e) {
                e.name + ': ' + e.message;
            }
        ").AsString();
        Assert.StartsWith("NoModificationAllowedError", err1);

        // documentElement (parent is Document)
        var err2 = engine.Evaluate(@"
            try {
                document.documentElement.outerHTML = '<html><body>fail</body></html>';
                'ok';
            } catch (e) {
                e.name + ': ' + e.message;
            }
        ").AsString();
        Assert.StartsWith("NoModificationAllowedError", err2);
    }

    [Fact]
    public void InsertAdjacentHTML_BeforeBegin_InsertsBeforeTarget()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const p = document.getElementById('p1'); p.insertAdjacentHTML('beforebegin', '<span>Before</span>');");

        var html = engine.Evaluate("document.getElementById('container').innerHTML").AsString();
        Assert.Equal("<span>Before</span><p id=\"p1\">Hello</p>", html);
    }

    [Fact]
    public void InsertAdjacentHTML_AfterBegin_InsertsAtStartOfTargetChildren()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const p = document.getElementById('p1'); p.insertAdjacentHTML('afterbegin', '<b>Start: </b>');");

        var html = engine.Evaluate("p.innerHTML").AsString();
        Assert.Equal("<b>Start: </b>Hello", html);
    }

    [Fact]
    public void InsertAdjacentHTML_BeforeEnd_InsertsAtEndOfTargetChildren()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const p = document.getElementById('p1'); p.insertAdjacentHTML('beforeend', ' <em>World</em>');");

        var html = engine.Evaluate("p.innerHTML").AsString();
        Assert.Equal("Hello <em>World</em>", html);
    }

    [Fact]
    public void InsertAdjacentHTML_AfterEnd_InsertsAfterTarget()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const p = document.getElementById('p1'); p.insertAdjacentHTML('afterend', '<span>After</span>');");

        var html = engine.Evaluate("document.getElementById('container').innerHTML").AsString();
        Assert.Equal("<p id=\"p1\">Hello</p><span>After</span>", html);
    }

    [Fact]
    public void InsertAdjacentHTML_CaseInsensitivePositions()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const p = document.getElementById('p1'); p.insertAdjacentHTML('BEFOREBEGIN', '<i>1</i>');");
        engine.Evaluate("p.insertAdjacentHTML('AfterBegin', '<i>2</i>');");
        engine.Evaluate("p.insertAdjacentHTML('BeforeEnd', '<i>3</i>');");
        engine.Evaluate("p.insertAdjacentHTML('AFTEREND', '<i>4</i>');");

        var html = engine.Evaluate("document.getElementById('container').innerHTML").AsString();
        Assert.Equal("<i>1</i><p id=\"p1\"><i>2</i>Hello<i>3</i></p><i>4</i>", html);
    }

    [Fact]
    public void InsertAdjacentHTML_ThrowsOnInvalidPosition()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        var err = engine.Evaluate(@"
            try {
                const p = document.getElementById('p1');
                p.insertAdjacentHTML('invalid_pos', '<div/>');
                'ok';
            } catch (e) {
                e.name;
            }
        ").AsString();

        Assert.Equal("SyntaxError", err);
    }

    [Fact]
    public void InsertAdjacentHTML_ThrowsNoModificationAllowedError_ForBeforeBeginAndAfterEndWithoutParent()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        var errBefore = engine.Evaluate(@"
            try {
                const div = document.createElement('div');
                div.insertAdjacentHTML('beforebegin', '<span/>');
                'ok';
            } catch (e) {
                e.name;
            }
        ").AsString();
        Assert.Equal("NoModificationAllowedError", errBefore);

        var errAfter = engine.Evaluate(@"
            try {
                const div = document.createElement('div');
                div.insertAdjacentHTML('afterend', '<span/>');
                'ok';
            } catch (e) {
                e.name;
            }
        ").AsString();
        Assert.Equal("NoModificationAllowedError", errAfter);
    }

    [Fact]
    public void TemplateElement_InstanceofAndContentProperty()
    {
        using var session = CreateDomSession("<!DOCTYPE html><html><body><template id='tpl'><div class='card'>Card Content</div></template></body></html>");
        var engine = session.Engine;

        Assert.True(engine.Evaluate("const tpl = document.getElementById('tpl'); tpl instanceof HTMLTemplateElement").AsBoolean());
        Assert.True(engine.Evaluate("tpl instanceof HTMLElement").AsBoolean());
        Assert.True(engine.Evaluate("tpl.content instanceof DocumentFragment").AsBoolean());

        // Template content is inert and kept in its DocumentFragment
        Assert.Equal(0, engine.Evaluate("tpl.childNodes.length").AsNumber());
        Assert.Equal(1, engine.Evaluate("tpl.content.childNodes.length").AsNumber());
        Assert.Equal("DIV", engine.Evaluate("tpl.content.firstElementChild.tagName").AsString());
        Assert.Equal("card", engine.Evaluate("tpl.content.firstElementChild.className").AsString());
        Assert.Equal("Card Content", engine.Evaluate("tpl.content.firstElementChild.textContent").AsString());
    }

    [Fact]
    public void TemplateElement_InnerHTML_SerializesAndSetsContent()
    {
        using var session = CreateDomSession("<!DOCTYPE html><html><body><template id='tpl'><span>Original</span></template></body></html>");
        var engine = session.Engine;

        Assert.Equal("<span>Original</span>", engine.Evaluate("document.getElementById('tpl').innerHTML").AsString());

        engine.Evaluate("document.getElementById('tpl').innerHTML = '<p>Updated</p>';");

        Assert.Equal("<p>Updated</p>", engine.Evaluate("document.getElementById('tpl').innerHTML").AsString());
        Assert.Equal(1, engine.Evaluate("document.getElementById('tpl').content.childNodes.length").AsNumber());
        Assert.Equal("P", engine.Evaluate("document.getElementById('tpl').content.firstChild.tagName").AsString());
        Assert.Equal("Updated", engine.Evaluate("document.getElementById('tpl').content.firstChild.textContent").AsString());
    }

    [Fact]
    public void TemplateElement_CloningClonesContentFragment()
    {
        using var session = CreateDomSession("<!DOCTYPE html><html><body><template id='tpl'><span>Clone Me</span></template></body></html>");
        var engine = session.Engine;

        engine.Evaluate(@"
            const tpl = document.getElementById('tpl');
            const clone = tpl.cloneNode(true);
        ");

        Assert.True(engine.Evaluate("clone instanceof HTMLTemplateElement").AsBoolean());
        Assert.True(engine.Evaluate("clone.content !== tpl.content").AsBoolean());
        Assert.Equal("<span>Clone Me</span>", engine.Evaluate("clone.innerHTML").AsString());
        Assert.Equal("Clone Me", engine.Evaluate("clone.content.firstElementChild.textContent").AsString());
    }

    [Fact]
    public void CreatedTemplateElement_HasDocumentFragmentContent()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const tpl = document.createElement('template');
            tpl.innerHTML = '<div>Created dynamically</div>';
        ");

        Assert.True(engine.Evaluate("tpl instanceof HTMLTemplateElement").AsBoolean());
        Assert.True(engine.Evaluate("tpl.content instanceof DocumentFragment").AsBoolean());
        Assert.Equal("<div>Created dynamically</div>", engine.Evaluate("tpl.innerHTML").AsString());
        Assert.Equal("Created dynamically", engine.Evaluate("tpl.content.firstElementChild.textContent").AsString());
    }
}

using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class DomAttributesAndReflectedTests
{
    private sealed class DomSession : IDisposable
    {
        public required JintScriptEngine Engine { get; init; }
        public required DocumentNode Document { get; init; }
        public required ScriptWrapperCache Cache { get; init; }

        public void Dispose() => Engine.Dispose();
    }

    private static DomSession CreateDomSession(string html = "<!DOCTYPE html><html><head><title>Test</title></head><body><div id='container' class='alpha beta' data-user-id='42' data-user-name='Alice'>Hello</div></body></html>")
    {
        var doc = TreeBuilder.Parse(html);
        var engine = new JintScriptEngine();
        var cache = DomBindings.Install(engine, doc);
        return new DomSession { Engine = engine, Document = doc, Cache = cache };
    }

    [Fact]
    public void ClassList_LengthItemContains()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.Equal(2, engine.Evaluate("const el = document.getElementById('container'); el.classList.length").AsNumber());
        Assert.Equal("alpha", engine.Evaluate("el.classList.item(0)").AsString());
        Assert.Equal("beta", engine.Evaluate("el.classList.item(1)").AsString());
        Assert.True(engine.Evaluate("el.classList.item(2) === null").AsBoolean());
        Assert.Equal("alpha", engine.Evaluate("el.classList[0]").AsString());
        Assert.Equal("beta", engine.Evaluate("el.classList[1]").AsString());
        Assert.True(engine.Evaluate("el.classList[2] === undefined").AsBoolean());

        Assert.True(engine.Evaluate("el.classList.contains('alpha')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('beta')").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());
    }

    [Fact]
    public void ClassList_AddAndRemove()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const el = document.getElementById('container'); el.classList.add('gamma', 'delta');");
        Assert.Equal(4, engine.Evaluate("el.classList.length").AsNumber());
        Assert.True(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('delta')").AsBoolean());
        Assert.Equal("alpha beta gamma delta", engine.Evaluate("el.className").AsString());
        Assert.Equal("alpha beta gamma delta", engine.Evaluate("el.getAttribute('class')").AsString());

        engine.Evaluate("el.classList.remove('beta', 'gamma');");
        Assert.Equal(2, engine.Evaluate("el.classList.length").AsNumber());
        Assert.False(engine.Evaluate("el.classList.contains('beta')").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('alpha')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('delta')").AsBoolean());
    }

    [Fact]
    public void ClassList_Toggle()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // Toggle non-existent -> added, returns true
        Assert.True(engine.Evaluate("const el = document.getElementById('container'); el.classList.toggle('gamma')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());

        // Toggle existing -> removed, returns false
        Assert.False(engine.Evaluate("el.classList.toggle('gamma')").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());

        // Toggle with force = true
        Assert.True(engine.Evaluate("el.classList.toggle('gamma', true)").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.toggle('gamma', true)").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());

        // Toggle with force = false
        Assert.False(engine.Evaluate("el.classList.toggle('gamma', false)").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.toggle('gamma', false)").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.contains('gamma')").AsBoolean());
    }

    [Fact]
    public void ClassList_Replace()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.True(engine.Evaluate("const el = document.getElementById('container'); el.classList.replace('alpha', 'zeta')").AsBoolean());
        Assert.False(engine.Evaluate("el.classList.contains('alpha')").AsBoolean());
        Assert.True(engine.Evaluate("el.classList.contains('zeta')").AsBoolean());

        // Replace non-existent token returns false
        Assert.False(engine.Evaluate("el.classList.replace('nonexistent', 'omega')").AsBoolean());
    }

    [Fact]
    public void ClassList_ValueAndIteration()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.Equal("alpha beta", engine.Evaluate("const el = document.getElementById('container'); el.classList.value").AsString());
        engine.Evaluate("el.classList.value = 'one two three';");
        Assert.Equal(3, engine.Evaluate("el.classList.length").AsNumber());
        Assert.Equal("one two three", engine.Evaluate("el.className").AsString());

        // for...of iterator
        engine.Evaluate(@"
            const collected = [];
            for (const c of el.classList) {
                collected.push(c);
            }
        ");
        Assert.Equal("one,two,three", engine.Evaluate("collected.join(',')").AsString());

        // forEach
        engine.Evaluate(@"
            const forEachCollected = [];
            el.classList.forEach((val, idx) => forEachCollected.push(idx + ':' + val));
        ");
        Assert.Equal("0:one,1:two,2:three", engine.Evaluate("forEachCollected.join(',')").AsString());
    }

    [Fact]
    public void ClassList_ThrowsOnEmptyOrWhitespaceToken()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // Empty token -> SyntaxError (code 12)
        Assert.ThrowsAny<Exception>(() => engine.Evaluate("document.getElementById('container').classList.add('')"));
        Assert.ThrowsAny<Exception>(() => engine.Evaluate("document.getElementById('container').classList.contains('')"));

        // Whitespace token -> InvalidCharacterError (code 5)
        Assert.ThrowsAny<Exception>(() => engine.Evaluate("document.getElementById('container').classList.add('foo bar')"));
        Assert.ThrowsAny<Exception>(() => engine.Evaluate("document.getElementById('container').classList.toggle('bad token')"));
    }

    [Fact]
    public void Dataset_CamelCaseToKebabCaseReflection()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // Reading initial data-* attributes
        Assert.Equal("42", engine.Evaluate("const el = document.getElementById('container'); el.dataset.userId").AsString());
        Assert.Equal("Alice", engine.Evaluate("el.dataset.userName").AsString());
        Assert.True(engine.Evaluate("el.dataset.nonExistent === undefined").AsBoolean());

        // Setting dataset properties reflects in element attribute
        engine.Evaluate("el.dataset.userRole = 'admin';");
        Assert.Equal("admin", engine.Evaluate("el.getAttribute('data-user-role')").AsString());
        Assert.Equal("admin", engine.Evaluate("el.dataset.userRole").AsString());

        // Deleting dataset property removes the attribute
        Assert.True(engine.Evaluate("delete el.dataset.userId").AsBoolean());
        Assert.True(engine.Evaluate("el.getAttribute('data-user-id') === null").AsBoolean());
        Assert.True(engine.Evaluate("el.dataset.userId === undefined").AsBoolean());
    }

    [Fact]
    public void Dataset_ObjectKeysEnumeration()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate("const el = document.getElementById('container'); const keys = Object.keys(el.dataset);");
        Assert.Equal(2, engine.Evaluate("keys.length").AsNumber());
        Assert.True(engine.Evaluate("keys.includes('userId')").AsBoolean());
        Assert.True(engine.Evaluate("keys.includes('userName')").AsBoolean());
    }

    [Fact]
    public void Dataset_InvalidPropertyNameThrowsSyntaxError()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // Dash followed by ASCII lowercase is prohibited in dataset property names
        Assert.ThrowsAny<Exception>(() => engine.Evaluate("document.getElementById('container').dataset['foo-bar'] = 'baz';"));
    }

    [Fact]
    public void Attributes_NamedNodeMapAndAttr()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.True(engine.Evaluate("const el = document.getElementById('container'); el.attributes instanceof NamedNodeMap").AsBoolean());
        var len = engine.Evaluate("el.attributes.length").AsNumber();
        Assert.True(len >= 4); // id, class, data-user-id, data-user-name

        // item(i)
        Assert.True(engine.Evaluate("const attr0 = el.attributes.item(0); attr0 instanceof Attr").AsBoolean());
        Assert.True(engine.Evaluate("attr0.ownerElement === el").AsBoolean());

        // getNamedItem
        Assert.True(engine.Evaluate("const idAttr = el.attributes.getNamedItem('id'); idAttr.value === 'container'").AsBoolean());
        Assert.Equal("id", engine.Evaluate("idAttr.name").AsString());

        // Named getter: el.attributes['id'] or el.attributes.id
        Assert.True(engine.Evaluate("el.attributes['id'] === idAttr").AsBoolean());

        // Modifying attr.value syncs with element attribute
        engine.Evaluate("idAttr.value = 'new-container';");
        Assert.Equal("new-container", engine.Evaluate("el.getAttribute('id')").AsString());
        Assert.Equal("new-container", engine.Evaluate("el.id").AsString());

        // removeNamedItem
        Assert.True(engine.Evaluate("const removed = el.attributes.removeNamedItem('class'); removed.value === 'alpha beta'").AsBoolean());
        Assert.True(engine.Evaluate("removed.ownerElement === null").AsBoolean());
        Assert.True(engine.Evaluate("el.getAttribute('class') === null").AsBoolean());

        // setNamedItem
        engine.Evaluate("el.attributes.setNamedItem(removed);");
        Assert.Equal("alpha beta", engine.Evaluate("el.getAttribute('class')").AsString());
        Assert.True(engine.Evaluate("removed.ownerElement === el").AsBoolean());

        // Iteration
        engine.Evaluate(@"
            const attrNames = [];
            for (const a of el.attributes) {
                attrNames.push(a.name);
            }
        ");
        Assert.True(engine.Evaluate("attrNames.includes('id')").AsBoolean());
        Assert.True(engine.Evaluate("attrNames.includes('class')").AsBoolean());
    }

    [Fact]
    public void Element_ExtendedAttributeMethods()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // toggleAttribute
        Assert.True(engine.Evaluate("const el = document.getElementById('container'); el.toggleAttribute('disabled')").AsBoolean());
        Assert.True(engine.Evaluate("el.hasAttribute('disabled')").AsBoolean());
        Assert.False(engine.Evaluate("el.toggleAttribute('disabled')").AsBoolean());
        Assert.False(engine.Evaluate("el.hasAttribute('disabled')").AsBoolean());

        Assert.True(engine.Evaluate("el.toggleAttribute('disabled', true)").AsBoolean());
        Assert.True(engine.Evaluate("el.hasAttribute('disabled')").AsBoolean());
        Assert.False(engine.Evaluate("el.toggleAttribute('disabled', false)").AsBoolean());
        Assert.False(engine.Evaluate("el.hasAttribute('disabled')").AsBoolean());

        // getAttributeNames
        engine.Evaluate("const names = el.getAttributeNames();");
        Assert.True(engine.Evaluate("Array.isArray(names)").AsBoolean());
        Assert.True(engine.Evaluate("names.includes('id')").AsBoolean());
        Assert.True(engine.Evaluate("names.includes('class')").AsBoolean());

        // hasAttributes
        Assert.True(engine.Evaluate("el.hasAttributes()").AsBoolean());
        Assert.False(engine.Evaluate("document.createElement('div').hasAttributes()").AsBoolean());

        // getAttributeNode, setAttributeNode, removeAttributeNode
        Assert.True(engine.Evaluate("const idNode = el.getAttributeNode('id'); idNode.value === 'container'").AsBoolean());
        Assert.True(engine.Evaluate("el.getAttributeNode('nonexistent') === null").AsBoolean());

        Assert.True(engine.Evaluate("const oldNode = el.removeAttributeNode(idNode); oldNode === idNode").AsBoolean());
        Assert.True(engine.Evaluate("el.getAttribute('id') === null").AsBoolean());
        Assert.True(engine.Evaluate("idNode.ownerElement === null").AsBoolean());

        engine.Evaluate("el.setAttributeNode(idNode);");
        Assert.Equal("container", engine.Evaluate("el.getAttribute('id')").AsString());
        Assert.True(engine.Evaluate("idNode.ownerElement === el").AsBoolean());
    }

    [Fact]
    public void HTMLElement_ReflectedAttributes()
    {
        using var session = CreateDomSession("<div id='el' title='Tooltip' lang='en' dir='ltr' hidden tabindex='5'>Text</div>");
        var engine = session.Engine;

        Assert.Equal("Tooltip", engine.Evaluate("const el = document.getElementById('el'); el.title").AsString());
        Assert.Equal("en", engine.Evaluate("el.lang").AsString());
        Assert.Equal("ltr", engine.Evaluate("el.dir").AsString());
        Assert.True(engine.Evaluate("el.hidden").AsBoolean());
        Assert.Equal(5, engine.Evaluate("el.tabIndex").AsNumber());

        engine.Evaluate(@"
            el.title = 'Updated';
            el.lang = 'fr';
            el.dir = 'rtl';
            el.hidden = false;
            el.tabIndex = 10;
        ");

        Assert.Equal("Updated", engine.Evaluate("el.getAttribute('title')").AsString());
        Assert.Equal("fr", engine.Evaluate("el.getAttribute('lang')").AsString());
        Assert.Equal("rtl", engine.Evaluate("el.getAttribute('dir')").AsString());
        Assert.True(engine.Evaluate("el.getAttribute('hidden') === null").AsBoolean());
        Assert.Equal("10", engine.Evaluate("el.getAttribute('tabindex')").AsString());
    }

    [Fact]
    public void FormControl_ReflectedProperties()
    {
        const string html = @"<!DOCTYPE html><html><body>
            <input id='inp' type='text' name='q' value='search' placeholder='type here' required readonly disabled checked />
            <button id='btn' type='submit' name='b' value='btnval' disabled>Click</button>
            <a id='link' href='https://example.com' target='_blank' rel='noopener'>Link</a>
            <img id='pic' src='foo.png' alt='Foo' width='100' height='200' />
            <select id='sel'>
                <option id='opt1' value='v1'>Item 1</option>
                <option id='opt2' value='v2' selected>Item 2</option>
            </select>
            <textarea id='txt' name='msg' placeholder='say something' rows='5' cols='30' disabled>Content</textarea>
        </body></html>";

        using var session = CreateDomSession(html);
        var engine = session.Engine;

        // HTMLInputElement
        Assert.True(engine.Evaluate("const inp = document.getElementById('inp'); inp instanceof HTMLInputElement").AsBoolean());
        Assert.Equal("search", engine.Evaluate("inp.value").AsString());
        Assert.True(engine.Evaluate("inp.checked").AsBoolean());
        Assert.True(engine.Evaluate("inp.disabled").AsBoolean());
        Assert.Equal("text", engine.Evaluate("inp.type").AsString());
        Assert.Equal("q", engine.Evaluate("inp.name").AsString());
        Assert.Equal("type here", engine.Evaluate("inp.placeholder").AsString());
        Assert.True(engine.Evaluate("inp.readOnly").AsBoolean());
        Assert.True(engine.Evaluate("inp.required").AsBoolean());

        engine.Evaluate(@"
            inp.value = 'hello';
            inp.checked = false;
            inp.disabled = false;
        ");
        Assert.Equal("hello", engine.Evaluate("inp.value").AsString());
        Assert.False(engine.Evaluate("inp.checked").AsBoolean());
        Assert.False(engine.Evaluate("inp.disabled").AsBoolean());

        // HTMLButtonElement
        Assert.True(engine.Evaluate("const btn = document.getElementById('btn'); btn instanceof HTMLButtonElement").AsBoolean());
        Assert.True(engine.Evaluate("btn.disabled").AsBoolean());
        Assert.Equal("submit", engine.Evaluate("btn.type").AsString());
        Assert.Equal("b", engine.Evaluate("btn.name").AsString());
        Assert.Equal("btnval", engine.Evaluate("btn.value").AsString());

        // HTMLAnchorElement
        Assert.True(engine.Evaluate("const link = document.getElementById('link'); link instanceof HTMLAnchorElement").AsBoolean());
        Assert.Equal("https://example.com", engine.Evaluate("link.href").AsString());
        Assert.Equal("_blank", engine.Evaluate("link.target").AsString());
        Assert.Equal("noopener", engine.Evaluate("link.rel").AsString());

        // HTMLImageElement
        Assert.True(engine.Evaluate("const pic = document.getElementById('pic'); pic instanceof HTMLImageElement").AsBoolean());
        Assert.Equal("foo.png", engine.Evaluate("pic.src").AsString());
        Assert.Equal("Foo", engine.Evaluate("pic.alt").AsString());
        Assert.Equal(100, engine.Evaluate("pic.width").AsNumber());
        Assert.Equal(200, engine.Evaluate("pic.height").AsNumber());

        // HTMLSelectElement & HTMLOptionElement
        Assert.True(engine.Evaluate("const sel = document.getElementById('sel'); sel instanceof HTMLSelectElement").AsBoolean());
        Assert.True(engine.Evaluate("const opt1 = document.getElementById('opt1'); opt1 instanceof HTMLOptionElement").AsBoolean());
        Assert.True(engine.Evaluate("const opt2 = document.getElementById('opt2'); opt2 instanceof HTMLOptionElement").AsBoolean());

        Assert.Equal("v2", engine.Evaluate("sel.value").AsString());
        Assert.Equal(1, engine.Evaluate("sel.selectedIndex").AsNumber());
        Assert.False(engine.Evaluate("opt1.selected").AsBoolean());
        Assert.True(engine.Evaluate("opt2.selected").AsBoolean());

        engine.Evaluate("sel.value = 'v1';");
        Assert.True(engine.Evaluate("opt1.selected").AsBoolean());
        Assert.False(engine.Evaluate("opt2.selected").AsBoolean());
        Assert.Equal(0, engine.Evaluate("sel.selectedIndex").AsNumber());

        // HTMLTextAreaElement
        Assert.True(engine.Evaluate("const txt = document.getElementById('txt'); txt instanceof HTMLTextAreaElement").AsBoolean());
        Assert.Equal("Content", engine.Evaluate("txt.value").AsString());
        Assert.True(engine.Evaluate("txt.disabled").AsBoolean());
        Assert.Equal("msg", engine.Evaluate("txt.name").AsString());
        Assert.Equal("say something", engine.Evaluate("txt.placeholder").AsString());
        Assert.Equal(5, engine.Evaluate("txt.rows").AsNumber());
        Assert.Equal(30, engine.Evaluate("txt.cols").AsNumber());

        engine.Evaluate("txt.value = 'New text';");
        Assert.Equal("New text", engine.Evaluate("txt.value").AsString());
    }
}

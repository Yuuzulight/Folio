using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class DomNodeBindingTests
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
    public void DocumentAndWindowGlobalsAreInstalled()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.Equal("Test", engine.Evaluate("document.title").AsString());
        Assert.True(engine.Evaluate("window === globalThis").AsBoolean());
        Assert.True(engine.Evaluate("window.document === document").AsBoolean());
        Assert.True(engine.Evaluate("document.documentElement !== null").AsBoolean());
        Assert.Equal("HTML", engine.Evaluate("document.documentElement.tagName").AsString());
        Assert.Equal("HEAD", engine.Evaluate("document.head.tagName").AsString());
        Assert.Equal("BODY", engine.Evaluate("document.body.tagName").AsString());
    }

    [Fact]
    public void InstanceofPrototypeChainWorksForDomTypes()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.True(engine.Evaluate("document instanceof Document").AsBoolean());
        Assert.True(engine.Evaluate("document instanceof Node").AsBoolean());

        Assert.True(engine.Evaluate("document.body instanceof HTMLBodyElement").AsBoolean());
        Assert.True(engine.Evaluate("document.body instanceof HTMLElement").AsBoolean());
        Assert.True(engine.Evaluate("document.body instanceof Element").AsBoolean());
        Assert.True(engine.Evaluate("document.body instanceof Node").AsBoolean());

        Assert.True(engine.Evaluate("const div = document.createElement('div'); div instanceof HTMLDivElement").AsBoolean());
        Assert.True(engine.Evaluate("div instanceof HTMLElement").AsBoolean());
        Assert.True(engine.Evaluate("div instanceof Element").AsBoolean());
        Assert.True(engine.Evaluate("div instanceof Node").AsBoolean());

        Assert.True(engine.Evaluate("const span = document.createElement('span'); span instanceof HTMLSpanElement").AsBoolean());
        Assert.True(engine.Evaluate("const p = document.createElement('p'); p instanceof HTMLParagraphElement").AsBoolean());
        Assert.True(engine.Evaluate("const a = document.createElement('a'); a instanceof HTMLAnchorElement").AsBoolean());

        Assert.True(engine.Evaluate("const text = document.createTextNode('txt'); text instanceof Text").AsBoolean());
        Assert.True(engine.Evaluate("text instanceof CharacterData").AsBoolean());
        Assert.True(engine.Evaluate("text instanceof Node").AsBoolean());

        Assert.True(engine.Evaluate("const comment = document.createComment('comm'); comment instanceof Comment").AsBoolean());
        Assert.True(engine.Evaluate("comment instanceof CharacterData").AsBoolean());

        Assert.True(engine.Evaluate("const frag = document.createDocumentFragment(); frag instanceof DocumentFragment").AsBoolean());
        Assert.True(engine.Evaluate("frag instanceof Node").AsBoolean());

        Assert.True(engine.Evaluate("document.childNodes instanceof NodeList").AsBoolean());
        Assert.True(engine.Evaluate("document.body.children instanceof HTMLCollection").AsBoolean());
    }

    [Fact]
    public void TreeNavigationAccessorsWorkCorrectly()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const container = document.getElementById('container');
            const p1 = document.getElementById('p1');
        ");

        Assert.Equal(1, engine.Evaluate("container.nodeType").AsNumber());
        Assert.Equal("DIV", engine.Evaluate("container.nodeName").AsString());
        Assert.True(engine.Evaluate("container.nodeValue === null").AsBoolean());
        Assert.True(engine.Evaluate("container.ownerDocument === document").AsBoolean());
        Assert.True(engine.Evaluate("container.parentNode === document.body").AsBoolean());
        Assert.True(engine.Evaluate("container.parentElement === document.body").AsBoolean());
        Assert.True(engine.Evaluate("container.isConnected").AsBoolean());
        Assert.True(engine.Evaluate("container.firstChild === p1").AsBoolean());
        Assert.True(engine.Evaluate("container.lastChild === p1").AsBoolean());
        Assert.True(engine.Evaluate("container.firstElementChild === p1").AsBoolean());
        Assert.True(engine.Evaluate("container.lastElementChild === p1").AsBoolean());
        Assert.Equal(1, engine.Evaluate("container.childElementCount").AsNumber());
        Assert.True(engine.Evaluate("p1.previousSibling === null").AsBoolean());
        Assert.True(engine.Evaluate("p1.nextSibling === null").AsBoolean());
        Assert.True(engine.Evaluate("p1.previousElementSibling === null").AsBoolean());
        Assert.True(engine.Evaluate("p1.nextElementSibling === null").AsBoolean());
    }

    [Fact]
    public void NodeMethodsContainsIsSameNodeIsEqualNodeCompareDocumentPosition()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const container = document.getElementById('container');
            const p1 = document.getElementById('p1');
            const clone = p1.cloneNode(true);
        ");

        Assert.True(engine.Evaluate("container.contains(p1)").AsBoolean());
        Assert.True(engine.Evaluate("container.contains(container)").AsBoolean());
        Assert.False(engine.Evaluate("p1.contains(container)").AsBoolean());

        Assert.True(engine.Evaluate("p1.isSameNode(p1)").AsBoolean());
        Assert.False(engine.Evaluate("p1.isSameNode(clone)").AsBoolean());

        Assert.True(engine.Evaluate("p1.isEqualNode(clone)").AsBoolean());

        // Compare document position
        // container contains p1: container contains p1 and p1 follows container
        var comp = (int)engine.Evaluate("container.compareDocumentPosition(p1)").AsNumber();
        Assert.Equal(0x10 | 0x04, comp); // DOCUMENT_POSITION_CONTAINED_BY | DOCUMENT_POSITION_FOLLOWING (20)

        var compRev = (int)engine.Evaluate("p1.compareDocumentPosition(container)").AsNumber();
        Assert.Equal(0x08 | 0x02, compRev); // DOCUMENT_POSITION_CONTAINS | DOCUMENT_POSITION_PRECEDING (10)
    }

    [Fact]
    public void TextContentGetterAndSetter()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.Equal("Hello", engine.Evaluate("document.getElementById('p1').textContent").AsString());

        engine.Evaluate("document.getElementById('p1').textContent = 'World';");
        Assert.Equal("World", engine.Evaluate("document.getElementById('p1').textContent").AsString());
        Assert.Equal(1, engine.Evaluate("document.getElementById('p1').childNodes.length").AsNumber());
        Assert.Equal(3, engine.Evaluate("document.getElementById('p1').firstChild.nodeType").AsNumber());
    }

    [Fact]
    public void AppendChildInsertBeforeRemoveChildReplaceChild()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const container = document.getElementById('container');
            const p2 = document.createElement('p');
            p2.id = 'p2';
            p2.textContent = 'Second';
            container.appendChild(p2);
        ");

        Assert.Equal(2, engine.Evaluate("container.children.length").AsNumber());
        Assert.Equal("p2", engine.Evaluate("container.lastElementChild.id").AsString());

        // insertBefore
        engine.Evaluate(@"
            const p0 = document.createElement('p');
            p0.id = 'p0';
            p0.textContent = 'Zero';
            container.insertBefore(p0, document.getElementById('p1'));
        ");
        Assert.Equal(3, engine.Evaluate("container.children.length").AsNumber());
        Assert.Equal("p0", engine.Evaluate("container.firstElementChild.id").AsString());

        // replaceChild
        engine.Evaluate(@"
            const p0New = document.createElement('p');
            p0New.id = 'p0-new';
            const old = container.replaceChild(p0New, document.getElementById('p0'));
        ");
        Assert.Equal("p0", engine.Evaluate("old.id").AsString());
        Assert.Equal("p0-new", engine.Evaluate("container.firstElementChild.id").AsString());

        // removeChild
        engine.Evaluate(@"
            const removed = container.removeChild(document.getElementById('p0-new'));
        ");
        Assert.Equal("p0-new", engine.Evaluate("removed.id").AsString());
        Assert.Equal(2, engine.Evaluate("container.children.length").AsNumber());
        Assert.Equal("p1", engine.Evaluate("container.firstElementChild.id").AsString());
    }

    [Fact]
    public void PreInsertionAndReplaceValidityThrowsHierarchyRequestErrorAndNotFoundError()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        // 1. Inserting ancestor into descendant throws HierarchyRequestError
        var err1 = engine.Evaluate(@"
            let caught = null;
            try {
                const container = document.getElementById('container');
                const p1 = document.getElementById('p1');
                p1.appendChild(container);
            } catch (e) {
                caught = e;
            }
            caught;
        ");
        Assert.True(engine.Evaluate("caught instanceof DOMException").AsBoolean());
        Assert.Equal("HierarchyRequestError", engine.Evaluate("caught.name").AsString());
        Assert.Equal(3, engine.Evaluate("caught.code").AsNumber());

        // 2. Removing child not belonging to parent throws NotFoundError
        var err2 = engine.Evaluate(@"
            let caught2 = null;
            try {
                const p1 = document.getElementById('p1');
                document.body.removeChild(p1);
            } catch (e) {
                caught2 = e;
            }
            caught2;
        ");
        Assert.True(engine.Evaluate("caught2 instanceof DOMException").AsBoolean());
        Assert.Equal("NotFoundError", engine.Evaluate("caught2.name").AsString());
        Assert.Equal(8, engine.Evaluate("caught2.code").AsNumber());

        // 3. Inserting text directly into document throws HierarchyRequestError
        var err3 = engine.Evaluate(@"
            let caught3 = null;
            try {
                document.appendChild(document.createTextNode('illegal'));
            } catch (e) {
                caught3 = e;
            }
            caught3;
        ");
        Assert.True(engine.Evaluate("caught3 instanceof DOMException").AsBoolean());
        Assert.Equal("HierarchyRequestError", engine.Evaluate("caught3.name").AsString());

        // 4. Inserting second element into document throws HierarchyRequestError
        var err4 = engine.Evaluate(@"
            let caught4 = null;
            try {
                document.appendChild(document.createElement('div'));
            } catch (e) {
                caught4 = e;
            }
            caught4;
        ");
        Assert.True(engine.Evaluate("caught4 instanceof DOMException").AsBoolean());
        Assert.Equal("HierarchyRequestError", engine.Evaluate("caught4.name").AsString());
    }

    [Fact]
    public void CharacterDataOperationsAndIndexSizeError()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const text = document.createTextNode('HelloWorld');
        ");

        Assert.Equal("HelloWorld", engine.Evaluate("text.data").AsString());
        Assert.Equal(10, engine.Evaluate("text.length").AsNumber());
        Assert.Equal("World", engine.Evaluate("text.substringData(5, 5)").AsString());

        engine.Evaluate("text.appendData('!');");
        Assert.Equal("HelloWorld!", engine.Evaluate("text.data").AsString());

        engine.Evaluate("text.insertData(5, ' ');");
        Assert.Equal("Hello World!", engine.Evaluate("text.data").AsString());

        engine.Evaluate("text.deleteData(5, 1);");
        Assert.Equal("HelloWorld!", engine.Evaluate("text.data").AsString());

        engine.Evaluate("text.replaceData(5, 5, 'Folio');");
        Assert.Equal("HelloFolio!", engine.Evaluate("text.data").AsString());

        // Split text
        engine.Evaluate(@"
            const container = document.getElementById('container');
            container.appendChild(text);
            const secondText = text.splitText(5);
        ");
        Assert.Equal("Hello", engine.Evaluate("text.data").AsString());
        Assert.Equal("Folio!", engine.Evaluate("secondText.data").AsString());
        Assert.True(engine.Evaluate("text.nextSibling === secondText").AsBoolean());

        // IndexSizeError on out of bounds offset
        engine.Evaluate(@"
            let caught = null;
            try {
                text.substringData(999, 1);
            } catch (e) {
                caught = e;
            }
        ");
        Assert.True(engine.Evaluate("caught instanceof DOMException").AsBoolean());
        Assert.Equal("IndexSizeError", engine.Evaluate("caught.name").AsString());
        Assert.Equal(1, engine.Evaluate("caught.code").AsNumber());
    }

    [Fact]
    public void ParentNodeAndChildNodeConvenienceMutations()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const container = document.getElementById('container');
            container.replaceChildren(); // Clears all
        ");
        Assert.Equal(0, engine.Evaluate("container.childNodes.length").AsNumber());

        // append string and elements
        engine.Evaluate(@"
            const d1 = document.createElement('div');
            d1.id = 'd1';
            container.append('Start', d1, 'End');
        ");
        Assert.Equal(3, engine.Evaluate("container.childNodes.length").AsNumber());
        Assert.Equal("Start", engine.Evaluate("container.firstChild.data").AsString());
        Assert.Equal("d1", engine.Evaluate("container.firstElementChild.id").AsString());
        Assert.Equal("End", engine.Evaluate("container.lastChild.data").AsString());

        // prepend
        engine.Evaluate(@"
            container.prepend('VeryStart');
        ");
        Assert.Equal("VeryStart", engine.Evaluate("container.firstChild.data").AsString());

        // ChildNode before, after, replaceWith, remove
        engine.Evaluate(@"
            const middle = document.getElementById('d1');
            const beforeEl = document.createElement('span');
            beforeEl.id = 'before';
            middle.before(beforeEl);

            const afterEl = document.createElement('span');
            afterEl.id = 'after';
            middle.after(afterEl);
        ");
        Assert.Equal("before", engine.Evaluate("document.getElementById('before').id").AsString());
        Assert.Equal("after", engine.Evaluate("document.getElementById('after').id").AsString());

        // remove
        engine.Evaluate(@"
            document.getElementById('before').remove();
        ");
        Assert.True(engine.Evaluate("document.getElementById('before') === null").AsBoolean());

        // replaceWith
        engine.Evaluate(@"
            const replacement = document.createElement('b');
            replacement.id = 'replaced';
            document.getElementById('after').replaceWith(replacement);
        ");
        Assert.True(engine.Evaluate("document.getElementById('after') === null").AsBoolean());
        Assert.Equal("replaced", engine.Evaluate("document.getElementById('replaced').id").AsString());
    }

    [Fact]
    public void LiveNodeListAndHTMLCollectionUpdateAfterMutations()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        engine.Evaluate(@"
            const container = document.getElementById('container');
            const childNodes = container.childNodes;
            const children = container.children;
        ");

        Assert.Equal(1, engine.Evaluate("childNodes.length").AsNumber());
        Assert.Equal(1, engine.Evaluate("children.length").AsNumber());

        // Mutation: append child
        engine.Evaluate(@"
            const p2 = document.createElement('p');
            p2.id = 'p2';
            p2.setAttribute('name', 'para2');
            container.appendChild(p2);
        ");

        // Collections update automatically
        Assert.Equal(2, engine.Evaluate("childNodes.length").AsNumber());
        Assert.Equal(2, engine.Evaluate("children.length").AsNumber());

        // Indexed access
        Assert.Equal("p1", engine.Evaluate("children[0].id").AsString());
        Assert.Equal("p2", engine.Evaluate("children[1].id").AsString());
        Assert.Equal("p2", engine.Evaluate("children.item(1).id").AsString());

        // Named access
        Assert.Equal("p2", engine.Evaluate("children.namedItem('para2').id").AsString());
        Assert.Equal("p2", engine.Evaluate("children['p2'].id").AsString());

        // Iteration
        Assert.Equal(2, engine.Evaluate("[...children].length").AsNumber());
        Assert.Equal(2, engine.Evaluate("[...childNodes].length").AsNumber());

        // forEach
        engine.Evaluate(@"
            let count = 0;
            childNodes.forEach((node, i) => { count++; });
        ");
        Assert.Equal(2, engine.Evaluate("count").AsNumber());
    }

    [Fact]
    public void DocumentTitleGetterAndSetterUpdatesTree()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;

        Assert.Equal("Test", engine.Evaluate("document.title").AsString());
        engine.Evaluate("document.title = 'New Title';");
        Assert.Equal("New Title", engine.Evaluate("document.title").AsString());
        Assert.Equal("New Title", session.Document.DocumentElement?.Descendants.OfType<ElementNode>().First(e => e.LocalName == "title").TextContent);
    }

    [Fact]
    public void ZeroWrapperAllocationDuringStaticRenderingAndCachedIdentity()
    {
        // 1. Static rendering creates 0 wrappers
        var doc = TreeBuilder.Parse("<!DOCTYPE html><html><head><title>Static</title></head><body><div id='box'>Content</div></body></html>");
        var cache = new ScriptWrapperCache();
        Assert.Equal(0, cache.CreatedWrapperCount);

        // 2. Install engine and access document
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, doc, cache);

        // After install, only document wrapper is created
        Assert.Equal(1, cache.CreatedWrapperCount);

        // Accessing body creates 1 wrapper
        engine.Evaluate("document.body;");
        Assert.Equal(2, cache.CreatedWrapperCount);

        // Accessing body again hits the cache (0 additional allocations)
        engine.Evaluate("document.body;");
        Assert.Equal(2, cache.CreatedWrapperCount);

        // Identity check in JS: document.body === document.body
        Assert.True(engine.Evaluate("document.body === document.body").AsBoolean());
    }

    [Fact]
    public void MutationMarksDocumentStructureMutated()
    {
        using var session = CreateDomSession();
        var engine = session.Engine;
        var doc = session.Document;

        doc.StructureMutated = false;
        engine.Evaluate(@"
            const div = document.createElement('div');
            document.body.appendChild(div);
        ");

        Assert.True(doc.StructureMutated);
    }
}

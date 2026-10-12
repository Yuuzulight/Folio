using Folio.Dom;
using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class DomSelectorQueryTests
{
    private sealed class DomSession : IDisposable
    {
        public required JintScriptEngine Engine { get; init; }
        public required DocumentNode Document { get; init; }
        public required ScriptWrapperCache Cache { get; init; }

        public void Dispose() => Engine.Dispose();
    }

    private static DomSession CreateDomSession(string html)
    {
        var doc = TreeBuilder.Parse(html);
        var engine = new JintScriptEngine();
        var cache = DomBindings.Install(engine, doc);
        return new DomSession { Engine = engine, Document = doc, Cache = cache };
    }

    [Fact]
    public void Document_QuerySelector_FindsFirstMatchingElement()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <body>
                <div class="box" id="b1"><span>First</span></div>
                <div class="box" id="b2"><span>Second</span></div>
            </body>
            </html>
            """;
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        var id = engine.Evaluate("document.querySelector('.box').id").AsString();
        Assert.Equal("b1", id);

        var spanText = engine.Evaluate("document.querySelector('div.box > span').textContent").AsString();
        Assert.Equal("First", spanText);

        var notFound = engine.Evaluate("document.querySelector('.nonexistent') === null").AsBoolean();
        Assert.True(notFound);
    }

    [Fact]
    public void Document_QuerySelectorAll_ReturnsStaticSnapshot()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <body>
                <ul id="list">
                    <li class="item">One</li>
                    <li class="item">Two</li>
                    <li class="item">Three</li>
                </ul>
            </body>
            </html>
            """;
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        // Verify querySelectorAll returns static NodeList
        engine.Evaluate("""
            const items = document.querySelectorAll('#list > li');
            const len1 = items.length;
            const firstText = items[0].textContent;
            const secondText = items.item(1).textContent;
            """);

        Assert.Equal(3, engine.Evaluate("len1").AsNumber());
        Assert.Equal("One", engine.Evaluate("firstText").AsString());
        Assert.Equal("Two", engine.Evaluate("secondText").AsString());

        // Mutate DOM tree: add another li
        engine.Evaluate("""
            const ul = document.getElementById('list');
            const newLi = document.createElement('li');
            newLi.className = 'item';
            newLi.textContent = 'Four';
            ul.appendChild(newLi);
            """);

        // Existing NodeList length must NOT change (static snapshot)
        Assert.Equal(3, engine.Evaluate("items.length").AsNumber());
        Assert.True(engine.Evaluate("items[3] === undefined").AsBoolean());

        // A new querySelectorAll call reflects the new node
        Assert.Equal(4, engine.Evaluate("document.querySelectorAll('#list > li').length").AsNumber());
    }

    [Fact]
    public void Element_QuerySelector_And_QuerySelectorAll_ScopeDescendants()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <body>
                <div id="container1">
                    <span class="highlight">C1 Span</span>
                </div>
                <div id="container2">
                    <span class="highlight">C2 Span</span>
                </div>
            </body>
            </html>
            """;
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        engine.Evaluate("""
            const c1 = document.getElementById('container1');
            const c2 = document.getElementById('container2');
            const s1 = c1.querySelector('.highlight');
            const s2 = c2.querySelector('.highlight');
            const all1 = c1.querySelectorAll('span');
            """);

        Assert.Equal("C1 Span", engine.Evaluate("s1.textContent").AsString());
        Assert.Equal("C2 Span", engine.Evaluate("s2.textContent").AsString());
        Assert.Equal(1, engine.Evaluate("all1.length").AsNumber());
        Assert.Equal("C1 Span", engine.Evaluate("all1[0].textContent").AsString());
    }

    [Fact]
    public void Element_Matches_EvaluatesSelector()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <body>
                <div id="main" class="active card" data-role="panel">
                    <button type="submit" class="btn primary">Submit</button>
                </div>
            </body>
            </html>
            """;
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        engine.Evaluate("""
            const div = document.getElementById('main');
            const btn = div.querySelector('button');
            """);

        Assert.True(engine.Evaluate("div.matches('div')").AsBoolean());
        Assert.True(engine.Evaluate("div.matches('#main.active')").AsBoolean());
        Assert.True(engine.Evaluate("div.matches('.card')").AsBoolean());
        Assert.True(engine.Evaluate("div.matches('[data-role=\"panel\"]')").AsBoolean());
        Assert.False(engine.Evaluate("div.matches('button')").AsBoolean());
        Assert.False(engine.Evaluate("div.matches('.inactive')").AsBoolean());

        Assert.True(engine.Evaluate("btn.matches('div > button.primary')").AsBoolean());
        Assert.True(engine.Evaluate("btn.matches('[type=\"submit\"]')").AsBoolean());
        Assert.False(engine.Evaluate("btn.matches('#main')").AsBoolean());
    }

    [Fact]
    public void Element_Closest_TraversesInclusiveAncestors()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <body>
                <article class="post" id="post1">
                    <section class="content">
                        <div class="inner">
                            <p id="target">Paragraph text</p>
                        </div>
                    </section>
                </article>
            </body>
            </html>
            """;
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        engine.Evaluate("""
            const p = document.getElementById('target');
            const selfMatch = p.closest('p');
            const innerMatch = p.closest('div.inner');
            const sectionMatch = p.closest('section');
            const postMatch = p.closest('.post');
            const bodyMatch = p.closest('body');
            const noMatch = p.closest('table');
            """);

        Assert.Equal("target", engine.Evaluate("selfMatch.id").AsString());
        Assert.Equal("inner", engine.Evaluate("innerMatch.className").AsString());
        Assert.Equal("content", engine.Evaluate("sectionMatch.className").AsString());
        Assert.Equal("post1", engine.Evaluate("postMatch.id").AsString());
        Assert.Equal("BODY", engine.Evaluate("bodyMatch.tagName").AsString());
        Assert.True(engine.Evaluate("noMatch === null").AsBoolean());
    }

    [Fact]
    public void DocumentFragment_QuerySelector_And_QuerySelectorAll()
    {
        var html = "<!DOCTYPE html><html><body></body></html>";
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        engine.Evaluate("""
            const frag = document.createDocumentFragment();
            const d1 = document.createElement('div');
            d1.className = 'frag-item';
            d1.id = 'f1';
            const d2 = document.createElement('div');
            d2.className = 'frag-item';
            d2.id = 'f2';
            frag.appendChild(d1);
            frag.appendChild(d2);

            const first = frag.querySelector('.frag-item');
            const all = frag.querySelectorAll('.frag-item');
            """);

        Assert.Equal("f1", engine.Evaluate("first.id").AsString());
        Assert.Equal(2, engine.Evaluate("all.length").AsNumber());
        Assert.Equal("f1", engine.Evaluate("all[0].id").AsString());
        Assert.Equal("f2", engine.Evaluate("all[1].id").AsString());
    }

    [Fact]
    public void InvalidSelectors_ThrowDomException_SyntaxError()
    {
        var html = "<!DOCTYPE html><html><body><div id='d'></div></body></html>";
        using var session = CreateDomSession(html);
        var engine = session.Engine;

        // Empty string
        var err1 = engine.Evaluate("""
            try {
                document.querySelector('');
                'ok';
            } catch (e) {
                e.name + ': ' + e.message;
            }
            """).AsString();
        Assert.StartsWith("SyntaxError", err1);

        // Invalid syntax: trailing combinator or bogus characters
        var err2 = engine.Evaluate("""
            try {
                document.querySelectorAll('div >');
                'ok';
            } catch (e) {
                e.name + ': ' + e.message;
            }
            """).AsString();
        Assert.StartsWith("SyntaxError", err2);

        // matches() with invalid selector
        var err3 = engine.Evaluate("""
            try {
                document.getElementById('d').matches(':::invalid');
                'ok';
            } catch (e) {
                e.name + ': ' + e.message;
            }
            """).AsString();
        Assert.StartsWith("SyntaxError", err3);

        // closest() with invalid selector
        var err4 = engine.Evaluate("""
            try {
                document.getElementById('d').closest('..');
                'ok';
            } catch (e) {
                e.name + ': ' + e.message;
            }
            """).AsString();
        Assert.StartsWith("SyntaxError", err4);
    }
}

using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// The CSS object model (issues #498 and #499): <c>document.styleSheets</c>, <c>CSSStyleSheet</c>, the rule interfaces and their
/// <c>cssText</c>, <c>insertRule</c> and <c>deleteRule</c> with their errors, rule edits, and the restyle each change causes.
/// </summary>
public class CssomTests
{
    private sealed class FakeLoader : IResourceLoader
    {
        public Dictionary<string, string> Sheets { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(Sheets.TryGetValue(request.Url, out var css)
                ? ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes(css), "text/css")
                : ResourceResponse.Refused("not found"));
    }

    private sealed class Page : IDisposable
    {
        public Document Document { get; }
        public JintScriptEngine Engine { get; }

        public Page(string html, FakeLoader? loader = null, ScriptLimits? limits = null)
        {
            Engine = new JintScriptEngine(new ScriptEngineOptions { Limits = limits ?? ScriptLimits.Default });
            Document = Document.Parse(html, new FolioOptions { BaseUri = new Uri("https://example.com/app/"), ResourceLoader = loader });
            Document.Paint(800, 600);
            DomBindings.Install(Engine, Document);
        }

        public string Js(string script) => Engine.Evaluate(script).AsString()!;

        // The colour the cascade gives the first element matching the selector, after the pending changes are applied.
        public string Color(string selector = "p")
        {
            Document.Update();
            return Js($"getComputedStyle(document.querySelector('{selector}')).color");
        }

        public void Dispose() => Engine.Dispose();
    }

    private const string Red = "rgb(255, 0, 0)";
    private const string Blue = "rgb(0, 0, 255)";

    private static Page Open(string css, string body = "<p>text</p>", FakeLoader? loader = null, ScriptLimits? limits = null) =>
        new($"<!DOCTYPE html><html><head><style>{css}</style></head><body>{body}</body></html>", loader, limits);

    // --------------------------------------------------------------------------------------------- reading (#498)

    [Fact]
    public void StyleSheetsHoldsAStyleAndLinkSheetPerOwnerInTreeOrderAndKeepsItsIdentity()
    {
        var loader = new FakeLoader();
        loader.Sheets["https://example.com/app/a.css"] = "a { color: red }";
        using var page = new Page("<!DOCTYPE html><html><head><style>p { color: red }</style><link rel='stylesheet' href='a.css'><link rel='icon' href='x.png'><style title='t' media='print'>b { }</style></head><body><p>x</p></body></html>", loader);
        Assert.Equal("3", page.Js("String(document.styleSheets.length)"));
        Assert.Equal("STYLE,LINK,STYLE", page.Js("Array.from(document.styleSheets, s => s.ownerNode.tagName).join()"));
        Assert.Equal("null|https://example.com/app/a.css|null", page.Js("Array.from(document.styleSheets, s => String(s.href)).join('|')"));
        Assert.Equal("text/css|null|t|print", page.Js("[document.styleSheets[0].type, String(document.styleSheets[0].title), document.styleSheets[2].title, document.styleSheets[2].media.mediaText].join('|')"));
        Assert.Equal("true", page.Js("String(document.styleSheets === document.styleSheets && document.styleSheets[1] === document.styleSheets.item(1))"));
        Assert.Equal("true", page.Js("String(document.querySelector('style').sheet === document.styleSheets[0] && document.querySelector('p').sheet === null)"));
        Assert.Equal("3", page.Js("let n = 0; for (const s of document.styleSheets) n++; String(n)"));
        Assert.Equal("true", page.Js("String(document.styleSheets[0] instanceof CSSStyleSheet && document.styleSheets[0] instanceof StyleSheet)"));
    }

    [Fact]
    public void AStyleRuleReadsItsSelectorAndDeclarations()
    {
        using var page = Open("p, .a>b  ,  #c { color: red; margin: 1px   2px; padding: 0 !important }");
        Assert.Equal("1|1|p, .a > b, #c", page.Js("const r = document.styleSheets[0].cssRules[0]; [document.styleSheets[0].cssRules.length, r.type, r.selectorText].join('|')"));
        Assert.Equal("3|red|1px 2px|important|0", page.Js("[r.style.length, r.style.getPropertyValue('color'), r.style.getPropertyValue('margin'), r.style.getPropertyPriority('padding'), r.style.getPropertyValue('padding')].join('|')"));
        Assert.Equal("p, .a > b, #c { color: red; margin: 1px 2px; padding: 0 !important; }", page.Js("r.cssText"));
        Assert.Equal("true", page.Js("String(r instanceof CSSStyleRule && r instanceof CSSRule && r.parentStyleSheet === document.styleSheets[0] && r.parentRule === null)"));
        Assert.Equal("1", page.Js("String(CSSRule.STYLE_RULE)"));
    }

    [Fact]
    public void CssTextAndTypeOfEveryRuleType()
    {
        using var page = Open("""
            @import url("a.css") screen;
            @namespace svg url(http://www.w3.org/2000/svg);
            @font-face { font-family: F; src: url(f.woff2) }
            @media (min-width:100px) and (max-width : 900px) { a { color: red } @media print { b { top: 0 } } }
            @supports (display:grid) { a { display: grid } }
            @keyframes spin { from { opacity: 0 } 50% { opacity: .5 } to { opacity: 1 } }
            @page :first { margin: 1in }
            @layer base, theme;
            @layer base { a { color: red } }
            @container card (min-width:300px) { a { color: red } }
            @property --x { syntax: '<length>'; inherits: false; initial-value: 0px }
            p { color: red }
            """);
        var texts = page.Js("Array.from(document.styleSheets[0].cssRules, r => r.cssText).join('\\n---\\n')");
        Assert.Equal("""
            @import url("a.css") screen;
            ---
            @namespace svg url("http://www.w3.org/2000/svg");
            ---
            @font-face { font-family: F; src: url("f.woff2"); }
            ---
            @media (min-width: 100px) and (max-width: 900px) {
              a { color: red; }
              @media print {
                b { top: 0; }
              }
            }
            ---
            @supports (display: grid) {
              a { display: grid; }
            }
            ---
            @keyframes spin {
              from { opacity: 0; }
              50% { opacity: .5; }
              to { opacity: 1; }
            }
            ---
            @page :first { margin: 1in; }
            ---
            @layer base, theme;
            ---
            @layer base {
              a { color: red; }
            }
            ---
            @container card (min-width: 300px) {
              a { color: red; }
            }
            ---
            @property --x { syntax: "<length>"; inherits: false; initial-value: 0px; }
            ---
            p { color: red; }
            """.Replace("\r\n", "\n"), texts);

        Assert.Equal("3|10|5|4|12|7|6|0|0|0|0|1", page.Js("Array.from(document.styleSheets[0].cssRules, r => r.type).join('|')"));
        Assert.Equal("a.css|screen|http://www.w3.org/2000/svg|svg", page.Js("const rs = document.styleSheets[0].cssRules; [rs[0].href, rs[0].media.mediaText, rs[1].namespaceURI, rs[1].prefix].join('|')"));
        Assert.Equal("(min-width: 100px) and (max-width: 900px)|2|print", page.Js("[rs[3].conditionText, rs[3].cssRules.length, rs[3].cssRules[1].media.mediaText].join('|')"));
        Assert.Equal("(display: grid)|spin|3|50%|.5", page.Js("[rs[4].conditionText, rs[5].name, rs[5].cssRules.length, rs[5].cssRules[1].keyText, rs[5].cssRules[1].style.getPropertyValue('opacity')].join('|')"));
        Assert.Equal(":first|F|base|base,theme|card|--x|<length>|false|0px", page.Js("[rs[6].selectorText, rs[2].style.getPropertyValue('font-family'), rs[8].name, rs[7].nameList, rs[9].containerName, rs[10].name, rs[10].syntax, rs[10].inherits, rs[10].initialValue].join('|')"));
        Assert.Equal("true", page.Js("String(rs[0] instanceof CSSImportRule && rs[2] instanceof CSSFontFaceRule && rs[3] instanceof CSSMediaRule && rs[4] instanceof CSSSupportsRule && rs[5] instanceof CSSKeyframesRule && rs[5].cssRules[0] instanceof CSSKeyframeRule && rs[1] instanceof CSSNamespaceRule)"));
    }

    [Fact]
    public void InvalidRulesSelectorsAndDeclarationsAreDroppedAsInABrowser()
    {
        using var page = Open("""
            p { color: red; color: notacolor; nonsense-prop: 1; margin: 1px }
            p:not-a-real-pseudo(x) { color: blue }
            @unknown thing { a { b: c } }
            @charset "utf-8";
            a { top: 0 }
            """);
        Assert.Equal("2|p { color: red; margin: 1px; }|a { top: 0; }", page.Js("const rules = document.styleSheets[0].cssRules; [rules.length, rules[0].cssText, rules[1].cssText].join('|')"));
    }

    [Fact]
    public void NestedStyleRulesKeepTheirChildren()
    {
        using var page = Open("a { color: red; & b { color: blue } .c & { top: 0 } }");
        Assert.Equal("2|a { color: red; & b { color: blue; } .c & { top: 0; } }", page.Js("const r = document.styleSheets[0].cssRules[0]; [r.cssRules.length, r.cssText].join('|')"));
    }

    [Fact]
    public void ALinkSheetsRulesAreReadThroughTheHostLoader()
    {
        var loader = new FakeLoader();
        loader.Sheets["https://example.com/app/site.css"] = "h1 { color: blue } @media print { h1 { top: 0 } }";
        using var page = new Page("<!DOCTYPE html><html><head><link rel='stylesheet' href='site.css'></head><body><h1>t</h1></body></html>", loader);
        Assert.Equal("2|h1 { color: blue; }|https://example.com/app/site.css", page.Js("const s = document.styleSheets[0]; [s.cssRules.length, s.cssRules[0].cssText, s.href].join('|')"));
        Assert.Equal(Blue, page.Color("h1"));
    }

    [Fact]
    public void ADisabledSheetDropsOutOfTheCascadeAndComesBack()
    {
        using var page = Open("p { color: red }");
        Assert.Equal(Red, page.Color());
        page.Js("document.styleSheets[0].disabled = true");
        Assert.Equal("true", page.Js("String(document.styleSheets[0].disabled)"));
        Assert.NotEqual(Red, page.Color());
        page.Js("document.styleSheets[0].disabled = false");
        Assert.Equal(Red, page.Color());
    }

    [Fact]
    public void AMediaListReadsAndEditsTheOwnersMediaAttribute()
    {
        using var page = new Page("<!DOCTYPE html><html><head><style media='screen,print'>p { color: red }</style></head><body><p>x</p></body></html>");
        Assert.Equal("screen, print|2|print", page.Js("const m = document.styleSheets[0].media; [m.mediaText, m.length, m.item(1)].join('|')"));
        page.Js("m.appendMedium('tv'); m.deleteMedium('screen')");
        Assert.Equal("print, tv", page.Js("document.querySelector('style').getAttribute('media')"));
    }

    [Fact]
    public void RuleListsAreLiveAndKeepTheirRuleObjects()
    {
        using var page = Open("a { top: 0 } b { top: 1 }");
        Assert.Equal("true", page.Js("const sheet = document.styleSheets[0]; const list = sheet.cssRules; const first = list[0]; String(list === sheet.cssRules && list.item(1) === list[1] && list.item(9) === null)"));
        page.Js("sheet.insertRule('c { top: 2 }', 0)");
        Assert.Equal("3|true|c|a", page.Js("[list.length, list[1] === first, list[0].selectorText, list[1].selectorText].join('|')"));
    }

    // --------------------------------------------------------------------------------------------- editing (#499)

    [Fact]
    public void InsertRuleAndDeleteRuleRestyleAndTheDomTextIsUntouched()
    {
        using var page = Open("p { color: red }");
        Assert.Equal(Red, page.Color());

        Assert.Equal("1", page.Js("String(document.styleSheets[0].insertRule('p { color: blue }', 1))"));
        Assert.Equal(Blue, page.Color());
        Assert.Equal("p { color: red }", page.Js("document.querySelector('style').textContent"));

        page.Js("document.styleSheets[0].deleteRule(1)");
        Assert.Equal(Red, page.Color());
        Assert.Equal("1", page.Js("String(document.styleSheets[0].cssRules.length)"));

        Assert.Equal("0", page.Js("String(document.styleSheets[0].insertRule('p { color: green }'))")); // the index defaults to 0
        Assert.Equal(Red, page.Color()); // the rule at index 0 comes first, so the later one wins
    }

    [Fact]
    public void InsertRuleAndDeleteRuleThrowTheSpecsErrors()
    {
        using var page = Open("p { color: red } @import url(a.css);");
        const string Try = "(f) => { try { f(); return 'ok'; } catch (e) { return e.name; } }";
        page.Js("var attempt = " + Try + "; var sheet = document.styleSheets[0];");
        Assert.Equal("IndexSizeError", page.Js("attempt(() => sheet.insertRule('a { }', 5))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => sheet.insertRule('not a rule', 0))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => sheet.insertRule('a { } b { }', 0))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => sheet.insertRule('', 0))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => sheet.insertRule('color: red', 0))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => sheet.insertRule('@unknown { }', 0))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => sheet.insertRule('a:no-such-pseudo(1) { }', 0))"));
        Assert.Equal("HierarchyRequestError", page.Js("attempt(() => sheet.insertRule('@import url(b.css);', 1))")); // an @import after a style rule
        Assert.Equal("HierarchyRequestError", page.Js("attempt(() => sheet.insertRule('a { }', 1))")); // a style rule before an @import
        Assert.Equal("InvalidStateError", page.Js("attempt(() => sheet.insertRule('@namespace url(http://example.com);', 0))")); // an @namespace with other rules
        Assert.Equal("IndexSizeError", page.Js("attempt(() => sheet.deleteRule(9))"));
        Assert.Equal("TypeError", page.Js("attempt(() => sheet.insertRule())"));
        Assert.Equal("TypeError", page.Js("attempt(() => sheet.deleteRule())"));
        Assert.Equal("ok", page.Js("attempt(() => sheet.insertRule('@import url(c.css);', 0))"));
        Assert.Equal("2", page.Js("String(document.styleSheets[0].cssRules.length - 1)"));
    }

    [Fact]
    public void GroupingRulesTakeInsertRuleAndDeleteRuleWithTheirOwnErrors()
    {
        using var page = Open("@media screen { p { color: red } }");
        page.Js("var media = document.styleSheets[0].cssRules[0]; var attempt = (f) => { try { f(); return 'ok'; } catch (e) { return e.name; } };");
        Assert.Equal("1", page.Js("String(media.insertRule('p { color: blue }', 1))"));
        Assert.Equal(Blue, page.Color());
        Assert.Equal("2|@media screen {\n  p { color: red; }\n  p { color: blue; }\n}", page.Js("[media.cssRules.length, media.cssText].join('|')"));
        Assert.Equal("IndexSizeError", page.Js("attempt(() => media.insertRule('a { }', 9))"));
        Assert.Equal("SyntaxError", page.Js("attempt(() => media.insertRule('nope', 0))"));
        Assert.Equal("HierarchyRequestError", page.Js("attempt(() => media.insertRule('@import url(x.css);', 0))"));
        Assert.Equal("HierarchyRequestError", page.Js("attempt(() => media.insertRule('@namespace url(x);', 0))"));
        Assert.Equal("IndexSizeError", page.Js("attempt(() => media.deleteRule(7))"));
        page.Js("media.deleteRule(1)");
        Assert.Equal(Red, page.Color());
    }

    [Fact]
    public void KeyframesRulesAppendFindAndDelete()
    {
        using var page = Open("@keyframes k { from { top: 0 } to { top: 1 } }");
        page.Js("var kf = document.styleSheets[0].cssRules[0];");
        page.Js("kf.appendRule('50% { top: 5px }')");
        Assert.Equal("3|50%|5px|true", page.Js("[kf.cssRules.length, kf.findRule('50%').keyText, kf.findRule('50%').style.getPropertyValue('top'), String(kf.findRule('FROM') === kf.cssRules[0])].join('|')"));
        page.Js("kf.deleteRule('50%')");
        Assert.Equal("2|null", page.Js("[kf.cssRules.length, String(kf.findRule('50%'))].join('|')"));
        page.Js("kf.cssRules[0].keyText = '0%, 10%'");
        Assert.Equal("0%, 10%", page.Js("kf.cssRules[0].keyText"));
        Assert.Equal("SyntaxError", page.Js("try { kf.cssRules[0].keyText = 'bogus'; 'ok' } catch (e) { e.name }"));
    }

    [Fact]
    public void SelectorTextAndRuleStyleChangesRestyle()
    {
        using var page = Open("p { color: red } i { color: blue }", "<p>text</p><i>it</i>");
        page.Js("var rule = document.styleSheets[0].cssRules[0];");
        Assert.Equal(Blue, page.Color("i"));

        page.Js("rule.selectorText = 'i'");
        Assert.Equal("i", page.Js("rule.selectorText"));
        Assert.NotEqual(Red, page.Color("p"));
        Assert.Equal(Blue, page.Color("i")); // the first rule now matches i too, but the later rule wins by order

        page.Js("rule.selectorText = 'p:no-such-pseudo(1)'");      // invalid: ignored
        Assert.Equal("i", page.Js("rule.selectorText"));

        page.Js("rule.selectorText = 'p'; rule.style.setProperty('color', 'green')");
        Assert.Equal("green", page.Js("rule.style.getPropertyValue('color')"));
        Assert.Equal("rgb(0, 128, 0)", page.Color("p"));
        page.Js("rule.style.removeProperty('color')");
        Assert.Equal("p { }", page.Js("rule.cssText"));
        page.Js("rule.style.cssText = 'color: blue; top: 1px'");
        Assert.Equal(Blue, page.Color("p"));
        Assert.Equal("p { color: blue; top: 1px; }", page.Js("rule.cssText"));
    }

    [Fact]
    public void ChangingTheStyleElementsTextReplacesTheRulesAndRestyles()
    {
        using var page = Open("p { color: red }");
        page.Js("var sheet = document.styleSheets[0]; var old = sheet.cssRules[0]; var el = document.querySelector('style');");
        page.Js("sheet.insertRule('p { color: green }', 1)");            // the override is in force
        page.Js("el.textContent = 'p { color: blue } p { top: 1px }'");
        Assert.Equal("2|p { color: blue; }", page.Js("[sheet.cssRules.length, sheet.cssRules[0].cssText].join('|')"));
        Assert.Equal(Blue, page.Color());
        Assert.Equal("true", page.Js("String(old.parentStyleSheet === null && sheet === document.styleSheets[0])")); // the sheet object stays, the old rule is detached
    }

    [Fact]
    public void AStyleElementAddedAtRunTimeJoinsTheSheetsAndTheCascade()
    {
        using var page = Open("p { color: red }");
        page.Js("var added = document.createElement('style'); added.textContent = 'p { color: blue }'; document.head.appendChild(added);");
        Assert.Equal("2|true", page.Js("[document.styleSheets.length, document.styleSheets[1] === added.sheet].join('|')"));
        Assert.Equal(Blue, page.Color());
        page.Js("added.remove()");
        Assert.Equal("1", page.Js("String(document.styleSheets.length)"));
        Assert.Equal(Red, page.Color());
    }

    [Fact]
    public void ALinkSheetCanBeEditedToo()
    {
        var loader = new FakeLoader();
        loader.Sheets["https://example.com/app/site.css"] = "h1 { color: red }";
        using var page = new Page("<!DOCTYPE html><html><head><link rel='stylesheet' href='site.css'></head><body><h1>t</h1></body></html>", loader);
        Assert.Equal(Red, page.Color("h1"));
        page.Js("document.styleSheets[0].insertRule('h1 { color: blue }', 1)");
        Assert.Equal(Blue, page.Color("h1"));
        page.Js("document.styleSheets[0].deleteRule(1)");
        Assert.Equal(Red, page.Color("h1"));
    }

    [Fact]
    public void AnElementsStyleCssTextReplacesTheWholeBlock()
    {
        using var page = Open("p { color: red }");
        page.Js("var el = document.querySelector('p'); el.style.cssText = 'color: blue; top: 2px'");
        Assert.Equal("color: blue; top: 2px;|color: blue; top: 2px;", page.Js("[el.getAttribute('style'), el.style.cssText].join('|')"));
        page.Js("el.style.cssText = 'margin: 1px'");
        Assert.Equal("margin: 1px;", page.Js("el.getAttribute('style')"));
        Assert.Equal(Red, page.Color("p")); // the block above sets no colour any more, so the sheet's red applies
    }

    [Fact]
    public void ALegacyAPIStillWorks()
    {
        using var page = Open("p { color: red }");
        Assert.Equal("-1", page.Js("String(document.styleSheets[0].addRule('p', 'color: blue'))"));
        Assert.Equal(Blue, page.Color());
        Assert.Equal("2|true", page.Js("[document.styleSheets[0].rules.length, document.styleSheets[0].rules === document.styleSheets[0].cssRules].join('|')"));
        page.Js("document.styleSheets[0].removeRule(1)");
        Assert.Equal(Red, page.Color());
    }

    [Fact]
    public void ASheetHoldsAtMost100000RulesAndGoingOverThrowsQuotaExceededError()
    {
        // Parsing 100,000 rules allocates more than the default script memory budget counts on this thread.
        using var page = Open(string.Concat(Enumerable.Repeat("a{}", CssomRegistry.MaxRules)), limits: new ScriptLimits { MemoryLimitBytes = 2L * 1024 * 1024 * 1024 });
        Assert.Equal("QuotaExceededError", page.Js("try { document.styleSheets[0].insertRule('b { }', 0); 'ok' } catch (e) { e.name }"));
    }

    // --------------------------------------------------------------------------------------------- equivalence (#499)

    private const string Body = "<div id='box'><p class='a'>one</p><p class='b'>two <i>it</i></p></div><ul><li>x</li></ul>";
    private const string BaseCss = ".a { color: red; margin-top: 4px } p { top: 1px } @media screen { .b { color: blue } } @keyframes k { from { top: 0 } to { top: 9px } }";

    private static string Styles(Page page) => page.Js(@"Array.from(document.querySelectorAll('body *'), e => e.localName + (e.id ? '#' + e.id : '') + ':'
        + ['color', 'margin-top', 'top', 'display', 'font-size', 'background-color'].map(p => getComputedStyle(e).getPropertyValue(p)).join(',')).join('|')");

    // After any CSSOM change, the page's styles after Update() are what a fresh page with the sheet as it now reads has.
    [Theory]
    [InlineData("document.styleSheets[0].insertRule('p { color: green }', 0)")]
    [InlineData("document.styleSheets[0].insertRule('p { color: green; margin-top: 9px }', document.styleSheets[0].cssRules.length)")]
    [InlineData("document.styleSheets[0].insertRule('li { display: block; color: red }', 1)")]
    [InlineData("document.styleSheets[0].deleteRule(0)")]
    [InlineData("document.styleSheets[0].deleteRule(2)")]
    [InlineData("document.styleSheets[0].cssRules[0].selectorText = 'p'")]
    [InlineData("document.styleSheets[0].cssRules[0].selectorText = '#box > p.b, li'")]
    [InlineData("document.styleSheets[0].cssRules[0].style.setProperty('color', 'purple')")]
    [InlineData("document.styleSheets[0].cssRules[0].style.removeProperty('margin-top')")]
    [InlineData("document.styleSheets[0].cssRules[1].style.cssText = 'top: 7px; font-size: 20px'")]
    [InlineData("document.styleSheets[0].cssRules[2].insertRule('p { color: orange }', 0)")]
    [InlineData("document.styleSheets[0].cssRules[2].deleteRule(0)")]
    [InlineData("document.styleSheets[0].cssRules[3].appendRule('50% { top: 4px }')")]
    [InlineData("document.querySelector('style').textContent = 'p { color: teal } i { display: block }'")]
    [InlineData("document.styleSheets[0].disabled = true")]
    [InlineData("var s = document.createElement('style'); s.textContent = 'li { color: navy }'; document.head.appendChild(s)")]
    [InlineData("document.styleSheets[0].insertRule('p { color: green }', 0); document.styleSheets[0].cssRules[0].style.setProperty('top', '3px'); document.styleSheets[0].deleteRule(1)")]
    public void AfterACssomChangeTheStylesAreWhatAFreshPageWithTheSameSheetHas(string change)
    {
        using var page = Open(BaseCss, Body);
        page.Document.Update();
        page.Js(change);
        page.Document.Update();
        var updated = Styles(page);

        var css = page.Js("Array.from(document.querySelectorAll('style'), s => (s.sheet.disabled ? '' : Array.from(s.sheet.cssRules, r => r.cssText).join(String.fromCharCode(10)))).join(String.fromCharCode(10))");
        using var fresh = Open(css, Body);
        fresh.Document.Update();
        Assert.Equal(Styles(fresh), updated);
    }
}


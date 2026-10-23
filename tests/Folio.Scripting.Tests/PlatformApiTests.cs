using System.Text.RegularExpressions;
using Folio.Hosting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>
/// The platform objects documents and workers share (#469): <c>console</c> with the Console Standard's formatting and a cap,
/// <c>atob</c>/<c>btoa</c>, <c>TextEncoder</c>/<c>TextDecoder</c>, <c>structuredClone</c> and <c>crypto</c>.
/// </summary>
public class PlatformApiTests
{
    private sealed class Page : IDisposable
    {
        public Page(int? seed = null)
        {
            Document = Document.Parse("<p id='el'>x</p>", new FolioOptions());
            Document.Node.DiagnosticSink = d => { if (d.Code == DiagnosticCode.ScriptConsole) Messages.Add(d); };
            Engine = new JintScriptEngine(seed is { } s ? new ScriptEngineOptions { RandomSeed = s } : ScriptEngineOptions.Default);
            DomBindings.Install(Engine, Document);
        }

        public Document Document { get; }
        public JintScriptEngine Engine { get; }
        public List<Diagnostic> Messages { get; } = [];

        public string Run(string script, string url = "page.js") => Engine.Evaluate(script, url).ToString() ?? "";

        public string[] Lines() => [.. Messages.Select(m => m.Message)];

        public void Dispose() => Engine.Dispose();
    }

    // ---- console ----

    [Fact]
    public void ConsoleMethodsReachTheDocumentAsDiagnosticsWithALevel()
    {
        using var page = new Page();
        page.Run("console.log('a'); console.info('b'); console.debug('c'); console.warn('d'); console.error('e');");
        Assert.Equal(["a", "b", "c", "d", "e"], page.Lines());
        Assert.Equal([Severity.Info, Severity.Info, Severity.Info, Severity.Warning, Severity.Error], page.Messages.Select(m => m.Severity));
        Assert.All(page.Messages, m => Assert.Equal("console", m.Feature));
    }

    [Fact]
    public void ConsoleMessagesCarryTheLineAndColumnOfTheCall()
    {
        using var page = new Page();
        page.Run("var x = 1;\n\n   console.log('here');\nfunction f() { console.warn('there'); }\nf();");
        Assert.Equal(3, page.Messages[0].Location?.Line);
        Assert.Equal(4, page.Messages[1].Location?.Line);
    }

    [Fact]
    public void ConsoleFormatSpecifiersFollowTheConsoleStandard()
    {
        using var page = new Page();
        page.Run("console.log('%s=%d %i %f', 'a', 42.5, 42.9, '1.5x');");
        page.Run("console.log('%o and %O', {a: 1}, [1, 'two']);");
        page.Run("console.log('%cstyled%c text', 'color: red', '');");
        page.Run("console.log('100%% of %s', 'it');");
        page.Run("console.log('%d');");
        page.Run("console.log('x', 1, 'y', {b: 2}, [3]);");
        page.Run("console.log({a: 1}, 'z');");
        page.Run("console.log('%s', {toString() { return 'custom'; }}, '%s');");
        page.Run("console.log('%d %i %f', 'abc', Symbol('s'), {});");
        Assert.Equal(
        [
            "a=42.5 42 1.5",
            "{ a: 1 } and [ 1, 'two' ]",
            "styled text",
            "100% of it",
            "%d",
            "x 1 y { b: 2 } [ 3 ]",
            "{ a: 1 } z",
            "custom %s",
            "NaN NaN NaN",
        ], page.Lines());
    }

    [Fact]
    public void ConsoleInspectsValuesTheWayADeveloperExpects()
    {
        using var page = new Page();
        page.Run("var o = {n: 1, s: 'str', nested: {deep: {deeper: {deepest: 1}}}}; o.self = o; console.log(o);");
        page.Run("console.log([1, , 3], new Map([['k', 1]]), new Set([1]), null, undefined, -0, 10n, Symbol('q'));");
        page.Run("console.log(function named() {}, class K {}, () => 1, new Date(0), /a+/g, new Uint8Array([1, 2]));");
        page.Run("console.dir('text'); console.dirxml({a: 1});");
        Assert.Equal("{\n  n: 1,\n  s: 'str',\n  nested: { deep: { deeper: [Object] } },\n  self: [Circular]\n}", page.Lines()[0]);
        Assert.Equal("[ 1, <1 empty item>, 3 ] Map(1) { 'k' => 1 } Set(1) { 1 } null undefined -0 10n Symbol(q)", page.Lines()[1]);
        Assert.Equal("[Function: named] [class K] [Function (anonymous)] 1970-01-01T00:00:00.000Z /a+/g Uint8Array(2) [ 1, 2 ]", page.Lines()[2]);
        Assert.Equal(["'text'", "{ a: 1 }"], page.Lines()[3..]);
    }

    [Fact]
    public void ConsoleGroupsIndentAndCountsAndTimersReport()
    {
        using var page = new Page();
        page.Run("console.group('outer'); console.log('in'); console.groupCollapsed(); console.log('deeper\\nsecond line'); console.groupEnd(); console.groupEnd(); console.groupEnd(); console.log('out');");
        Assert.Equal(["outer", "  in", "    deeper\n    second line", "out"], page.Lines());

        using var counting = new Page();
        counting.Run("console.count(); console.count(); console.count('x'); console.countReset(); console.count(); console.countReset('missing');");
        Assert.Equal(["default: 1", "default: 2", "x: 1", "default: 1", "Count for 'missing' does not exist"], counting.Lines());
        Assert.Equal(Severity.Warning, counting.Messages[^1].Severity);

        using var timing = new Page();
        timing.Run("console.timeEnd('nope'); console.time('t'); console.time('t'); console.timeLog('t', 'extra'); console.timeEnd('t'); console.timeEnd('t');");
        Assert.Equal("Timer 'nope' does not exist", timing.Lines()[0]);
        Assert.Equal("Timer 't' already exists", timing.Lines()[1]);
        Assert.Matches(@"^t: \d+ms extra$", timing.Lines()[2]);
        Assert.Matches(@"^t: \d+ms$", timing.Lines()[3]);
        Assert.Equal("Timer 't' does not exist", timing.Lines()[4]);
    }

    [Fact]
    public void ConsoleAssertPrintsOnlyWhenTheConditionFails()
    {
        using var page = new Page();
        page.Run("console.assert(true, 'hidden'); console.assert(false); console.assert(0, 'value is %d', 3); console.assert(false, {a: 1});");
        Assert.Equal(["Assertion failed", "Assertion failed: value is 3", "Assertion failed: { a: 1 }"], page.Lines());
        Assert.All(page.Messages, m => Assert.Equal(Severity.Error, m.Severity));
    }

    [Fact]
    public void ConsoleTableDrawsRowsAsText()
    {
        using var page = new Page();
        page.Run("console.table([{a: 1, b: 'x'}, {a: 2}]);");
        page.Run("console.table(['p', 'q']);");
        page.Run("console.table([{a: 1, b: 2}], ['b']);");
        page.Run("console.table(5);");
        Assert.Equal(
            "┌─────────┬───┬─────┐\n│ (index) │ a │  b  │\n├─────────┼───┼─────┤\n│    0    │ 1 │ 'x' │\n│    1    │ 2 │     │\n└─────────┴───┴─────┘",
            page.Lines()[0]);
        Assert.Equal("┌─────────┬────────┐\n│ (index) │ Values │\n├─────────┼────────┤\n│    0    │  'p'   │\n│    1    │  'q'   │\n└─────────┴────────┘", page.Lines()[1]);
        Assert.Equal("┌─────────┬───┐\n│ (index) │ b │\n├─────────┼───┤\n│    0    │ 2 │\n└─────────┴───┘", page.Lines()[2]);
        Assert.Equal("5", page.Lines()[3]);
    }

    [Fact]
    public void ConsoleTraceShowsTheCallers()
    {
        using var page = new Page();
        page.Run("function inner() { console.trace('where', 1); }\nfunction outer() { inner(); }\nouter();");
        var lines = page.Lines()[0].Split('\n');
        Assert.Equal("Trace: where 1", lines[0]);
        Assert.Contains(lines, l => l.Contains("inner"));
        Assert.Contains(lines, l => l.Contains("outer"));
        Assert.DoesNotContain(lines, l => l.Contains("folio:platform"));
    }

    [Fact]
    public void AConsoleLoopCannotFloodTheHost()
    {
        using var page = new Page();
        page.Run("for (var i = 0; i < 1500; i++) console.log('line ' + i);");
        Assert.Equal(1001, page.Messages.Count);
        Assert.Equal("line 999", page.Messages[999].Message);
        Assert.Equal(Severity.Warning, page.Messages[1000].Severity);
        Assert.Contains("dropped", page.Messages[1000].Message);
        page.Run("console.log('still dropped');");
        Assert.Equal(1001, page.Messages.Count);
    }

    [Fact]
    public void ALongConsoleMessageIsCut()
    {
        using var page = new Page();
        page.Run("console.log('x'.repeat(100000));");
        Assert.InRange(page.Messages[0].Message.Length, 16384, 16384 + 80);
        Assert.Contains("cut", page.Messages[0].Message);
    }

    // ---- atob and btoa ----

    [Fact]
    public void AtobAndBtoaFollowForgivingBase64()
    {
        using var page = new Page();
        Assert.Equal("aGVsbG8=", page.Run("btoa('hello')"));
        Assert.Equal("6Q==", page.Run("btoa('\\u00e9')"));
        Assert.Equal("hello", page.Run("atob('aGVsbG8=')"));
        Assert.Equal("a", page.Run("atob('YQ')")); // padding may be left out
        Assert.Equal("hello", page.Run("atob(' aGV\\tsbG\\n8= ')")); // ASCII whitespace is ignored
        Assert.Equal("é", page.Run("atob('6Q==')"));
        Assert.Equal("InvalidCharacterError", page.Run("try { atob('!!'); 'no' } catch (e) { e.name }"));
        Assert.Equal("InvalidCharacterError", page.Run("try { atob('Y'); 'no' } catch (e) { e.name }")); // a lone sextet
        Assert.Equal("InvalidCharacterError", page.Run("try { atob('YQ='); 'no' } catch (e) { e.name }")); // padding that does not complete a quartet
        Assert.Equal("InvalidCharacterError", page.Run("try { btoa('\\u0100'); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { atob(); 'no' } catch (e) { e.name }"));
        Assert.Equal("null", page.Run("btoa(null) === 'bnVsbA==' ? 'null' : 'wrong'"));
    }

    // ---- TextEncoder and TextDecoder ----

    [Fact]
    public void TextEncoderEncodesUtf8AndFillsABuffer()
    {
        using var page = new Page();
        Assert.Equal("utf-8", page.Run("new TextEncoder().encoding"));
        Assert.Equal("9", page.Run("new TextEncoder().encode('\\u00e9\\u20ac\\ud83d\\ude00').length.toString()"));
        Assert.Equal("239,191,189", page.Run("Array.from(new TextEncoder().encode('\\ud800')).join()")); // a lone surrogate becomes U+FFFD
        Assert.Equal("0", page.Run("new TextEncoder().encode().length.toString()"));
        Assert.Equal("2,4", page.Run("var out = new Uint8Array(4); var r = new TextEncoder().encodeInto('a\\u20acz', out); r.read + ',' + r.written"));
        Assert.Equal("1,1", page.Run("var out = new Uint8Array(2); var r = new TextEncoder().encodeInto('a\\u20ac', out); r.read + ',' + r.written")); // no half character
        Assert.Equal("TypeError", page.Run("try { new TextEncoder().encodeInto('a', []); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { TextEncoder(); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void TextDecoderDecodesTheEncodingsFolioHas()
    {
        using var page = new Page();
        Assert.Equal("é€😀", page.Run("new TextDecoder().decode(new Uint8Array([0xc3,0xa9,0xe2,0x82,0xac,0xf0,0x9f,0x98,0x80]))"));
        Assert.Equal("�", page.Run("new TextDecoder().decode(new Uint8Array([0xff]))"));
        Assert.Equal("hi", page.Run("new TextDecoder('utf-16le').decode(new Uint8Array([0x68,0,0x69,0]))"));
        Assert.Equal("hi", page.Run("new TextDecoder('UTF-16BE').decode(new Uint8Array([0,0x68,0,0x69]))"));
        Assert.Equal("€é", page.Run("new TextDecoder('latin1').decode(new Uint8Array([0x80,0xe9]))")); // 0x80 is the euro sign in windows-1252
        Assert.Equal("windows-1252", page.Run("new TextDecoder(' ISO-8859-1 ').encoding"));
        Assert.Equal("utf-16le", page.Run("new TextDecoder('unicode').encoding"));
        Assert.Equal("RangeError", page.Run("try { new TextDecoder('shift_jis'); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeError", page.Run("try { TextDecoder(); 'no' } catch (e) { e.name }"));
        Assert.Equal("ab", page.Run("new TextDecoder().decode(new Uint8Array([97,98]).buffer)"));
        Assert.Equal("b", page.Run("new TextDecoder().decode(new DataView(new Uint8Array([97,98]).buffer, 1))"));
    }

    [Fact]
    public void TextDecoderHonoursFatalIgnoreBomAndStreaming()
    {
        using var page = new Page();
        Assert.Equal("TypeError", page.Run("try { new TextDecoder('utf-8', {fatal: true}).decode(new Uint8Array([0xc3])); 'no' } catch (e) { e.name }"));
        Assert.Equal("true,false", page.Run("var d = new TextDecoder('utf-8', {fatal: true, ignoreBOM: false}); d.fatal + ',' + d.ignoreBOM"));
        Assert.Equal("a", page.Run("new TextDecoder().decode(new Uint8Array([0xef,0xbb,0xbf,97]))")); // the BOM is dropped
        Assert.Equal("﻿a", page.Run("new TextDecoder('utf-8', {ignoreBOM: true}).decode(new Uint8Array([0xef,0xbb,0xbf,97]))"));
        Assert.Equal("€!", page.Run("var d = new TextDecoder(); d.decode(new Uint8Array([0xe2,0x82]), {stream: true}) + d.decode(new Uint8Array([0xac,33]))"));
        Assert.Equal("😀", page.Run("var d = new TextDecoder('utf-16le'); d.decode(new Uint8Array([0x3d,0xd8]), {stream: true}) + d.decode(new Uint8Array([0x00,0xde]))"));
        // A BOM is only dropped at the start of a stream, not of each chunk.
        Assert.Equal("a﻿b", page.Run("var d = new TextDecoder(); d.decode(new Uint8Array([97]), {stream: true}) + d.decode(new Uint8Array([0xef,0xbb,0xbf,98]))"));
    }

    // ---- structuredClone ----

    [Fact]
    public void StructuredCloneCopiesWhatTheEngineHasKeepingCyclesAndSharedReferences()
    {
        using var page = new Page();
        Assert.Equal("true,true", page.Run("var a = {}; a.self = a; var b = structuredClone(a); (b.self === b) + ',' + (b !== a)"));
        Assert.Equal("true", page.Run("var o = {}; var c = structuredClone([o, o]); c[0] === c[1] && c[0] !== o"));
        Assert.Equal("1,true,1", page.Run("var c = structuredClone(new Map([[1, new Set([2])]])); c.size + ',' + c.get(1).has(2) + ',' + c.get(1).size"));
        Assert.Equal("a+bgi,true", page.Run("var r = structuredClone(/a+b/gi); r.source + r.flags + ',' + (r instanceof RegExp)"));
        Assert.Equal("5,true", page.Run("var d = structuredClone(new Date(5)); d.getTime() + ',' + (d instanceof Date)"));
        Assert.Equal("RangeError:boom,true", page.Run("var e = structuredClone(new RangeError('boom')); e.name + ':' + e.message + ',' + (e instanceof RangeError)"));
        Assert.Equal("2,1,2,true", page.Run("var s = new Uint8Array([1, 2, 3, 4]).subarray(1, 3); var c = structuredClone(s); c.length + ',' + c.byteOffset + ',' + c[0] + ',' + (c.buffer !== s.buffer)"));
        Assert.Equal("true", page.Run("var ab = new ArrayBuffer(8); var c = structuredClone({a: ab, b: new Uint8Array(ab)}); c.a === c.b.buffer && c.a !== ab"));
        Assert.Equal("true,5,false", page.Run("var c = structuredClone([new Number(5), new Boolean(false)]); (c[0] instanceof Number) + ',' + c[0].valueOf() + ',' + c[1].valueOf()"));
        Assert.Equal("false,1", page.Run("class A { constructor() { this.v = 1; } } var c = structuredClone(new A()); (c instanceof A) + ',' + c.v")); // plain object: the prototype is not kept
        Assert.Equal("undefined,null,NaN", page.Run("[structuredClone(undefined), structuredClone(null), structuredClone(NaN)].map(String).join()"));
        Assert.Equal("-0", page.Run("Object.is(structuredClone(-0), -0) ? '-0' : 'plus'"));
        Assert.Equal("false,3", page.Run("var c = structuredClone([1, , 3]); (1 in c) + ',' + c.length"));
        Assert.Equal("0,4", page.Run("var t = new ArrayBuffer(4); var c = structuredClone(t, {transfer: [t]}); t.byteLength + ',' + c.byteLength"));
    }

    [Fact]
    public void StructuredCloneRefusesWhatCannotBeCloned()
    {
        using var page = new Page();
        foreach (var value in new[] { "function () {}", "{f() {}}", "Symbol('x')", "{s: Symbol('x')}", "document.getElementById('el')", "document", "Object(Symbol('x'))", "new WeakMap()", "window.localStorage" })
            Assert.Equal("DataCloneError", page.Run($"try {{ structuredClone({value}); 'cloned' }} catch (e) {{ e.name }}"));
        Assert.Equal("TypeError", page.Run("try { structuredClone(); 'no' } catch (e) { e.name }"));
    }

    // ---- crypto ----

    [Fact]
    public void CryptoGetRandomValuesFillsIntegerArraysUpToTheQuota()
    {
        using var page = new Page();
        Assert.Equal("16,true", page.Run("var a = new Uint8Array(16); var r = crypto.getRandomValues(a); r.length + ',' + (r === a)"));
        Assert.Equal("true", page.Run("var a = new Uint32Array(8); crypto.getRandomValues(a); a.some(function (v) { return v !== 0; })"));
        Assert.Equal("ok", page.Run("crypto.getRandomValues(new Uint8Array(65536)); 'ok'"));
        Assert.Equal("QuotaExceededError", page.Run("try { crypto.getRandomValues(new Uint8Array(65537)); 'no' } catch (e) { e.name }"));
        Assert.Equal("QuotaExceededError", page.Run("try { crypto.getRandomValues(new Uint32Array(16385)); 'no' } catch (e) { e.name }")); // bytes, not elements
        Assert.Equal("TypeMismatchError", page.Run("try { crypto.getRandomValues(new Float32Array(2)); 'no' } catch (e) { e.name }"));
        Assert.Equal("TypeMismatchError", page.Run("try { crypto.getRandomValues([1, 2]); 'no' } catch (e) { e.name }"));
    }

    [Fact]
    public void CryptoRandomUuidMakesVersion4Uuids()
    {
        using var page = new Page();
        var uuids = Enumerable.Range(0, 20).Select(_ => page.Run("crypto.randomUUID()")).ToList();
        Assert.All(uuids, u => Assert.Matches(new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$"), u));
        Assert.Equal(20, uuids.Distinct().Count());
    }

    [Fact]
    public void CryptoRepeatsOnlyInTestMode()
    {
        string Draw(int? seed)
        {
            using var page = new Page(seed);
            return page.Run("var a = new Uint8Array(8); crypto.getRandomValues(a); Array.from(a).join() + '|' + crypto.randomUUID()");
        }
        Assert.Equal(Draw(7), Draw(7));
        Assert.NotEqual(Draw(7), Draw(8));
        Assert.NotEqual(Draw(null), Draw(null));
    }
}

using System.Text;
using System.Text.Json;
using Folio.Css;
using Folio.Diagrams;
using Folio.Dom;
using Folio.Html;
using Folio.Imaging;
using Folio.Layout;
using Folio.Painting;
using Folio.Resources;
using Folio.RenderTests;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Style;
using Folio.Tests.Typography;
using Folio.Typography;
using Folio.Xml;

namespace Folio.Fuzz;

/// <summary>One fuzzed entry point: it must not throw on any input.</summary>
internal sealed record Target(string Name, Action<byte[]> Run, Func<IEnumerable<byte[]>> Seeds, string[] Dictionary);

internal static class Targets
{
    // Images stay small so that each input decodes quickly.
    private const long MaxPixels = 1 << 20;

    private static readonly MediaContext Media = new(800, 600);

    private const string StyledDocument =
        "<!DOCTYPE html><html lang=en><head><title>t</title></head><body class=a><main id=m><h1>Title</h1>" +
        "<p class='b c' style='color: red'>Text <a href=#x>link</a> <em>em</em></p><ul><li>1<li>2</ul>" +
        "<table><tr><td>cell</td></tr></table><div dir=rtl><span>span</span></div></main></body></html>";

    public static readonly Target[] All =
    [
        new("html", Html, () => Cases("Html", "Style"), ["<", ">", "</", "<!--", "-->", "<!DOCTYPE html>", "&amp;", "&#x", "<table>",
            "<tr>", "<td>", "<template>", "<svg>", "<math>", "<select>", "<option>", "<p>", "<b>", "<a>", "<style>", "</style>",
            "<script>", "<textarea>", "<frameset>", "<body>", "<html>", " style='", "=\"", "\0"]),
        new("css", Css, () => Cases("Css", "Style").Append(UserAgentStyleSheet()).Concat(CssResetSeeds()), ["{", "}", "(", ")", "[", "]", ";", ":", ",",
            "@media ", "@supports ", "@layer ", "@import ", "@property ", "@font-face", "!important", "var(--a)", "--a:", "calc(",
            "url(", "\\", "/*", "*/", "\"", "'", "&", ":is(", ":not(", ":nth-child(", "::before", "attr(", "#", ".",
            "all:", "initial", "inherit", "unset", "revert", "revert-layer"]),
        new("png", data => PngDecoder.Decode(data, MaxPixels), () => Images("*.png"), []),
        new("jpeg", data => JpegDecoder.Decode(data, MaxPixels), () => Images("*.jpg"), []),
        new("gif", data => GifDecoder.Decode(data, MaxPixels), () => Images("*.gif"), ["GIF89a", "GIF87a", "!", ",", ";", "\0", "ÿ"]),
        new("webp", data => WebpDecoder.Decode(data, MaxPixels), () => Images("*.webp"), ["RIFF", "WEBP", "VP8L", "VP8X", "VP8 ", "ALPH", "ANMF", "ANIM", "/"]),
        new("font", Font, FontSeeds, ["wOFF", "wOF2", "OTTO", "true", "ttcf", "glyf", "loca", "hmtx", "cmap", "head", "hhea", "maxp"]),
        new("svg", Svg, SvgSeeds, ["<svg xmlns=\"http://www.w3.org/2000/svg\">", "</svg>", "<?xml version=\"1.0\"?>", "<!DOCTYPE svg [", "]>",
            "<![CDATA[", "]]>", "<!--", "&#x", "&amp;", "xmlns:x=\"", "<g>", "</g>", "<path d=\"", "M0 0", "A1 1 0 1 1", "C", "Z", " viewBox=\"",
            " preserveAspectRatio=\"", " transform=\"", "rotate(", "matrix(", "<text", "<tspan", " x=\"", " dx=\"", "%", "e9", "-", ".",
            " stroke-dasharray=\"", "url(#", "<rect", "<circle", " r=\"", "<polygon points=\"", "<svg", " width=\"", " style=\"", "<style>",
            "<mask", "<filter", "<feGaussianBlur stdDeviation=\"", "<feTurbulence baseFrequency=\"", "<feMorphology radius=\"", " result=\"", " in=\"",
            " in2=\"", " numOctaves=\"", " primitiveUnits=\"objectBoundingBox\"", " filter=\"", " mask=\"",
            "<pattern id=\"", " patternUnits=\"userSpaceOnUse\"", " patternTransform=\"", " href=\"#", " fill=\"url(#",
            "<foreignObject width=\"", "<div xmlns=\"http://www.w3.org/1999/xhtml\">"]),
        new("cssom", Cssom, CssomSeeds, ["{", "}", ";", ":", "@media ", "@supports ", "@import ", "@namespace ", "@keyframes ", "@layer ", "@container ",
            "@font-face", "@property ", "@page ", "@charset ", "@starting-style", "!important", "&", "from", "to", "50%", "url(", "(", ")", "\"", "'", "\\", "/*", "*/",
            "all:", "unset", "var(--x,revert-layer)"]),
        new("canvas", Canvas, CanvasSeeds, ["M", "L", "C", "Z", "a", "e", "red", "#", "rgb(", "hsl(", "blur(", "drop-shadow(", "px ", "serif", "bold ", "repeat", "evenodd", "lighter", "xor", "NaN", ",", "-", "e999"]),
        new("webvtt", WebVtt, WebVttSeeds, ["WEBVTT", "NOTE", "--> ", "00:00.000", "align:start", "&amp;", "<b>", "\n\n"]),
        new("url", Url, UrlSeeds, ["http://", "https://", "file:///", "//", "../", "./", "%2e", "%41", "[::1]", "@", ":", "?", "#", "\\", "xn--", "0x", "..", "\n"]),
        new("diagrams", Diagrams, DiagramSeeds, ["flowchart", "sequenceDiagram", "classDiagram", "stateDiagram-v2", "erDiagram", "timeline", "gantt", "pie", "-->", "<|--", "||--o{", ":", "section", "dateFormat"]),
        new("ipc", Ipc, IpcSeeds, ["{", "}", "Heartbeat", "Load", "ResourceRequest", "\0", "\xff"]),
        new("cache-storage", CacheStorage, CacheStorageSeeds, ["Operation", "open", "put", "match", "delete", "release", "$last", "vary", "GET", "Headers", "null"]),
    ];

    public static Target Find(string name) => All.Single(t => t.Name == name);

    private static void Html(byte[] data)
    {
        var document = TreeBuilder.Parse(Encoding.UTF8.GetString(data));
        CheckLinks(document);
        StyleResolver.Resolve(document, Media);
    }

    // A standalone SVG file through parsing, style, layout and the display list; inline SVG in HTML through its render
    // tree and painting (the HTML around it is the html target's).
    private static void Svg(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var standalone = XmlParser.IsSvgDocument(text);
        var document = standalone ? XmlParser.Parse(text) : TreeBuilder.Parse(text);
        CheckLinks(document);
        StyleResolver.Resolve(document, Media);
        if (standalone)
        {
            if (BoxTreeBuilder.Build(document, new ImageLoader(ResourceLoader.DataUrlsOnly, null)) is { } root)
                DisplayListBuilder.Build(LayoutEngine.LayoutDocument(root, 800, 600));
            return;
        }
        var context = new LayoutContext(new FontCollection());
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is ElementNode { LocalName: "svg" } svg && svg.Name.Namespace == Namespaces.Svg
                && (svg.Parent as ElementNode)?.Name.Namespace != Namespaces.Svg
                && Folio.Svg.SvgRenderTree.Build(svg, 300, 150, context) is { } tree)
                SvgPainter.Paint(tree, System.Numerics.Vector2.Zero, []);
        }
    }

    private static IEnumerable<byte[]> SvgSeeds() =>
        new[] { "reftests", "goldens" }.SelectMany(folder => Directory.GetFiles(Path.Combine(RepoPaths.Tests, folder, "svg"), "*.html"))
            .Select(File.ReadAllBytes);

    // Web font unwrapping, then the font reader, its kerning and its substitutions, as a document's @font-face would run them.
    private static void Font(byte[] data)
    {
        if (WebFontDecoder.Decode(data) is not { } sfnt || FontFace.Parse(sfnt) is not { } face)
            return;
        face.Kerning(face.GlyphFor('A'), face.GlyphFor('V'));
        face.Substitute(face.GlyphFor('1'), "pnum tnum");
        foreach (var glyph in new[] { (ushort)0, (ushort)1, face.GlyphFor('A'), face.GlyphFor('g'), face.GlyphFor('\u00E9'), (ushort)Math.Max(0, face.GlyphCount - 1) })
            Folio.Painting.GlyphOutlines.Get(face, glyph);
    }

    private static IEnumerable<byte[]> FontSeeds()
    {
        var box = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "FolioBox.ttf"));
        // The text font brings GPOS kerning (both pair adjustment formats) for the mutations to reach.
        var text = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "SourceSans3-Regular.ttf"));
        return [box, WebFontWriter.Woff(box), WebFontWriter.Woff2(box), text];
    }

    private static void Css(byte[] data)
    {
        var css = Encoding.UTF8.GetString(data);
        CssParser.ParseBlockContents(css);
        StyleResolver.Resolve(TreeBuilder.Parse(StyledDocument), Media, userStyleSheet: css);
    }

    private static IEnumerable<byte[]> CssResetSeeds() => new[]
    {
        "main { all: unset; display: block; color: green }",
        "main { --x: 7px; all: var(--x) }",
        "@layer a { main { color: red } } @layer b { main { all: var(--missing, revert-layer) } }",
        "main { color: red; all: var(--missing, revert) }",
        "@layer a { main { --x: blue } } @layer b { main { --x: var(--missing, revert-layer); --a: var(--x); color: var(--a) } }",
        "main { --x: var(--a, revert-layer); --a: var(--x); all: var(--x, initial) }",
        "@property --x { syntax: '*'; inherits: false } body { --x: blue } main { all: var(--x, initial) }"
    }.Select(Encoding.UTF8.GetBytes);

    // CSSOM insertRule/appendRule/deleteRule with generated rule text (issue #499): a DOMException is the expected answer to a
    // bad rule; nothing else may escape, and a sheet never grows past its rule limit.
    private static void Cssom(byte[] data)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(Encoding.UTF8.GetString(data));
        var document = Document.Parse("<!DOCTYPE html><style>a { color: red } @media screen { b { top: 0 } } @keyframes k { from { top: 0 } } @layer base { c { top: 1px } }</style><p>x</p>");
        document.Paint(800, 600);
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        engine.Evaluate($$"""
            const rule = {{text}};
            const attempt = (f) => { try { f(); } catch (e) { if (!(e instanceof DOMException) && !(e instanceof TypeError)) throw e; } };
            const sheet = document.styleSheets[0];
            for (let index = 0; index <= 5; index++) attempt(() => sheet.insertRule(rule, index));
            attempt(() => sheet.cssRules[1].insertRule(rule, 0));
            attempt(() => sheet.cssRules[1].insertRule(rule, 99));
            attempt(() => sheet.cssRules[2].appendRule(rule));
            attempt(() => sheet.cssRules[3].insertRule(rule, 0));
            attempt(() => { sheet.cssRules[0].selectorText = rule; });
            attempt(() => { sheet.cssRules[0].style.cssText = rule; });
            attempt(() => sheet.deleteRule(0));
            attempt(() => sheet.cssRules[0].cssText);
            if (sheet.cssRules.length > 100000) throw new Error('rule limit');
            """);
        document.Update();
    }

    private static IEnumerable<byte[]> CssomSeeds() => new[]
    {
        "p { all: unset }", "@layer a { p { color: blue } } @layer b { p { all: var(--missing, revert-layer) } }",
        "p { color: red }", "@media (min-width: 1px) { p { top: 0 } }", "@import url(a.css);", "@namespace url(http://example.com);",
        "@keyframes x { 50% { top: 1px } }", "@layer a, b;", "@supports (display: grid) { a { b: c } }", "@font-face { font-family: F }",
        "a { & b { c: d } }", "50% { top: 1px }", "not a rule", "a { } b { }", "", "@charset \"utf-8\";",
    }.Select(Encoding.UTF8.GetBytes);

    private static void WebVtt(byte[] data)
    {
        WebVttParser.Parse(Encoding.UTF8.GetString(data));
    }

    private static IEnumerable<byte[]> WebVttSeeds() => new[]
    {
        "WEBVTT\n\n00:00.000 --> 00:01.000\nHello",
        "WEBVTT\n\nintro\n00:01.500 --> 00:03.250 align:start\nHello &amp; world",
        "WEBVTT\n\nNOTE this is ignored\ncomment\n",
        "WEBVTT\n\n00:02.000 --> 00:01.000\nreversed",
    }.Select(Encoding.UTF8.GetBytes);

    // A URL against an optional base (the first line), as the parser and, through a copy, each setter take it; what parses must
    // serialize to text that parses to the same URL again.
    private static void Url(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var split = text.IndexOf('\n');
        var baseUrl = split > 0 ? WhatwgUrl.Parse(text[..split]) : null;
        var input = split >= 0 ? text[(split + 1)..] : text;
        if (WhatwgUrl.Parse(input, baseUrl) is { } url)
        {
            var href = url.Serialize();
            var again = WhatwgUrl.Parse(href) ?? throw new InvalidOperationException($"'{href}' does not parse again.");
            if (again.Serialize() != href)
                throw new InvalidOperationException($"'{href}' parses to '{again.Serialize()}'.");
            _ = (url.Origin, url.HostString, url.PathString);
            foreach (var set in new Action<WhatwgUrl>[] { u => u.SetProtocol(input), u => u.SetUsername(input), u => u.SetPassword(input), u => u.SetHost(input),
                         u => u.SetHostname(input), u => u.SetPort(input), u => u.SetPathname(input), u => u.SetSearch(input), u => u.SetHash(input) })
            {
                var copy = url.Clone();
                set(copy);
                _ = copy.Serialize();
            }
        }
        var pairs = WhatwgUrl.ParseForm(text);
        if (WhatwgUrl.ParseForm(WhatwgUrl.SerializeForm(pairs)).SequenceEqual(pairs) is false)
            throw new InvalidOperationException("Form data does not round-trip.");
    }

    private static IEnumerable<byte[]> UrlSeeds() => new[]
    {
        "https://user:pw@example.com:8080/a/b/../c?x=1&y=%20z#frag",
        "http://example.com/dir/\n../up/./down?q#h", "file:///C:/a/b", "//host/path\nhttps://base.test/x/y", "http://[2001:db8::1]:80/",
        "http://0x7f.1/", "mailto:someone@example.com", "data:text/plain,hello world#frag", "http://xn--nxasmq6b.com/", "a=1&b=%C3%A9&a=2+3",
    }.Select(Encoding.UTF8.GetBytes);

    private static void Diagrams(byte[] data)
    {
        // Every parser is deliberately total: unsupported or malformed text must return an SVG or throw only from
        // the outer fuzz harness's normal input limits, never escape as an unexpected parser exception.
        DiagramRenderer.Render(Encoding.UTF8.GetString(data));
    }

    private static IEnumerable<byte[]> DiagramSeeds() => new[]
    {
        "flowchart LR\nA[Start] --> B[Finish]", "sequenceDiagram\nAlice->>Bob: Hello",
        "classDiagram\nBase <|-- Child", "stateDiagram-v2\n[*] --> Ready",
        "erDiagram\nCUSTOMER ||--o{ ORDER : places", "timeline\n2024 : Launch",
        "gantt\ndateFormat YYYY-MM-DD\nBuild :done, id, 2024-01-30, 3d", "pie title Sales\n\"North\" : 60",
    }.Select(Encoding.UTF8.GetBytes);

    // Canvas 2D calls with hostile numbers and strings (issues #504 and #507): the text picks the calls and their arguments from
    // a list of awkward values. Only DOMException, TypeError and RangeError may come out of a call, and the bitmap stays readable.
    private static void Canvas(byte[] data)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(Encoding.UTF8.GetString(data));
        var document = Document.Parse("<!DOCTYPE html><canvas id=c width=48 height=48></canvas><canvas id=s width=8 height=8></canvas>",
            new FolioOptions { Raster = new Folio.Skia.SkiaRasterBackend() });
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, document);
        try
        {
        engine.Evaluate($$"""
            const s = {{text}};
            const canvas = document.getElementById('c'), ctx = canvas.getContext('2d'), source = document.getElementById('s');
            source.getContext('2d').fillRect(0, 0, 4, 4);
            const awkward = [0, 1, -1, 0.5, 2, 7, 48, 100, NaN, Infinity, -Infinity, 1e300, -1e300, 1e-300, 65536, 2 ** 31, -(2 ** 31), 2 ** 53];
            const code = (i) => (s.charCodeAt(i % Math.max(1, s.length)) || 0);
            const num = (i) => awkward[code(i) % awkward.length];
            const str = (i) => s.slice(code(i) % Math.max(1, s.length));
            const calls = [
              (i) => ctx.fillRect(num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.strokeRect(num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.clearRect(num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.moveTo(num(i), num(i + 1)),
              (i) => ctx.lineTo(num(i), num(i + 1)),
              (i) => ctx.arc(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), code(i) % 2 === 0),
              (i) => ctx.arcTo(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4)),
              (i) => ctx.ellipse(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5), num(i + 6), code(i) % 2 === 0),
              (i) => ctx.roundRect(num(i), num(i + 1), num(i + 2), num(i + 3), [num(i + 4), num(i + 5)]),
              (i) => ctx.quadraticCurveTo(num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.bezierCurveTo(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5)),
              (i) => ctx.rect(num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.closePath(),
              (i) => ctx.beginPath(),
              (i) => ctx.fill(code(i) % 2 ? 'evenodd' : 'nonzero'),
              (i) => ctx.stroke(),
              (i) => ctx.clip(),
              (i) => ctx.fill(new Path2D(str(i))),
              (i) => ctx.stroke(new Path2D(str(i))),
              (i) => ctx.isPointInPath(num(i), num(i + 1)),
              (i) => ctx.isPointInStroke(num(i), num(i + 1)),
              (i) => ctx.translate(num(i), num(i + 1)),
              (i) => ctx.scale(num(i), num(i + 1)),
              (i) => ctx.rotate(num(i)),
              (i) => ctx.transform(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5)),
              (i) => ctx.setTransform(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5)),
              (i) => ctx.resetTransform(),
              (i) => ctx.save(),
              (i) => ctx.restore(),
              (i) => { ctx.fillStyle = str(i); },
              (i) => { ctx.strokeStyle = str(i); },
              (i) => { ctx.shadowColor = str(i); ctx.shadowBlur = num(i + 1); ctx.shadowOffsetX = num(i + 2); },
              (i) => { ctx.filter = str(i); },
              (i) => { ctx.font = str(i); },
              (i) => { ctx.globalCompositeOperation = str(i); },
              (i) => { ctx.globalAlpha = num(i); },
              (i) => { ctx.lineWidth = num(i); ctx.miterLimit = num(i + 1); ctx.lineDashOffset = num(i + 2); },
              (i) => ctx.setLineDash([num(i), num(i + 1), num(i + 2)]),
              (i) => { const g = ctx.createLinearGradient(num(i), num(i + 1), num(i + 2), num(i + 3)); g.addColorStop(code(i) / 255, str(i + 1)); ctx.fillStyle = g; },
              (i) => { const g = ctx.createRadialGradient(num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5)); g.addColorStop(0.5, 'red'); ctx.strokeStyle = g; },
              (i) => { const g = ctx.createConicGradient(num(i), num(i + 1), num(i + 2)); g.addColorStop(0, 'blue'); ctx.fillStyle = g; },
              (i) => { const p = ctx.createPattern(source, str(i)); if (p) { p.setTransform({a: num(i), d: num(i + 1)}); ctx.fillStyle = p; } },
              (i) => ctx.drawImage(source, num(i), num(i + 1)),
              (i) => ctx.drawImage(source, num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.drawImage(source, num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5), num(i + 6), num(i + 7)),
              (i) => ctx.drawImage(canvas, num(i), num(i + 1)),
              (i) => ctx.getImageData(num(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.putImageData(ctx.createImageData(2, 2), num(i), num(i + 1), num(i + 2), num(i + 3), num(i + 4), num(i + 5)),
              (i) => ctx.createImageData(num(i), num(i + 1)),
              (i) => new ImageData(num(i), num(i + 1)),
              (i) => ctx.fillText(str(i), num(i + 1), num(i + 2), num(i + 3)),
              (i) => ctx.strokeText(str(i), num(i + 1), num(i + 2)),
              (i) => ctx.measureText(str(i)).width,
              (i) => canvas.toDataURL(str(i), num(i + 1)),
              (i) => { canvas.width = Math.min(Math.abs(num(i)) | 0, 96); },
            ];
            const allowed = (e) => e instanceof DOMException || e instanceof TypeError || e instanceof RangeError;
            for (let step = 0; step < 120; step++) {
              try { calls[code(step * 3) % calls.length](step * 5 + code(step)); } catch (e) { if (!allowed(e)) throw e; }
            }
            ctx.getImageData(0, 0, 1, 1);
            """);
        }
        catch (Folio.Scripting.ScriptLimitExceededException)
        {
            // The sandbox's own limits (memory, time) ending a script that asked for too much is the limit holding.
        }
        document.Paint(300, 300);
    }

    private static IEnumerable<byte[]> CanvasSeeds() => new[]
    {
        "M10 10 L 20 20 C 1 2 3 4 5 6 Z", "red", "rgb(1,2,3)", "blur(5px) drop-shadow(1px 1px 1px red)", "bold 12px serif", "lighter", "repeat-x", "e999", "NaN",
        "10px", "M 0 0 A 1 1 0 0 1 10 10", "hsl(120, 50%, 50%)", "", "evenodd", "xor",
    }.Select(Encoding.UTF8.GetBytes);

    private static void Ipc(byte[] data)
    {
        if (ContentProtocol.TryDecode(data, out var message) && message is not null)
        {
            message.TryGetCapabilities(out _);
            message.TryGetDisplayList(out _);
            message.TryGetInput(out _);
        }
        if (data.Length >= 12)
            CanvasFrameStore.IsValidDescriptor(BitConverter.ToInt32(data, 0), BitConverter.ToInt32(data, 4), BitConverter.ToInt32(data, 8));
        DisplayListCodec.TryDecode(data, out _);
        var reader = new ContentProtocolReader();
        for (var offset = 0; offset < data.Length; offset += Math.Min(7, data.Length - offset))
            reader.Push(data.AsMemory(offset, Math.Min(7, data.Length - offset)));
    }

    private static IEnumerable<byte[]> IpcSeeds()
    {
        using var payload = System.Text.Json.JsonDocument.Parse("{}");
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Hello, 1, payload.RootElement.Clone()));
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Heartbeat, 2, payload.RootElement.Clone()));
        using var capabilities = System.Text.Json.JsonDocument.Parse("{\"capabilities\":{\"webGl\":true,\"media\":true,\"sound\":true,\"camera\":true,\"microphone\":true,\"allowSoundAutoplay\":true,\"workers\":true,\"webAssembly\":true,\"iframes\":true,\"webRtc\":true,\"serviceWorkers\":true}}");
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Load, 3, capabilities.RootElement.Clone()));
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Capabilities, 4, capabilities.RootElement.Clone()));
        using var storageRequest = System.Text.Json.JsonDocument.Parse("{\"operation\":\"begin\",\"length\":1,\"hash\":\"0000000000000000000000000000000000000000000000000000000000000000\"}");
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.StorageRequest, 5, storageRequest.RootElement.Clone()));
        using var storageResponse = System.Text.Json.JsonDocument.Parse("{\"requestId\":5,\"result\":{\"token\":\"a\"}}");
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.StorageResponse, 6, storageResponse.RootElement.Clone()));
        var seedList = new DisplayList();
        seedList.Items.Add(new DisplayItem(DisplayItemKind.Fill, Shape: new RoundedRect(new RectF(0, 0, 50, 50), default), Color: new Folio.Css.CssColor(255, 0, 0, 255)));
        var encodedDl = Convert.ToBase64String(DisplayListCodec.Encode(seedList));
        using var frameDoc = System.Text.Json.JsonDocument.Parse($"{{\"displayList\":\"{encodedDl}\",\"canvasFrame\":{{\"path\":\"test\",\"width\":100,\"height\":100,\"stride\":400,\"sequence\":1}}}}");
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Frame, 7, frameDoc.RootElement.Clone()));
        yield return DisplayListCodec.Encode(seedList);
    }

    private sealed class FuzzCacheStorage : IArtifactStorage
    {
        internal readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public string? Read(string key) => Values.GetValueOrDefault(key);
        public void Write(string key, string value) => Values[key] = value;
        public void Delete(string key) => Values.Remove(key);
        public IReadOnlyList<string> Keys(string prefix) => Values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    private static void CacheStorage(byte[] data)
    {
        ContentCacheBroker.Request[]? requests;
        try { requests = System.Text.Json.JsonSerializer.Deserialize<ContentCacheBroker.Request[]>(data); }
        catch (System.Text.Json.JsonException) { return; }
        if (requests is null || requests.Length > 64) return;
        var storage = new FuzzCacheStorage();
        using var broker = new ContentCacheBroker(storage, "fuzz", 16384);
        string? last = null;
        foreach (var request in requests)
        {
            if (request is null) continue;
            var current = request.Handle == "$last" ? request with { Handle = last } : request;
            var reply = System.Text.Json.JsonSerializer.Deserialize<ContentCacheBroker.Reply>(broker.Execute(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(current)))!;
            if (current.Operation == "open" && reply.ErrorName is null) last = reply.Value.GetString();
        }
        foreach (var pair in storage.Values)
        {
            if (pair.Key == CacheStorageState.StorageKey("fuzz"))
            {
                if (Encoding.UTF8.GetByteCount(pair.Value) > 16384) throw new InvalidOperationException("Cache quota was bypassed.");
            }
            else if (!pair.Key.StartsWith("fuzz:service-worker-registration/", StringComparison.Ordinal))
                throw new InvalidOperationException("A storage request escaped its artifact.");
        }
    }

    private static IEnumerable<byte[]> CacheStorageSeeds()
    {
        const string context = "00000000000000000000000000000001";
        var entry = new CacheStorageState.Entry(new("https://fuzz.example/data", "GET", []), 200, "OK", [], [0, 1, 255], false, "", "default");
        ContentCacheBroker.Request[] sequence = [new("open", context, Name: "data"), new("put", context, "$last", Entries: [entry]),
            new("match", context, "$last", RequestValue: entry.Request, Query: new(false, false, false)), new("delete", context, Name: "data"), new("releaseContext", context)];
        yield return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(sequence);
        yield return Encoding.UTF8.GetBytes("[null,{}, {\"Operation\":\"metadataWrite\",\"Context\":\"00000000000000000000000000000001\",\"Key\":\"other:service-worker-registration/x\",\"Value\":\"no\"}]");
    }

    // DOM invariant: parent, child and sibling links agree.
    private static void CheckLinks(ContainerNode parent)
    {
        Node? previous = null;
        for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child.Parent != parent || child.PreviousSibling != previous)
                throw new InvalidOperationException("Broken DOM links.");
            if (child is ContainerNode container)
                CheckLinks(container);
            previous = child;
        }
        if (parent.LastChild != previous)
            throw new InvalidOperationException("Broken DOM links.");
    }

    // The inputs of the parser and style case files in Folio.Tests: "=== name", "%directive" lines, input, "---", expected.
    private static IEnumerable<byte[]> Cases(params string[] folders) =>
        folders.SelectMany(folder => Directory.GetFiles(Path.Combine(RepoPaths.Tests, "Folio.Tests", folder), "*.txt", SearchOption.AllDirectories))
            .SelectMany(file => File.ReadAllText(file).ReplaceLineEndings("\n").Split("\n=== ").Skip(1))
            .Select(block => string.Join('\n', block.Split("\n---")[0].Split('\n').Skip(1).Where(line => !line.StartsWith('%'))))
            .Select(Encoding.UTF8.GetBytes);

    private static byte[] UserAgentStyleSheet() => File.ReadAllBytes(Path.Combine(RepoPaths.Root, "src", "Folio", "Style", "UserAgent.css"));

    private static IEnumerable<byte[]> Images(string pattern) =>
        Directory.GetFiles(Path.Combine(RepoPaths.Tests, "images"), pattern).Select(File.ReadAllBytes);
}

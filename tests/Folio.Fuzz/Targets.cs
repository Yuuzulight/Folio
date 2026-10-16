using System.Text;
using Folio.Css;
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
        new("css", Css, () => Cases("Css", "Style").Append(UserAgentStyleSheet()), ["{", "}", "(", ")", "[", "]", ";", ":", ",",
            "@media ", "@supports ", "@layer ", "@import ", "@property ", "@font-face", "!important", "var(--a)", "--a:", "calc(",
            "url(", "\\", "/*", "*/", "\"", "'", "&", ":is(", ":not(", ":nth-child(", "::before", "attr(", "#", "."]),
        new("png", data => PngDecoder.Decode(data, MaxPixels), () => Images("*.png"), []),
        new("jpeg", data => JpegDecoder.Decode(data, MaxPixels), () => Images("*.jpg"), []),
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
            "@font-face", "@property ", "@page ", "@charset ", "@starting-style", "!important", "&", "from", "to", "50%", "url(", "(", ")", "\"", "'", "\\", "/*", "*/"]),
        new("canvas", Canvas, CanvasSeeds, ["M", "L", "C", "Z", "a", "e", "red", "#", "rgb(", "hsl(", "blur(", "drop-shadow(", "px ", "serif", "bold ", "repeat", "evenodd", "lighter", "xor", "NaN", ",", "-", "e999"]),
        new("ipc", Ipc, IpcSeeds, ["{", "}", "Heartbeat", "Load", "ResourceRequest", "\0", "\xff"]),
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
        "p { color: red }", "@media (min-width: 1px) { p { top: 0 } }", "@import url(a.css);", "@namespace url(http://example.com);",
        "@keyframes x { 50% { top: 1px } }", "@layer a, b;", "@supports (display: grid) { a { b: c } }", "@font-face { font-family: F }",
        "a { & b { c: d } }", "50% { top: 1px }", "not a rule", "a { } b { }", "", "@charset \"utf-8\";",
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
        ContentProtocol.TryDecode(data, out _);
        var reader = new ContentProtocolReader();
        for (var offset = 0; offset < data.Length; offset += Math.Min(7, data.Length - offset))
            reader.Push(data.AsMemory(offset, Math.Min(7, data.Length - offset)));
    }

    private static IEnumerable<byte[]> IpcSeeds()
    {
        using var payload = System.Text.Json.JsonDocument.Parse("{}");
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Hello, 1, payload.RootElement.Clone()));
        yield return ContentProtocol.Encode(new ContentMessage(ContentMessageKind.Heartbeat, 2, payload.RootElement.Clone()));
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

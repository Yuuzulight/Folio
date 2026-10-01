using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Imaging;
using Folio.Layout;
using Folio.Painting;
using Folio.Resources;
using Folio.RenderTests;
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

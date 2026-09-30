using System.Text;
using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Imaging;
using Folio.RenderTests;
using Folio.Style;
using Folio.Tests.Typography;
using Folio.Typography;

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
    ];

    public static Target Find(string name) => All.Single(t => t.Name == name);

    private static void Html(byte[] data)
    {
        var document = TreeBuilder.Parse(Encoding.UTF8.GetString(data));
        CheckLinks(document);
        StyleResolver.Resolve(document, Media);
    }

    // Web font unwrapping, then the font reader, as a document's @font-face would run them.
    private static void Font(byte[] data)
    {
        if (WebFontDecoder.Decode(data) is { } sfnt && FontFace.Parse(sfnt) is { } face)
            face.GlyphFor('A');
    }

    private static IEnumerable<byte[]> FontSeeds()
    {
        var box = File.ReadAllBytes(Path.Combine(RepoPaths.Tests, "fonts", "FolioBox.ttf"));
        return [box, WebFontWriter.Woff(box), WebFontWriter.Woff2(box)];
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

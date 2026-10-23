using System.Numerics;
using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;
using Folio.Style;
using Folio.Typography;

namespace Folio.Tests.Painting;

public class DisplayListCodecTests
{
    private static readonly FolioOptions BoxFontOptions = new()
    {
        Fonts = new FontSettings
        {
            Source = new FontFolderSource(Path.Combine(AppContext.BaseDirectory, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["serif"] = ["Folio Box"],
                ["sans-serif"] = ["Folio Box"],
                ["monospace"] = ["Folio Box"],
            },
        },
    };

    [Fact]
    public void AllSeventyTwoConformancePagesDumpAndCompareEqual()
    {
        var root = ConformanceRoot();
        var files = Directory.EnumerateFiles(root, "index.html", SearchOption.AllDirectories)
            .Where(f =>
            {
                var rel = Path.GetRelativePath(root, f);
                return rel.StartsWith("S1", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S2", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S3", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S4", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S5", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("S6", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(72, files.Count);

        foreach (var file in files)
        {
            var html = File.ReadAllText(file);
            using var document = Document.Parse(html, BoxFontOptions);
            var original = document.Paint(800, 600).List;

            var sender = new DisplayListContext();
            var receiver = new DisplayListContext { FontSource = BoxFontOptions.Fonts?.Source };

            var bytes = DisplayListCodec.Encode(original, sender);
            Assert.True(DisplayListCodec.TryDecode(bytes, out var decoded, receiver));
            Assert.NotNull(decoded);

            var relPath = Path.GetRelativePath(root, file);
            var equal = DisplayListCodec.AreEqual(original, decoded, out var difference);
            Assert.True(equal, $"Display list mismatch for {relPath}: {difference}");
        }
    }

    [Fact]
    public void WebFontCrossesPipeOnceAcrossMultipleFrames()
    {
        var fontBytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", "FolioBox.ttf"));
        var fontFace = FontFace.Parse(fontBytes)!;

        var list1 = new DisplayList();
        list1.Items.Add(new DisplayItem(DisplayItemKind.Glyphs,
            Glyphs: new GlyphRun(fontFace, 16f, [1, 2, 3], [new Vector2(0, 20), new Vector2(10, 20), new Vector2(20, 20)])));

        var sender = new DisplayListContext();
        var receiver = new DisplayListContext();

        // Frame 1: font bytes transmitted
        var bytes1 = DisplayListCodec.Encode(list1, sender);
        Assert.True(bytes1.Length > fontBytes.Length, "First frame must include full font bytes.");

        var decoded1 = DisplayListCodec.Decode(bytes1, receiver);
        Assert.True(DisplayListCodec.AreEqual(list1, decoded1, out var diff1), diff1);
        Assert.Single(receiver.WebFontsByHash);

        // Frame 2: same font used, font bytes omitted
        var list2 = new DisplayList();
        list2.Items.Add(new DisplayItem(DisplayItemKind.Glyphs,
            Glyphs: new GlyphRun(fontFace, 24f, [1, 2], [new Vector2(0, 30), new Vector2(15, 30)])));

        var bytes2 = DisplayListCodec.Encode(list2, sender);
        Assert.True(bytes2.Length < bytes1.Length - (fontBytes.Length / 2),
            $"Second frame must omit font bytes. Frame 1: {bytes1.Length} bytes, Frame 2: {bytes2.Length} bytes.");

        var decoded2 = DisplayListCodec.Decode(bytes2, receiver);
        Assert.True(DisplayListCodec.AreEqual(list2, decoded2, out var diff2), diff2);
    }

    [Fact]
    public void ImagePixelsCrossPipeOnceAcrossMultipleFrames()
    {
        var rawPixels = new byte[100 * 100 * 4];
        rawPixels[0] = 255; rawPixels[3] = 255;
        var imageHandle = new DecodedImage(100, 100, rawPixels);

        var list1 = new DisplayList();
        list1.Items.Add(new DisplayItem(DisplayItemKind.Image,
            Shape: new RoundedRect(new RectF(0, 0, 100, 100), default),
            Image: imageHandle));

        var sender = new DisplayListContext();
        var receiver = new DisplayListContext();

        // Frame 1: pixels transmitted
        var bytes1 = DisplayListCodec.Encode(list1, sender);
        Assert.True(bytes1.Length >= rawPixels.Length, "First frame must include image pixels.");

        var decoded1 = DisplayListCodec.Decode(bytes1, receiver);
        Assert.True(DisplayListCodec.AreEqual(list1, decoded1, out var diff1), diff1);

        // Frame 2: same image used, pixels omitted
        var list2 = new DisplayList();
        list2.Items.Add(new DisplayItem(DisplayItemKind.Image,
            Shape: new RoundedRect(new RectF(10, 10, 100, 100), default),
            Image: imageHandle));

        var bytes2 = DisplayListCodec.Encode(list2, sender);
        Assert.True(bytes2.Length < bytes1.Length - (rawPixels.Length / 2),
            $"Second frame must omit pixels. Frame 1: {bytes1.Length} bytes, Frame 2: {bytes2.Length} bytes.");

        var decoded2 = DisplayListCodec.Decode(bytes2, receiver);
        Assert.True(DisplayListCodec.AreEqual(list2, decoded2, out var diff2), diff2);
    }

    [Fact]
    public void TabPanelsHtmlEncodesAndDecodesCleanly()
    {
        var html = """
            <!DOCTYPE html>
            <html>
            <head>
              <style>
                body { margin: 0; padding: 0; }
                button { width: 100px; height: 40px; margin: 0; padding: 0; }
                #p1 { width: 300px; height: 200px; background-color: rgb(255, 0, 0); }
                #p2 { width: 300px; height: 200px; background-color: rgb(0, 255, 0); }
              </style>
            </head>
            <body>
              <div id="tabs">
                <button id="tab1" onclick="document.getElementById('p1').style.display='block';document.getElementById('p2').style.display='none'">Tab 1</button>
                <button id="tab2" onclick="document.getElementById('p1').style.display='none';document.getElementById('p2').style.display='block'">Tab 2</button>
              </div>
              <div id="p1" style="display:block">Panel 1 Content</div>
              <div id="p2" style="display:none">Panel 2 Content</div>
            </body>
            </html>
            """;
        using var doc = Document.Parse(html);
        var original = doc.Paint(1280, 720).List;
        var bytes = DisplayListCodec.Encode(original);
        Assert.True(DisplayListCodec.TryDecode(bytes, out var decoded));
        Assert.True(DisplayListCodec.AreEqual(original, decoded!, out var diff), diff);
    }

    private static string ConformanceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Folio.slnx")))
                return Path.Combine(dir.FullName, "tests", "conformance");
        }
        throw new DirectoryNotFoundException("Folio.slnx not found above " + AppContext.BaseDirectory);
    }
}

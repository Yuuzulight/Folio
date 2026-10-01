using System.Text;

namespace Folio.Tests.Hosting;

public class DocumentTests
{
    [Fact]
    public void ParsesAStringAndReadsTheTitle()
    {
        using var document = Document.Parse("<!DOCTYPE html><title>  Quarterly \n  report </title><p>x");

        Assert.Equal("Quarterly report", document.Title);
        Assert.Empty(document.Diagnostics);
        Assert.True(document.ResourcesSettled.IsCompleted);
    }

    [Fact]
    public void AStandaloneSvgDocumentIsParsedAsXml()
    {
        using var document = Document.Parse("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\">"
            + "<title> An \n icon </title><rect width=\"5\" height=\"5\"/></svg>");

        Assert.Equal("svg", document.DocumentElement!.LocalName);
        Assert.Equal("http://www.w3.org/2000/svg", document.DocumentElement.NamespaceUri);
        Assert.Equal("An icon", document.Title);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void SvgWithoutAnXmlSignIsHtml()
    {
        Assert.Equal("html", Document.Parse("<svg><rect/></svg>").DocumentElement!.LocalName);
    }

    [Fact]
    public void XmlErrorsBecomeDiagnostics()
    {
        var document = Document.Parse("<svg xmlns=\"http://www.w3.org/2000/svg\">\n<g></svg>");

        var error = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticCode.ParseError, error.Code);
        Assert.Contains("mismatched-end-tag", error.Message);
        Assert.Equal("xml-parsing", error.Feature);
    }

    [Fact]
    public void TitleIsEmptyWithoutATitleElement()
    {
        Assert.Equal("", Document.Parse("<p>x").Title);
    }

    [Fact]
    public void ParseErrorsBecomeDiagnosticsWithLocations()
    {
        var document = Document.Parse("<!DOCTYPE html>\n<p>a\u0000b");

        // The tokenizer reports the null character and the tree builder ignores it, which is a parse error too.
        Assert.Equal(2, document.Diagnostics.Count);
        var error = document.Diagnostics[0];
        Assert.Equal(DiagnosticCode.ParseError, error.Code);
        Assert.Equal(Severity.Info, error.Severity);
        Assert.Contains("unexpected-null-character", error.Message);
        Assert.Equal(new SourceLocation(2, 5), error.Location);
    }

    [Fact]
    public void ParseErrorsAreSkippedWhenNotCollected()
    {
        var document = Document.Parse("<p>a\u0000b", new FolioOptions { CollectDiagnostics = false });

        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void LimitsAreReportedEvenWhenDiagnosticsAreOff()
    {
        var options = new FolioOptions
        {
            CollectDiagnostics = false,
            Limits = ResourceLimits.Default with { MaxNestingDepth = 10, MaxInputSize = 1000 },
        };

        var document = Document.Parse(string.Concat(Enumerable.Repeat("<div>", 300)), options);

        Assert.Equal(2, document.Diagnostics.Count(d => d.Code == DiagnosticCode.LimitExceeded));
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF }, "utf-8")]
    [InlineData(new byte[] { 0xFF, 0xFE }, "utf-16le")]
    [InlineData(new byte[] { 0xFE, 0xFF }, "utf-16be")]
    public void ByteOrderMarksChooseTheEncoding(byte[] bom, string encoding)
    {
        var text = "<title>café €</title>";
        var body = encoding switch
        {
            "utf-16le" => Encoding.Unicode.GetBytes(text),
            "utf-16be" => Encoding.BigEndianUnicode.GetBytes(text),
            _ => Encoding.UTF8.GetBytes(text),
        };

        var document = Document.Parse(new MemoryStream([.. bom, .. body]));

        Assert.Equal("café €", document.Title);
    }

    [Fact]
    public void MetaCharsetSelectsWindows1252()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("<meta charset=\"iso-8859-1\"><title>"), 0x80, 0xE9, .. Encoding.ASCII.GetBytes("</title>")];

        var document = Document.Parse(new MemoryStream(bytes));

        Assert.Equal("€é", document.Title);
    }

    [Fact]
    public void MetaHttpEquivContentTypeIsRead()
    {
        byte[] bytes = [.. Encoding.ASCII.GetBytes("<!-- <meta charset=utf-8> --><meta http-equiv=Content-Type content='text/html; charset=windows-1252'><title>"), 0x93, .. Encoding.ASCII.GetBytes("</title>")];

        var document = Document.Parse(new MemoryStream(bytes));

        Assert.Equal("“", document.Title);
    }

    [Fact]
    public void WithoutMarkersBytesAreUtf8()
    {
        var document = Document.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<title>日本</title>")));

        Assert.Equal("日本", document.Title);
    }

    [Fact]
    public void UnsupportedEncodingsAreReadAsUtf8WithADiagnostic()
    {
        var document = Document.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<meta charset=shift_jis><title>ok</title>")));

        Assert.Equal("ok", document.Title);
        Assert.Contains(document.Diagnostics, d => d.Code == DiagnosticCode.UnsupportedEncoding);
    }

    [Fact]
    public void OversizedStreamsAreCut()
    {
        var options = new FolioOptions { Limits = ResourceLimits.Default with { MaxInputSize = 20 } };

        var document = Document.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<title>abc</title>" + new string('x', 1000))), options);

        Assert.Contains(document.Diagnostics, d => d.Code == DiagnosticCode.LimitExceeded);
    }

    private static readonly FolioOptions BoxFont = new()
    {
        BaseUri = new Uri("https://example.invalid/docs/"),
        Fonts = new Folio.Typography.FontSettings
        {
            Source = new Folio.Typography.FontFolderSource(Path.Combine(AppContext.BaseDirectory, "fonts")),
            GenericFamilies = new Dictionary<string, IReadOnlyList<string>> { ["serif"] = ["Folio Box"] },
        },
    };

    [Fact]
    public void FindsTheElementAndLinkUnderAPoint()
    {
        using var document = Document.Parse("<!DOCTYPE html><body style='margin: 0'><p style='margin: 0'>ab<a href='page.html'><b>cd</b></a></p>" +
            "<div id=box style='height: 20px'></div><div style='position: absolute; top: 20px; left: 0; width: 10px; height: 10px'></div>", BoxFont);
        Assert.Null(document.ElementAt(5, 5)); // not painted yet

        document.Paint(800, 600);

        Assert.Equal("p", document.ElementAt(5, 5)?.LocalName);           // over "a" (plain text in p)
        Assert.Equal("b", document.ElementAt(40, 5)?.LocalName);          // over "c", inside b inside a
        Assert.Equal("https://example.invalid/docs/page.html", document.LinkAt(40, 5)?.AbsoluteUri);
        Assert.Null(document.LinkAt(5, 5));
        Assert.Equal("div", document.ElementAt(5, 25)?.LocalName);        // the positioned box, painted on top
        Assert.Equal("box", document.ElementAt(50, 25)?.GetAttribute("id"));
        Assert.Null(document.ElementAt(50, 100)); // below the page
    }

    [Fact]
    public void LoadsImagesFromDataUrlsOnlyAndReportsTheRest()
    {
        using var document = Document.Parse("<!DOCTYPE html><img src='data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAEklEQVR4nGP4z8DwH4SRIKoAAAslD/HAvA0nAAAAAElFTkSuQmCC'><img src='photo.png'>" +
            "<img src='file:///C:/Windows/win.ini'><img src='data:text/plain,hello'><img src='photo.png'>", BoxFont);

        document.Paint(800, 600);
        document.Paint(800, 600);

        // The relative URL resolves against the https base and is refused; no loader but data: URLs exists by default.
        var messages = document.Diagnostics.Where(d => d.Code == DiagnosticCode.ResourceNotLoaded).Select(d => d.Message).ToList();
        Assert.Equal(3, messages.Count); // once per URL, however often the document is painted
        Assert.Contains(messages, m => m.Contains("https://example.invalid/docs/photo.png") && m.Contains("https:"));
        Assert.Contains(messages, m => m.Contains("file:"));
        Assert.Contains(messages, m => m.Contains("not a PNG, JPEG or SVG"));
    }

    [Fact]
    public void LoadsCssImagesThroughTheSameLoaderAndReportsWhatUsedThem()
    {
        using var document = Document.Parse("<!DOCTYPE html><div style='height: 10px; background: url(data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAEklEQVR4nGP4z8DwH4SRIKoAAAslD/HAvA0nAAAAAElFTkSuQmCC)'></div>" +
            "<div style='height: 10px; background: url(bg.png)'></div><ul style='list-style-image: url(\"data:text/plain,x\")'><li>a</ul>", BoxFont);

        document.Paint(800, 600);
        document.Paint(800, 600);

        var reports = document.Diagnostics.Where(d => d.Code == DiagnosticCode.ResourceNotLoaded).ToList();
        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, d => d.Feature == "background-image" && d.Message.Contains("https://example.invalid/docs/bg.png"));
        Assert.Contains(reports, d => d.Feature == "list-style-image" && d.Message.Contains("not a PNG, JPEG or SVG"));
    }

    [Fact]
    public void ExposesElementsReadOnly()
    {
        using var document = Document.Parse("<div id=main CLASS='a b'><p>One <b>two</b></p><p lang=en>Three</p></div><p id=main>late");

        var root = document.DocumentElement!;
        Assert.Equal(("html", "http://www.w3.org/1999/xhtml"), (root.LocalName, root.NamespaceUri));
        Assert.Null(root.Parent);
        Assert.Equal(["head", "body"], root.Children.Select(e => e.LocalName));

        var main = document.GetElementById("main")!;
        Assert.Equal("div", main.LocalName);                       // the first in tree order
        Assert.Equal("main", main.Id);
        Assert.Equal("a b", main.GetAttribute("Class"));           // HTML attribute names ignore case
        Assert.Null(main.GetAttribute("title"));
        Assert.Equal([new("id", "main"), new("class", "a b")], main.Attributes);
        Assert.Equal("One twoThree", main.TextContent);
        Assert.Same(main, document.QuerySelector("#main"));        // one wrapper per element
        Assert.Same(main, main.Children[0].Parent);
        Assert.Null(document.GetElementById(""));
    }

    [Fact]
    public void QueriesSelectorsInTreeOrder()
    {
        using var document = Document.Parse("<div><p>1</p><section><p>2</p></section></div><p>3</p>");

        Assert.Equal(["1", "2", "3"], document.QuerySelectorAll("p").Select(e => e.TextContent));
        var div = document.QuerySelector("div")!;
        Assert.Equal(["1", "2"], div.QuerySelectorAll("p").Select(e => e.TextContent));
        Assert.Equal("2", div.QuerySelector("section > p")!.TextContent);
        Assert.Null(div.QuerySelector("div"));                     // descendants only
        Assert.Empty(document.QuerySelectorAll("table"));
        Assert.Throws<ArgumentException>(() => document.QuerySelector("p["));
    }
}

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
}

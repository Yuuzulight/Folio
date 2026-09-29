using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Style;

namespace Folio.Tests.Style;

public class ImportTests
{
    private static readonly CssColor Red = new(1, 0, 0, 1);
    private static readonly CssColor Blue = new(0, 0, 1, 1);
    private static readonly CssColor Black = new(0, 0, 0, 1);

    [Theory]
    [InlineData("@import url(\"data:text/css,p{color:red}\");")]
    [InlineData("@import url(data:text/css,p{color:red});")]
    [InlineData("@import \"data:text/css,p{color:red}\";")]
    [InlineData("@charset \"utf-8\"; @layer a; @import \"data:text/css,p{color:red}\";")]
    [InlineData("@import \"data:text/css,p{color:red}\" screen and (min-width: 100px);")]
    [InlineData("@import \"data:text/css,p{color:red}\" supports(display: grid);")]
    [InlineData("@import \"data:text/css,p{color:red}\" supports((display: grid) and (not (foo: bar)));")]
    public void ImportsApply(string css) => Assert.Equal(Red, Color($"<style>{css}</style><p id=a>x"));

    [Theory]
    [InlineData("p{} @import \"data:text/css,p{color:red!important}\";")] // not at the top
    [InlineData("@media screen {} @import \"data:text/css,p{color:red!important}\";")]
    [InlineData("@import \"data:text/css,p{color:red}\" print;")]
    [InlineData("@import \"data:text/css,p{color:red}\" (min-width: 2000px);")]
    [InlineData("@import \"data:text/css,p{color:red}\" supports(foo: bar);")]
    [InlineData("@import \"data:text/css,p{color:red}\" layer(a, b);")]
    [InlineData("@import \"data:text/plain,p{color:red}\";")] // not text/css
    [InlineData("@import \"http://example.invalid/a.css\";")] // no network
    [InlineData("@import \"a.css\";")] // no base URL
    public void ImportsAreIgnored(string css) => Assert.Equal(Black, Color($"<!DOCTYPE html><style>{css}</style><p id=a>x"));

    [Fact]
    public void ImportedRulesPrecedeTheImportingSheet() =>
        Assert.Equal(Blue, Color("<style>@import \"data:text/css,p{color:red}\"; p{color:blue}</style><p id=a>x"));

    [Fact]
    public void QuirksModeIgnoresTheContentType() =>
        Assert.Equal(Red, Color("<style>@import \"data:text/plain,p{color:red}\";</style><p id=a>x"));

    [Fact]
    public void ImportLayersRankBelowUnlayeredRules()
    {
        // Without the layer the imported rule would win on specificity.
        Assert.Equal(Blue, Color("<style>@import \"data:text/css,.a{color:red}\" layer(base); p{color:blue}</style><p id=a class=a>x"));
        Assert.Equal(Blue, Color("<style>@import \"data:text/css,.a{color:red}\" layer; p{color:blue}</style><p id=a class=a>x"));
        // Important declarations reverse: the layered one wins.
        Assert.Equal(Red, Color("<style>@import \"data:text/css,p{color:red!important}\" layer(base); p{color:blue!important}</style><p id=a>x"));
    }

    [Fact]
    public void LinkElementsLoadStylesheets()
    {
        Assert.Equal(Red, Color("<link rel=stylesheet href=\"data:text/css,p{color:red}\"><p id=a>x"));
        Assert.Equal(Red, Color("<link rel=\"STYLESHEET preload\" href=\"data:text/css,p{color:red}\"><p id=a>x"));
        Assert.Equal(Black, Color("<link rel=\"alternate stylesheet\" href=\"data:text/css,p{color:red}\"><p id=a>x"));
        Assert.Equal(Black, Color("<link rel=stylesheet disabled href=\"data:text/css,p{color:red}\"><p id=a>x"));
        Assert.Equal(Black, Color("<link rel=stylesheet media=print href=\"data:text/css,p{color:red}\"><p id=a>x"));
        Assert.Equal(Black, Color("<link rel=stylesheet type=text/plain href=\"data:text/css,p{color:red}\"><p id=a>x"));
        Assert.Equal(Black, Color("<link rel=icon href=\"data:text/css,p{color:red}\"><p id=a>x"));
    }

    [Fact]
    public void LinksAndStyleElementsApplyInTreeOrder()
    {
        Assert.Equal(Blue, Color("<link rel=stylesheet href=\"data:text/css,p{color:red}\"><style>p{color:blue}</style><p id=a>x"));
        Assert.Equal(Red, Color("<style>p{color:blue}</style><link rel=stylesheet href=\"data:text/css,p{color:red}\"><p id=a>x"));
    }

    [Fact]
    public void ImportsNestAtMostFourDeep()
    {
        // Level k colours #p{k} and imports level k + 1, each level a data: URL inside the one before.
        var url = "";
        for (var k = 6; k >= 1; k--)
            url = "data:text/css," + Uri.EscapeDataString($"@import \"{url}\"; #p{k} {{ color: red }}");
        var html = $"<!DOCTYPE html><style>@import \"{url}\";</style>" + string.Concat(Enumerable.Range(1, 6).Select(k => $"<p id=p{k}>x"));
        var reports = new List<string>();
        var document = TreeBuilder.Parse(html);
        StyleResolver.Resolve(document, new MediaContext(800, 600), sources: new StyleSources(ResourceLoader.DataUrlsOnly, null, Report: reports.Add));

        var colors = Enumerable.Range(1, 6).Select(k => Find(document, $"p{k}").ComputedStyle()!.Inherited.Color).ToArray();
        Assert.Equal([Red, Red, Red, Red, Black, Black], colors);
        Assert.Contains(reports, r => r.Contains("nested deeper than 4", StringComparison.Ordinal));
    }

    [Fact]
    public void RefusalsAreReported()
    {
        var reports = new List<string>();
        var document = TreeBuilder.Parse("<!DOCTYPE html><base href=\"file:///site/\"><link rel=stylesheet href=a.css><style>@import \"https://example.invalid/b.css\";</style>");
        StyleResolver.Resolve(document, new MediaContext(800, 600), sources: new StyleSources(ResourceLoader.DataUrlsOnly, null, Report: reports.Add));

        Assert.Equal(2, reports.Count);
        Assert.Contains("file:///site/a.css", reports[0], StringComparison.Ordinal);
        Assert.Contains("https: URLs is not allowed", reports[1], StringComparison.Ordinal);
    }

    private static CssColor Color(string html) => Resolve(html, null).Inherited.Color;

    private static ComputedStyle Resolve(string html, StyleSources? sources)
    {
        var document = TreeBuilder.Parse(html);
        StyleResolver.Resolve(document, new MediaContext(800, 600), sources: sources);
        return Find(document, "a").ComputedStyle()!;
    }

    private static Element Find(DocumentNode document, string id)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is Element e && e.GetAttribute("id") == id)
                return e;
        }
        throw new InvalidOperationException($"No element #{id}.");
    }
}

using Folio.Css;
using Folio.Dom;
using Folio.Html;
using Folio.Resources;
using Folio.Style;

namespace Folio.Tests.Style;

public sealed class LocalStyleSheetTests : IDisposable
{
    private static readonly CssColor Red = new(1, 0, 0, 1);
    private static readonly CssColor Black = new(0, 0, 0, 1);

    private readonly string _folder = Directory.CreateTempSubdirectory("folio-sheets-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void LocalFilesLoadFromAllowedFoldersOnly()
    {
        Write("site/main.css", "@import \"parts/b.css\"; p { margin-top: 3px }");
        Write("site/parts/b.css", "p { color: red }");
        Write("outside.css", "p { color: red }");
        var site = Path.Combine(_folder, "site");
        var html = "<!DOCTYPE html><link rel=stylesheet href=main.css><p id=a>x";

        var style = Resolve(html, Sources(site));
        Assert.Equal(Red, style.Inherited.Color);
        Assert.Equal(3, style.Spacing.MarginTop.Length.Px);

        // Relative references cannot climb out of the allowed folder.
        Assert.Equal(Black, Resolve("<!DOCTYPE html><link rel=stylesheet href=../outside.css><p id=a>x", Sources(site)).Inherited.Color);

        // The default loader reads no files at all.
        var reports = new List<string>();
        var none = new StyleSources(ResourceLoader.DataUrlsOnly, new Uri(Path.Combine(site, "index.html")).AbsoluteUri, Report: reports.Add);
        Assert.Equal(Black, Resolve(html, none).Inherited.Color);
        Assert.Contains(reports, r => r.Contains("file: URLs is not allowed", StringComparison.Ordinal));
    }

    [Fact]
    public void BaseElementSetsTheBaseUrl()
    {
        Write("site/css/main.css", "p { color: red }");
        var html = "<!DOCTYPE html><base href=css/><link rel=stylesheet href=main.css><p id=a>x";
        Assert.Equal(Red, Resolve(html, Sources(Path.Combine(_folder, "site"))).Inherited.Color);
    }

    [Fact]
    public void ImportCyclesTerminate()
    {
        Write("a.css", "@import \"b.css\"; p { color: red }");
        Write("b.css", "@import \"a.css\"; p { color: blue }");
        Assert.Equal(Red, Resolve("<!DOCTYPE html><link rel=stylesheet href=a.css><p id=a>x", Sources(_folder)).Inherited.Color);
    }

    [Fact]
    public void LinkedSheetsGetTheSameImportDepthAsInlineOnes()
    {
        for (var k = 1; k <= 6; k++)
            Write($"{k}.css", $"@import \"{k + 1}.css\"; #p{k} {{ color: red }}");
        var body = string.Concat(Enumerable.Range(1, 6).Select(k => $"<p id=p{k}>x"));
        var document = TreeBuilder.Parse("<!DOCTYPE html><link rel=stylesheet href=1.css>" + body);

        StyleResolver.Resolve(document, new MediaContext(800, 600), sources: Sources(_folder));

        // 1.css is the linked sheet; it may import four levels (2 to 5) below it.
        var colors = Enumerable.Range(1, 6).Select(k => Find(document, $"p{k}").ComputedStyle()!.Inherited.Color).ToArray();
        Assert.Equal([Red, Red, Red, Red, Red, Black], colors);
    }

    private static StyleSources Sources(string folder) =>
        new(new ResourceLoader([folder]), new Uri(Path.Combine(folder, "index.html")).AbsoluteUri);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static ComputedStyle Resolve(string html, StyleSources sources)
    {
        var document = TreeBuilder.Parse(html);
        StyleResolver.Resolve(document, new MediaContext(800, 600), sources: sources);
        return Find(document, "a").ComputedStyle()!;
    }

    private static ElementNode Find(DocumentNode document, string id)
    {
        for (Node? node = document; node is not null; node = node.NextInTree(document))
        {
            if (node is ElementNode e && e.GetAttribute("id") == id)
                return e;
        }
        throw new InvalidOperationException($"No element #{id}.");
    }
}

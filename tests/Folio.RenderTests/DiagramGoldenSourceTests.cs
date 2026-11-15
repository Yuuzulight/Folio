using Folio.Diagrams;

namespace Folio.RenderTests;

/// <summary>
/// The diagram goldens embed the SVG that Folio.Diagrams produces for the source kept in their source script, so the
/// image is a picture of the real renderer. Set FOLIO_REGEN_DIAGRAMS=1 and run this class to rewrite the embedded SVG,
/// then review and approve the changed images.
/// </summary>
public class DiagramGoldenSourceTests
{
    private static readonly string Folder = Path.Combine(RepoPaths.Tests, "goldens", "diagrams");

    public static TheoryData<string> Pages =>
        Directory.Exists(Folder) && Directory.GetFiles(Folder, "*.html") is { Length: > 0 } files
            ? new(files.Select(Path.GetFileName).Order()!) : new("(none)");

    [Theory]
    [MemberData(nameof(Pages))]
    public void EmbeddedSvgMatchesTheRenderer(string file)
    {
        Assert.SkipWhen(file == "(none)", "No diagram goldens.");
        var path = Path.Combine(Folder, file);
        var html = File.ReadAllText(path).Replace("\r\n", "\n");
        const string sourceStart = "<script type=\"text/plain\" id=\"source\">\n", svgStart = "<!--svg-->", svgEnd = "<!--/svg-->";
        var sourceFrom = html.IndexOf(sourceStart, StringComparison.Ordinal) + sourceStart.Length;
        var source = html[sourceFrom..html.IndexOf("\n</script>", sourceFrom, StringComparison.Ordinal)];
        var svg = DiagramRenderer.Render(source);
        var from = html.IndexOf(svgStart, StringComparison.Ordinal) + svgStart.Length;
        var to = html.IndexOf(svgEnd, StringComparison.Ordinal);
        if (Environment.GetEnvironmentVariable("FOLIO_REGEN_DIAGRAMS") == "1")
            File.WriteAllText(path, html[..from] + svg + html[to..]);
        else
            Assert.Equal(svg, html[from..to]);
    }
}

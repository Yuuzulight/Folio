using Folio.Css;
using Folio.Html;
using Folio.Imaging;
using Folio.Layout;
using Folio.Painting;
using Folio.Resources;
using Folio.Style;
using Folio.Tests.Layout;

namespace Folio.Tests.Svg;

public class SvgFilterTests
{
    [Fact]
    public void BoxesOfOneSizeShareTheirFiltersPrimitives()
    {
        // 500 boxes use one filter of 200 primitives: built once, not once per box.
        var primitives = string.Concat(Enumerable.Repeat("<feOffset dx=\"1\" />", 200));
        var html = "<!DOCTYPE html><style>div { width: 50px; height: 10px; background: red; filter: url(#f) }</style>" +
            $"<svg width=\"0\" height=\"0\" style=\"position: absolute\"><filter id=\"f\">{primitives}</filter></svg>" +
            string.Concat(Enumerable.Repeat("<div></div>", 500)) + "<div style=\"width: 60px\"></div>";

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var filters = list.Items.Where(i => i.Kind == DisplayItemKind.PushLayer && i.Filters is { Count: 200 }).Select(i => i.Filters!).ToList();
        Assert.Equal(501, filters.Count);
        // One list for the 500 boxes of one size, another for the wider one.
        Assert.Equal(2, filters.Distinct(ReferenceEqualityComparer.Instance).Count());
    }

    [Fact]
    public void SvgElementsShareAFilterInUserSpace()
    {
        var html = "<!DOCTYPE html><svg width=\"200\" height=\"100\"><filter id=\"f\" filterUnits=\"userSpaceOnUse\"><feGaussianBlur stdDeviation=\"2\" /></filter>" +
            string.Concat(Enumerable.Range(0, 20).Select(i => $"<rect x=\"{i * 5}\" width=\"4\" height=\"4\" filter=\"url(#f)\" />")) + "</svg>";

        var list = DisplayListBuilder.Build(BlockLayoutTests.LayOut(html));

        var filters = list.Items.Where(i => i.Kind == DisplayItemKind.PushLayer && i.Filters is not null).Select(i => i.Filters!).ToList();
        Assert.Equal(20, filters.Count);
        Assert.Single(filters.Distinct(ReferenceEqualityComparer.Instance));
    }

    [Fact]
    public void AnSvgImageLoadsTheImagesOfItsFeImagesFromDataUrlsOnly()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAQAAAACCAYAAAB/qH1jAAAAEklEQVR4nGP4z8DwH4SRIKoAAAslD/HAvA0nAAAAAElFTkSuQmCC";
        var folder = Directory.CreateTempSubdirectory("folio-feimage").FullName;
        try
        {
            string Url(string name) => new Uri(Path.Combine(folder, name)).AbsoluteUri;
            static string Svg(string href) => "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"10\"><filter id=\"f\">"
                + $"<feImage href=\"{href}\" /></filter><rect width=\"20\" height=\"10\" filter=\"url(#f)\" /></svg>";
            File.WriteAllBytes(Path.Combine(folder, "outside.png"), Convert.FromBase64String(png));
            File.WriteAllText(Path.Combine(folder, "outside.svg"), Svg(Url("outside.png")));
            File.WriteAllText(Path.Combine(folder, "inside.svg"), Svg("data:image/png;base64," + png));
            // The loader may read the folder: the page's own feImage loads from it, an SVG image's does not.
            var document = TreeBuilder.Parse("<!DOCTYPE html>" + Svg(Url("outside.png")) + $"<img src=\"{Url("outside.svg")}\"><img src=\"{Url("inside.svg")}\">");
            StyleResolver.Resolve(document, new MediaContext(800, 600));
            var images = new ImageLoader(new ResourceLoader([folder]), null);
            var page = LayoutEngine.LayoutDocument(BoxTreeBuilder.Build(document, images)!, 800, 600, BlockLayoutTests.BoxFont.Value, images: images);

            var kinds = DisplayListBuilder.Build(page, images).Items.Where(i => i.Filters is { Count: 1 }).Select(i => i.Filters![0].Kind).ToList();

            Assert.Equal(new[] { FilterKind.Image, FilterKind.Flood, FilterKind.Image }, kinds);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

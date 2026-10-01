using Folio.Dom;
using Folio.Imaging;
using Folio.Resources;
using Folio.Style;

namespace Folio.Tests.Imaging;

public class ImageLoaderTests
{
    [Fact]
    public void AnSvgImageIsStyledByItsOwnSheetsAndLoadsNothingFromOutside()
    {
        var folder = Directory.CreateTempSubdirectory("folio-svg-image").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "outside.css"), "svg { background-color: rgb(1, 2, 3) }");
            var outside = new Uri(Path.Combine(folder, "outside.css")).AbsoluteUri;
            var svg = "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='10'>"
                + $"<style>@import url('{outside}'); svg {{ color: rgb(4, 5, 6) }}</style></svg>";
            // The loader may read the folder, so the image itself loads, but nothing it refers to does.
            var loader = new ImageLoader(new ResourceLoader([folder]), null);
            File.WriteAllText(Path.Combine(folder, "image.svg"), svg);

            var root = Assert.IsType<ElementNode>(loader.LoadAny(new Uri(Path.Combine(folder, "image.svg")).AbsoluteUri));

            var style = root.ComputedStyle()!;
            Assert.Equal("rgb(4, 5, 6)", style.Inherited.Color.ToString());
            Assert.Equal(0, style.Background.Color.A);
            Assert.Null(loader.Load(new Uri(Path.Combine(folder, "image.svg")).AbsoluteUri, "background-image"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

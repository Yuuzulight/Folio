using Folio.Dom;
using Folio.Imaging;
using Folio.Resources;
using Folio.Style;
using System.Buffers.Binary;

namespace Folio.Tests.Imaging;

public class ImageLoaderTests
{
    [Fact]
    public void DataGifUsesFolioDecoder()
    {
        const string gif = "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";
        var loader = new ImageLoader(ResourceLoader.DataUrlsOnly, null);

        var image = loader.Load($"data:image/gif;base64,{gif}");

        Assert.NotNull(image);
        Assert.Equal((1, 1), (image.Width, image.Height));
        Assert.Equal([255, 255, 255, 255], image.Pixels);
    }

    [Fact]
    public void DataBmpAndIcoUseFolioDecodersThroughImageLoader()
    {
        var loader = new ImageLoader(ResourceLoader.DataUrlsOnly, null);
        var bmp = new byte[58];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bmp.AsSpan(28), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(34), 4);
        bmp[54] = 0; bmp[55] = 0x7C;
        var bmpImage = loader.Load($"data:image/bmp;base64,{Convert.ToBase64String(bmp)}");
        Assert.NotNull(bmpImage);
        Assert.Equal([255, 0, 0, 255], bmpImage.Pixels);

        var ico = new byte[70];
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(4), 1);
        ico[6] = ico[7] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(10), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(14), 48);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(18), 22);
        BinaryPrimitives.WriteUInt32LittleEndian(ico.AsSpan(22), 40);
        BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(26), 1);
        BinaryPrimitives.WriteInt32LittleEndian(ico.AsSpan(30), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(34), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(36), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(ico.AsSpan(62), 0x7C00);
        var icoImage = loader.Load($"data:image/x-icon;base64,{Convert.ToBase64String(ico)}");
        Assert.NotNull(icoImage);
        Assert.Equal([255, 0, 0, 255], icoImage.Pixels);
    }

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

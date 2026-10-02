using System.Text;
using Folio.Dom;
using Folio.Resources;

namespace Folio.Imaging;

/// <summary>
/// Loads the images a document asks for through its resource loader and decodes them (PNG, JPEG, and SVG for img
/// elements), once per URL. With the default loader only <c>data:</c> URLs load (docs/study/16-resources-and-security.md).
/// </summary>
/// <param name="report">
/// Told about every image that was not loaded or not decoded, and why, with the feature that asked for it first
/// (once per URL).
/// </param>
internal sealed class ImageLoader(ResourceLoader loader, string? baseUrl, Action<string, string>? report = null)
{
    // Each URL's image: a DecodedImage, the root of an SVG document, or null when it cannot be used.
    private readonly Dictionary<string, object?> _images = new(StringComparer.Ordinal);

    /// <summary>The decoded image a reference (resolved against the base URL) points to; null when it cannot be used.</summary>
    /// <param name="feature">What uses the image, for the report: img, background-image, list-style-image.</param>
    public DecodedImage? Load(string reference, string feature = "img") => LoadAny(reference, feature, svg: false) as DecodedImage;

    /// <summary>
    /// What an img element shows: a <see cref="DecodedImage"/>, or the styled root <c>svg</c> element of an SVG
    /// document; null when it cannot be used.
    /// </summary>
    // ponytail: SVG images in backgrounds, list markers and border images wait for a display list that can be tiled.
    public object? LoadAny(string reference, string feature = "img", bool svg = true)
    {
        if (ResourceLoader.Resolve(baseUrl, reference) is not { } url)
        {
            report?.Invoke($"Image \"{Shorten(reference)}\" has no base URL to resolve against.", feature);
            return null;
        }
        if (_images.TryGetValue(url, out var known))
            return known;

        object? image = null;
        var response = loader.Load(new ResourceRequest(url, ResourceKind.Image));
        if (!response.Succeeded)
            report?.Invoke($"Image {Shorten(url)} was not loaded: {response.Error}", feature);
        else if ((image = (object?)Decode(response.Data!) ?? SvgDocument(response.Data!)) is null)
            report?.Invoke($"Image {Shorten(url)} is not a PNG, JPEG or SVG image Folio can decode.", feature);
        else if (image is ElementNode && !svg)
            report?.Invoke($"Image {Shorten(url)} is an SVG image, which Folio draws only in img elements so far.", feature);
        _images[url] = image;
        return image;
    }

    public static DecodedImage? Decode(ReadOnlySpan<byte> data) =>
        PngDecoder.CanDecode(data) ? PngDecoder.Decode(data)
        : JpegDecoder.CanDecode(data) ? JpegDecoder.Decode(data)
        : null;

    /// <summary>
    /// The root svg element of an SVG image, parsed and styled as a document of its own. Like any SVG used as an image,
    /// it loads nothing from outside (data: URLs only) and runs no script
    /// (https://www.w3.org/TR/SVG2/conform.html#secure-static-mode).
    /// </summary>
    private static ElementNode? SvgDocument(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        if (!Xml.XmlParser.IsSvgDocument(text))
            return null;
        var document = Xml.XmlParser.Parse(text);
        document.IsImage = true;
        if (document.DocumentElement is not { LocalName: "svg" } root)
            return null;
        // ponytail: media queries inside the image see a 300x150 viewport, not the size it is drawn at.
        Style.StyleResolver.Resolve(document, new Css.MediaContext(300, 150));
        return root;
    }

    private static string Shorten(string url) => url.Length > 80 ? url[..77] + "..." : url;
}

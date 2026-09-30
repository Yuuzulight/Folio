using Folio.Resources;

namespace Folio.Imaging;

/// <summary>
/// Loads the images a document asks for through its resource loader and decodes them (PNG and JPEG), once per URL.
/// With the default loader only <c>data:</c> URLs load (docs/study/16-resources-and-security.md).
/// </summary>
/// <param name="report">
/// Told about every image that was not loaded or not decoded, and why, with the feature that asked for it first
/// (once per URL).
/// </param>
internal sealed class ImageLoader(ResourceLoader loader, string? baseUrl, Action<string, string>? report = null)
{
    private readonly Dictionary<string, DecodedImage?> _images = new(StringComparer.Ordinal);

    /// <summary>The decoded image a reference (resolved against the base URL) points to; null when it cannot be used.</summary>
    /// <param name="feature">What uses the image, for the report: img, background-image, list-style-image.</param>
    public DecodedImage? Load(string reference, string feature = "img")
    {
        if (ResourceLoader.Resolve(baseUrl, reference) is not { } url)
        {
            report?.Invoke($"Image \"{Shorten(reference)}\" has no base URL to resolve against.", feature);
            return null;
        }
        if (_images.TryGetValue(url, out var known))
            return known;

        DecodedImage? image = null;
        var response = loader.Load(new ResourceRequest(url, ResourceKind.Image));
        if (!response.Succeeded)
            report?.Invoke($"Image {Shorten(url)} was not loaded: {response.Error}", feature);
        else if ((image = Decode(response.Data!)) is null)
            report?.Invoke($"Image {Shorten(url)} is not a PNG or JPEG image Folio can decode.", feature);
        _images[url] = image;
        return image;
    }

    public static DecodedImage? Decode(ReadOnlySpan<byte> data) =>
        PngDecoder.CanDecode(data) ? PngDecoder.Decode(data)
        : JpegDecoder.CanDecode(data) ? JpegDecoder.Decode(data)
        : null;

    private static string Shorten(string url) => url.Length > 80 ? url[..77] + "..." : url;
}

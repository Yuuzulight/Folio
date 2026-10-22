using System.Buffers.Binary;
using System.Numerics;
using Folio.Imaging;
using Folio.Painting;
using Folio.Typography;
using Xunit;

namespace Folio.Tests;

public class AllFolioTests
{
    private static readonly FontFace Box = FontFace.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fonts", "FolioBox.ttf")))!;

    [Fact]
    public void GifDecoder_CanDecode_RecognizesGifHeaders()
    {
        byte[] gif87 = "GIF87a\x0A\0\x0A\0\x80\0\0"u8.ToArray();
        byte[] gif89 = "GIF89a\x0A\0\x0A\0\x80\0\0"u8.ToArray();
        byte[] notGif = "PNG\r\n\x1A\n"u8.ToArray();

        Assert.True(GifDecoder.CanDecode(gif87));
        Assert.True(GifDecoder.CanDecode(gif89));
        Assert.False(GifDecoder.CanDecode(notGif));
    }

    [Fact]
    public void GifDecoder_DecodesMinimal1x1Gif()
    {
        // Minimal valid 1x1 GIF89a with 2-color global palette and 1 pixel
        byte[] gifData = [
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, // GIF89a
            0x01, 0x00, 0x01, 0x00,             // 1x1
            0x80, 0x00, 0x00,                   // GCT flag, 2 colors
            0xFF, 0x00, 0x00,                   // Red (index 0)
            0x00, 0xFF, 0x00,                   // Green (index 1)
            0x2C,                               // Image separator
            0x00, 0x00, 0x00, 0x00,             // Left, Top
            0x01, 0x00, 0x01, 0x00,             // 1x1
            0x00,                               // No LCT
            0x02,                               // LZW min code size = 2
            0x02, 0x44, 0x01,                   // Sub-block: 2 bytes (clear code 4, index 0, end code 5)
            0x00,                               // Block terminator
            0x3B                                // Trailer
        ];

        var decoded = GifDecoder.Decode(gifData);
        Assert.NotNull(decoded);
        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Equal(4, decoded.Pixels.Length);
        // Red color from palette index 0
        Assert.Equal(255, decoded.Pixels[0]); // R
        Assert.Equal(0, decoded.Pixels[1]);   // G
        Assert.Equal(0, decoded.Pixels[2]);   // B
        Assert.Equal(255, decoded.Pixels[3]); // A
    }

    [Fact]
    public void GifDecoder_RejectsTruncatedData()
    {
        byte[] truncated = "GIF89a\x01\0\x01\0"u8.ToArray();
        var decoded = GifDecoder.Decode(truncated);
        Assert.Null(decoded);
    }

    [Fact]
    public void WebpDecoder_CanDecode_RecognizesRiffWebp()
    {
        byte[] validHeader = [
            0x52, 0x49, 0x46, 0x46, // RIFF
            0x20, 0x00, 0x00, 0x00, // Size
            0x57, 0x45, 0x42, 0x50  // WEBP
        ];
        byte[] invalidHeader = [0x52, 0x49, 0x46, 0x46, 0x20, 0x00, 0x00, 0x00, 0x41, 0x56, 0x49, 0x20];

        Assert.True(WebpDecoder.CanDecode(validHeader));
        Assert.False(WebpDecoder.CanDecode(invalidHeader));
    }

    [Fact]
    public void WebpDecoder_DecodesVp8LossyHeader()
    {
        // Minimal VP8 RIFF chunk with 10x10 dimensions
        byte[] webpData = [
            0x52, 0x49, 0x46, 0x46,             // RIFF
            0x1E, 0x00, 0x00, 0x00,             // Size = 30
            0x57, 0x45, 0x42, 0x50,             // WEBP
            0x56, 0x50, 0x38, 0x20,             // "VP8 "
            0x12, 0x00, 0x00, 0x00,             // Chunk size = 18
            0x00, 0x00, 0x00,                   // Frame tag (keyframe)
            0x9D, 0x01, 0x2A,                   // Start code
            0x0A, 0x00,                         // Width = 10
            0x0A, 0x00,                         // Height = 10
            0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 // Data
        ];

        var decoded = WebpDecoder.Decode(webpData);
        Assert.NotNull(decoded);
        Assert.Equal(10, decoded.Width);
        Assert.Equal(10, decoded.Height);
        Assert.Equal(10 * 10 * 4, decoded.Pixels.Length);
    }

    [Fact]
    public void WebpDecoder_RejectsTruncatedData()
    {
        byte[] truncated = [0x52, 0x49, 0x46, 0x46, 0x10, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50];
        var decoded = WebpDecoder.Decode(truncated);
        Assert.Null(decoded);
    }

    [Fact]
    public void ComplexShaper_ShapesArabicJoining()
    {
        // Arabic text: "سلام" (Seen, Lam, Alef, Meem)
        var text = "\u0633\u0644\u0627\u0645";
        Assert.False(SimpleShaper.CanShape(text, Box));

        var shaped = ComplexShaper.Instance.Shape(text, 0, text.Length, Box, 16f, rightToLeft: true, language: "ar");
        Assert.NotNull(shaped);
        Assert.Equal(4, shaped.Glyphs.Length);
        Assert.Equal(4, shaped.Advances.Length);
    }

    [Fact]
    public void ComplexShaper_ShapesHebrewWithNiqqud()
    {
        // Hebrew letter Bet with Dagesh (0x05D1 + 0x05BC)
        var text = "\u05D1\u05BC";
        var shaped = ComplexShaper.Instance.Shape(text, 0, text.Length, Box, 16f, rightToLeft: true, language: "he");
        Assert.NotNull(shaped);
        Assert.Equal(2, shaped.Glyphs.Length);
        // The niqqud mark has zero advance
        Assert.Equal(0f, shaped.Advances[1]);
    }

    [Fact]
    public void SoftwareRasterBackend_CreatesSurfaceAndDrawsRects()
    {
        var backend = SoftwareRasterBackend.Instance;
        using var surface = backend.CreateSurface(100, 100);
        Assert.NotNull(surface);
        Assert.Equal(100, surface.Width);
        Assert.Equal(100, surface.Height);

        var canvas = surface.Canvas;
        var redPaint = new Paint(new Rgba(1f, 0f, 0f, 1f));
        canvas.FillRoundedRect(new RoundedRect(new RectF(10, 10, 20, 20), default), redPaint);

        var pixels = surface.ReadPixels(10, 10, 1, 1);
        Assert.Equal(255, pixels[0]); // R
        Assert.Equal(0, pixels[1]);   // G
        Assert.Equal(0, pixels[2]);   // B
        Assert.Equal(255, pixels[3]); // A

        var snapshot = surface.Snapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(100 * 100 * 4, snapshot.Pixels.Length);
    }

    [Fact]
    public void SoftwareRasterBackend_HandlesClippingAndClearing()
    {
        var backend = SoftwareRasterBackend.Instance;
        using var surface = backend.CreateSurface(50, 50);
        Assert.NotNull(surface);

        var canvas = surface.Canvas;
        var greenPaint = new Paint(new Rgba(0f, 1f, 0f, 1f));

        canvas.Save();
        canvas.ClipRoundedRect(new RoundedRect(new RectF(10, 10, 10, 10), default));
        canvas.FillRoundedRect(new RoundedRect(new RectF(0, 0, 50, 50), default), greenPaint);
        canvas.Restore();

        // Inside clip: green
        var inside = surface.ReadPixels(15, 15, 1, 1);
        Assert.Equal(255, inside[1]);

        // Outside clip: transparent
        var outside = surface.ReadPixels(5, 5, 1, 1);
        Assert.Equal(0, outside[1]);
        Assert.Equal(0, outside[3]);

        // ClearRect
        canvas.ClearRect(new RectF(10, 10, 10, 10));
        var cleared = surface.ReadPixels(15, 15, 1, 1);
        Assert.Equal(0, cleared[1]);
        Assert.Equal(0, cleared[3]);
    }
}

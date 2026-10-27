using System.Runtime.CompilerServices;
using Folio.Imaging;
using Folio.Typography;

namespace Folio.Painting.Software;

/// <summary>
/// Colour glyphs stored as PNG bitmaps in a font's <c>CBLC</c> and <c>CBDT</c> tables (the colour emoji fonts)
/// (https://learn.microsoft.com/typography/opentype/spec/cbdt). The strike with the largest size is used and scaled.
/// </summary>
internal static class BitmapGlyphs
{
    /// <summary>A glyph's bitmap and where it sits: the strike's size in pixels per em, and the bitmap's bearings and size in strike pixels.</summary>
    internal sealed record Bitmap(DecodedImage Image, float PixelsPerEm, float BearingX, float BearingY, float Advance);

    private sealed class Strike
    {
        public int PixelsPerEm;
        public readonly List<(int First, int Last, int Table)> Ranges = [];
    }

    private sealed class Index(FontData cblc, FontData cbdt, Strike strike)
    {
        public FontData Locations = cblc;
        public FontData Data = cbdt;
        public Strike Strike = strike;
    }

    private static readonly ConditionalWeakTable<FontFace, Index?> Indexes = new();

    public static bool Has(FontFace face) => face.RawTable("CBLC") is not null && face.RawTable("CBDT") is not null || face.RawTable("sbix") is not null;

    public static Bitmap? Get(FontFace face, ushort glyph)
    {
        try
        {
            var index = Indexes.GetValue(face, f => f.RawTable("CBLC") is { } cblc && f.RawTable("CBDT") is { } cbdt && ReadStrike(cblc) is { } strike ? new Index(cblc, cbdt, strike) : null);
            return index is not null ? Find(index, glyph) : FindSbix(face, glyph);
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static Bitmap? FindSbix(FontFace face, ushort glyph)
    {
        if (face.RawTable("sbix") is not { } sbix || sbix.Length < 8)
            return null;
        var strikes = sbix.U32(4);
        if (strikes == 0 || strikes > 256 || 8 + 4 * strikes > sbix.Length)
            return null;
        var bestPpem = 0;
        var bestOffset = 0;
        for (var i = 0; i < strikes; i++)
        {
            var offset = (int)sbix.U32(8 + 4 * i);
            if (offset < 0 || offset + 4 > sbix.Length)
                continue;
            var ppem = sbix.U16(offset);
            if (ppem > bestPpem) { bestPpem = ppem; bestOffset = offset; }
        }
        if (bestPpem == 0 || bestOffset + 4 + 4 * (face.GlyphCount + 1) > sbix.Length || glyph >= face.GlyphCount)
            return null;
        var start = (int)sbix.U32(bestOffset + 4 + 4 * glyph);
        var end = (int)sbix.U32(bestOffset + 4 + 4 * (glyph + 1));
        if (end <= start || bestOffset + start < 0 || bestOffset + end > sbix.Length)
            return null;
        var data = bestOffset + start;
        if (data + 8 > sbix.Length || sbix.Tag(data + 4) != "png ")
            return null;
        var imageBytes = sbix.Bytes(data + 8, end - start - 8);
        if (PngDecoder.Decode(imageBytes) is not { } image)
            return null;
        // sbix originOffsetY is measured upward from the baseline; BitmapGlyphs uses a top bearing.
        return new Bitmap(image, bestPpem, sbix.S16(data), -sbix.S16(data + 2) + image.Height, image.Width);
    }

    // The largest strike: its size and its index sub-tables.
    private static Strike? ReadStrike(FontData cblc)
    {
        var count = (int)cblc.U32(4);
        Strike? best = null;
        for (var i = 0; i < count; i++)
        {
            var record = 8 + 48 * i;
            var arrayOffset = (int)cblc.U32(record);
            var subTables = (int)cblc.U32(record + 8);
            var ppem = cblc.U8(record + 44);
            if (best is not null && ppem <= best.PixelsPerEm)
                continue;
            var strike = new Strike { PixelsPerEm = ppem };
            for (var k = 0; k < subTables; k++)
            {
                var entry = arrayOffset + 8 * k;
                strike.Ranges.Add((cblc.U16(entry), cblc.U16(entry + 2), arrayOffset + (int)cblc.U32(entry + 4)));
            }
            best = strike;
        }
        return best;
    }

    private static Bitmap? Find(Index index, ushort glyph)
    {
        foreach (var (first, last, table) in index.Strike.Ranges)
        {
            if (glyph < first || glyph > last)
                continue;
            var locations = index.Locations;
            var indexFormat = locations.U16(table);
            var imageFormat = locations.U16(table + 2);
            var imageData = (int)locations.U32(table + 4);
            int offset, length;
            (float BearingX, float BearingY, float Advance)? sharedMetrics = null;
            var slot = glyph - first;
            switch (indexFormat)
            {
                case 1:
                    offset = imageData + (int)locations.U32(table + 8 + 4 * slot);
                    length = (int)locations.U32(table + 8 + 4 * (slot + 1)) - (int)locations.U32(table + 8 + 4 * slot);
                    break;
                case 3:
                    offset = imageData + locations.U16(table + 8 + 2 * slot);
                    length = locations.U16(table + 8 + 2 * (slot + 1)) - locations.U16(table + 8 + 2 * slot);
                    break;
                case 2:
                {
                    var size = (int)locations.U32(table + 8);
                    offset = imageData + size * slot;
                    length = size;
                    sharedMetrics = ((sbyte)locations.U8(table + 14), (sbyte)locations.U8(table + 15), locations.U8(table + 16));
                    break;
                }
                default:
                    return null;
            }
            if (length <= 0)
                return null;
            return ReadImage(index, imageFormat, offset, length, sharedMetrics);
        }
        return null;
    }

    private static Bitmap? ReadImage(Index index, int format, int offset, int length, (float BearingX, float BearingY, float Advance)? shared)
    {
        var data = index.Data;
        float bearingX, bearingY, advance;
        int pngStart;
        switch (format)
        {
            case 17:
                // Small metrics: height, width, bearingX, bearingY, advance; then the PNG's length.
                bearingX = (sbyte)data.U8(offset + 2);
                bearingY = (sbyte)data.U8(offset + 3);
                advance = data.U8(offset + 4);
                pngStart = offset + 9;
                break;
            case 18:
                bearingX = (sbyte)data.U8(offset + 2);
                bearingY = (sbyte)data.U8(offset + 3);
                advance = data.U8(offset + 4);
                pngStart = offset + 12;
                break;
            case 19 when shared is { } metrics:
                (bearingX, bearingY, advance) = metrics;
                pngStart = offset + 4;
                break;
            default:
                return null;
        }
        var pngLength = (int)data.U32(pngStart - 4);
        if (pngLength <= 0 || pngStart + pngLength > data.Length)
            return null;
        if (PngDecoder.Decode(data.Bytes(pngStart, pngLength)) is not { } image)
            return null;
        return new Bitmap(image, index.Strike.PixelsPerEm, bearingX, bearingY, advance);
    }
}

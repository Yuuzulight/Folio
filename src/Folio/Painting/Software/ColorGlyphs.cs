using System.Runtime.CompilerServices;
using Folio.Typography;

namespace Folio.Painting.Software;

/// <summary>Layered colour glyphs from the OpenType COLR v0 and CPAL tables.</summary>
internal static class ColorGlyphs
{
    internal readonly record struct Layer(ushort Glyph, Rgba? Color);

    private sealed class Palette
    {
        public readonly Dictionary<ushort, Layer[]> Glyphs = [];
    }

    private static readonly ConditionalWeakTable<FontFace, Palette?> Palettes = new();

    public static bool Has(FontFace face)
    {
        try { return face.RawTable("COLR") is { } colr && face.RawTable("CPAL") is not null && colr.U16(0) == 0; }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException) { return false; }
    }

    public static Layer[]? Get(FontFace face, ushort glyph)
    {
        try
        {
            var palette = Palettes.GetValue(face, Read);
            return palette is not null && palette.Glyphs.TryGetValue(glyph, out var layers) ? layers : null;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static Palette? Read(FontFace face)
    {
        if (face.RawTable("COLR") is not { } colr || face.RawTable("CPAL") is not { } cpal || colr.U16(0) != 0)
            return null; // v1 is intentionally left for the paint-graph implementation.

        var records = cpal.U16(2);
        var palettes = cpal.U16(4);
        var colorOffset = (int)cpal.U32(8);
        if (records == 0 || palettes == 0)
            return null;
        var palette = new Rgba[records];
        for (var i = 0; i < records; i++)
        {
            var p = colorOffset + 4 * i;
            palette[i] = new Rgba(cpal.U8(p + 2) / 255f, cpal.U8(p + 1) / 255f, cpal.U8(p) / 255f, cpal.U8(p + 3) / 255f);
        }

        var count = colr.U16(2);
        var baseOffset = (int)colr.U32(4);
        var layerOffset = (int)colr.U32(8);
        var layerCount = colr.U16(12);
        var result = new Palette();
        for (var i = 0; i < count; i++)
        {
            var b = baseOffset + 6 * i;
            var glyph = colr.U16(b);
            var first = colr.U16(b + 2);
            var number = colr.U16(b + 4);
            if ((long)first + number > layerCount)
                throw new InvalidDataException("COLR layer range out of bounds.");
            var layers = new Layer[number];
            for (var j = 0; j < number; j++)
            {
                var l = layerOffset + 4 * (first + j);
                var layerGlyph = colr.U16(l);
                var paletteIndex = colr.U16(l + 2);
                if (paletteIndex != 0xFFFF && paletteIndex >= records)
                    throw new InvalidDataException("CPAL palette index out of bounds.");
                layers[j] = new Layer(layerGlyph, paletteIndex == 0xFFFF ? null : palette[paletteIndex]);
            }
            result.Glyphs[glyph] = layers;
        }
        return result;
    }
}

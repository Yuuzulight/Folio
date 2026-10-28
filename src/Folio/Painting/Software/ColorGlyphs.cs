using System.Runtime.CompilerServices;
using System.Numerics;
using Folio.Typography;

namespace Folio.Painting.Software;

/// <summary>Layered colour glyphs from the OpenType COLR v0/v1 and CPAL tables.</summary>
internal static class ColorGlyphs
{
    internal readonly record struct Layer(ushort Glyph, Rgba? Color, Matrix3x2 Transform = default)
    {
        public Matrix3x2 TransformOrIdentity => Transform == default ? Matrix3x2.Identity : Transform;
    }

    private sealed class Palette
    {
        public readonly Dictionary<ushort, Layer[]> Glyphs = [];
    }

    private static readonly ConditionalWeakTable<FontFace, Palette?> Palettes = new();

    public static bool Has(FontFace face)
    {
        try { return face.RawTable("COLR") is { } colr && face.RawTable("CPAL") is not null && colr.U16(0) is 0 or 1; }
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
        if (face.RawTable("COLR") is not { } colr || face.RawTable("CPAL") is not { } cpal)
            return null;

        var version = colr.U16(0);
        if (version is not (0 or 1)) return null;
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

        var result = new Palette();
        if (version == 1 && colr.Length >= 34 && colr.U32(14) is var baseListOffset and not 0)
        {
            var baseList = checked((int)baseListOffset);
            var count = checked((int)colr.U32(baseList));
            if (count > 65535 || (long)baseList + 4 + 6L * count > colr.Length)
                throw new InvalidDataException("COLR base glyph list is out of bounds.");
            var roots = new Dictionary<ushort, int>();
            for (var i = 0; i < count; i++)
            {
                var record = baseList + 4 + 6 * i;
                roots[colr.U16(record)] = checked(baseList + (int)colr.U32(record + 2));
            }
            foreach (var (glyph, root) in roots)
                if (Paint(colr, root, baseList, colr.U32(18) is var layerList and not 0 ? checked((int)layerList) : 0, palette, records, roots, new HashSet<int>()) is { Count: > 0 } layers)
                    result.Glyphs[glyph] = layers.ToArray();
            if (result.Glyphs.Count > 0) return result;
        }

        if (version != 0) return result.Glyphs.Count == 0 ? null : result;
        var countV0 = colr.U16(2);
        var baseOffset = (int)colr.U32(4);
        var layerOffset = (int)colr.U32(8);
        var layerCount = colr.U16(12);
        for (var i = 0; i < countV0; i++)
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

    private static List<Layer>? Paint(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        if (offset < 0 || offset >= colr.Length || !path.Add(offset)) return null;
        try
        {
            return colr.U8(offset) switch
            {
                1 when layerList != 0 => PaintLayers(colr, offset, baseList, layerList, palette, records, roots, path),
                10 => PaintGlyph(colr, offset, baseList, layerList, palette, records, roots, path),
                11 => roots.TryGetValue(colr.U16(offset + 1), out var root) ? Paint(colr, root, baseList, layerList, palette, records, roots, path) : null,
                14 => PaintTranslate(colr, offset, baseList, layerList, palette, records, roots, path),
                _ => null,
            };
        }
        finally { path.Remove(offset); }
    }

    private static List<Layer>? PaintLayers(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var count = colr.U8(offset + 1);
        var first = checked((int)colr.U32(offset + 2));
        var total = checked((int)colr.U32(layerList));
        if (first < 0 || count > total || first > total - count || (long)layerList + 4 + 4L * total > colr.Length)
            throw new InvalidDataException("COLR layer list is out of bounds.");
        var result = new List<Layer>();
        for (var i = 0; i < count; i++)
        {
            var child = checked(layerList + 4 + 4 * (first + i));
            var childOffset = checked(layerList + (int)colr.U32(child));
            if (Paint(colr, childOffset, baseList, layerList, palette, records, roots, path) is { } layers)
                result.AddRange(layers);
        }
        return result;
    }

    private static List<Layer>? PaintGlyph(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var childOffset = checked(offset + ReadU24(colr, offset + 1));
        var glyph = colr.U16(offset + 4);
        if (colr.U8(childOffset) != 2) return null;
        var index = colr.U16(childOffset + 1);
        if (index == 0xFFFF) return [new Layer(glyph, null)];
        if (PaintSolid(colr, childOffset, palette, records) is not { } color) return null;
        return [new Layer(glyph, color)];
    }

    private static List<Layer>? PaintTranslate(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var childOffset = checked(offset + ReadU24(colr, offset + 1));
        var dx = colr.S32(offset + 4) / 65536f;
        var dy = colr.S32(offset + 8) / 65536f;
        if (Paint(colr, childOffset, baseList, layerList, palette, records, roots, path) is not { } layers)
            return null;
        var translation = Matrix3x2.CreateTranslation(dx, dy);
        for (var i = 0; i < layers.Count; i++)
            layers[i] = layers[i] with { Transform = translation * layers[i].TransformOrIdentity };
        return layers;
    }

    private static Rgba? PaintSolid(FontData colr, int offset, Rgba[] palette, int records)
    {
        if (colr.U8(offset) != 2) return null;
        var index = colr.U16(offset + 1);
        var alpha = Math.Clamp(colr.S16(offset + 3) / 16384f, 0, 1);
        if (index == 0xFFFF) return null;
        if (index >= records) throw new InvalidDataException("COLR palette index out of bounds.");
        return palette[index] with { A = palette[index].A * alpha };
    }

    private static int ReadU24(FontData data, int offset) => checked(data.U8(offset) << 16 | data.U8(offset + 1) << 8 | data.U8(offset + 2));
}

using System.Runtime.CompilerServices;
using System.Numerics;
using Folio.Painting;
using Folio.Typography;

namespace Folio.Painting.Software;

/// <summary>Layered colour glyphs from the OpenType COLR v0/v1 and CPAL tables.</summary>
internal static class ColorGlyphs
{
    internal readonly record struct Layer(ushort Glyph, Rgba? Color, Matrix3x2 Transform = default, Gradient? Gradient = null, BlendMode Blend = BlendMode.Normal)
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
                12 or 13 => PaintTransform(colr, offset, baseList, layerList, palette, records, roots, path),
                14 or 15 => PaintTranslate(colr, offset, baseList, layerList, palette, records, roots, path),
                16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 => PaintScale(colr, offset, baseList, layerList, palette, records, roots, path),
                24 or 25 or 26 or 27 => PaintRotate(colr, offset, baseList, layerList, palette, records, roots, path),
                28 or 29 or 30 or 31 => PaintSkew(colr, offset, baseList, layerList, palette, records, roots, path),
                32 => PaintComposite(colr, offset, baseList, layerList, palette, records, roots, path),
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
        var index = colr.U16(childOffset + 1);
        if (colr.U8(childOffset) is 2 or 3)
        {
            if (index == 0xFFFF) return [new Layer(glyph, null)];
            if (PaintSolid(colr, childOffset, palette, records) is not { } color) return null;
            return [new Layer(glyph, color)];
        }
        if (colr.U8(childOffset) is 4 or 5 or 6 or 7 or 8 or 9 && PaintGradient(colr, childOffset, palette, records) is { } gradient)
            return [new Layer(glyph, gradient.Stops[0].Color, Gradient: gradient)];
        return null;
    }

    private static Gradient? PaintGradient(FontData colr, int offset, Rgba[] palette, int records)
    {
        // PaintLinearGradient/PaintRadialGradient: format, ColorLine offset and FWORD geometry.
        var colorLine = checked(offset + ReadU24(colr, offset + 1));
        if (colr.U8(offset) is not (4 or 5 or 6 or 7 or 8 or 9) || colorLine < 0 || colorLine + 3 > colr.Length)
            return null;
        var stops = ReadColorStops(colr, colorLine, palette, records, out var spread);
        if (stops is null) return null;
        var x0 = colr.S16(offset + 4);
        var y0 = colr.S16(offset + 6);
        var x1 = colr.S16(offset + 8);
        var y1 = colr.S16(offset + 10);
        if (colr.U8(offset) is 8 or 9)
        {
            // COLR angles are counter-clockwise from +X in the font design grid;
            // GradientShader uses clockwise canvas angles from the top. Preserve
            // the sweep span while converting the start axis.
            var start = (x1 / 16384f + 1) * 180;
            var end = (y1 / 16384f + 1) * 180;
            var shaderStart = 90 - start;
            return new Gradient(GradientKind.Conic, stops, spread,
                Center: new Vector2(x0, y0), StartAngle: shaderStart, EndAngle: shaderStart + end - start);
        }
        if (colr.U8(offset) is 6 or 7)
        {
            var r0 = Math.Max(0, (int)colr.S16(offset + 12));
            var r1 = Math.Max(0, (int)colr.S16(offset + 14));
            return new Gradient(GradientKind.Radial, stops, spread,
                Center: new Vector2(x1, y1), Radii: new Vector2(r1, r1),
                Focus: new Vector2(x0, y0), FocusRadius: r0);
        }
        return new Gradient(GradientKind.Linear, stops, spread, new Vector2(x0, y0), new Vector2(x1, y1));
    }

    private static List<GradientStop>? ReadColorStops(FontData colr, int colorLine, Rgba[] palette, int records, out GradientSpread spread)
    {
        spread = GradientSpread.Pad;
        var extend = colr.U8(colorLine);
        var count = colr.U16(colorLine + 1);
        if (count == 0 || (long)colorLine + 3 + 6L * count > colr.Length)
            throw new InvalidDataException("COLR color line is out of bounds.");
        var stops = new List<GradientStop>(count);
        for (var i = 0; i < count; i++)
        {
            var stop = colorLine + 3 + 6 * i;
            var position = Math.Clamp(colr.S16(stop) / 16384f, 0, 1);
            var paletteIndex = colr.U16(stop + 2);
            if (paletteIndex == 0xFFFF || paletteIndex >= records)
                continue;
            var alpha = Math.Clamp(colr.S16(stop + 4) / 16384f, 0, 1);
            var color = palette[paletteIndex] with { A = palette[paletteIndex].A * alpha };
            stops.Add(new GradientStop(position, color));
        }
        if (stops.Count == 0) return null;
        spread = extend switch
        {
            1 => GradientSpread.Repeat,
            2 => GradientSpread.Reflect,
            _ => GradientSpread.Pad,
        };
        return stops;
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

    private static List<Layer>? PaintComposite(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        // The backdrop is painted first; the source layers then carry the
        // supported COLR composite mode into the software compositor.
        var sourceOffset = checked(offset + ReadU24(colr, offset + 1));
        var mode = colr.U8(offset + 4);
        var backdropOffset = checked(offset + ReadU24(colr, offset + 5));
        var backdrop = Paint(colr, backdropOffset, baseList, layerList, palette, records, roots, path);
        var source = Paint(colr, sourceOffset, baseList, layerList, palette, records, roots, path);
        if (backdrop is null || source is null || !TryMapComposite(mode, out var blend))
            return backdrop;
        for (var i = 0; i < source.Count; i++)
            source[i] = source[i] with { Blend = blend };
        backdrop.AddRange(source);
        return backdrop;
    }

    private static bool TryMapComposite(byte mode, out BlendMode blend)
    {
        blend = mode switch
        {
            1 => BlendMode.Copy, 3 => BlendMode.Normal, 12 => BlendMode.PlusLighter,
            13 => BlendMode.Screen, 14 => BlendMode.Overlay,
            15 => BlendMode.Darken, 16 => BlendMode.Lighten, 17 => BlendMode.ColorDodge,
            18 => BlendMode.ColorBurn, 19 => BlendMode.HardLight, 20 => BlendMode.SoftLight,
            21 => BlendMode.Difference, 22 => BlendMode.Exclusion, 23 => BlendMode.Multiply,
            24 => BlendMode.Hue, 25 => BlendMode.Saturation, 26 => BlendMode.Color,
            27 => BlendMode.Luminosity, 5 => BlendMode.SourceIn, 6 => BlendMode.DestinationIn,
            7 => BlendMode.SourceOut, 8 => BlendMode.DestinationOut, 9 => BlendMode.SourceAtop,
            10 => BlendMode.DestinationAtop, 11 => BlendMode.Xor, 4 => BlendMode.DestinationOver,
            _ => BlendMode.Normal,
        };
        return mode is 1 or >= 3 and <= 27;
    }

    private static List<Layer>? PaintTransform(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var childOffset = checked(offset + ReadU24(colr, offset + 1));
        var xx = colr.S32(offset + 4) / 65536f;
        var xy = colr.S32(offset + 8) / 65536f;
        var yx = colr.S32(offset + 12) / 65536f;
        var yy = colr.S32(offset + 16) / 65536f;
        var dx = colr.S32(offset + 20) / 65536f;
        var dy = colr.S32(offset + 24) / 65536f;
        if (Paint(colr, childOffset, baseList, layerList, palette, records, roots, path) is not { } layers)
            return null;
        var transform = new Matrix3x2(xx, xy, yx, yy, dx, dy);
        for (var i = 0; i < layers.Count; i++)
            layers[i] = layers[i] with { Transform = transform * layers[i].TransformOrIdentity };
        return layers;
    }

    private static List<Layer>? PaintScale(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var childOffset = checked(offset + ReadU24(colr, offset + 1));
        var format = colr.U8(offset);
        var sx = colr.S16(offset + 4) / 16384f;
        var sy = format is 20 or 21 or 22 or 23 ? sx : colr.S16(offset + 6) / 16384f;
        var transform = Matrix3x2.CreateScale(sx, sy);
        if (format is 18 or 19 or 22 or 23)
        {
            var cx = colr.S16(offset + (format == 18 ? 8 : 6));
            var cy = colr.S16(offset + (format == 18 ? 10 : 8));
            transform = Matrix3x2.CreateTranslation(-cx, -cy) * transform * Matrix3x2.CreateTranslation(cx, cy);
        }
        if (Paint(colr, childOffset, baseList, layerList, palette, records, roots, path) is not { } layers)
            return null;
        for (var i = 0; i < layers.Count; i++)
            layers[i] = layers[i] with { Transform = transform * layers[i].TransformOrIdentity };
        return layers;
    }

    private static List<Layer>? PaintRotate(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var childOffset = checked(offset + ReadU24(colr, offset + 1));
        var radians = colr.S16(offset + 4) / 16384f * MathF.PI;
        var transform = Matrix3x2.CreateRotation(radians);
        if (colr.U8(offset) is 26 or 27)
        {
            var cx = colr.S16(offset + 6);
            var cy = colr.S16(offset + 8);
            transform = Matrix3x2.CreateTranslation(-cx, -cy) * transform * Matrix3x2.CreateTranslation(cx, cy);
        }
        if (Paint(colr, childOffset, baseList, layerList, palette, records, roots, path) is not { } layers)
            return null;
        for (var i = 0; i < layers.Count; i++)
            layers[i] = layers[i] with { Transform = transform * layers[i].TransformOrIdentity };
        return layers;
    }

    private static List<Layer>? PaintSkew(FontData colr, int offset, int baseList, int layerList, Rgba[] palette, int records, Dictionary<ushort, int> roots, HashSet<int> path)
    {
        var childOffset = checked(offset + ReadU24(colr, offset + 1));
        var xAngle = colr.S16(offset + 4) / 16384f * MathF.PI;
        var yAngle = colr.S16(offset + 6) / 16384f * MathF.PI;
        var transform = new Matrix3x2(1, MathF.Tan(yAngle), -MathF.Tan(xAngle), 1, 0, 0);
        if (colr.U8(offset) is 30 or 31)
        {
            var cx = colr.S16(offset + 8);
            var cy = colr.S16(offset + 10);
            transform = Matrix3x2.CreateTranslation(-cx, -cy) * transform * Matrix3x2.CreateTranslation(cx, cy);
        }
        if (Paint(colr, childOffset, baseList, layerList, palette, records, roots, path) is not { } layers)
            return null;
        for (var i = 0; i < layers.Count; i++)
            layers[i] = layers[i] with { Transform = transform * layers[i].TransformOrIdentity };
        return layers;
    }

    private static Rgba? PaintSolid(FontData colr, int offset, Rgba[] palette, int records)
    {
        if (colr.U8(offset) is not (2 or 3)) return null;
        var index = colr.U16(offset + 1);
        var alpha = Math.Clamp(colr.S16(offset + 3) / 16384f, 0, 1);
        if (index == 0xFFFF) return null;
        if (index >= records) throw new InvalidDataException("COLR palette index out of bounds.");
        return palette[index] with { A = palette[index].A * alpha };
    }

    private static int ReadU24(FontData data, int offset) => checked(data.U8(offset) << 16 | data.U8(offset + 1) << 8 | data.U8(offset + 2));
}

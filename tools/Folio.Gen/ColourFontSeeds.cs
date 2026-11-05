using System.Buffers.Binary;

// Small, valid colour fonts keep mutations inside the fuzzer's 64 KiB input ceiling.
internal static class ColourFontSeeds
{
    private static void U16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), checked((ushort)value));
    private static void U32(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), checked((uint)value));
    internal static int Generate(string root)
    {
        var folder = Path.Combine(root, "tests", "Folio.Fuzz", "Seeds", "font");
        Directory.CreateDirectory(folder);
        var glyph = Array.IndexOf(BoxFont.CodePoints, (int)'A') + 1;
        byte[] palette = [0,0, 0,2, 0,1, 0,3, 0,0,0,14, 0,1, 0,255,0,255, 0,0,255,255, 255,0,0,255];
        void Write(string name, params (string Tag, byte[] Data)[] tables)
        {
            var bytes = BoxFont.Build(tables.ToDictionary(t => t.Tag, t => t.Data));
            File.WriteAllBytes(Path.Combine(folder, name + ".ttf"), bytes);
            Console.WriteLine($"{name}: {bytes.Length} bytes");
        }
        var v0 = new byte[24]; U16(v0, 2, 1); U32(v0, 4, 14); U32(v0, 8, 20); U16(v0, 12, 1);
        U16(v0, 14, glyph); U16(v0, 18, 1); U16(v0, 20, glyph);
        Write("colr-v0", ("COLR", v0), ("CPAL", palette));

        byte[] Header(byte[] paint)
        {
            var colr = new byte[44 + paint.Length]; U16(colr, 0, 1); U32(colr, 14, 34);
            U32(colr, 34, 1); U16(colr, 38, glyph); U32(colr, 40, 10); paint.CopyTo(colr, 44); return colr;
        }
        byte[] fill = [10,0,0,6,0,0, 2,0,0,0x40,0]; U16(fill, 4, glyph);
        // Offset-addressed affine, then a signed FWORD translation, then PaintGlyph/PaintSolid.
        var affine = new byte[24]; U32(affine, 0, 1 << 16); U32(affine, 12, 1 << 16); U32(affine, 16, 100 << 16);
        byte[] translate = [14,0,0,8, 0,100, 255,156];
        Write("colr-v1-transform", ("COLR", Header([12,0,0,31,0,0,7, .. affine, .. translate, .. fill])), ("CPAL", palette));

        byte[] gradient = [4,0,0,16, 0,0, 0,0, 3,232, 0,0, 0,0, 3,232,
            0,0,2, 0,0,0,0,0x40,0, 0x40,0,0,1,0x40,0];
        byte[] shape = [10,0,0,6,0,0]; U16(shape, 4, glyph);
        Write("colr-v1-gradient", ("COLR", Header([.. shape, .. gradient])), ("CPAL", palette));

        var count = BoxFont.CodePoints.Length + 1;
        var png = File.ReadAllBytes(Path.Combine(root, "tests", "images", "gray-1.png"));
        var start = 4 + 4 * (count + 1);
        var sbix = new byte[12 + start + 8 + png.Length]; U16(sbix, 0, 1); U16(sbix, 2, 1); U32(sbix, 4, 1); U32(sbix, 8, 12); U16(sbix, 12, 64); U16(sbix, 14, 72);
        for (var g = 0; g <= count; g++) U32(sbix, 16 + 4 * g, g <= glyph ? start : start + 8 + png.Length);
        "png "u8.CopyTo(sbix.AsSpan(12 + start + 4)); png.CopyTo(sbix, 12 + start + 8);
        Write("sbix-png", ("sbix", sbix));
        return 0;
    }
}

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Folio.Typography;
using HarfBuzzSharp;
using Buffer = HarfBuzzSharp.Buffer;

namespace Folio.Skia;

/// <summary>An <see cref="ITextShaper"/> on HarfBuzzSharp, for complex scripts and everything SimpleShaper leaves.</summary>
public sealed class HarfBuzzShaper : ITextShaper
{
    // One HarfBuzz font per font handle, at the face's own units per em; shaping only reads it.
    private sealed record ShapingFont(Font Font, int UnitsPerEm);

    private static readonly ConditionalWeakTable<IFontHandle, ShapingFont?> Fonts = new();

    public ShapedText Shape(string text, int start, int length, IFontHandle font, float size, bool rightToLeft, string? language)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start + length, text.Length);
        if (length == 0 || FontFor(font) is not { } shapingFont)
            return new ShapedText([], [], [], []);

        using var buffer = new Buffer();
        buffer.AddUtf16(text, start, length);
        buffer.GuessSegmentProperties();
        buffer.Direction = rightToLeft ? Direction.RightToLeft : Direction.LeftToRight;
        if (language is not null)
            buffer.Language = new Language(language);
        shapingFont.Font.Shape(buffer);

        var infos = buffer.GlyphInfos;
        var positions = buffer.GlyphPositions;
        var count = infos.Length;
        var scale = size / shapingFont.UnitsPerEm;
        var (glyphs, clusters, advances, offsets) = (new ushort[count], new int[count], new float[count], new Vector2[count]);
        for (var i = 0; i < count; i++)
        {
            // HarfBuzz returns right-to-left runs in visual order; reversing gives logical order.
            var j = rightToLeft ? count - 1 - i : i;
            glyphs[i] = (ushort)infos[j].Codepoint;
            clusters[i] = (int)infos[j].Cluster;
            advances[i] = positions[j].XAdvance * scale;
            offsets[i] = new Vector2(positions[j].XOffset * scale, -positions[j].YOffset * scale);
        }
        return new ShapedText(glyphs, clusters, advances, offsets);
    }

    private static ShapingFont? FontFor(IFontHandle handle) => Fonts.GetValue(handle, h =>
    {
        // HarfBuzz reads the bytes for the font's lifetime, so they live in unmanaged memory the GC cannot move; the blob
        // frees them when HarfBuzz lets go of the last reference.
        var bytes = h.Data.ToArray();
        var data = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, data, bytes.Length);
        using var blob = new Blob(data, bytes.Length, MemoryMode.ReadOnly, () => Marshal.FreeHGlobal(data));
        var face = new Face(blob, h.FaceIndex);
        if (face.UnitsPerEm <= 0)
            return null;
        var font = new Font(face);
        font.SetScale(face.UnitsPerEm, face.UnitsPerEm);
        return new ShapingFont(font, face.UnitsPerEm);
    });
}

using Folio.Typography;
using SkiaSharp;

namespace Folio.Skia;

/// <summary>
/// The system's installed fonts through SkiaSharp's font manager (DirectWrite on Windows, fontconfig on Linux), as an
/// <see cref="IFontSource"/>. Each face's file is read when its family is first opened.
/// </summary>
public sealed class SystemFontSource : IFontSource
{
    private readonly SKFontManager _manager = SKFontManager.Default;

    public IReadOnlyList<IFontHandle> OpenFamily(string family)
    {
        ArgumentNullException.ThrowIfNull(family);
        using var styles = _manager.GetFontStyles(family);
        var faces = new List<IFontHandle>();
        if (styles is null)
            return faces;
        for (var i = 0; i < styles.Count; i++)
        {
            using var typeface = styles.CreateTypeface(i);
            if (typeface is null || !typeface.FamilyName.Equals(family, StringComparison.OrdinalIgnoreCase))
                continue; // a substitute family: not what was asked for
            using var stream = typeface.OpenStream(out var faceIndex);
            if (stream is null || stream.Length <= 0)
                continue;
            var data = new byte[stream.Length];
            if (stream.Read(data, data.Length) == data.Length)
                faces.Add(new FontData(data, faceIndex));
        }
        return faces;
    }

    public string? MatchCharacter(int codePoint, int weight, bool italic)
    {
        var style = new SKFontStyle(Math.Clamp(weight, 1, 1000), (int)SKFontStyleWidth.Normal, italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);
        using var typeface = _manager.MatchCharacter(null, style, null, codePoint);
        return typeface?.FamilyName;
    }

    private sealed record FontData(ReadOnlyMemory<byte> Data, int FaceIndex) : IFontHandle;
}

using System.Numerics;
using Folio.Typography;

namespace Folio.Painting;

/// <summary>The drawing surface the display list replays onto. Coordinates are CSS pixels.</summary>
/// <remarks>
/// Swap-out interface, sketched in docs/dependencies.md. Members are added together with the value
/// types they use when the first implementation needs them: shadows come later.
/// </remarks>
public interface ICanvas
{
    /// <summary>Saves the clip, to be restored by the matching <see cref="Restore"/>.</summary>
    void Save();

    void Restore();

    /// <summary>Intersects the clip with a (rounded) rectangle, antialiased.</summary>
    void ClipRoundedRect(in RoundedRect rect);

    /// <summary>Intersects the clip with a path, antialiased.</summary>
    void ClipPath(PathData path, FillRule rule);

    void FillRoundedRect(in RoundedRect rect, in Paint paint);

    void FillPath(PathData path, FillRule rule, in Paint paint);

    void StrokePath(PathData path, in Stroke stroke, in Paint paint);

    /// <summary>Draws glyphs of a font by id at a size in CSS pixels, each at its baseline origin.</summary>
    void DrawGlyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint);

    /// <summary>Draws a whole image scaled into <paramref name="destination"/>.</summary>
    void DrawImage(Imaging.IImageHandle image, in RectF destination, ImageSampling sampling);
    /// <summary>
    /// Where the outlines of glyphs drawn as by <see cref="DrawGlyphs"/> cross the horizontal band from
    /// <paramref name="top"/> to <paramref name="bottom"/>: pairs of left and right x coordinates, one pair per crossing,
    /// in CSS pixels. Used to leave gaps in underlines and overlines (text-decoration-skip-ink). A canvas that cannot
    /// tell returns none, and lines are drawn unbroken.
    /// </summary>
    float[] GlyphIntercepts(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, float top, float bottom) => [];

    /// <summary>Starts a layer that is composited with <paramref name="options"/> at the matching <see cref="PopLayer"/>.</summary>
    void PushLayer(in LayerOptions options);

    void PopLayer();
}

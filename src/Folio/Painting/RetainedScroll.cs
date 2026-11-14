using System.Numerics;

namespace Folio.Painting;

/// <summary>
/// Scrolling a page by moving the pixels already drawn and painting only the rows that scrolled into view, instead of
/// replaying the display list for the whole viewport. The result is byte-for-byte what a full repaint at the new offset
/// gives, or the shift is not done: <see cref="TryScroll"/> says false and the caller repaints in full.
/// </summary>
/// <remarks>
/// A frame is drawn with <c>scale(scale)</c> and then a whole number of device pixels of scroll taken off every device position
/// (<see cref="Begin"/>), so scrolling by whole device pixels moves every device-space coordinate by exactly that many rows (the
/// position is the unscrolled one, the same bits at any scroll offset, less a whole number, which is exact) and every shape
/// rasterizes to the same coverage, only on another row. That holds at any scale, for what the list paints on its own. It does
/// not hold, and the shift is refused, when the list has something anchored to the viewport (<see cref="DisplayList.ViewportAnchored"/>),
/// a backdrop-filter layer, or a filter layer other than colour matrices (saturate, brightness and the like work on one pixel at a time and are fine; a blur's reach ends at the viewport edge, which moves), a blend mode (it may read what the
/// shift cannot vouch for), a perspective transform, or the shift is nearly the whole viewport (nothing left to reuse).
/// One known gap: a blur of two pixels or more is done in box passes that keep running sums, whose rounding depends on where the
/// blurred shape's pixels start, and that moves with the viewport, so a wide soft shadow can come out one level off in a few
/// isolated bytes (measured: text-shadow 4px at 150%, a handful of bytes in 480,000). It is bounded and does not build up,
/// because a row is painted once and then only moved.
/// The kill switch is the environment variable FOLIO_RETAINED_SCROLL=0.
/// </remarks>
internal static class RetainedScroll
{
    /// <summary>Whether shifting is on at all; FOLIO_RETAINED_SCROLL=0 turns it off.</summary>
    internal static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("FOLIO_RETAINED_SCROLL") != "0";

    /// <summary>The transform a frame scrolled to <paramref name="top"/> device pixels is drawn with.</summary>
    // (The control used translate(-top / scale) * scale: the same to within float noise of about 1e-4 px; this form makes the offset a whole number.)
    internal static Matrix3x2 Transform(float scale, int top) => Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(0, -top);

    /// <summary>Sets <paramref name="canvas"/> up to draw the page scrolled to <paramref name="top"/> device pixels: <see cref="Transform"/>, and for the software canvas the offset kept apart so gradients do not depend on it.</summary>
    internal static void Begin(ICanvas canvas, float scale, int top)
    {
        if (canvas is Software.SoftwareCanvas software)
            software.TransformScrolled(Matrix3x2.CreateScale(scale), top);
        else
            canvas.Transform(Transform(scale, top));
    }

    /// <summary>Draws the whole frame at <paramref name="top"/> device pixels of scroll.</summary>
    internal static void Repaint(DisplayList list, SoftwareRasterSurface surface, float scale, int top, Paint background) =>
        PaintRows(list, surface, scale, top, 0, surface.Height, background);

    /// <summary>
    /// Brings a surface that holds the frame at <paramref name="fromTop"/> to the frame at <paramref name="toTop"/> (device
    /// pixels, the same list, scale and size) by shifting and painting the exposed rows. Returns false, leaving the surface
    /// as it was, when that cannot be done exactly.
    /// </summary>
    internal static bool TryScroll(DisplayList list, SoftwareRasterSurface surface, float scale, int fromTop, int toTop, Paint background)
    {
        var delta = toTop - fromTop;
        var height = surface.Height;
        if (!Enabled || delta == 0 || list.ViewportAnchored || !(scale > 0) || !float.IsFinite(scale) || Math.Abs(delta) > height - height / 8 || !ShiftSafe(list))
            return false;
        surface.ShiftRows(delta);
        if (delta > 0)
            PaintRows(list, surface, scale, toTop, height - delta, height, background);
        else
            PaintRows(list, surface, scale, toTop, 0, -delta, background);
        return true;
    }

    // The rows [from, to) of the frame at `top`: the background, then the items that reach them, all clipped to the rows.
    // The clip and the background are in device space, before the page's transform, so they cover whole pixels exactly.
    private static void PaintRows(DisplayList list, SoftwareRasterSurface surface, float scale, int top, int from, int to, Paint background)
    {
        var canvas = surface.Canvas;
        var rows = new RoundedRect(new RectF(0, from, surface.Width, to - from), default);
        canvas.Save();
        canvas.ClipRoundedRect(rows);
        canvas.FillRoundedRect(rows, background);
        Begin(canvas, scale, top);
        // Items are culled by estimated bounds (glyph ink is a guess), and the full repaint culls against the whole viewport:
        // look a little beyond the rows too, so ink the estimate misses is not dropped here when the full repaint draws it.
        var reach = CullMargin / scale;
        DisplayListPlayer.Replay(list, canvas, new RectF(-reach, (from + top) / scale - reach, surface.Width / scale + 2 * reach, (to - from) / scale + 2 * reach));
        canvas.Restore();
    }

    private const float CullMargin = 96; // device pixels

    private static bool ShiftSafe(DisplayList list)
    {
        if (list.ShiftSafeCache is { } known)
            return known;
        var safe = true;
        foreach (var item in list.Items)
        {
            if (item.Blend != BlendMode.Normal || item.Projection is not null
                || item is { Kind: DisplayItemKind.PushLayer } && (item.Backdrop is { Count: > 0 } || item.Filters?.Any(f => f.Kind != FilterKind.ColorMatrix || f.Subregion is not null) == true))
            {
                safe = false;
                break;
            }
        }
        return (list.ShiftSafeCache = safe).Value;
    }
}

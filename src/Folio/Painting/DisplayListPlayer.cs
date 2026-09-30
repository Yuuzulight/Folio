using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

/// <summary>Replays a display list onto a canvas.</summary>
internal static class DisplayListPlayer
{
    public static void Replay(DisplayList list, ICanvas canvas)
    {
        var open = new Stack<DisplayItemKind>();
        foreach (var item in list.Items)
        {
            switch (item.Kind)
            {
                case DisplayItemKind.Fill:
                    canvas.FillRoundedRect(item.Shape, new Paint(ToRgba(item.Color), Gradient: item.Gradient, Blend: item.Blend));
                    break;
                case DisplayItemKind.BoxShadow:
                    PaintBoxShadow(canvas, item);
                    break;
                case DisplayItemKind.Border:
                    PaintBorder(canvas, item.Shape, item.Border!);
                    break;
                case DisplayItemKind.Glyphs:
                    canvas.DrawGlyphs(item.Glyphs!.Font, item.Glyphs.Size, item.Glyphs.Glyphs, item.Glyphs.Origins, new Paint(ToRgba(item.Color), item.Blur, item.Gradient));
                    break;
                case DisplayItemKind.Decoration when item.Glyphs is { } ink && SkipInk(canvas, item.Shape.Rect, item.LineStyle, ink) is { } gaps:
                    canvas.Save();
                    canvas.ClipPath(gaps, FillRule.NonZero);
                    PaintDecoration(canvas, item.Shape.Rect, item.LineStyle, new Paint(ToRgba(item.Color)));
                    canvas.Restore();
                    break;
                case DisplayItemKind.Decoration:
                    PaintDecoration(canvas, item.Shape.Rect, item.LineStyle, new Paint(ToRgba(item.Color)));
                    break;
                case DisplayItemKind.Image:
                    canvas.DrawImage(item.Image!, item.Shape.Rect, item.Sampling);
                    break;
                case DisplayItemKind.FillPath:
                    canvas.FillPath(item.Path!, item.Rule, new Paint(ToRgba(item.Color), Gradient: item.Gradient));
                    break;
                case DisplayItemKind.StrokePath:
                    canvas.StrokePath(item.Path!, item.Stroke!.Value, new Paint(ToRgba(item.Color), Gradient: item.Gradient));
                    break;
                case DisplayItemKind.PushClip:
                    canvas.Save();
                    if (item.Path is { } path)
                        canvas.ClipPath(path, item.Rule);
                    else
                        canvas.ClipRoundedRect(item.Shape);
                    open.Push(item.Kind);
                    break;
                case DisplayItemKind.PushLayer:
                    canvas.PushLayer(new LayerOptions(item.Opacity, item.Filters, item.Backdrop, item.Shape, item.Blend));
                    open.Push(item.Kind);
                    break;
                case DisplayItemKind.PushTransform:
                    canvas.Save();
                    canvas.Transform(item.Transform);
                    open.Push(item.Kind);
                    break;
                case DisplayItemKind.Pop when open.TryPop(out var kind):
                    if (kind == DisplayItemKind.PushLayer)
                        canvas.PopLayer();
                    else
                        canvas.Restore();
                    break;
            }
        }
    }

    // The parts of a decoration line that do not come near the glyphs' ink, as a clip path: the canvas finds where the
    // outlines cross the line's band, and a gap of the line's thickness is left on each side
    // (https://www.w3.org/TR/css-text-decor-4/#text-decoration-skip-ink-property). Null when nothing crosses it.
    private static PathData? SkipInk(ICanvas canvas, RectF rect, TextDecorationStyle style, GlyphRun ink)
    {
        var t = rect.Height;
        var (top, bottom) = (rect.Y, rect.Y + (style == TextDecorationStyle.Wavy ? 3 * t : t));
        var intercepts = canvas.GlyphIntercepts(ink.Font, ink.Size, ink.Glyphs, ink.Origins, top, bottom);
        if (intercepts.Length < 2)
            return null;
        var gap = Math.Max(1, t);
        var cuts = new List<(float From, float To)>();
        for (var i = 0; i + 1 < intercepts.Length; i += 2)
            cuts.Add((intercepts[i] - gap, intercepts[i + 1] + gap));
        cuts.Sort();
        var path = new PathData();
        var (x, y, height) = (rect.X, top - t, bottom - top + 2 * t);
        foreach (var (from, to) in cuts)
        {
            if (from > x)
                path.AddRoundedRect(new RoundedRect(new RectF(x, y, Math.Min(from, rect.Right) - x, height), default));
            x = Math.Max(x, to);
        }
        if (x < rect.Right)
            path.AddRoundedRect(new RoundedRect(new RectF(x, y, rect.Right - x, height), default));
        // Everything is cut away: an empty rectangle keeps the clip empty.
        return path.Commands.Count > 0 ? path : new PathData().AddRoundedRect(new RoundedRect(new RectF(rect.X, y, 0, 0), default));
    }

    // A decoration line of thickness rect.Height from rect.Y down: dots and dashes along its middle, waves below its top.
    // Patterns start at multiples of their period from the canvas origin, so the pieces of one line on neighbouring
    // text fragments join up.
    private static void PaintDecoration(ICanvas canvas, RectF rect, TextDecorationStyle style, in Paint paint)
    {
        var t = rect.Height;
        var middle = rect.Y + t / 2;
        if (style == TextDecorationStyle.Solid || t <= 0)
        {
            canvas.FillRoundedRect(new RoundedRect(rect, default), paint);
            return;
        }

        var period = style switch { TextDecorationStyle.Dotted => 2 * t, TextDecorationStyle.Dashed => 5 * t, _ => 6 * t };
        var start = MathF.Floor(rect.X / period) * period;
        var path = new PathData();
        var stroke = new Stroke(t);
        switch (style)
        {
            case TextDecorationStyle.Dotted:
                path.MoveTo(start + t / 2, middle).LineTo(rect.Right + t, middle);
                stroke = new Stroke(t, LineCap.Round, [0, period]);
                break;
            case TextDecorationStyle.Dashed:
                path.MoveTo(start, middle).LineTo(rect.Right, middle);
                stroke = new Stroke(t, LineCap.Butt, [3 * t, 2 * t]);
                break;
            default:
                // Half waves as cubic arches (controls at 4/3 of the amplitude t), from the top edge to 2t below it.
                var baseline = middle + t;
                path.MoveTo(start, baseline);
                var up = true;
                for (var x = start; x < rect.Right; x += period / 2, up = !up)
                {
                    var control = baseline + (up ? -1 : 1) * t * 4 / 3;
                    path.CubicTo(new(x + period / 6, control), new(x + period / 3, control), new(x + period / 2, baseline));
                }
                break;
        }
        canvas.Save();
        canvas.ClipRoundedRect(new RoundedRect(new RectF(rect.X, rect.Y - t, rect.Width, 5 * t), default));
        canvas.StrokePath(path, stroke, paint);
        canvas.Restore();
    }

    // A box shadow: blurred, and clipped to the outside of the border box (outer) or the inside of the padding box
    // (inset, where the shadow is the region outside its shape).
    private static void PaintBoxShadow(ICanvas canvas, in DisplayItem item)
    {
        var paint = new Paint(ToRgba(item.Color), item.Blur);
        var box = item.Box;
        var margin = 3 * item.Blur + Math.Abs(item.Shape.Rect.X - box.Rect.X) + Math.Abs(item.Shape.Rect.Y - box.Rect.Y)
                     + Math.Abs(item.Shape.Rect.Width - box.Rect.Width) + Math.Abs(item.Shape.Rect.Height - box.Rect.Height) + 1;
        var around = new RoundedRect(box.Rect.Inset(-margin, -margin, -margin, -margin), default);
        canvas.Save();
        if (item.Inset)
        {
            canvas.ClipRoundedRect(box);
            canvas.FillPath(new PathData().AddRoundedRect(around).AddRoundedRect(item.Shape), FillRule.EvenOdd, paint);
        }
        else
        {
            canvas.ClipPath(new PathData().AddRoundedRect(around).AddRoundedRect(box), FillRule.EvenOdd);
            canvas.FillRoundedRect(item.Shape, paint);
        }
        canvas.Restore();
    }

    internal static Rgba ToRgba(CssColor color) => new(color.R, color.G, color.B, color.A);

    private enum Side { Top, Right, Bottom, Left }

    /// <summary>
    /// Paints a border between its outer edge and its padding edge (css-backgrounds-3 §4): each side in the region
    /// bounded by the diagonal joins at the corners, in its own style and colour.
    /// </summary>
    // ponytail: dash and dot spacing is fixed (3 × width and 2 × width) rather than adjusted to fit each side.
    private static void PaintBorder(ICanvas canvas, RoundedRect outer, BorderGroup border)
    {
        float[] widths = [border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth];
        BorderStyle[] styles = [border.TopStyle, border.RightStyle, border.BottomStyle, border.LeftStyle];
        CssColor[] colors = [border.TopColor, border.RightColor, border.BottomColor, border.LeftColor];
        var inner = outer.Inset(widths[0], widths[1], widths[2], widths[3]);

        // One solid colour all round: a single ring.
        if (styles.All(s => s == BorderStyle.Solid) && colors.Distinct().Count() == 1 && widths.All(w => w > 0))
        {
            canvas.FillPath(Ring(outer, inner), FillRule.EvenOdd, new Paint(ToRgba(colors[0])));
            return;
        }

        var (o, i) = (outer.Rect, inner.Rect);
        for (var side = 0; side < 4; side++)
        {
            if (widths[side] <= 0 || colors[side].A <= 0)
                continue;
            var region = (Side)side switch
            {
                Side.Top => new PathData().MoveTo(o.X, o.Y).LineTo(o.Right, o.Y).LineTo(i.Right, i.Y).LineTo(i.X, i.Y).Close(),
                Side.Right => new PathData().MoveTo(o.Right, o.Y).LineTo(o.Right, o.Bottom).LineTo(i.Right, i.Bottom).LineTo(i.Right, i.Y).Close(),
                Side.Bottom => new PathData().MoveTo(o.Right, o.Bottom).LineTo(o.X, o.Bottom).LineTo(i.X, i.Bottom).LineTo(i.Right, i.Bottom).Close(),
                _ => new PathData().MoveTo(o.X, o.Bottom).LineTo(o.X, o.Y).LineTo(i.X, i.Y).LineTo(i.X, i.Bottom).Close(),
            };
            canvas.Save();
            canvas.ClipPath(region, FillRule.NonZero);
            PaintSide(canvas, (Side)side, styles[side], colors[side], widths[side], outer, inner, widths);
            canvas.Restore();
        }
    }

    private static void PaintSide(ICanvas canvas, Side side, BorderStyle style, CssColor color, float width,
                                  RoundedRect outer, RoundedRect inner, float[] widths)
    {
        RoundedRect At(float fraction) => outer.Inset(widths[0] * fraction, widths[1] * fraction, widths[2] * fraction, widths[3] * fraction);
        var topLeft = side is Side.Top or Side.Left;
        var dark = new CssColor(color.R * 2 / 3, color.G * 2 / 3, color.B * 2 / 3, color.A);
        void FillRing(RoundedRect from, RoundedRect to, CssColor c) => canvas.FillPath(Ring(from, to), FillRule.EvenOdd, new Paint(ToRgba(c)));

        switch (style)
        {
            case BorderStyle.Double when width >= 3:
                FillRing(outer, At(1f / 3), color);
                FillRing(At(2f / 3), inner, color);
                break;
            case BorderStyle.Dashed or BorderStyle.Dotted:
                var dotted = style == BorderStyle.Dotted;
                var stroke = new Stroke(width, dotted ? LineCap.Round : LineCap.Butt, dotted ? [0, 2 * width] : [3 * width, 3 * width]);
                canvas.StrokePath(new PathData().AddRoundedRect(At(0.5f)), stroke, new Paint(ToRgba(color)));
                break;
            // The 3D styles darken the sides facing away from a light at the top left (css-backgrounds-3 §4.3).
            case BorderStyle.Inset or BorderStyle.Outset:
                FillRing(outer, inner, topLeft == (style == BorderStyle.Inset) ? dark : color);
                break;
            case BorderStyle.Groove or BorderStyle.Ridge:
                var outerDark = topLeft == (style == BorderStyle.Groove);
                FillRing(outer, At(0.5f), outerDark ? dark : color);
                FillRing(At(0.5f), inner, outerDark ? color : dark);
                break;
            default:
                FillRing(outer, inner, color);
                break;
        }
    }

    private static PathData Ring(in RoundedRect outer, in RoundedRect inner) => new PathData().AddRoundedRect(outer).AddRoundedRect(inner);
}

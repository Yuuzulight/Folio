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
                    canvas.FillRoundedRect(item.Shape, new Paint(ToRgba(item.Color)));
                    break;
                case DisplayItemKind.Border:
                    PaintBorder(canvas, item.Shape, item.Border!);
                    break;
                case DisplayItemKind.Glyphs:
                    canvas.DrawGlyphs(item.Glyphs!.Font, item.Glyphs.Size, item.Glyphs.Glyphs, item.Glyphs.Origins, new Paint(ToRgba(item.Color)));
                    break;
                case DisplayItemKind.PushClip:
                    canvas.Save();
                    canvas.ClipRoundedRect(item.Shape);
                    open.Push(item.Kind);
                    break;
                case DisplayItemKind.PushOpacity:
                    canvas.PushLayer(new LayerOptions(item.Opacity));
                    open.Push(item.Kind);
                    break;
                case DisplayItemKind.Pop when open.TryPop(out var kind):
                    if (kind == DisplayItemKind.PushClip)
                        canvas.Restore();
                    else
                        canvas.PopLayer();
                    break;
            }
        }
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

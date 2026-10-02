using System.Numerics;
using Folio.Css;
using Folio.Style;

namespace Folio.Painting;

/// <summary>Replays a display list onto a canvas.</summary>
internal static class DisplayListPlayer
{
    /// <param name="visible">The part of the page the canvas shows, in the list's coordinates (CSS pixels), when known:
    /// items wholly outside it are not drawn. A tall page replayed into a viewport draws only what is in view.</param>
    public static void Replay(DisplayList list, ICanvas canvas, RectF? visible = null)
    {
        var open = new Stack<(DisplayItemKind Kind, bool Bounded)>();
        // Layer bounds walk the whole list: only worth it when there are layers.
        var bounds = list.Items.Exists(i => i.Kind == DisplayItemKind.PushLayer) ? LayerBounds(list) : null;
        // Items are culled only in the page's own coordinates: not under a transform or inside a layer, whose filters
        // may draw beyond what is in it.
        var moved = 0;
        for (var index = 0; index < list.Items.Count; index++)
        {
            var item = list.Items[index];
            if (visible is { } view && moved == 0 && item.Kind is not (DisplayItemKind.PushClip or DisplayItemKind.PushLayer or DisplayItemKind.PushTransform or DisplayItemKind.Pop)
                && !Intersects(ItemBounds(item), view))
                continue;
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
                    open.Push((item.Kind, false));
                    break;
                case DisplayItemKind.PushLayer:
                    // A clip around everything the layer draws, so the canvas makes it no larger than that.
                    if (bounds![index] is { } layer)
                    {
                        canvas.Save();
                        canvas.ClipRoundedRect(new RoundedRect(layer, default));
                    }
                    canvas.PushLayer(new LayerOptions(item.Opacity, item.Filters, item.Backdrop, item.Shape, item.Blend));
                    open.Push((item.Kind, bounds[index] is not null));
                    moved++;
                    break;
                case DisplayItemKind.PushTransform:
                    canvas.Save();
                    if (item.Projection is { } projection)
                        canvas.Transform(projection);
                    else
                        canvas.Transform(item.Transform);
                    open.Push((item.Kind, false));
                    moved++;
                    break;
                case DisplayItemKind.Pop when open.TryPop(out var top):
                    if (top.Kind is DisplayItemKind.PushLayer or DisplayItemKind.PushTransform)
                        moved--;
                    if (top.Kind == DisplayItemKind.PushLayer)
                    {
                        canvas.PopLayer();
                        if (top.Bounded)
                            canvas.Restore();
                    }
                    else
                    {
                        canvas.Restore();
                    }
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

    /// <summary>
    /// For each layer, a rectangle in its own coordinates holding everything it draws: its items (through nested
    /// transforms), grown by its filters' reach and holding its backdrop clip. Null where that is not known (a
    /// projective transform, a filter that can paint transparent pixels, an unbalanced list) or where the layer also
    /// changes what lies around it (a Porter-Duff blend, as masks use).
    /// </summary>
    // ponytail: glyph ink is estimated from the origins and the size (an em before, two after); clips inside a layer
    // are not used to shrink it.
    internal static RectF?[] LayerBounds(DisplayList list)
    {
        var result = new RectF?[list.Items.Count];
        // Open groups: the index of their push and the bounds of what they hold so far (null: nothing yet).
        var open = new Stack<(int Index, RectF? Bounds, bool Unknown)>();
        var (top, unknownTop) = ((RectF?)null, false);
        void Add(RectF? rect, bool unknown)
        {
            if (unknown)
                unknownTop = true;
            else if (rect is { } r)
                top = top is { } t ? Union(t, r) : r;
        }
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            switch (item.Kind)
            {
                case DisplayItemKind.PushClip or DisplayItemKind.PushLayer or DisplayItemKind.PushTransform:
                    open.Push((i, top, unknownTop));
                    (top, unknownTop) = (null, false);
                    break;
                case DisplayItemKind.Pop when open.TryPop(out var outer):
                    var push = list.Items[outer.Index];
                    var (inner, unknown) = (top, unknownTop);
                    if (push.Kind == DisplayItemKind.PushLayer)
                    {
                        if (push.Backdrop is not null)
                            inner = inner is { } b ? Union(b, push.Shape.Rect) : push.Shape.Rect;
                        (inner, unknown) = Reach(inner, unknown, push.Filters);
                        // A Porter-Duff layer (a mask) also changes what lies outside what it draws, so it is not bounded.
                        result[outer.Index] = unknown || push.Blend > BlendMode.PlusLighter ? null : inner ?? default;
                    }
                    else if (push.Kind == DisplayItemKind.PushTransform)
                    {
                        unknown |= push.Projection is not null;
                        inner = inner is { } r ? Map(r, push.Transform) : null;
                    }
                    (top, unknownTop) = (outer.Bounds, outer.Unknown);
                    Add(inner, unknown);
                    break;
                case DisplayItemKind.Pop:
                    break;
                default:
                    Add(ItemBounds(item), false);
                    break;
            }
        }
        // Layers never popped stay unbounded (null).
        foreach (var (index, _, _) in open)
            result[index] = null;
        return result;
    }

    private static (RectF?, bool Unknown) Reach(RectF? bounds, bool unknown, IReadOnlyList<Filter>? filters)
    {
        // A graph of SVG filter primitives (floods, offsets, inputs from other results) reaches as far as its last
        // primitive's subregion, which crops what it draws; without one its reach is not known.
        if (filters is { Count: > 0 } list && list.Any(f => f.Kind > FilterKind.DropShadow || f.In.Source != FilterSource.Previous || f.Subregion is not null))
            return list[^1].Subregion is { } region ? (region, false) : (null, true);
        foreach (var filter in filters ?? [])
        {
            if (filter.Kind == FilterKind.ColorMatrix && filter.Matrix is { Count: 20 } m && m[19] > 0)
                return (null, true); // it can make transparent pixels visible
            if (bounds is not { } r)
                continue;
            var blur = 3 * Math.Max(filter.StdDeviation, filter.Deviations is { } d ? Math.Max(d.X, d.Y) : 0);
            var grown = new RectF(r.X - blur, r.Y - blur, r.Width + 2 * blur, r.Height + 2 * blur);
            bounds = filter.Kind == FilterKind.DropShadow
                ? Union(r, grown with { X = grown.X + filter.Offset.X, Y = grown.Y + filter.Offset.Y })
                : filter.Kind == FilterKind.Blur ? grown : r;
        }
        return (bounds, unknown);
    }

    private static RectF ItemBounds(in DisplayItem item)
    {
        var r = item.Kind switch
        {
            DisplayItemKind.BoxShadow when item.Inset => item.Box.Rect,
            DisplayItemKind.BoxShadow => Grow(item.Shape.Rect, 3 * item.Blur),
            DisplayItemKind.Glyphs when item.Glyphs is { Origins.Length: > 0 } run => Grow(GlyphBounds(run), 3 * item.Blur),
            DisplayItemKind.Decoration => item.Shape.Rect with { Height = 3 * item.Shape.Rect.Height },
            DisplayItemKind.FillPath or DisplayItemKind.StrokePath when item.Path is { Commands.Count: > 0 } path => PathBounds(path),
            _ => item.Shape.Rect,
        };
        if (item.Kind == DisplayItemKind.StrokePath && item.Stroke is { } stroke)
            r = Grow(r, stroke.Width / 2 * Math.Max(stroke.MiterLimit, 1.5f));
        return Grow(r, 1); // antialiased edges
    }

    // Around the glyphs' origins: a size to the left, one and a half above, two to the right and a half below.
    private static RectF GlyphBounds(GlyphRun run)
    {
        var (min, max) = (run.Origins[0], run.Origins[0]);
        foreach (var o in run.Origins)
            (min, max) = (Vector2.Min(min, o), Vector2.Max(max, o));
        return new RectF(min.X - run.Size, min.Y - 1.5f * run.Size, max.X - min.X + 3 * run.Size, max.Y - min.Y + 2 * run.Size);
    }

    private static bool Intersects(RectF a, RectF b) => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static RectF PathBounds(PathData path)
    {
        var points = path.Commands.SelectMany(c => c.Verb == PathVerb.CubicTo ? new[] { c.P1, c.P2, c.P3 } : c.Verb == PathVerb.Close ? [] : new[] { c.P1 }).ToList();
        if (points.Count == 0)
            return default;
        var (minX, minY, maxX, maxY) = (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }

    private static RectF Grow(RectF r, float by) => new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    private static RectF Union(RectF a, RectF b)
    {
        var (x, y) = (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y));
        return new RectF(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    // The bounding box of a rectangle mapped by an affine transform.
    private static RectF Map(RectF r, Matrix3x2 m)
    {
        Vector2[] corners = [Vector2.Transform(new(r.X, r.Y), m), Vector2.Transform(new(r.Right, r.Y), m), Vector2.Transform(new(r.X, r.Bottom), m), Vector2.Transform(new(r.Right, r.Bottom), m)];
        var (minX, minY, maxX, maxY) = (corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }

    internal static Rgba ToRgba(CssColor color) => new(color.R, color.G, color.B, color.A);

    private enum Side { Top, Right, Bottom, Left }

    /// <summary>
    /// Paints a border between its outer edge and its padding edge (css-backgrounds-3 §4): each side in the region
    /// bounded by the diagonal joins at the corners, in its own style and colour.
    /// </summary>
    // ponytail: dots, and dashes around rounded corners, keep a fixed spacing (2 × width, and 3 × width) rather than
    // one fitted to each side.
    private static void PaintBorder(ICanvas canvas, RoundedRect outer, BorderGroup border)
    {
        // One solid colour all round: a single ring. Checked before anything is made: most boxes with a border, and
        // every cell of a table with collapsed borders, take this path.
        var (top, right, bottom, left) = (border.TopWidth, border.RightWidth, border.BottomWidth, border.LeftWidth);
        if (border is { TopStyle: BorderStyle.Solid, RightStyle: BorderStyle.Solid, BottomStyle: BorderStyle.Solid, LeftStyle: BorderStyle.Solid }
            && border.TopColor == border.RightColor && border.TopColor == border.BottomColor && border.TopColor == border.LeftColor
            && top > 0 && right > 0 && bottom > 0 && left > 0)
        {
            canvas.FillPath(Ring(outer, outer.Inset(top, right, bottom, left)), FillRule.EvenOdd, new Paint(ToRgba(border.TopColor)));
            return;
        }

        float[] widths = [top, right, bottom, left];
        BorderStyle[] styles = [border.TopStyle, border.RightStyle, border.BottomStyle, border.LeftStyle];
        CssColor[] colors = [border.TopColor, border.RightColor, border.BottomColor, border.LeftColor];
        var inner = outer.Inset(widths[0], widths[1], widths[2], widths[3]);

        // Each side owns the region between its outer edge and the joins from the outer corners through the inner
        // corners (css-backgrounds-3 §5.5). The joins run on into the box until they meet, so a side keeps the part of a
        // rounded border that curves inside the inner rectangle; i is where the four regions meet.
        static float Span(float size, float widths) => widths > 0 ? size / widths : float.PositiveInfinity;
        var o = outer.Rect;
        var t = MathF.Min(Span(o.Width, widths[1] + widths[3]), Span(o.Height, widths[0] + widths[2]));
        var i = new RectF(o.X + t * widths[3], o.Y + t * widths[0], o.Width - t * (widths[1] + widths[3]), o.Height - t * (widths[0] + widths[2]));
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
            // Square-cornered dashes run along each side from corner to corner, a dash at each end: 3 × width long with
            // gaps near 2 × width for thin borders, 2 × width long with gaps near the width from 3px up.
            case BorderStyle.Dashed when outer.Radii.IsZero:
                var o = outer.Rect;
                var (from, to) = side switch
                {
                    Side.Top => (new Vector2(o.X, o.Y + width / 2), new Vector2(o.Right, o.Y + width / 2)),
                    Side.Right => (new Vector2(o.Right - width / 2, o.Y), new Vector2(o.Right - width / 2, o.Bottom)),
                    Side.Bottom => (new Vector2(o.Right, o.Bottom - width / 2), new Vector2(o.X, o.Bottom - width / 2)),
                    _ => (new Vector2(o.X + width / 2, o.Bottom), new Vector2(o.X + width / 2, o.Y)),
                };
                var dash = width >= 3 ? 2 * width : 3 * width;
                var gap = DashGap(Vector2.Distance(from, to), dash, width >= 3 ? width : 2 * width);
                canvas.StrokePath(new PathData().MoveTo(from.X, from.Y).LineTo(to.X, to.Y), new Stroke(width, LineCap.Butt, [dash, gap]), new Paint(ToRgba(color)));
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

    // The gap that fits a whole number of dashes into the length with a dash at each end, of the two counts nearest
    // the preferred gap: the one whose gap is closer to it (or the longer gap when the shorter would vanish).
    private static float DashGap(float length, float dash, float gap)
    {
        var fewer = MathF.Floor((int)(length + gap) / (dash + gap));
        if (fewer < 2)
            return gap; // too short to fit two dashes: the stroke's own pattern
        var more = fewer + 1;
        var (fewerGap, moreGap) = ((length - fewer * dash) / (fewer - 1), (length - more * dash) / (more - 1));
        return moreGap <= 0 || MathF.Abs(fewerGap - gap) < MathF.Abs(moreGap - gap) ? fewerGap : moreGap;
    }

    private static PathData Ring(in RoundedRect outer, in RoundedRect inner) =>
        new PathData((outer.Radii.IsZero ? 5 : 10) + (inner.Radii.IsZero ? 5 : 10)).AddRoundedRect(outer).AddRoundedRect(inner);
}

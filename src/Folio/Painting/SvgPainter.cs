using System.Numerics;
using Folio.Css;
using Folio.Svg;

namespace Folio.Painting;

/// <summary>
/// Paints an SVG render tree into a display list (https://www.w3.org/TR/SVG2/render.html): children in document order,
/// each node's transform, clip and group opacity pushed around what it holds, and each shape's fill and stroke in its
/// paint order.
/// </summary>
internal static class SvgPainter
{
    /// <summary>Paints <paramref name="root"/> with its user space's origin at <paramref name="origin"/> on the canvas.</summary>
    public static void Paint(SvgRenderNode root, Vector2 origin, List<DisplayItem> items)
    {
        items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(origin)));
        Emit(root, items);
        items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    /// <summary>
    /// A CSS box's SVG clip path, its coordinates' origin at <paramref name="origin"/> on the canvas, as a path clip; null
    /// when it is more than one plain shape and is drawn with <see cref="PaintClipMask"/> instead.
    /// </summary>
    public static DisplayItem? ClipItem(SvgClipPath clip, Vector2 origin) =>
        ClipPathItem(clip with { Transform = clip.Transform * Matrix3x2.CreateTranslation(origin) });

    /// <summary>
    /// Draws a CSS box's SVG clip path as a destination-in layer over what the box painted, so that stays only inside
    /// the clip region; its own clip path is in the box's coordinates, like the region.
    /// </summary>
    public static void PaintClipMask(SvgClipPath clip, Vector2 origin, List<DisplayItem> items)
    {
        items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: BlendMode.DestinationIn));
        Paint(new SvgContainerNode(Matrix3x2.Identity, 1, [new SvgContainerNode(clip.Transform, 1, clip.Children)]) { ClipPath = clip.ClipPath }, origin, items);
        items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    private static void Emit(SvgRenderNode node, List<DisplayItem> items)
    {
        if (!System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
            return;
        var pushed = 0;
        void Push(DisplayItem item)
        {
            items.Add(item);
            pushed++;
        }

        if (!node.Transform.IsIdentity)
            Push(new DisplayItem(DisplayItemKind.PushTransform, Transform: node.Transform));
        // A mask groups the node in a layer of its own, which the mask is applied to once the node is drawn.
        if (node.Mask is not null)
            Push(new DisplayItem(DisplayItemKind.PushLayer));
        var beforeClip = pushed;
        if (node is SvgContainerNode { Clip: { } clip })
            Push(new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(new RectF(clip.X, clip.Y, clip.Width, clip.Height), default)));
        // A clip path of one plain shape clips with its path; any other is a mask of its region, drawn over the node's
        // content in a layer of its own and kept where it is opaque.
        var mask = node.ClipPath is { } clipPath && ClipPathItem(clipPath) is null ? clipPath : null;
        if (node.ClipPath is not null && mask is null)
            Push(ClipPathItem(node.ClipPath)!.Value);
        else if (mask is not null)
            Push(new DisplayItem(DisplayItemKind.PushLayer));
        var beforeContent = pushed;
        // A filter applies to the node before its clip path, mask and opacity
        // (https://drafts.csswg.org/filter-effects-1/#placement): one layer per filter, the last outermost, inside a
        // layer for the opacity.
        var opacity = node.Opacity;
        if (node.Filters is { } filters)
        {
            if (opacity < 1)
                Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: opacity));
            for (var i = filters.Count - 1; i >= 0; i--)
                Push(new DisplayItem(DisplayItemKind.PushLayer, Filters: filters[i]));
            opacity = 1;
        }

        switch (node)
        {
            case SvgContainerNode container:
                if (opacity < 1)
                    Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: opacity));
                foreach (var child in container.Children)
                    Emit(child, items);
                break;
            case SvgTextNode text:
                // One run takes the opacity into its colour; several are grouped, as they may overlap.
                var textFade = opacity;
                if (textFade < 1 && text.Runs.Count > 1)
                {
                    Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: textFade));
                    textFade = 1;
                }
                foreach (var run in text.Runs.Where(r => r.Paint.Pattern is null))
                {
                    var (color, gradient) = Paint(run.Paint, textFade);
                    items.Add(new DisplayItem(DisplayItemKind.Glyphs, Color: color, Gradient: gradient, Glyphs: new GlyphRun(run.Font, run.Size, run.Glyphs, run.Origins)));
                }
                break;
            case SvgShapeNode shape:
                // A shape with one paint takes its opacity into the paint's colour; with both, or with markers, they are
                // grouped.
                var fade = opacity;
                if (fade < 1 && (shape.Fill is not null && shape.Stroke is not null || shape.Markers is not null))
                {
                    Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: fade));
                    fade = 1;
                }
                var path = ToPathData(shape.Path);
                if (shape.StrokeFirst)
                    StrokeItem(shape, path, fade, items);
                if (shape.Fill is { Paint.Pattern: { } pattern } patterned)
                {
                    PatternFill(pattern, patterned, shape, path, fade, items);
                }
                else if (shape.Fill is { } fill)
                {
                    var (color, gradient) = Paint(fill.Paint, fade);
                    items.Add(new DisplayItem(DisplayItemKind.FillPath, Color: color, Gradient: gradient, Path: path,
                        Rule: fill.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero));
                }
                if (!shape.StrokeFirst)
                    StrokeItem(shape, path, fade, items);
                foreach (var marker in shape.Markers ?? [])
                    Emit(marker, items);
                break;
        }

        if (mask is not null)
        {
            for (; pushed > beforeContent; pushed--)
                items.Add(new DisplayItem(DisplayItemKind.Pop));
            items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: BlendMode.DestinationIn));
            Emit(new SvgContainerNode(mask.Transform, 1, mask.Children) { ClipPath = mask.ClipPath }, items);
            items.Add(new DisplayItem(DisplayItemKind.Pop));
        }
        if (node.Mask is { } svgMask)
        {
            for (; pushed > beforeClip; pushed--)
                items.Add(new DisplayItem(DisplayItemKind.Pop));
            MaskItems(svgMask, items);
        }
        for (var i = 0; i < pushed; i++)
            items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    /// <summary>
    /// Draws an SVG mask's content for a CSS box's mask layer, its coordinates' origin at <paramref name="origin"/>,
    /// clipped to the mask's region.
    /// </summary>
    public static void PaintMaskContent(SvgMask mask, Vector2 origin, List<DisplayItem> items)
    {
        items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(origin)));
        items.Add(new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(new RectF(mask.Region.X, mask.Region.Y, mask.Region.Width, mask.Region.Height), default)));
        Emit(new SvgContainerNode(mask.ContentTransform, 1, mask.Children), items);
        items.Add(new DisplayItem(DisplayItemKind.Pop));
        items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    // A mask over what was drawn: its content, in a destination-in layer clipped to its region. A luminance mask is drawn
    // over opaque black and turned into alpha, so its alpha is the luminance of its colour times its own alpha.
    private static void MaskItems(SvgMask mask, List<DisplayItem> items)
    {
        var region = new RoundedRect(new RectF(mask.Region.X, mask.Region.Y, mask.Region.Width, mask.Region.Height), default);
        items.Add(new DisplayItem(DisplayItemKind.PushLayer, Blend: BlendMode.DestinationIn));
        items.Add(new DisplayItem(DisplayItemKind.PushClip, region));
        if (mask.Luminance)
        {
            items.Add(new DisplayItem(DisplayItemKind.PushLayer, Filters: FilterPrimitives.LuminanceToAlpha));
            items.Add(new DisplayItem(DisplayItemKind.Fill, region, CssColor.Black));
        }
        Emit(new SvgContainerNode(mask.ContentTransform, 1, mask.Children), items);
        if (mask.Luminance)
            items.Add(new DisplayItem(DisplayItemKind.Pop));
        items.Add(new DisplayItem(DisplayItemKind.Pop));
        items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    // A clip path that is one shape with no clip path of its own, or nothing at all, as a path clip in the clipped
    // node's user space; null for one that needs a mask.
    private static DisplayItem? ClipPathItem(SvgClipPath clip)
    {
        if (clip.ClipPath is not null)
            return null;
        if (clip.Children.Count == 0)
            return new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(default, default));
        if (clip.Children is not [SvgShapeNode { ClipPath: null, Fill: { } fill } shape])
            return null;
        return new DisplayItem(DisplayItemKind.PushClip, Path: ToPathData(shape.Path, shape.Transform * clip.Transform),
            Rule: fill.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero);
    }

    private static void StrokeItem(SvgShapeNode shape, PathData path, float fade, List<DisplayItem> items)
    {
        if (shape.Stroke is not { } s || s.Paint.Pattern is not null)
            return;
        var cap = s.Cap switch { Style.StrokeLinecap.Round => LineCap.Round, Style.StrokeLinecap.Square => LineCap.Square, _ => LineCap.Butt };
        var join = s.Join switch { Style.StrokeLinejoin.Round => LineJoin.Round, Style.StrokeLinejoin.Bevel => LineJoin.Bevel, _ => LineJoin.Miter };
        var stroke = new Stroke(s.Width, cap, s.Dashes, join, s.MiterLimit, s.Dashes is null ? 0 : s.DashOffset);
        var (color, gradient) = Paint(s.Paint, fade);
        items.Add(new DisplayItem(DisplayItemKind.StrokePath, Color: color, Gradient: gradient, Path: path, Stroke: stroke));
    }

    /// <summary>The most tile contents one pattern fill draws (tiles times top-level nodes in the tile); a fill that would need more draws none.</summary>
    internal const int MaxPatternWork = 20_000;

    // A pattern fill: the shape's path clips copies of the tile laid edge to edge in pattern space over the shape's
    // bounding box, each clipped to its tile (overflow: hidden), in a layer when the fill is translucent.
    // ponytail: pattern strokes and text are not drawn; tiles are drawn one by one, not as a cached image shader.
    private static void PatternFill(SvgPattern pattern, SvgFill fill, SvgShapeNode shape, PathData path, float fade, List<DisplayItem> items)
    {
        if (SvgGeometry.Bounds(shape.Path) is not { } bounds || !Matrix3x2.Invert(pattern.Transform, out var inverse))
            return;
        var area = SvgGeometry.Transform(bounds, inverse);
        var tile = pattern.Tile;
        var (left, top) = (MathF.Floor((area.X - tile.X) / tile.Width), MathF.Floor((area.Y - tile.Y) / tile.Height));
        var (right, bottom) = (MathF.Ceiling((area.X + area.Width - tile.X) / tile.Width), MathF.Ceiling((area.Y + area.Height - tile.Y) / tile.Height));
        if (!float.IsFinite(left + top + right + bottom) || (double)(right - left) * (bottom - top) * pattern.Children.Count > MaxPatternWork)
            return;
        var opacity = fill.Paint.Color.A * fade;
        items.Add(new DisplayItem(DisplayItemKind.PushClip, Path: path, Rule: fill.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero));
        var turned = !pattern.Transform.IsIdentity;
        if (turned)
            items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: pattern.Transform));
        if (opacity < 1)
            items.Add(new DisplayItem(DisplayItemKind.PushLayer, Opacity: opacity));
        for (var row = top; row < bottom; row++)
        {
            for (var column = left; column < right; column++)
            {
                items.Add(new DisplayItem(DisplayItemKind.PushTransform, Transform: Matrix3x2.CreateTranslation(tile.X + column * tile.Width, tile.Y + row * tile.Height)));
                items.Add(new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(new RectF(0, 0, tile.Width, tile.Height), default)));
                Emit(new SvgContainerNode(pattern.Content, 1, pattern.Children), items);
                items.Add(new DisplayItem(DisplayItemKind.Pop));
                items.Add(new DisplayItem(DisplayItemKind.Pop));
            }
        }
        if (opacity < 1)
            items.Add(new DisplayItem(DisplayItemKind.Pop));
        if (turned)
            items.Add(new DisplayItem(DisplayItemKind.Pop));
        items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    private static CssColor Fade(CssColor color, float opacity) => opacity < 1 ? color with { A = color.A * opacity } : color;

    // A resolved paint as a display item's colour or gradient, faded by an opacity folded into it.
    private static (CssColor Color, Gradient? Gradient) Paint(SvgResolvedPaint paint, float opacity)
    {
        if (paint.Gradient is not { } g)
            return (Fade(paint.Color, opacity), null);
        var stops = g.Stops.Select(s => new GradientStop(s.Offset, DisplayListPlayer.ToRgba(Fade(s.Color, opacity)))).ToList();
        var spread = g.Spread switch { SvgSpreadMethod.Reflect => GradientSpread.Reflect, SvgSpreadMethod.Repeat => GradientSpread.Repeat, _ => GradientSpread.Pad };
        var gradient = g.Radial
            ? new Gradient(GradientKind.Radial, stops, spread, Center: g.P1, Radii: new Vector2(g.Radius, g.Radius),
                Focus: g.P2 != g.P1 || g.FocusRadius > 0 ? g.P2 : null, FocusRadius: g.FocusRadius, Transform: g.Transform)
            : new Gradient(GradientKind.Linear, stops, spread, g.P1, g.P2, Transform: g.Transform);
        return (CssColor.Black, gradient);
    }

    /// <summary>Normalised path segments (absolute M, L, C and Z) as a canvas path, mapped by a transform when given.</summary>
    public static PathData ToPathData(IReadOnlyList<PathSegment> segments, Matrix3x2? transform = null)
    {
        var m = transform ?? Matrix3x2.Identity;
        Vector2 P(Vector2 p) => Vector2.Transform(p, m);
        var path = new PathData();
        foreach (var segment in segments)
        {
            _ = segment.Verb switch
            {
                'M' => path.MoveTo(P(segment.P1).X, P(segment.P1).Y),
                'L' => path.LineTo(P(segment.P1).X, P(segment.P1).Y),
                'C' => path.CubicTo(P(segment.P1), P(segment.P2), P(segment.P3)),
                _ => path.Close(),
            };
        }
        return path;
    }
}

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
        if (node is SvgContainerNode { Clip: { } clip })
            Push(new DisplayItem(DisplayItemKind.PushClip, new RoundedRect(new RectF(clip.X, clip.Y, clip.Width, clip.Height), default)));

        switch (node)
        {
            case SvgContainerNode container:
                if (container.Opacity < 1)
                    Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: container.Opacity));
                foreach (var child in container.Children)
                    Emit(child, items);
                break;
            case SvgTextNode text:
                // One run takes the opacity into its colour; several are grouped, as they may overlap.
                var textFade = text.Opacity;
                if (textFade < 1 && text.Runs.Count > 1)
                {
                    Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: textFade));
                    textFade = 1;
                }
                foreach (var run in text.Runs)
                    items.Add(new DisplayItem(DisplayItemKind.Glyphs, Color: Fade(run.Color, textFade), Glyphs: new GlyphRun(run.Font, run.Size, run.Glyphs, run.Origins)));
                break;
            case SvgShapeNode shape:
                // A shape with one paint takes its opacity into the paint's colour; with both, they are grouped.
                var fade = shape.Opacity;
                if (fade < 1 && shape.Fill is not null && shape.Stroke is not null)
                {
                    Push(new DisplayItem(DisplayItemKind.PushLayer, Opacity: fade));
                    fade = 1;
                }
                var path = ToPathData(shape.Path);
                if (shape.StrokeFirst)
                    StrokeItem(shape, path, fade, items);
                if (shape.Fill is { } fill)
                    items.Add(new DisplayItem(DisplayItemKind.FillPath, Color: Fade(fill.Color, fade), Path: path,
                        Rule: fill.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero));
                if (!shape.StrokeFirst)
                    StrokeItem(shape, path, fade, items);
                break;
        }

        for (var i = 0; i < pushed; i++)
            items.Add(new DisplayItem(DisplayItemKind.Pop));
    }

    private static void StrokeItem(SvgShapeNode shape, PathData path, float fade, List<DisplayItem> items)
    {
        if (shape.Stroke is not { } s)
            return;
        var cap = s.Cap switch { Style.StrokeLinecap.Round => LineCap.Round, Style.StrokeLinecap.Square => LineCap.Square, _ => LineCap.Butt };
        var join = s.Join switch { Style.StrokeLinejoin.Round => LineJoin.Round, Style.StrokeLinejoin.Bevel => LineJoin.Bevel, _ => LineJoin.Miter };
        var stroke = new Stroke(s.Width, cap, s.Dashes, join, s.MiterLimit, s.Dashes is null ? 0 : s.DashOffset);
        items.Add(new DisplayItem(DisplayItemKind.StrokePath, Color: Fade(s.Color, fade), Path: path, Stroke: stroke));
    }

    private static CssColor Fade(CssColor color, float opacity) => opacity < 1 ? color with { A = color.A * opacity } : color;

    /// <summary>Normalised path segments (absolute M, L, C and Z) as a canvas path.</summary>
    public static PathData ToPathData(IReadOnlyList<PathSegment> segments)
    {
        var path = new PathData();
        foreach (var segment in segments)
        {
            _ = segment.Verb switch
            {
                'M' => path.MoveTo(segment.P1.X, segment.P1.Y),
                'L' => path.LineTo(segment.P1.X, segment.P1.Y),
                'C' => path.CubicTo(segment.P1, segment.P2, segment.P3),
                _ => path.Close(),
            };
        }
        return path;
    }
}

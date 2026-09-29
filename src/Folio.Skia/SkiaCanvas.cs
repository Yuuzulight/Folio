using Folio.Painting;
using SkiaSharp;

namespace Folio.Skia;

/// <summary>An <see cref="ICanvas"/> drawing onto a SkiaSharp canvas, antialiased, one CSS pixel per unit.</summary>
public sealed class SkiaCanvas(SKCanvas canvas) : ICanvas
{
    public void Save() => canvas.Save();

    public void Restore() => canvas.Restore();

    public void ClipRoundedRect(in RoundedRect rect)
    {
        using var skRect = ToSkia(rect);
        canvas.ClipRoundRect(skRect, SKClipOperation.Intersect, antialias: true);
    }

    public void ClipPath(PathData path, FillRule rule)
    {
        using var skPath = ToSkia(path, rule);
        canvas.ClipPath(skPath, SKClipOperation.Intersect, antialias: true);
    }

    public void FillRoundedRect(in RoundedRect rect, in Paint paint)
    {
        using var skRect = ToSkia(rect);
        using var skPaint = Fill(paint);
        canvas.DrawRoundRect(skRect, skPaint);
    }

    public void FillPath(PathData path, FillRule rule, in Paint paint)
    {
        using var skPath = ToSkia(path, rule);
        using var skPaint = Fill(paint);
        canvas.DrawPath(skPath, skPaint);
    }

    public void StrokePath(PathData path, in Stroke stroke, in Paint paint)
    {
        using var skPath = ToSkia(path, FillRule.NonZero);
        using var skPaint = Fill(paint);
        skPaint.Style = SKPaintStyle.Stroke;
        skPaint.StrokeWidth = stroke.Width;
        skPaint.StrokeCap = stroke.Cap == LineCap.Round ? SKStrokeCap.Round : SKStrokeCap.Butt;
        using var dash = stroke.Dashes is { Count: > 0 } d ? SKPathEffect.CreateDash([.. d], 0) : null;
        skPaint.PathEffect = dash;
        canvas.DrawPath(skPath, skPaint);
    }

    public void PushLayer(in LayerOptions options)
    {
        using var skPaint = new SKPaint { Color = SKColors.Black.WithAlpha(ToByte(options.Opacity)) };
        canvas.SaveLayer(skPaint);
    }

    public void PopLayer() => canvas.Restore();

    private static SKPaint Fill(in Paint paint) => new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = ToSkia(paint.Color) };

    private static SKColor ToSkia(Rgba c) => new(ToByte(c.R), ToByte(c.G), ToByte(c.B), ToByte(c.A));

    private static byte ToByte(float v) => (byte)MathF.Round(Math.Clamp(v, 0, 1) * 255);

    private static SKRoundRect ToSkia(in RoundedRect rect)
    {
        var (r, c) = (rect.Rect, rect.Radii);
        var skRect = new SKRoundRect();
        skRect.SetRectRadii(new SKRect(r.X, r.Y, r.Right, r.Bottom),
            [new(c.TopLeft.X, c.TopLeft.Y), new(c.TopRight.X, c.TopRight.Y), new(c.BottomRight.X, c.BottomRight.Y), new(c.BottomLeft.X, c.BottomLeft.Y)]);
        return skRect;
    }

    private static SKPath ToSkia(PathData path, FillRule rule)
    {
        using var builder = new SKPathBuilder { FillType = rule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
        foreach (var command in path.Commands)
        {
            switch (command.Verb)
            {
                case PathVerb.MoveTo:
                    builder.MoveTo(command.P1.X, command.P1.Y);
                    break;
                case PathVerb.LineTo:
                    builder.LineTo(command.P1.X, command.P1.Y);
                    break;
                case PathVerb.CubicTo:
                    builder.CubicTo(command.P1.X, command.P1.Y, command.P2.X, command.P2.Y, command.P3.X, command.P3.Y);
                    break;
                default:
                    builder.Close();
                    break;
            }
        }
        return builder.Detach();
    }
}

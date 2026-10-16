using System.Numerics;
using Folio.Painting;

namespace Folio.Scripting.Dom;

internal sealed partial class Canvas2DContext
{
    /// <summary>The font property: a value the CSS <c>font</c> shorthand accepts is kept, anything else is ignored.</summary>
    public void SetFont(string font)
    {
        if (font.Length <= PaintSource.MaxText && _styles?.Resolve(font) is not null)
            State.Font = font.Trim();
    }

    /// <summary>The filter property: <c>none</c> or a valid filter list is kept, anything else is ignored.</summary>
    public void SetFilter(string filter)
    {
        var text = filter.Trim();
        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            (State.Filter, State.FilterPrimitives) = ("none", null);
            return;
        }
        if (_styles is null || text.Length > PaintSource.MaxText)
            return;
        var (valid, primitives) = _styles.ResolveFilter(text, CurrentColor());
        if (valid)
            (State.Filter, State.FilterPrimitives) = (text, primitives);
    }

    // Text is drawn as one line: tabs, newlines and the like become spaces (HTML §4.12.5.1.10).
    private static string Flatten(string text) => text.Replace('\t', ' ').Replace('\n', ' ').Replace('\f', ' ').Replace('\r', ' ');

    private bool RightToLeft => State.Direction == "rtl";

    // How far the line's start is from the alignment point, and how far the baseline is below the vertical anchor.
    private (float X, float Y) Anchor(Hosting.CanvasTextLayout layout)
    {
        var align = State.TextAlign;
        if (align == "start")
            align = RightToLeft ? "right" : "left";
        else if (align == "end")
            align = RightToLeft ? "left" : "right";
        var x = align switch { "right" => -layout.Width, "center" => -layout.Width / 2, _ => 0 };
        var y = State.TextBaseline switch
        {
            "top" => layout.Ascent,
            "hanging" => layout.Ascent * 0.8f,
            "middle" => (layout.Ascent - layout.Descent) / 2,
            "ideographic" or "bottom" => -layout.Descent,
            _ => 0,
        };
        return (x, y);
    }

    /// <summary>fillText and strokeText (HTML §4.12.5.1.10).</summary>
    public void DrawText(string text, double x, double y, double? maxWidth, bool stroke)
    {
        if (!Finite(x, y) || maxWidth is { } limit && (double.IsNaN(limit) || limit <= 0) || _styles is null || _styles.Resolve(State.Font, State.LetterSpacing, State.WordSpacing, State.FontKerning) is not { } style)
            return;
        var layout = _styles.Layout(Flatten(text), style, RightToLeft);
        if (layout.Runs.Count == 0)
            return;
        var (offsetX, offsetY) = Anchor(layout);
        // Text wider than the maximum is squeezed horizontally about the alignment point.
        var squeeze = maxWidth is { } max && layout.Width > max ? (float)(max / layout.Width) : 1f;
        var place = Matrix3x2.CreateScale(squeeze, 1) * Matrix3x2.CreateTranslation((float)x, (float)y + offsetY);
        var strokeStyle = CurrentStroke();
        Render(stroke ? State.StrokeStyle : State.FillStyle, (canvas, paint) =>
        {
            canvas.Save();
            canvas.Transform(place);
            foreach (var run in layout.Runs)
            {
                var origins = new Vector2[run.Origins.Length];
                for (var i = 0; i < origins.Length; i++)
                    origins[i] = run.Origins[i] + new Vector2(offsetX, 0);
                if (stroke)
                    canvas.StrokeGlyphs(run.Font, run.Size, run.Glyphs, origins, strokeStyle, paint);
                else
                    canvas.DrawGlyphs(run.Font, run.Size, run.Glyphs, origins, paint);
            }
            canvas.Restore();
        });
    }

    /// <summary>measureText (HTML §4.12.5.1.10). The bounding boxes are the font's, not the ink of the glyphs.</summary>
    // ponytail: actualBoundingBox* use the font's ascent and descent and the advance width, not the glyph outlines.
    public CanvasTextMetrics MeasureText(string text)
    {
        if (_styles is null || _styles.Resolve(State.Font, State.LetterSpacing, State.WordSpacing, State.FontKerning) is not { } style)
            return new CanvasTextMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var layout = _styles.Layout(Flatten(text), style, RightToLeft);
        var (offsetX, offsetY) = Anchor(layout);
        return new CanvasTextMetrics(layout.Width, -offsetX, layout.Width + offsetX, layout.Ascent - offsetY, layout.Descent + offsetY,
            layout.Ascent - offsetY, layout.Descent + offsetY, layout.Ascent - offsetY, layout.Descent + offsetY);
    }
}

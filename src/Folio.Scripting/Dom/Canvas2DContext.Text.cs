using System.Buffers;
using System.Numerics;
using Folio.Painting;

namespace Folio.Scripting.Dom;

internal sealed partial class Canvas2DContext
{
    private readonly HashSet<string> _validFonts = new(StringComparer.Ordinal);

    /// <summary>The font property: a value the CSS <c>font</c> shorthand accepts is kept, anything else is ignored.</summary>
    public void SetFont(string font)
    {
        var trimmed = font.Trim();
        if (string.Equals(trimmed, State.Font, StringComparison.Ordinal))
            return;
        if (_validFonts.Contains(trimmed))
        {
            State.Font = trimmed;
            return;
        }
        if (font.Length <= PaintSource.MaxText && _styles?.Resolve(trimmed) is { } resolved)
        {
            if (_validFonts.Count < 128)
                _validFonts.Add(trimmed);
            var key = string.Concat(trimmed, "\0", State.LetterSpacing, "\0", State.WordSpacing, "\0", State.FontKerning);
            if (_textStyles.Count < 64 && !_textStyles.ContainsKey(key))
                _textStyles[key] = resolved;
            State.Font = trimmed;
        }
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

    private readonly Dictionary<(string Text, string Font, string LetterSpacing, string WordSpacing, string FontKerning, bool Rtl), Hosting.CanvasTextLayout> _textLayouts = new();

    private Hosting.CanvasTextLayout GetTextLayout(string text, Folio.Style.ComputedStyle style, bool rtl)
    {
        var key = (text, State.Font, State.LetterSpacing, State.WordSpacing, State.FontKerning, rtl);
        if (_textLayouts.TryGetValue(key, out var cached))
            return cached;
        var layout = _styles!.Layout(text, style, rtl);
        if (_textLayouts.Count < 128)
            _textLayouts[key] = layout;
        return layout;
    }

    /// <summary>fillText and strokeText (HTML §4.12.5.1.10).</summary>
    public void DrawText(string text, double x, double y, double? maxWidth, bool stroke)
    {
        if (State.GlobalAlpha <= 0 && !ShadowActive && (BlendOf(State.GlobalCompositeOperation) ?? BlendMode.Normal) == BlendMode.Normal)
            return;
        if (!Finite(x, y) || maxWidth is { } limit && (double.IsNaN(limit) || limit <= 0) || ResolveTextStyle() is not { } style)
            return;
        var layout = GetTextLayout(Flatten(text), style, RightToLeft);
        if (layout.Runs.Count == 0)
            return;
        var (offsetX, offsetY) = Anchor(layout);
        // Text wider than the maximum is squeezed horizontally about the alignment point.
        var squeeze = maxWidth is { } max && layout.Width > max ? (float)(max / layout.Width) : 1f;
        var state = State;
        if (!stroke && squeeze == 1f && state.Transform == Matrix3x2.Identity && !ShadowActive && state.FilterPrimitives is null && (BlendOf(state.GlobalCompositeOperation) ?? BlendMode.Normal) == BlendMode.Normal)
        {
            var paint = state.GetOrCreateFillPaint();
            var shift = new Vector2((float)x + offsetX, (float)y + offsetY);
            foreach (var run in layout.Runs)
            {
                var origins = ArrayPool<Vector2>.Shared.Rent(run.Origins.Length);
                try
                {
                    for (var i = 0; i < run.Origins.Length; i++)
                        origins[i] = run.Origins[i] + shift;
                    Canvas.DrawGlyphs(run.Font, run.Size, run.Glyphs, origins.AsSpan(0, run.Origins.Length), paint);
                }
                finally
                {
                    ArrayPool<Vector2>.Shared.Return(origins);
                }
            }
            Version++;
            return;
        }
        var place = Matrix3x2.CreateScale(squeeze, 1) * Matrix3x2.CreateTranslation((float)x, (float)y + offsetY);
        var strokeStyle = CurrentStroke();
        Render(stroke ? State.StrokeStyle : State.FillStyle, (canvas, paint) =>
        {
            canvas.Save();
            canvas.Transform(place);
            foreach (var run in layout.Runs)
            {
                var origins = ArrayPool<Vector2>.Shared.Rent(run.Origins.Length);
                try
                {
                    for (var i = 0; i < run.Origins.Length; i++)
                        origins[i] = run.Origins[i] + new Vector2(offsetX, 0);
                    if (stroke)
                        canvas.StrokeGlyphs(run.Font, run.Size, run.Glyphs, origins.AsSpan(0, run.Origins.Length), strokeStyle, paint);
                    else
                        canvas.DrawGlyphs(run.Font, run.Size, run.Glyphs, origins.AsSpan(0, run.Origins.Length), paint);
                }
                finally
                {
                    ArrayPool<Vector2>.Shared.Return(origins);
                }
            }
            canvas.Restore();
        });
    }

    /// <summary>measureText (HTML §4.12.5.1.10). The bounding boxes are the font's, not the ink of the glyphs.</summary>
    // ponytail: actualBoundingBox* use the font's ascent and descent and the advance width, not the glyph outlines.
    public CanvasTextMetrics MeasureText(string text)
    {
        if (ResolveTextStyle() is not { } style)
            return new CanvasTextMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var layout = GetTextLayout(Flatten(text), style, RightToLeft);
        var (offsetX, offsetY) = Anchor(layout);
        return new CanvasTextMetrics(layout.Width, -offsetX, layout.Width + offsetX, layout.Ascent - offsetY, layout.Descent + offsetY,
            layout.Ascent - offsetY, layout.Descent + offsetY, layout.Ascent - offsetY, layout.Descent + offsetY);
    }
}

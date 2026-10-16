using System.Globalization;
using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Imaging;
using Folio.Painting;

namespace Folio.Scripting.Dom;

/// <summary>A script's <c>ImageData</c>: its size, its pixels, and the <c>Uint8ClampedArray</c> a script sees them through.</summary>
internal sealed class CanvasImageData(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    /// <summary>The pixels at creation; a script's changes live in <see cref="Data"/> until it is read.</summary>
    public byte[] Pixels { get; } = pixels;

    /// <summary>The array a script reads and writes, made on first use.</summary>
    public ScriptValue? Data { get; set; }
}

/// <summary>The result of measureText.</summary>
internal sealed record CanvasTextMetrics(double Width, double ActualLeft, double ActualRight, double FontAscent, double FontDescent,
                                         double ActualAscent, double ActualDescent, double EmAscent, double EmDescent);

/// <summary>The host-object templates for 2D canvas: the context and the objects it makes (HTML §4.12.5).</summary>
internal sealed class CanvasTemplates
{
    // Larger bitmaps than this are refused rather than copied into script memory.
    private const long MaxImageDataBytes = 256L * 1024 * 1024;

    private readonly DomContext _context;
    private ScriptValue? _matrixFactory;

    public CanvasTemplates(DomContext context)
    {
        _context = context;
        Gradient = new HostObjectTemplate("CanvasGradient")
            .DefineMethod("addColorStop", (target, _, args) =>
            {
                Need(args, 2, "addColorStop");
                var offset = WebIdlConversions.ToUnrestrictedDouble(args[0]);
                if (!(offset >= 0 && offset <= 1))
                    throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The color stop offset must be between 0 and 1.");
                var text = WebIdlConversions.ToDOMString(args[1]);
                if (PaintSource.ParseColor(text, new Rgba(0, 0, 0, 1)) is not { } color)
                    throw WebIdlException.DomException(DomExceptionNames.SyntaxError, $"'{text}' is not a color.");
                ((CanvasGradient)target).AddColorStop(offset, color);
                return ScriptValue.Undefined;
            });
        Pattern = new HostObjectTemplate("CanvasPattern")
            .DefineMethod("setTransform", (target, _, args) =>
            {
                ((CanvasPattern)target).Transform = args.Length > 0 && MatrixOf(args[0]) is { } m ? m : Matrix3x2.Identity;
                return ScriptValue.Undefined;
            });
        ImageData = new HostObjectTemplate("ImageData")
            .DefineProperty("width", (target, _) => ScriptValue.FromNumber(((CanvasImageData)target).Width))
            .DefineProperty("height", (target, _) => ScriptValue.FromNumber(((CanvasImageData)target).Height))
            .DefineProperty("colorSpace", (_, _) => ScriptValue.FromString("srgb"))
            .DefineProperty("data", (target, _) => DataOf((CanvasImageData)target));
        TextMetrics = new HostObjectTemplate("TextMetrics")
            .DefineProperty("width", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).Width))
            .DefineProperty("actualBoundingBoxLeft", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).ActualLeft))
            .DefineProperty("actualBoundingBoxRight", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).ActualRight))
            .DefineProperty("fontBoundingBoxAscent", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).FontAscent))
            .DefineProperty("fontBoundingBoxDescent", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).FontDescent))
            .DefineProperty("actualBoundingBoxAscent", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).ActualAscent))
            .DefineProperty("actualBoundingBoxDescent", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).ActualDescent))
            .DefineProperty("emHeightAscent", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).EmAscent))
            .DefineProperty("emHeightDescent", (target, _) => ScriptValue.FromNumber(((CanvasTextMetrics)target).EmDescent));
        Path2D = DefinePathMethods(new HostObjectTemplate("Path2D"), pathOf: target => (CanvasPath)target, transformOf: _ => Matrix3x2.Identity)
            .DefineMethod("addPath", (target, _, args) =>
            {
                Need(args, 1, "addPath");
                if (!_context.Engine.TryGetHostTarget(args[0], out var other) || other is not CanvasPath path)
                    throw WebIdlException.TypeError("addPath requires a Path2D.");
                var m = args.Length > 1 && !args[1].IsNullOrUndefined ? MatrixOf(args[1]) : Matrix3x2.Identity;
                if (m is { } matrix)
                    ((CanvasPath)target).AddPath(path, matrix);
                return ScriptValue.Undefined;
            });
        Context = CreateContextTemplate();
    }

    public HostObjectTemplate Context { get; }

    public HostObjectTemplate Gradient { get; }

    public HostObjectTemplate Pattern { get; }

    public HostObjectTemplate ImageData { get; }

    public HostObjectTemplate TextMetrics { get; }

    public HostObjectTemplate Path2D { get; }

    /// <summary><c>new Path2D()</c>, <c>new Path2D(path)</c> and <c>new Path2D(svgPathData)</c>.</summary>
    public CanvasPath NewPath2D(ReadOnlySpan<ScriptValue> args)
    {
        if (args.Length == 0 || args[0].IsUndefined)
            return new CanvasPath();
        if (_context.Engine.TryGetHostTarget(args[0], out var other) && other is CanvasPath source)
            return new CanvasPath(source);
        var path = new CanvasPath();
        // Data with an error draws up to it, as SVG does (https://www.w3.org/TR/SVG2/paths.html#PathDataErrorHandling).
        if (PathDataParser.Parse(WebIdlConversions.ToDOMString(args[0]), upToError: true) is { } segments)
            path.AddSegments(segments);
        return path;
    }

    // ---------------------------------------------------------------- argument helpers

    private static void Need(ReadOnlySpan<ScriptValue> args, int count, string method)
    {
        if (args.Length < count)
            throw WebIdlException.TypeError($"{method} requires {count} argument{(count == 1 ? "" : "s")}, but only {args.Length} {(args.Length == 1 ? "was" : "were")} given.");
    }

    private static double Number(ReadOnlySpan<ScriptValue> args, int index) => WebIdlConversions.ToUnrestrictedDouble(args[index]);

    private static ScriptValue Done => ScriptValue.Undefined;

    // A transform from a DOMMatrix2DInit (a to f, or m11 to m42), or null if it is not one or has a non-finite entry.
    private Matrix3x2? MatrixOf(ScriptValue init)
    {
        if (!init.IsObject)
            return null;
        double Entry(string name, string alias, double fallback)
        {
            var value = _context.Engine.GetProperty(init, name);
            if (value.IsUndefined)
                value = _context.Engine.GetProperty(init, alias);
            return value.IsUndefined ? fallback : WebIdlConversions.ToUnrestrictedDouble(value);
        }
        var values = new[] { Entry("a", "m11", 1), Entry("b", "m12", 0), Entry("c", "m21", 0), Entry("d", "m22", 1), Entry("e", "m41", 0), Entry("f", "m42", 0) };
        return values.All(double.IsFinite) ? new Matrix3x2((float)values[0], (float)values[1], (float)values[2], (float)values[3], (float)values[4], (float)values[5]) : null;
    }

    private ScriptValue MatrixObject(Matrix3x2 m)
    {
        _matrixFactory ??= _context.Engine.Evaluate(
            "(function(a,b,c,d,e,f){return {a:a,b:b,c:c,d:d,e:e,f:f,m11:a,m12:b,m13:0,m14:0,m21:c,m22:d,m23:0,m24:0,m31:0,m32:0,m33:1,m34:0,m41:e,m42:f,m43:0,m44:1,is2D:true,isIdentity:a===1&&b===0&&c===0&&d===1&&e===0&&f===0};})");
        return _context.Engine.Call(_matrixFactory.Value, ScriptValue.Undefined,
            [ScriptValue.FromNumber(m.M11), ScriptValue.FromNumber(m.M12), ScriptValue.FromNumber(m.M21), ScriptValue.FromNumber(m.M22), ScriptValue.FromNumber(m.M31), ScriptValue.FromNumber(m.M32)]);
    }

    private ScriptValue DataOf(CanvasImageData image)
    {
        image.Data ??= _context.Engine.CreateUint8ClampedArray(image.Pixels);
        return image.Data.Value;
    }

    // The pixels of an ImageData as a script has left them.
    private byte[] PixelsOf(CanvasImageData image) =>
        image.Data is { } data && _context.Engine.TryGetBytes(data, out var bytes) && bytes.Length == image.Pixels.Length ? bytes : image.Pixels;

    private static FillRule RuleOf(ReadOnlySpan<ScriptValue> args, int index) =>
        args.Length > index && args[index].IsString && args[index].AsString() == "evenodd" ? FillRule.EvenOdd : FillRule.NonZero;

    // ---------------------------------------------------------------- paths (shared by the context and Path2D)

    private HostObjectTemplate DefinePathMethods(HostObjectTemplate t, Func<object, CanvasPath> pathOf, Func<object, Matrix3x2> transformOf)
    {
        t.DefineMethod("moveTo", (target, _, args) => { Need(args, 2, "moveTo"); pathOf(target).MoveTo(transformOf(target), Number(args, 0), Number(args, 1)); return Done; });
        t.DefineMethod("lineTo", (target, _, args) => { Need(args, 2, "lineTo"); pathOf(target).LineTo(transformOf(target), Number(args, 0), Number(args, 1)); return Done; });
        t.DefineMethod("closePath", (target, _, _) => { pathOf(target).ClosePath(); return Done; });
        t.DefineMethod("quadraticCurveTo", (target, _, args) =>
        {
            Need(args, 4, "quadraticCurveTo");
            pathOf(target).QuadraticCurveTo(transformOf(target), Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3));
            return Done;
        });
        t.DefineMethod("bezierCurveTo", (target, _, args) =>
        {
            Need(args, 6, "bezierCurveTo");
            pathOf(target).BezierCurveTo(transformOf(target), Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), Number(args, 5));
            return Done;
        });
        t.DefineMethod("arcTo", (target, _, args) =>
        {
            Need(args, 5, "arcTo");
            if (!pathOf(target).ArcTo(transformOf(target), Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4)))
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The radius provided is negative.");
            return Done;
        });
        t.DefineMethod("rect", (target, _, args) => { Need(args, 4, "rect"); pathOf(target).Rect(transformOf(target), Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3)); return Done; });
        t.DefineMethod("arc", (target, _, args) =>
        {
            Need(args, 5, "arc");
            if (!pathOf(target).Arc(transformOf(target), Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), args.Length > 5 && WebIdlConversions.ToBoolean(args[5])))
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The radius provided is negative.");
            return Done;
        });
        t.DefineMethod("ellipse", (target, _, args) =>
        {
            Need(args, 7, "ellipse");
            if (!pathOf(target).Ellipse(transformOf(target), Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), Number(args, 5), Number(args, 6), args.Length > 7 && WebIdlConversions.ToBoolean(args[7])))
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The radius provided is negative.");
            return Done;
        });
        t.DefineMethod("roundRect", (target, _, args) =>
        {
            Need(args, 4, "roundRect");
            var (x, y, w, h) = (Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3));
            var radii = RoundRectRadii(args.Length > 4 ? args[4] : ScriptValue.Undefined);
            if (double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(w) && double.IsFinite(h))
                pathOf(target).RoundRect(transformOf(target), x, y, w, h, radii);
            return Done;
        });
        return t;
    }

    // roundRect's radii (HTML §4.12.5.1.8): a number, a {x, y} point, or a list of one to four of either, for the corners
    // clockwise from the top left as CSS border-radius lists give them.
    private (double X, double Y)[] RoundRectRadii(ScriptValue value)
    {
        if (value.IsUndefined)
            return new (double, double)[4];
        var list = _context.Engine.TryGetArrayItems(value, out var items) ? items.ToArray() : [value];
        if (list.Length is 0 or > 4)
            throw WebIdlException.RangeError("roundRect takes between one and four radii.");
        var radii = list.Select(RadiusOf).ToArray();
        if (radii.Any(r => !double.IsFinite(r.X) || !double.IsFinite(r.Y)))
            return new (double, double)[4];
        if (radii.Any(r => r.X < 0 || r.Y < 0))
            throw WebIdlException.RangeError("A radius must not be negative.");
        return radii.Length switch
        {
            1 => [radii[0], radii[0], radii[0], radii[0]],
            2 => [radii[0], radii[1], radii[0], radii[1]],
            3 => [radii[0], radii[1], radii[2], radii[1]],
            _ => radii,
        };
    }

    private (double X, double Y) RadiusOf(ScriptValue value)
    {
        if (value.IsObject)
        {
            var (x, y) = (_context.Engine.GetProperty(value, "x"), _context.Engine.GetProperty(value, "y"));
            var rx = x.IsUndefined ? 0 : WebIdlConversions.ToUnrestrictedDouble(x);
            return (rx, y.IsUndefined ? 0 : WebIdlConversions.ToUnrestrictedDouble(y));
        }
        var r = WebIdlConversions.ToUnrestrictedDouble(value);
        return (r, r);
    }

    // ---------------------------------------------------------------- the context

    private Canvas2DContext Ctx(object target) =>
        ((CanvasElementState)target).Sync() ?? throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The canvas has no 2D context.");

    private HostObjectTemplate CreateContextTemplate()
    {
        var t = new HostObjectTemplate("CanvasRenderingContext2D");
        t.DefineProperty("canvas", (target, _) => _context.Wrap(((CanvasElementState)target).Element));

        // styles
        t.DefineProperty("fillStyle", (target, _) => StyleValue(Ctx(target).State.FillStyle), (target, _, value) => SetStyle(Ctx(target), value, stroke: false));
        t.DefineProperty("strokeStyle", (target, _) => StyleValue(Ctx(target).State.StrokeStyle), (target, _, value) => SetStyle(Ctx(target), value, stroke: true));
        t.DefineProperty("globalAlpha", (target, _) => ScriptValue.FromNumber(Ctx(target).State.GlobalAlpha), (target, _, value) =>
        {
            var alpha = WebIdlConversions.ToUnrestrictedDouble(value);
            if (alpha >= 0 && alpha <= 1)
                Ctx(target).State.GlobalAlpha = alpha;
        });
        t.DefineProperty("globalCompositeOperation", (target, _) => ScriptValue.FromString(Ctx(target).State.GlobalCompositeOperation), (target, _, value) =>
        {
            var operation = WebIdlConversions.ToDOMString(value);
            if (Canvas2DContext.BlendOf(operation) is not null)
                Ctx(target).State.GlobalCompositeOperation = operation;
        });
        t.DefineProperty("imageSmoothingEnabled", (target, _) => ScriptValue.FromBoolean(Ctx(target).State.ImageSmoothingEnabled),
            (target, _, value) => Ctx(target).State.ImageSmoothingEnabled = WebIdlConversions.ToBoolean(value));
        t.DefineProperty("imageSmoothingQuality", (target, _) => ScriptValue.FromString(Ctx(target).State.ImageSmoothingQuality), (target, _, value) =>
        {
            var quality = WebIdlConversions.ToDOMString(value);
            if (quality is "low" or "medium" or "high")
                Ctx(target).State.ImageSmoothingQuality = quality;
        });

        // line styles
        t.DefineProperty("lineWidth", (target, _) => ScriptValue.FromNumber(Ctx(target).State.LineWidth), (target, _, value) =>
        {
            var width = WebIdlConversions.ToUnrestrictedDouble(value);
            if (double.IsFinite(width) && width > 0)
                Ctx(target).State.LineWidth = width;
        });
        t.DefineProperty("lineCap", (target, _) => ScriptValue.FromString(Ctx(target).State.LineCap), (target, _, value) =>
        {
            var cap = WebIdlConversions.ToDOMString(value);
            if (cap is "butt" or "round" or "square")
                Ctx(target).State.LineCap = cap;
        });
        t.DefineProperty("lineJoin", (target, _) => ScriptValue.FromString(Ctx(target).State.LineJoin), (target, _, value) =>
        {
            var join = WebIdlConversions.ToDOMString(value);
            if (join is "round" or "bevel" or "miter")
                Ctx(target).State.LineJoin = join;
        });
        t.DefineProperty("miterLimit", (target, _) => ScriptValue.FromNumber(Ctx(target).State.MiterLimit), (target, _, value) =>
        {
            var limit = WebIdlConversions.ToUnrestrictedDouble(value);
            if (double.IsFinite(limit) && limit > 0)
                Ctx(target).State.MiterLimit = limit;
        });
        t.DefineProperty("lineDashOffset", (target, _) => ScriptValue.FromNumber(Ctx(target).State.LineDashOffset), (target, _, value) =>
        {
            var offset = WebIdlConversions.ToUnrestrictedDouble(value);
            if (double.IsFinite(offset))
                Ctx(target).State.LineDashOffset = offset;
        });
        t.DefineMethod("setLineDash", (target, _, args) =>
        {
            Need(args, 1, "setLineDash");
            var segments = _context.Engine.TryGetArrayItems(args[0], out var items) ? items.Select(WebIdlConversions.ToUnrestrictedDouble).ToArray() : [];
            Ctx(target).SetLineDash(segments);
            return Done;
        });
        t.DefineMethod("getLineDash", (target, _, _) =>
            _context.Engine.CreateArray(Ctx(target).State.LineDash.Select(ScriptValue.FromNumber).ToArray()));

        // shadows and filters
        t.DefineProperty("shadowBlur", (target, _) => ScriptValue.FromNumber(Ctx(target).State.ShadowBlur), (target, _, value) =>
        {
            var blur = WebIdlConversions.ToUnrestrictedDouble(value);
            if (double.IsFinite(blur) && blur >= 0)
                Ctx(target).State.ShadowBlur = blur;
        });
        t.DefineProperty("shadowColor", (target, _) => ScriptValue.FromString(PaintSource.Serialize(Ctx(target).State.ShadowColor)), (target, _, value) =>
        {
            var context = Ctx(target);
            if (PaintSource.ParseColor(WebIdlConversions.ToDOMString(value), context.CurrentColor()) is { } color)
                context.State.ShadowColor = color;
        });
        t.DefineProperty("shadowOffsetX", (target, _) => ScriptValue.FromNumber(Ctx(target).State.ShadowOffsetX), (target, _, value) =>
        {
            var offset = WebIdlConversions.ToUnrestrictedDouble(value);
            if (double.IsFinite(offset))
                Ctx(target).State.ShadowOffsetX = offset;
        });
        t.DefineProperty("shadowOffsetY", (target, _) => ScriptValue.FromNumber(Ctx(target).State.ShadowOffsetY), (target, _, value) =>
        {
            var offset = WebIdlConversions.ToUnrestrictedDouble(value);
            if (double.IsFinite(offset))
                Ctx(target).State.ShadowOffsetY = offset;
        });
        t.DefineProperty("filter", (target, _) => ScriptValue.FromString(Ctx(target).State.Filter), (target, _, value) => Ctx(target).SetFilter(WebIdlConversions.ToDOMString(value)));

        // state
        t.DefineMethod("save", (target, _, _) => { Ctx(target).Save(); return Done; });
        t.DefineMethod("restore", (target, _, _) => { Ctx(target).Restore(); return Done; });
        t.DefineMethod("reset", (target, _, _) => { ((CanvasElementState)target).Restart(); return Done; });
        t.DefineMethod("isContextLost", (_, _, _) => ScriptValue.False);
        t.DefineMethod("getContextAttributes", (target, _, _) =>
            _context.Engine.Evaluate(Ctx(target).Opaque ? "({alpha:false,desynchronized:false,colorSpace:'srgb',willReadFrequently:false})" : "({alpha:true,desynchronized:false,colorSpace:'srgb',willReadFrequently:false})"));

        // transforms
        t.DefineMethod("scale", (target, _, args) => { Need(args, 2, "scale"); Ctx(target).Scale(Number(args, 0), Number(args, 1)); return Done; });
        t.DefineMethod("rotate", (target, _, args) => { Need(args, 1, "rotate"); Ctx(target).Rotate(Number(args, 0)); return Done; });
        t.DefineMethod("translate", (target, _, args) => { Need(args, 2, "translate"); Ctx(target).Translate(Number(args, 0), Number(args, 1)); return Done; });
        t.DefineMethod("transform", (target, _, args) =>
        {
            Need(args, 6, "transform");
            Ctx(target).Transform(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), Number(args, 5));
            return Done;
        });
        t.DefineMethod("setTransform", (target, _, args) =>
        {
            var context = Ctx(target);
            if (args.Length >= 6)
                context.SetTransform(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), Number(args, 5));
            else if (args.Length == 1 || args.Length == 0)
            {
                var m = args.Length == 0 || args[0].IsNullOrUndefined ? Matrix3x2.Identity : MatrixOf(args[0]);
                if (m is { } matrix)
                    context.State.Transform = matrix;
            }
            else
                throw WebIdlException.TypeError("setTransform takes six numbers or a matrix.");
            return Done;
        });
        t.DefineMethod("getTransform", (target, _, _) => MatrixObject(Ctx(target).State.Transform));
        t.DefineMethod("resetTransform", (target, _, _) => { Ctx(target).ResetTransform(); return Done; });

        // rectangles
        t.DefineMethod("fillRect", (target, _, args) => { Need(args, 4, "fillRect"); Ctx(target).FillRect(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3)); return Done; });
        t.DefineMethod("strokeRect", (target, _, args) => { Need(args, 4, "strokeRect"); Ctx(target).StrokeRect(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3)); return Done; });
        t.DefineMethod("clearRect", (target, _, args) => { Need(args, 4, "clearRect"); Ctx(target).ClearRect(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3)); return Done; });

        // paths: the context's own path is in canvas coordinates, so the current transform maps what is added to it
        DefinePathMethods(t, target => Ctx(target).Path, target => Ctx(target).State.Transform);
        t.DefineMethod("beginPath", (target, _, _) => { Ctx(target).BeginPath(); return Done; });
        t.DefineMethod("fill", (target, _, args) =>
        {
            var (path, rule) = PathAndRule(args);
            Ctx(target).Fill(path, rule);
            return Done;
        });
        t.DefineMethod("stroke", (target, _, args) =>
        {
            Ctx(target).Stroke(args.Length > 0 ? Path2DOf(args[0]) : null);
            return Done;
        });
        t.DefineMethod("clip", (target, _, args) =>
        {
            var (path, rule) = PathAndRule(args);
            Ctx(target).Clip(path, rule);
            return Done;
        });
        t.DefineMethod("isPointInPath", (target, _, args) =>
        {
            Need(args, 2, "isPointInPath");
            var path = Path2DOf(args[0]);
            var offset = path is null ? 0 : 1;
            Need(args, offset + 2, "isPointInPath");
            return ScriptValue.FromBoolean(Ctx(target).IsPointInPath(path, Number(args, offset), Number(args, offset + 1), RuleOf(args, offset + 2)));
        });
        t.DefineMethod("isPointInStroke", (target, _, args) =>
        {
            Need(args, 2, "isPointInStroke");
            var path = Path2DOf(args[0]);
            var offset = path is null ? 0 : 1;
            Need(args, offset + 2, "isPointInStroke");
            return ScriptValue.FromBoolean(Ctx(target).IsPointInStroke(path, Number(args, offset), Number(args, offset + 1)));
        });

        // gradients and patterns
        t.DefineMethod("createLinearGradient", (_, _, args) =>
        {
            Need(args, 4, "createLinearGradient");
            return GradientObject(Canvas2DContext.CreateLinearGradient(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3)));
        });
        t.DefineMethod("createRadialGradient", (_, _, args) =>
        {
            Need(args, 6, "createRadialGradient");
            if (Number(args, 2) < 0 || Number(args, 5) < 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The radius provided is negative.");
            return GradientObject(Canvas2DContext.CreateRadialGradient(Number(args, 0), Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), Number(args, 5)));
        });
        t.DefineMethod("createConicGradient", (_, _, args) =>
        {
            Need(args, 3, "createConicGradient");
            return GradientObject(Canvas2DContext.CreateConicGradient(Number(args, 0), Number(args, 1), Number(args, 2)));
        });
        t.DefineMethod("createPattern", (_, _, args) =>
        {
            Need(args, 2, "createPattern");
            var repetition = args[1].IsNull ? "repeat" : WebIdlConversions.ToDOMString(args[1]);
            if (repetition == "")
                repetition = "repeat";
            if (repetition is not ("repeat" or "repeat-x" or "repeat-y" or "no-repeat"))
                throw WebIdlException.DomException(DomExceptionNames.SyntaxError, $"The provided type ('{repetition}') is not a valid repetition.");
            var image = ImageOf(args[0], "createPattern");
            if (image is null)
                return ScriptValue.Null;
            return _context.Engine.CreateHostObject(Pattern, new CanvasPattern(image, repetition is "repeat" or "repeat-x", repetition is "repeat" or "repeat-y"));
        });

        // images and pixels
        t.DefineMethod("drawImage", (target, _, args) =>
        {
            Need(args, 3, "drawImage");
            var context = Ctx(target);
            var image = ImageOf(args[0], "drawImage");
            if (image is null)
                return Done;
            switch (args.Length)
            {
                case 5:
                    context.DrawImage(image, 0, 0, image.Width, image.Height, Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4));
                    break;
                case >= 9:
                    context.DrawImage(image, Number(args, 1), Number(args, 2), Number(args, 3), Number(args, 4), Number(args, 5), Number(args, 6), Number(args, 7), Number(args, 8));
                    break;
                case 3:
                    context.DrawImage(image, 0, 0, image.Width, image.Height, Number(args, 1), Number(args, 2), image.Width, image.Height);
                    break;
                default:
                    throw WebIdlException.TypeError("drawImage takes 3, 5 or 9 arguments.");
            }
            return Done;
        });
        t.DefineMethod("createImageData", (_, _, args) =>
        {
            Need(args, 1, "createImageData");
            if (args.Length == 1)
            {
                if (!_context.Engine.TryGetHostTarget(args[0], out var other) || other is not CanvasImageData source)
                    throw WebIdlException.TypeError("createImageData requires an ImageData or a size.");
                return NewImageData(source.Width, source.Height);
            }
            var (w, h) = (Number(args, 0), Number(args, 1));
            return NewImageData(CheckedSize(w, "width"), CheckedSize(h, "height"));
        });
        t.DefineMethod("getImageData", (target, _, args) =>
        {
            Need(args, 4, "getImageData");
            var context = Ctx(target);
            // The arguments are IDL longs: NaN and infinities become 0, and a size of 0 is an error.
            long x = WebIdlConversions.ToInt32(args[0]), y = WebIdlConversions.ToInt32(args[1]), w = WebIdlConversions.ToInt32(args[2]), h = WebIdlConversions.ToInt32(args[3]);
            if (w == 0 || h == 0)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The source width or height is 0.");
            var (sw, sh) = ((int)Math.Abs(w), (int)Math.Abs(h));
            if ((long)sw * sh * 4 > MaxImageDataBytes)
                throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The image data would be too large.");
            var pixels = context.GetImageData((int)(w < 0 ? x + w : x), (int)(h < 0 ? y + h : y), sw, sh);
            return _context.Engine.CreateHostObject(ImageData, new CanvasImageData(sw, sh, pixels));
        });
        t.DefineMethod("putImageData", (target, _, args) =>
        {
            Need(args, 3, "putImageData");
            var context = Ctx(target);
            if (!_context.Engine.TryGetHostTarget(args[0], out var other) || other is not CanvasImageData image)
                throw WebIdlException.TypeError("putImageData requires an ImageData.");
            int dx = WebIdlConversions.ToInt32(args[1]), dy = WebIdlConversions.ToInt32(args[2]);
            int dirtyX = 0, dirtyY = 0, dirtyW = image.Width, dirtyH = image.Height;
            if (args.Length >= 7)
                (dirtyX, dirtyY, dirtyW, dirtyH) = (WebIdlConversions.ToInt32(args[3]), WebIdlConversions.ToInt32(args[4]), WebIdlConversions.ToInt32(args[5]), WebIdlConversions.ToInt32(args[6]));
            context.PutImageData(PixelsOf(image), image.Width, image.Height, dx, dy, dirtyX, dirtyY, dirtyW, dirtyH);
            return Done;
        });

        // text
        t.DefineProperty("font", (target, _) => ScriptValue.FromString(Ctx(target).State.Font), (target, _, value) => Ctx(target).SetFont(WebIdlConversions.ToDOMString(value)));
        t.DefineProperty("textAlign", (target, _) => ScriptValue.FromString(Ctx(target).State.TextAlign), (target, _, value) =>
        {
            var align = WebIdlConversions.ToDOMString(value);
            if (align is "start" or "end" or "left" or "right" or "center")
                Ctx(target).State.TextAlign = align;
        });
        t.DefineProperty("textBaseline", (target, _) => ScriptValue.FromString(Ctx(target).State.TextBaseline), (target, _, value) =>
        {
            var baseline = WebIdlConversions.ToDOMString(value);
            if (baseline is "top" or "hanging" or "middle" or "alphabetic" or "ideographic" or "bottom")
                Ctx(target).State.TextBaseline = baseline;
        });
        t.DefineProperty("direction", (target, _) => ScriptValue.FromString(Ctx(target).State.Direction), (target, _, value) =>
        {
            var direction = WebIdlConversions.ToDOMString(value);
            if (direction is "ltr" or "rtl" or "inherit")
                Ctx(target).State.Direction = direction;
        });
        t.DefineProperty("fontKerning", (target, _) => ScriptValue.FromString(Ctx(target).State.FontKerning), (target, _, value) =>
        {
            var kerning = WebIdlConversions.ToDOMString(value);
            if (kerning is "auto" or "normal" or "none")
                Ctx(target).State.FontKerning = kerning;
        });
        t.DefineProperty("letterSpacing", (target, _) => ScriptValue.FromString(Ctx(target).State.LetterSpacing), (target, _, value) =>
        {
            var spacing = WebIdlConversions.ToDOMString(value).Trim();
            if (spacing.Length <= 64 && Conditions.SupportsDeclaration("letter-spacing", spacing))
                Ctx(target).State.LetterSpacing = spacing;
        });
        t.DefineProperty("wordSpacing", (target, _) => ScriptValue.FromString(Ctx(target).State.WordSpacing), (target, _, value) =>
        {
            var spacing = WebIdlConversions.ToDOMString(value).Trim();
            if (spacing.Length <= 64 && Conditions.SupportsDeclaration("word-spacing", spacing))
                Ctx(target).State.WordSpacing = spacing;
        });
        t.DefineProperty("fontStretch", (target, _) => ScriptValue.FromString(Ctx(target).State.FontStretch), (target, _, value) => Ctx(target).State.FontStretch = WebIdlConversions.ToDOMString(value));
        t.DefineProperty("fontVariantCaps", (target, _) => ScriptValue.FromString(Ctx(target).State.FontVariantCaps), (target, _, value) => Ctx(target).State.FontVariantCaps = WebIdlConversions.ToDOMString(value));
        t.DefineProperty("textRendering", (target, _) => ScriptValue.FromString(Ctx(target).State.TextRendering), (target, _, value) => Ctx(target).State.TextRendering = WebIdlConversions.ToDOMString(value));
        t.DefineMethod("fillText", (target, _, args) => DrawText(Ctx(target), args, stroke: false));
        t.DefineMethod("strokeText", (target, _, args) => DrawText(Ctx(target), args, stroke: true));
        t.DefineMethod("measureText", (target, _, args) =>
        {
            Need(args, 1, "measureText");
            return _context.Engine.CreateHostObject(TextMetrics, Ctx(target).MeasureText(WebIdlConversions.ToDOMString(args[0])));
        });
        return t;
    }

    private ScriptValue DrawText(Canvas2DContext context, ReadOnlySpan<ScriptValue> args, bool stroke)
    {
        Need(args, 3, stroke ? "strokeText" : "fillText");
        var text = WebIdlConversions.ToDOMString(args[0]);
        var maxWidth = args.Length > 3 && !args[3].IsUndefined ? WebIdlConversions.ToUnrestrictedDouble(args[3]) : (double?)null;
        context.DrawText(text, Number(args, 1), Number(args, 2), maxWidth, stroke);
        return Done;
    }

    // A width or height for pixel data: an integer from 1 up, small enough to hold in memory.
    private static int CheckedSize(double value, string name)
    {
        if (!double.IsFinite(value))
            throw WebIdlException.TypeError($"The {name} is not a finite number.");
        var size = Math.Truncate(value);
        if (size == 0 || size > int.MaxValue / 8)
            throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, $"The source {name} is 0 or too large.");
        return (int)Math.Abs(size);
    }

    private ScriptValue NewImageData(int width, int height)
    {
        if ((long)width * height * 4 > MaxImageDataBytes)
            throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The image data would be too large.");
        return _context.Engine.CreateHostObject(ImageData, new CanvasImageData(width, height, new byte[width * height * 4]));
    }

    /// <summary><c>new ImageData(width, height)</c> and <c>new ImageData(data, width[, height])</c>.</summary>
    public ScriptValue ConstructImageData(ReadOnlySpan<ScriptValue> args)
    {
        Need(args, 1, "ImageData");
        if (args[0].IsNumber || args.Length == 1)
        {
            Need(args, 2, "ImageData");
            return NewImageData(CheckedSize(Number(args, 0), "width"), CheckedSize(Number(args, 1), "height"));
        }
        if (!_context.Engine.TryGetBytes(args[0], out var bytes))
            throw WebIdlException.TypeError("ImageData requires a Uint8ClampedArray.");
        Need(args, 2, "ImageData");
        var width = CheckedSize(Number(args, 1), "width");
        if (bytes.Length == 0 || bytes.Length % 4 != 0)
            throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The input data length is not a multiple of 4.");
        var rows = bytes.Length / 4;
        if (rows % width != 0)
            throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The input data length is not a multiple of (4 * width).");
        var height = rows / width;
        if (args.Length > 2 && !args[2].IsUndefined && CheckedSize(Number(args, 2), "height") != height)
            throw WebIdlException.DomException(DomExceptionNames.IndexSizeError, "The input data length is not equal to (4 * width * height).");
        var image = new CanvasImageData(width, height, bytes) { Data = args[0] };
        return _context.Engine.CreateHostObject(ImageData, image);
    }

    private ScriptValue GradientObject(CanvasGradient gradient)
    {
        var value = _context.Engine.CreateHostObject(Gradient, gradient);
        return value;
    }

    // ---------------------------------------------------------------- styles

    private ScriptValue StyleValue(PaintSource style) =>
        style.Script is { } script ? script : ScriptValue.FromString(style.Text);

    private void SetStyle(Canvas2DContext context, ScriptValue value, bool stroke)
    {
        PaintSource? source = null;
        if (value.IsObject && _context.Engine.TryGetHostTarget(value, out var target))
        {
            source = target switch
            {
                CanvasGradient gradient => new PaintSource(gradient) { Script = value },
                CanvasPattern pattern => new PaintSource(pattern) { Script = value },
                _ => null,
            };
        }
        else if (!value.IsObject)
        {
            if (PaintSource.ParseColor(WebIdlConversions.ToDOMString(value), context.CurrentColor()) is { } color)
                source = new PaintSource(color);
        }
        if (source is null)
            return;
        if (stroke)
            context.State.StrokeStyle = source;
        else
            context.State.FillStyle = source;
    }

    // ---------------------------------------------------------------- images, paths as arguments

    private CanvasPath? Path2DOf(ScriptValue value) =>
        value.IsObject && _context.Engine.TryGetHostTarget(value, out var target) && target is CanvasPath path ? path : null;

    private (CanvasPath? Path, FillRule Rule) PathAndRule(ReadOnlySpan<ScriptValue> args)
    {
        var path = args.Length > 0 ? Path2DOf(args[0]) : null;
        return (path, RuleOf(args, path is null ? 0 : 1));
    }

    // The pixels an image source draws: an img element that has loaded, or another canvas. Null draws nothing.
    private IImageHandle? ImageOf(ScriptValue source, string method)
    {
        if (!source.IsObject || !_context.Engine.TryGetHostTarget(source, out var target) || target is not ElementNode element)
            throw WebIdlException.TypeError($"{method}: the image source is not a supported type.");
        if (element.LocalName == "canvas")
        {
            var canvas = _context.GetCanvas(element);
            if (canvas.Width == 0 || canvas.Height == 0)
                throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The canvas has a width or height of 0.");
            return canvas.Snapshot();
        }
        if (element.LocalName == "img")
            return _context.ImageOf(element);
        throw WebIdlException.TypeError($"{method}: the image source is not a supported type.");
    }
}

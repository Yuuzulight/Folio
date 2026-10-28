using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Folio.Imaging;
using Folio.Typography;

namespace Folio.Painting.Software;

/// <summary>
/// An <see cref="ICanvas"/> that draws on a bitmap in Folio's own code: antialiased paths, strokes, clips, gradients,
/// image fills, text from glyph outlines, and layers with opacity and blend modes. Coordinates are CSS pixels, one per device pixel.
/// </summary>
internal sealed class SoftwareCanvas : ICanvas
{
    private const float FlattenTolerance = 0.05f;

    private sealed class State
    {
        public Matrix33 Transform = Matrix33.Identity;
        public IntRect Clip;
        public byte[]? ClipMask; // coverage over Clip, null when everything inside Clip is fully inside

        public State Copy() => new() { Transform = Transform, Clip = Clip, ClipMask = ClipMask };
    }

    // A layer is a bitmap covering `Bounds` (device pixels), which may reach past the canvas so that a filter has the content around its edges.
    private sealed record Layer(PixelSurface Surface, IntRect Bounds, PixelSurface Parent, IntRect ParentBounds, LayerOptions Options, State Saved);

    private readonly PixelSurface _root;
    private PixelSurface _target;
    private IntRect _targetBounds; // the device pixels the target covers: its top left is device (Left, Top)
    private State _state;
    private readonly Stack<State> _saved = new();
    private readonly Stack<Layer> _layers = new();

    public SoftwareCanvas(PixelSurface surface)
    {
        _root = _target = surface;
        _targetBounds = new IntRect(0, 0, surface.Width, surface.Height);
        _state = new State { Clip = new IntRect(0, 0, surface.Width, surface.Height) };
    }

    // ---- state ----

    public void Save() => _saved.Push(_state.Copy());

    public void Restore()
    {
        if (_saved.Count > 0)
            _state = _saved.Pop();
    }

    public void Transform(in Matrix3x2 matrix) => _state.Transform = Matrix33.FromAffine(matrix) * _state.Transform;

    public void Transform(in Matrix4x4 matrix) => _state.Transform = Matrix33.FromProjective(matrix) * _state.Transform;

    private float Scale()
    {
        var m = _state.Transform.Affine;
        return Math.Max(MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12), MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22));
    }

    // ---- clipping ----

    public void ClipRoundedRect(in RoundedRect rect) => ClipPath(new PathData().AddRoundedRect(rect), FillRule.NonZero);

    public void ClipPath(PathData path, FillRule rule)
    {
        var polygons = PathGeometry.Polygons(PathGeometry.Flatten(path, FlattenTolerance / Math.Max(Scale(), 1e-3f)), _state.Transform);
        var coverage = Rasterizer.Fill(polygons, rule, _state.Clip);
        if (coverage is null)
        {
            _state.Clip = new IntRect(0, 0, 0, 0);
            _state.ClipMask = null;
            return;
        }
        IntersectClip(coverage);
    }

    private void IntersectClip(Coverage coverage)
    {
        var old = _state.Clip;
        var bounds = IntRect.Intersect(old, coverage.Bounds);
        if (bounds.IsEmpty)
        {
            _state.Clip = new IntRect(0, 0, 0, 0);
            _state.ClipMask = null;
            return;
        }
        var mask = new byte[bounds.Width * bounds.Height];
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            for (var x = bounds.Left; x < bounds.Right; x++)
            {
                var c = coverage.At(x, y);
                if (_state.ClipMask is { } existing)
                    c = (byte)((c * existing[(y - old.Top) * old.Width + (x - old.Left)] + 127) / 255);
                mask[(y - bounds.Top) * bounds.Width + (x - bounds.Left)] = c;
            }
        }
        _state.Clip = bounds;
        _state.ClipMask = mask;
    }

    private float ClipAt(int x, int y)
    {
        if (_state.ClipMask is not { } mask)
            return 1;
        return mask[(y - _state.Clip.Top) * _state.Clip.Width + (x - _state.Clip.Left)] / 255f;
    }

    // ---- filling ----

    public void FillRoundedRect(in RoundedRect rect, in Paint paint)
    {
        // Plain rectangles are by far the most common primitive in dashboard frames. Avoid constructing and
        // flattening a PathData when there are no corner curves; the rasterizer already has an exact rectangle path.
        if (rect.Radii.IsZero)
        {
            if (TryFillOpaqueAxisAlignedRect(rect.Rect, paint))
                return;
            var r = rect.Rect;
            var polygons = new[]
            {
                new[]
                {
                    _state.Transform.Apply(new Vector2(r.X, r.Y)),
                    _state.Transform.Apply(new Vector2(r.Right, r.Y)),
                    _state.Transform.Apply(new Vector2(r.Right, r.Bottom)),
                    _state.Transform.Apply(new Vector2(r.X, r.Bottom)),
                },
            };
            if (Rasterizer.Fill(polygons, FillRule.NonZero, ClipForPaint(paint), snapEdges: true) is { } coverage)
                Draw(coverage, paint);
            return;
        }
        FillPath(new PathData().AddRoundedRect(rect), FillRule.NonZero, paint);
    }

    private bool TryFillOpaqueAxisAlignedRect(RectF rect, in Paint paint)
    {
        if (paint.Blur > 0 || paint.Gradient is not null || paint.Pattern is not null || paint.Blend != BlendMode.Normal
            || paint.Color.A < 1 || _state.ClipMask is not null || !_state.Transform.IsAffine)
            return false;
        var m = _state.Transform;
        if (m.M12 != 0 || m.M21 != 0 || m.M11 <= 0 || m.M22 <= 0)
            return false;
        var topLeft = m.Apply(new Vector2(rect.X, rect.Y));
        var bottomRight = m.Apply(new Vector2(rect.Right, rect.Bottom));
        if (!Integral(topLeft.X) || !Integral(topLeft.Y) || !Integral(bottomRight.X) || !Integral(bottomRight.Y))
            return false;
        var area = IntRect.Intersect(_state.Clip, _targetBounds);
        area = IntRect.Intersect(area, new IntRect((int)topLeft.X, (int)topLeft.Y,
            (int)bottomRight.X, (int)bottomRight.Y));
        // The target is a tightly packed RGBA8 surface.  Once the geometry and paint
        // are known to be an integral opaque rectangle, fill whole pixels as words;
        // calling the general compositor for every pixel adds needless bounds and
        // floating-point work to the hottest dashboard path.
        var packed = (uint)To8(paint.Color.R)
            | ((uint)To8(paint.Color.G) << 8)
            | ((uint)To8(paint.Color.B) << 16)
            | 0xFF000000u;
        var words = MemoryMarshal.Cast<byte, uint>(_target.Data.AsSpan());
        for (var y = area.Top; y < area.Bottom; y++)
        {
            var first = (y - _targetBounds.Top) * _target.Width + (area.Left - _targetBounds.Left);
            words.Slice(first, area.Width).Fill(packed);
        }
        return true;

        static bool Integral(float value) => MathF.Abs(value - MathF.Round(value)) < 1e-4f;
        static byte To8(float value) => value <= 0 ? (byte)0 : value >= 1 ? (byte)255 : (byte)(value * 255 + 0.5f);
    }

    public void ClearRect(in RectF rect)
    {
        var polygons = PathGeometry.Polygons(PathGeometry.Flatten(new PathData().AddRoundedRect(new RoundedRect(rect, default)), FlattenTolerance), _state.Transform);
        if (Rasterizer.Fill(polygons, FillRule.NonZero, _state.Clip) is not { } coverage)
            return;
        ForEachPixel(coverage, (x, y, c, offset) =>
        {
            var keep = 1 - c;
            for (var k = 0; k < 4; k++)
                _target.Data[offset + k] = (byte)(_target.Data[offset + k] * keep + 0.5f);
        });
    }

    public void FillPath(PathData path, FillRule rule, in Paint paint)
    {
        var polygons = PathGeometry.Polygons(PathGeometry.Flatten(path, FlattenTolerance / Math.Max(Scale(), 1e-3f)), _state.Transform);
        if (Rasterizer.Fill(polygons, rule, ClipForPaint(paint), snapEdges: true) is { } coverage)
            Draw(coverage, paint);
    }

    public void StrokePath(PathData path, in Stroke stroke, in Paint paint)
    {
        var scale = Math.Max(Scale(), 1e-3f);
        var subpaths = PathGeometry.Flatten(path, FlattenTolerance / scale);
        var pieces = PathGeometry.Stroke(subpaths, stroke, FlattenTolerance / scale);
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            for (var k = 0; k < piece.Length; k++)
                piece[k] = _state.Transform.Apply(piece[k]);
        }
        if (Rasterizer.Fill(pieces, FillRule.NonZero, ClipForPaint(paint), snapEdges: true) is { } coverage)
            Draw(coverage, paint);
    }

    // A blurred fill spreads past the shape, so the area worked in is the clip grown by what the blur reaches.
    private IntRect ClipForPaint(in Paint paint)
    {
        if (!(paint.Blur > 0))
            return _state.Clip;
        var reach = (int)MathF.Ceiling(paint.Blur * Scale() * 3) + 2;
        return new IntRect(_state.Clip.Left - reach, _state.Clip.Top - reach, _state.Clip.Right + reach, _state.Clip.Bottom + reach);
    }

    private void Draw(Coverage coverage, in Paint paint)
    {
        if (paint.Blur > 0)
            coverage = Blurred(coverage, paint.Blur);
        var shader = Shader.For(paint, _state.Transform);
        // A gradient's alpha is its stops'; any other fill carries the paint's alpha.
        var alpha = paint.Gradient is null ? (paint.Pattern is null ? 1f : Math.Clamp(paint.Color.A, 0f, 1f)) : 1f;
        var blend = paint.Blend;
        if (shader is SolidShader solid)
        {
            BlendSolid(coverage, solid.Color, blend);
            return;
        }
        ForEachPixel(coverage, (x, y, c, offset) => Compositor.Blend(_target.Data, offset, shader.ColorAt(x, y) * alpha, c, blend));
    }

    // Keep the common solid-paint path allocation-free: dashboard backgrounds, borders and text shadows
    // account for most software-raster pixels, so avoiding a delegate per primitive materially reduces replay cost.
    private void BlendSolid(Coverage coverage, Vector4 color, BlendMode blend)
    {
        var area = IntRect.Intersect(IntRect.Intersect(coverage.Bounds, _state.Clip), _targetBounds);
        if (_state.ClipMask is null)
        {
            var opaque = blend == BlendMode.Normal && color.W >= 1f;
            var words = opaque ? MemoryMarshal.Cast<byte, uint>(_target.Data.AsSpan()) : default;
            var packed = opaque
                ? (uint)To8(color.X) | ((uint)To8(color.Y) << 8) | ((uint)To8(color.Z) << 16) | 0xFF000000u
                : 0u;
            for (var y = area.Top; y < area.Bottom; y++)
            {
                var row = (y - coverage.Bounds.Top) * coverage.Bounds.Width - coverage.Bounds.Left;
                var x = area.Left;
                while (x < area.Right)
                {
                    var c = coverage.Data[row + x];
                    if (c == 255 && opaque)
                    {
                        var runStart = x;
                        do x++; while (x < area.Right && coverage.Data[row + x] == 255);
                        var first = (y - _targetBounds.Top) * _target.Width + (runStart - _targetBounds.Left);
                        words.Slice(first, x - runStart).Fill(packed);
                        continue;
                    }
                    if (c != 0)
                    {
                        var offset = ((y - _targetBounds.Top) * _target.Width + (x - _targetBounds.Left)) * 4;
                        Compositor.Blend(_target.Data, offset, color, c / 255f, blend);
                    }
                    x++;
                }
            }
            return;
        }
        for (var y = area.Top; y < area.Bottom; y++)
        {
            var row = (y - coverage.Bounds.Top) * coverage.Bounds.Width - coverage.Bounds.Left;
            for (var x = area.Left; x < area.Right; x++)
            {
                var c = coverage.Data[row + x];
                if (c == 0)
                    continue;
                var amount = c / 255f * ClipAt(x, y);
                if (amount > 0)
                    Compositor.Blend(_target.Data, ((y - _targetBounds.Top) * _target.Width + (x - _targetBounds.Left)) * 4, color, amount, blend);
            }
        }

        static byte To8(float value) => value <= 0 ? (byte)0 : value >= 1 ? (byte)255 : (byte)(value * 255 + 0.5f);
    }

    // A mask blur's deviation is scaled by the square root of the area the transform stretches by, the same in both directions
    // (as the Skia backend's is: a per-axis scale was measured to differ from it under a stretch).
    private Coverage Blurred(Coverage coverage, float sigma)
    {
        var m = _state.Transform.Affine;
        var sigmaX = sigma * MathF.Sqrt(MathF.Abs(m.M11 * m.M22 - m.M12 * m.M21));
        var sigmaY = sigmaX;
        if (!(sigmaX > 0 || sigmaY > 0))
            return coverage;
        var (reachX, reachY) = ((int)MathF.Ceiling(sigmaX * 3), (int)MathF.Ceiling(sigmaY * 3));
        var bounds = new IntRect(coverage.Bounds.Left - reachX, coverage.Bounds.Top - reachY, coverage.Bounds.Right + reachX, coverage.Bounds.Bottom + reachY);
        var width = bounds.Width;
        var data = new byte[width * bounds.Height];
        for (var y = 0; y < coverage.Bounds.Height; y++)
            Array.Copy(coverage.Data, y * coverage.Bounds.Width, data, (y + reachY) * width + reachX, coverage.Bounds.Width);
        return new Coverage(bounds, GaussianBlur.Apply(data, width, bounds.Height, 1, sigmaX, sigmaY));
    }

    // Calls back for every pixel of the coverage inside the clip that it touches, with its coverage (clip included) 0 to 1 and the
    // offset of its pixel in the target.
    private void ForEachPixel(Coverage coverage, Action<int, int, float, int> action)
    {
        var area = IntRect.Intersect(IntRect.Intersect(coverage.Bounds, _state.Clip), _targetBounds);
        if (_state.ClipMask is null)
        {
            for (var y = area.Top; y < area.Bottom; y++)
            {
                var row = (y - coverage.Bounds.Top) * coverage.Bounds.Width - coverage.Bounds.Left;
                for (var x = area.Left; x < area.Right; x++)
                {
                    var c = coverage.Data[row + x];
                    if (c != 0)
                        action(x, y, c / 255f, ((y - _targetBounds.Top) * _target.Width + (x - _targetBounds.Left)) * 4);
                }
            }
            return;
        }
        for (var y = area.Top; y < area.Bottom; y++)
        {
            var row = (y - coverage.Bounds.Top) * coverage.Bounds.Width - coverage.Bounds.Left;
            for (var x = area.Left; x < area.Right; x++)
            {
                var c = coverage.Data[row + x];
                if (c == 0)
                    continue;
                var amount = c / 255f * ClipAt(x, y);
                if (amount > 0)
                    action(x, y, amount, ((y - _targetBounds.Top) * _target.Width + (x - _targetBounds.Left)) * 4);
            }
        }
    }

    // ---- text ----

    // Keep the native outline cache bounded per font.  The cache is shared by canvases for
    // the same face, but a face can be released as soon as the font itself is no longer
    // reachable through the ConditionalWeakTable.  Eight MiB is the documented default
    // backend glyph-cache budget; entries are evicted least-recently-used when a font's
    // parsed outlines exceed that budget.
    private const long OutlineCacheBudget = 8L * 1024 * 1024;
    private sealed class OutlineCache
    {
        private readonly Dictionary<ushort, (PathData? Path, long Bytes, LinkedListNode<ushort> Node)> _entries = [];
        private readonly LinkedList<ushort> _lru = [];
        private long _bytes;

        public PathData? Get(FontFace face, ushort glyph)
        {
            lock (_entries)
            {
                if (_entries.TryGetValue(glyph, out var cached))
                {
                    _lru.Remove(cached.Node);
                    cached.Node = _lru.AddFirst(glyph);
                    _entries[glyph] = cached;
                    return cached.Path;
                }

                var path = GlyphOutlines.Get(face, glyph);
                var bytes = Estimate(path);
                var node = _lru.AddFirst(glyph);
                _entries[glyph] = (path, bytes, node);
                _bytes += bytes;
                while (_bytes > OutlineCacheBudget && _lru.Last is { } last)
                {
                    var evictedGlyph = last.Value;
                    _lru.RemoveLast();
                    if (_entries.Remove(evictedGlyph, out var evicted))
                        _bytes -= evicted.Bytes;
                }
                return path;
            }
        }

        private static long Estimate(PathData? path) => path is null
            ? 128
            : 256L + path.Commands.Count * 64L;
    }

    private static readonly ConditionalWeakTable<FontFace, OutlineCache> Outlines = new();

    // Curve flattening is deterministic for a face, glyph and tolerance. Keep a
    // small per-face cache so animation frames do not repeatedly subdivide the
    // same outlines. The bound prevents a large font corpus from retaining all
    // tolerances indefinitely.
    private sealed class FlattenedGlyphCache
    {
        private readonly Dictionary<(ushort Glyph, int ToleranceBits), Subpath[]> _entries = [];
        private long _bytes;

        public Subpath[] Get(FontFace face, ushort glyph, float tolerance)
        {
            var key = (glyph, BitConverter.SingleToInt32Bits(tolerance));
            lock (_entries)
            {
                if (_entries.TryGetValue(key, out var cached))
                    return cached;
                var flattened = Outlines.GetOrCreateValue(face).Get(face, glyph) is { } path
                    ? PathGeometry.Flatten(path, tolerance).ToArray()
                    : [];
                var bytes = Estimate(flattened);
                if (bytes > OutlineCacheBudget)
                    return flattened;
                while (_bytes + bytes > OutlineCacheBudget && _entries.Count > 0)
                {
                    var evictedKey = _entries.Keys.First();
                    _bytes -= Estimate(_entries[evictedKey]);
                    _entries.Remove(evictedKey);
                }
                _entries[key] = flattened;
                _bytes += bytes;
                return flattened;
            }
        }

        private static long Estimate(Subpath[] paths)
        {
            var bytes = 128L;
            foreach (var path in paths)
                bytes += 64L + path.Points.Length * 8L;
            return bytes;
        }
    }

    private static readonly ConditionalWeakTable<FontFace, FlattenedGlyphCache> FlattenedGlyphs = new();

    internal static Subpath[] Flattened(FontFace face, ushort glyph, float tolerance) =>
        FlattenedGlyphs.GetOrCreateValue(face).Get(face, glyph, tolerance);

    private PathData? Outline(IFontHandle font, ushort glyph)
    {
        if (font is not FontFace face)
            return null;
        return Outlines.GetOrCreateValue(face).Get(face, glyph);
    }

    // The outlines of a run, in the space the run is drawn in (y down), as subpaths.
    private List<Subpath> Glyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins)
    {
        var result = new List<Subpath>();
        if (font is not FontFace face || !(size > 0))
            return result;
        var scale = size / face.UnitsPerEm;
        var tolerance = FlattenTolerance / Math.Max(scale * Math.Max(Scale(), 1e-3f), 1e-6f);
        for (var i = 0; i < glyphs.Length && i < origins.Length; i++)
        {
            var matrix = Matrix3x2.CreateScale(scale, -scale) * Matrix3x2.CreateTranslation(SnapToTextGrid(origins[i]));
            foreach (var subpath in Flattened(face, glyphs[i], tolerance))
            {
                var points = new Vector2[subpath.Points.Length];
                for (var k = 0; k < points.Length; k++)
                    points[k] = Vector2.Transform(subpath.Points[k], matrix);
                result.Add(new Subpath(points, subpath.Closed));
            }
        }
        return result;
    }

    // Text placed on a flat screen sits on whole pixel rows and on quarter-pixel columns, as the Skia backend places it
    // (subpixel positioning with four horizontal positions); rotated or skewed text is placed exactly.
    private Vector2 SnapToTextGrid(Vector2 origin)
    {
        var m = _state.Transform;
        if (!m.IsAffine || m.M12 != 0 || m.M21 != 0 || m.M11 <= 0 || m.M22 <= 0 || m.M33 != 1)
            return origin;
        var device = m.Apply(origin);
        var snapped = new Vector2(MathF.Floor(device.X * 4 + 0.5f) / 4, MathF.Floor(device.Y + 0.5f));
        return origin + new Vector2((snapped.X - device.X) / m.M11, (snapped.Y - device.Y) / m.M22);
    }

    public void DrawGlyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint)
    {
        if (font is FontFace colorFace && ColorGlyphs.Has(colorFace) && HasRenderableColorGlyph(colorFace, glyphs))
        {
            DrawColorGlyphs(colorFace, size, glyphs, origins, paint);
            return;
        }
        if (font is FontFace colorFace2 && BitmapGlyphs.Has(colorFace2))
        {
            DrawBitmapGlyphs(colorFace2, size, glyphs, origins, paint);
            return;
        }
        var polygons = PathGeometry.Polygons(Glyphs(font, size, glyphs, origins), _state.Transform);
        if (polygons.Count > 0 && Rasterizer.Fill(polygons, FillRule.NonZero, ClipForPaint(paint)) is { } coverage)
            Draw(coverage, paint);
    }

    private static bool HasRenderableColorGlyph(FontFace face, ReadOnlySpan<ushort> glyphs)
    {
        for (var i = 0; i < glyphs.Length; i++)
            if (ColorGlyphs.Get(face, glyphs[i]) is not null)
                return true;
        return false;
    }

    private void DrawColorGlyphs(FontFace face, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint)
    {
        var scale = size / face.UnitsPerEm;
        var tolerance = FlattenTolerance / Math.Max(scale * Math.Max(Scale(), 1e-3f), 1e-6f);
        for (var i = 0; i < glyphs.Length && i < origins.Length; i++)
        {
            if (ColorGlyphs.Get(face, glyphs[i]) is not { } layers)
                continue;
            var origin = SnapToTextGrid(origins[i]);
            foreach (var layer in layers)
            {
                if (Outline(face, layer.Glyph) is not { } path)
                    continue;
                var matrix = Matrix3x2.CreateScale(scale, -scale) * layer.TransformOrIdentity * Matrix3x2.CreateTranslation(origin);
                var subpaths = PathGeometry.Flatten(path, tolerance);
                var points = new List<Subpath>(subpaths.Count);
                foreach (var subpath in subpaths)
                {
                    var transformed = new Vector2[subpath.Points.Length];
                    for (var k = 0; k < transformed.Length; k++)
                        transformed[k] = Vector2.Transform(subpath.Points[k], matrix);
                    points.Add(new Subpath(transformed, subpath.Closed));
                }
                if (Rasterizer.Fill(PathGeometry.Polygons(points, _state.Transform), FillRule.NonZero, ClipForPaint(paint)) is { } coverage)
                {
                    if (layer.Gradient is { } gradient)
                    {
                        var blend = layer.Blend == BlendMode.Normal ? paint.Blend : layer.Blend;
                        var paintAlpha = paint.Color.A;
                        var stops = gradient.Stops
                            .Select(stop => stop with { Color = stop.Color with { A = stop.Color.A * paintAlpha } })
                            .ToArray();
                        var transformedGradient = gradient with
                        {
                            Stops = stops,
                            Start = Vector2.Transform(gradient.Start, matrix),
                            End = Vector2.Transform(gradient.End, matrix),
                            Center = Vector2.Transform(gradient.Center, matrix),
                            Radii = new Vector2(Vector2.TransformNormal(new Vector2(gradient.Radii.X, 0), matrix).Length(),
                                Vector2.TransformNormal(new Vector2(0, gradient.Radii.Y), matrix).Length()),
                            Focus = gradient.Focus is { } focus ? Vector2.Transform(focus, matrix) : null,
                            FocusRadius = gradient.FocusRadius * Vector2.TransformNormal(Vector2.UnitX, matrix).Length(),
                            Transform = null,
                        };
                        Draw(coverage, new Paint(paint.Color, Gradient: transformedGradient, Blend: blend));
                        continue;
                    }
                    var colour = (layer.Color ?? paint.Color) with { A = (layer.Color?.A ?? 1) * paint.Color.A };
                    var blendMode = layer.Blend == BlendMode.Normal ? paint.Blend : layer.Blend;
                    Draw(coverage, new Paint(colour, Blend: blendMode));
                }
            }
        }
    }

    // Colour glyphs that are pictures: each bitmap scaled from its strike's size to the text's, placed by its bearings.
    private void DrawBitmapGlyphs(FontFace face, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Paint paint)
    {
        for (var i = 0; i < glyphs.Length && i < origins.Length; i++)
        {
            if (BitmapGlyphs.Get(face, glyphs[i]) is not { } bitmap)
                continue;
            var scale = size / bitmap.PixelsPerEm;
            var origin = SnapToTextGrid(origins[i]);
            var destination = new RectF(origin.X + bitmap.BearingX * scale, origin.Y - bitmap.BearingY * scale, bitmap.Image.Width * scale, bitmap.Image.Height * scale);
            DrawImage(bitmap.Image, new RectF(0, 0, bitmap.Image.Width, bitmap.Image.Height), destination, ImageSampling.Smooth, new Paint(new Rgba(0, 0, 0, paint.Color.A), Blend: paint.Blend));
        }
    }

    public void StrokeGlyphs(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, in Stroke stroke, in Paint paint)
    {
        var scale = Math.Max(Scale(), 1e-3f);
        var pieces = PathGeometry.Stroke(Glyphs(font, size, glyphs, origins), stroke, FlattenTolerance / scale);
        for (var i = 0; i < pieces.Count; i++)
        {
            var piece = pieces[i];
            for (var k = 0; k < piece.Length; k++)
                piece[k] = _state.Transform.Apply(piece[k]);
        }
        if (pieces.Count > 0 && Rasterizer.Fill(pieces, FillRule.NonZero, ClipForPaint(paint)) is { } coverage)
            Draw(coverage, paint);
    }

    public float[] GlyphIntercepts(IFontHandle font, float size, ReadOnlySpan<ushort> glyphs, ReadOnlySpan<Vector2> origins, float top, float bottom)
    {
        var polygons = PathGeometry.Polygons(Glyphs(font, size, glyphs, origins), Matrix33.Identity);
        var spans = new List<(float, float)>();
        // Samples across the band: where any outline is inside at a sample, the span counts.
        const int Samples = 5;
        for (var s = 0; s < Samples; s++)
        {
            var y = top + (bottom - top) * (s + 0.5f) / Samples;
            var crossings = new List<(float X, int Direction)>();
            foreach (var polygon in polygons)
            {
                for (var i = 0; i < polygon.Length; i++)
                {
                    var a = polygon[i];
                    var b = polygon[(i + 1) % polygon.Length];
                    if (a.Y == b.Y || (a.Y > y) == (b.Y > y))
                        continue;
                    crossings.Add((a.X + (y - a.Y) / (b.Y - a.Y) * (b.X - a.X), a.Y < b.Y ? 1 : -1));
                }
            }
            crossings.Sort((p, q) => p.X.CompareTo(q.X));
            var winding = 0;
            var start = 0f;
            foreach (var (x, direction) in crossings)
            {
                var was = winding != 0;
                winding += direction;
                if (!was && winding != 0)
                    start = x;
                else if (was && winding == 0)
                    spans.Add((start, x));
            }
        }
        // Overlapping spans from different samples are one crossing.
        spans.Sort();
        var merged = new List<float>();
        foreach (var (from, to) in spans)
        {
            if (merged.Count > 0 && from <= merged[^1])
                merged[^1] = Math.Max(merged[^1], to);
            else
            {
                merged.Add(from);
                merged.Add(to);
            }
        }
        return [.. merged];
    }

    public void DrawText(string text, in RectF bounds, in Paint paint)
    {
        // A short label in a system font: there is no system font here, so nothing is drawn.
    }

    // ---- images ----

    public void DrawImage(IImageHandle image, in RectF destination, ImageSampling sampling) =>
        DrawImage(image, new RectF(0, 0, image.Width, image.Height), destination, sampling, new Paint(new Rgba(0, 0, 0, 1)));

    public void DrawImage(IImageHandle image, in RectF source, in RectF destination, ImageSampling sampling, in Paint paint)
    {
        if (source.Width <= 0 || source.Height <= 0 || destination.Width <= 0 || destination.Height <= 0)
            return;
        var imageToUser = Matrix3x2.CreateTranslation(-source.X, -source.Y)
            * Matrix3x2.CreateScale(destination.Width / source.Width, destination.Height / source.Height)
            * Matrix3x2.CreateTranslation(destination.X, destination.Y);
        if (ImageShader.ForDrawing(image, imageToUser * _state.Transform, sampling == ImageSampling.Smooth) is not { } shader)
            return;
        var polygons = PathGeometry.Polygons(PathGeometry.Flatten(new PathData().AddRoundedRect(new RoundedRect(destination, default)), FlattenTolerance), _state.Transform);
        if (Rasterizer.Fill(polygons, FillRule.NonZero, ClipForPaint(paint)) is not { } coverage)
            return;
        if (paint.Blur > 0)
            coverage = Blurred(coverage, paint.Blur);
        var alpha = Math.Clamp(paint.Color.A, 0f, 1f);
        var blend = paint.Blend;
        ForEachPixel(coverage, (x, y, c, offset) => Compositor.Blend(_target.Data, offset, shader.ColorAt(x, y) * alpha, c, blend));
    }

    // ---- layers ----

    // How far, in device pixels, the filters take what is around a pixel into it.
    private static int FilterReach(IReadOnlyList<Filter>? filters, float scale)
    {
        if (filters is null)
            return 0;
        var reach = 0f;
        foreach (var f in filters)
        {
            var sigma = (f.Deviations is { } d ? Math.Max(d.X, d.Y) : f.StdDeviation) * scale;
            reach += f.Kind switch
            {
                FilterKind.Blur => sigma * 3,
                FilterKind.DropShadow => sigma * 3 + Math.Max(Math.Abs(f.Offset.X), Math.Abs(f.Offset.Y)) * scale,
                FilterKind.Offset => Math.Max(Math.Abs(f.Offset.X), Math.Abs(f.Offset.Y)) * scale,
                FilterKind.Morphology => Math.Max(f.Radius.X, f.Radius.Y) * scale,
                FilterKind.ConvolveMatrix => Math.Max(f.KernelColumns, f.KernelRows),
                FilterKind.DisplacementMap => Math.Abs(f.Scale) * scale,
                _ => 0f,
            };
        }
        return (int)Math.Min(MathF.Ceiling(reach) + 2, 2048);
    }

    public void PushLayer(in LayerOptions options)
    {
        var saved = _state.Copy();
        Save();
        var margin = FilterReach(options.Filters, Scale());
        var bounds = new IntRect(_state.Clip.Left - margin, _state.Clip.Top - margin, _state.Clip.Right + margin, _state.Clip.Bottom + margin);
        // Drawing inside the layer is clipped to its bounds; the clip in force now is applied when the layer is composited back.
        _state.Clip = bounds;
        _state.ClipMask = null;
        var surface = bounds.IsEmpty ? new PixelSurface(1, 1) : new PixelSurface(bounds.Width, bounds.Height);
        _layers.Push(new Layer(surface, bounds, _target, _targetBounds, options, saved));
        var parentTarget = _target;
        var parentBounds = _targetBounds;
        _target = surface;
        _targetBounds = bounds;
        if (options.Backdrop is { Count: > 0 } backdrop)
            StartFromBackdrop(parentTarget, parentBounds, surface, bounds, backdrop, options.BackdropClip);
    }

    public void PopLayer()
    {
        if (_layers.Count == 0)
            return;
        var layer = _layers.Pop();
        _target = layer.Parent;
        _targetBounds = layer.ParentBounds;
        var content = layer.Surface;
        if (layer.Options.Filters is { Count: > 0 } filters && !layer.Bounds.IsEmpty)
            content = SoftwareFilters.Apply(content, filters, layer.Saved.Transform.Affine * Matrix3x2.CreateTranslation(-layer.Bounds.Left, -layer.Bounds.Top));

        var opacity = Math.Clamp(layer.Options.Opacity, 0f, 1f);
        var blend = layer.Options.Blend;
        // Composited where the clip in force when the layer was pushed lets it through.
        var clip = IntRect.Intersect(IntRect.Intersect(layer.Saved.Clip, layer.Bounds), layer.ParentBounds);
        for (var y = clip.Top; y < clip.Bottom; y++)
        {
            for (var x = clip.Left; x < clip.Right; x++)
            {
                var i = ((y - layer.Bounds.Top) * content.Width + (x - layer.Bounds.Left)) * 4;
                var a = content.Data[i + 3];
                if (a == 0 && blend is BlendMode.Normal or BlendMode.PlusLighter or BlendMode.DestinationOver)
                    continue;
                var source = new Vector4(content.Data[i], content.Data[i + 1], content.Data[i + 2], a) / 255f * opacity;
                var mask = layer.Saved.ClipMask is { } m ? m[(y - layer.Saved.Clip.Top) * layer.Saved.Clip.Width + (x - layer.Saved.Clip.Left)] / 255f : 1f;
                Compositor.Blend(layer.Parent.Data, ((y - layer.ParentBounds.Top) * layer.Parent.Width + (x - layer.ParentBounds.Left)) * 4, source, mask, blend);
            }
        }
        Restore();
    }

    // A backdrop-filter layer starts as what is behind it, filtered, inside the clip's rectangle (its edges mirrored outwards, so a
    // blur does not fade to transparent at them) and cut to the clip's shape (https://drafts.csswg.org/filter-effects-2/#BackdropFilterProperty).
    private void StartFromBackdrop(PixelSurface behind, IntRect behindBounds, PixelSurface layer, IntRect layerBounds, IReadOnlyList<Filter> filters, RoundedRect clip)
    {
        var corners = new[] { new Vector2(clip.Rect.X, clip.Rect.Y), new Vector2(clip.Rect.Right, clip.Rect.Y), new Vector2(clip.Rect.Right, clip.Rect.Bottom), new Vector2(clip.Rect.X, clip.Rect.Bottom) }
            .Select(p => _state.Transform.Apply(p)).ToArray();
        var bounds = IntRect.Intersect(IntRect.Intersect(new IntRect((int)MathF.Floor(corners.Min(p => p.X)), (int)MathF.Floor(corners.Min(p => p.Y)), (int)MathF.Ceiling(corners.Max(p => p.X)), (int)MathF.Ceiling(corners.Max(p => p.Y))),
            behindBounds), layerBounds);
        if (bounds.IsEmpty)
            return;
        var (w, h) = (bounds.Width, bounds.Height);

        var scale = Scale();
        var margin = Math.Min(FilterReach(filters, scale), 4096);
        var padded = new PixelSurface(w + 2 * margin, h + 2 * margin);
        for (var y = 0; y < padded.Height; y++)
        {
            var sy = bounds.Top + Mirror(y - margin, h) - behindBounds.Top;
            for (var x = 0; x < padded.Width; x++)
            {
                var sx = bounds.Left + Mirror(x - margin, w) - behindBounds.Left;
                Array.Copy(behind.Data, (sy * behind.Width + sx) * 4, padded.Data, (y * padded.Width + x) * 4, 4);
            }
        }
        var filtered = SoftwareFilters.Apply(padded, filters, _state.Transform.Affine * Matrix3x2.CreateTranslation(margin - bounds.Left, margin - bounds.Top));

        // Cut to the clip's shape: what its antialiased edge leaves outside is cleared.
        var polygons = PathGeometry.Polygons(PathGeometry.Flatten(new PathData().AddRoundedRect(clip), FlattenTolerance / Math.Max(scale, 1e-3f)), _state.Transform);
        var shape = Rasterizer.Fill(polygons, FillRule.NonZero, bounds);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (px, py) = (bounds.Left + x, bounds.Top + y);
                var cover = shape is null || px < shape.Bounds.Left || px >= shape.Bounds.Right || py < shape.Bounds.Top || py >= shape.Bounds.Bottom ? (byte)0 : shape.At(px, py);
                if (cover == 0)
                    continue;
                var from = ((y + margin) * padded.Width + x + margin) * 4;
                var to = ((py - layerBounds.Top) * layer.Width + (px - layerBounds.Left)) * 4;
                for (var k = 0; k < 4; k++)
                    layer.Data[to + k] = cover == 255 ? filtered.Data[from + k] : (byte)((filtered.Data[from + k] * cover + 127) / 255);
            }
        }
    }

    // The index reflected back into 0..size-1 (edge pixel repeated: -1 is 0, -2 is 1, size is size-1).
    private static int Mirror(int index, int size)
    {
        var period = 2 * size;
        var m = ((index % period) + period) % period;
        return m < size ? m : period - 1 - m;
    }
}

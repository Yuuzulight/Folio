using System.Numerics;

namespace Folio.Painting;

/// <summary>A rectangle in CSS pixels.</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    /// <summary>The rectangle shrunk by the given distances (never below zero size).</summary>
    public RectF Inset(float top, float right, float bottom, float left) =>
        new(X + left, Y + top, Math.Max(0, Width - left - right), Math.Max(0, Height - top - bottom));
}

/// <summary>Elliptical corner radii (horizontal, vertical), clockwise from the top left.</summary>
public readonly record struct CornerRadii(Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft)
{
    public bool IsZero => TopLeft == Vector2.Zero && TopRight == Vector2.Zero && BottomRight == Vector2.Zero && BottomLeft == Vector2.Zero;

    /// <summary>The radii of a curve inset from this one (https://www.w3.org/TR/css-backgrounds-3/#corner-shaping).</summary>
    public CornerRadii Inset(float top, float right, float bottom, float left) => new(
        Vector2.Max(TopLeft - new Vector2(left, top), Vector2.Zero), Vector2.Max(TopRight - new Vector2(right, top), Vector2.Zero),
        Vector2.Max(BottomRight - new Vector2(right, bottom), Vector2.Zero), Vector2.Max(BottomLeft - new Vector2(left, bottom), Vector2.Zero));
}

/// <summary>A rectangle with elliptical corners; zero radii make a plain rectangle.</summary>
public readonly record struct RoundedRect(RectF Rect, CornerRadii Radii)
{
    public RoundedRect Inset(float top, float right, float bottom, float left) =>
        new(Rect.Inset(top, right, bottom, left), Radii.Inset(top, right, bottom, left));
}

/// <summary>An sRGB colour with straight (not premultiplied) alpha, each channel from 0 to 1.</summary>
public readonly record struct Rgba(float R, float G, float B, float A);

/// <summary>What a fill or stroke paints with: a solid colour, or a gradient when one is given.</summary>
/// <param name="Blur">The standard deviation, in CSS pixels, of a Gaussian blur applied to what is painted (shadows); 0 for none.</param>
/// <param name="Blend">How what is painted blends with what is under it.</param>
public readonly record struct Paint(Rgba Color, float Blur = 0, Gradient? Gradient = null, BlendMode Blend = BlendMode.Normal);

/// <summary>
/// How a source blends with its backdrop: source-over with the blend functions of
/// https://drafts.csswg.org/compositing-2/#blending; for <see cref="PlusLighter"/>, added and clamped; or composited by
/// the Porter-Duff operator named (https://drafts.csswg.org/compositing-2/#advancedcompositing), which also removes
/// backdrop outside the source where the operator says so.
/// </summary>
public enum BlendMode
{
    Normal,
    Multiply,
    Screen,
    Overlay,
    Darken,
    Lighten,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
    PlusLighter,

    /// <summary>Porter-Duff source in: the source where the backdrop is, nothing elsewhere.</summary>
    SourceIn,

    /// <summary>Porter-Duff source out: the source where the backdrop is not, nothing elsewhere.</summary>
    SourceOut,

    /// <summary>Porter-Duff destination in: the backdrop where the source is, nothing elsewhere (masking).</summary>
    DestinationIn,

    /// <summary>Porter-Duff xor: the source and the backdrop where they do not overlap.</summary>
    Xor,
}

public enum GradientKind
{
    Linear,
    Radial,
    Conic,
}

/// <summary>A gradient colour stop: its offset along the gradient, from 0 to 1, and its colour.</summary>
public readonly record struct GradientStop(float Offset, Rgba Color);

/// <summary>How a gradient continues beyond its first and last stops.</summary>
public enum GradientSpread
{
    /// <summary>The end colours continue.</summary>
    Pad,

    /// <summary>The stops repeat.</summary>
    Repeat,

    /// <summary>The stops repeat, every other time in reverse.</summary>
    Reflect,
}

/// <summary>
/// A gradient. Its stops run from 0 to 1: from <paramref name="Start"/> to <paramref name="End"/> (linear), from
/// <paramref name="Center"/> out to the ellipse of <paramref name="Radii"/> (radial), or around <paramref name="Center"/>
/// from <paramref name="StartAngle"/> to <paramref name="EndAngle"/>, in degrees clockwise from the top (conic). Colours
/// between stops are interpolated in sRGB with straight alpha; any other interpolation (premultiplied, other colour
/// spaces) is already expressed in the stops. Beyond the ends the gradient continues as <paramref name="Spread"/> says.
/// </summary>
/// <param name="Focus">
/// For radial gradients: the centre of a focal circle of <paramref name="FocusRadius"/> where offset 0 lies, the
/// gradient running from it to the ellipse (https://www.w3.org/TR/SVG2/pservers.html#RadialGradientNotes); null when
/// offset 0 is the centre itself.
/// </param>
/// <param name="Transform">
/// Maps the gradient's own coordinates, in which the points and radii above are given, to canvas coordinates (row-vector
/// form); null when they are canvas coordinates already.
/// </param>
public sealed record Gradient(GradientKind Kind, IReadOnlyList<GradientStop> Stops, GradientSpread Spread = GradientSpread.Pad,
                              Vector2 Start = default, Vector2 End = default, Vector2 Center = default, Vector2 Radii = default,
                              float StartAngle = 0, float EndAngle = 360, Vector2? Focus = null, float FocusRadius = 0,
                              Matrix3x2? Transform = null);

public enum FillRule
{
    NonZero,
    EvenOdd,
}

/// <summary>How open subpaths end (https://www.w3.org/TR/SVG2/painting.html#LineCaps).</summary>
public enum LineCap
{
    Butt,
    Round,

    /// <summary>Extended by half the stroke width, square.</summary>
    Square,
}

/// <summary>How segments meet (https://www.w3.org/TR/SVG2/painting.html#LineJoin).</summary>
public enum LineJoin
{
    /// <summary>A sharp corner, beveled instead where it would reach further than the miter limit allows.</summary>
    Miter,
    Round,
    Bevel,
}

/// <summary>
/// How a path is stroked: its width, cap, an optional dash pattern (on, off, ... lengths) started
/// <paramref name="DashOffset"/> into the pattern, and how segments join. A miter join longer than
/// <paramref name="MiterLimit"/> times the width becomes a bevel.
/// </summary>
public readonly record struct Stroke(float Width, LineCap Cap = LineCap.Butt, IReadOnlyList<float>? Dashes = null,
                                     LineJoin Join = LineJoin.Miter, float MiterLimit = 4, float DashOffset = 0);

/// <summary>How image pixels are sampled when an image is drawn larger or smaller than its pixel size.</summary>
public enum ImageSampling
{
    /// <summary>Smoothly interpolated.</summary>
    Smooth,

    /// <summary>Nearest pixel, so enlarged pixels stay square (image-rendering: pixelated and crisp-edges).</summary>
    Pixelated,
}

/// <summary>
/// Options for a compositing layer; the layer is blended back when popped. What is drawn into it is filtered by
/// <paramref name="Filters"/>, then composited with <paramref name="Opacity"/> and <paramref name="Blend"/>. With <paramref name="Backdrop"/>, the
/// layer starts as what lies under it, filtered by those filters (their blur reading mirrored edges at the clip) and
/// clipped to <paramref name="BackdropClip"/>, instead of empty (https://drafts.csswg.org/filter-effects-2/#backdrop-filter-operation).
/// </summary>
public readonly record struct LayerOptions(float Opacity, IReadOnlyList<Filter>? Filters = null, IReadOnlyList<Filter>? Backdrop = null,
                                           RoundedRect BackdropClip = default, BlendMode Blend = BlendMode.Normal);

public enum FilterKind
{
    /// <summary>A Gaussian blur of <see cref="Filter.StdDeviation"/>.</summary>
    Blur,

    /// <summary>A colour matrix (<see cref="Filter.Matrix"/>).</summary>
    ColorMatrix,

    /// <summary>
    /// A drop shadow: the alpha moved by <see cref="Filter.Offset"/>, blurred by <see cref="Filter.StdDeviation"/> and
    /// filled with <see cref="Filter.Color"/>, drawn under the input.
    /// </summary>
    DropShadow,

    /// <summary>The input moved by <see cref="Filter.Offset"/>.</summary>
    Offset,

    /// <summary><see cref="Filter.Color"/> everywhere in the subregion; it takes no input.</summary>
    Flood,

    /// <summary><see cref="Filter.In"/> composited onto <see cref="Filter.In2"/> by <see cref="Filter.Operator"/>.</summary>
    Composite,

    /// <summary><see cref="Filter.Inputs"/> drawn over each other in order, the first at the bottom.</summary>
    Merge,

    /// <summary><see cref="Filter.In"/> blended onto <see cref="Filter.In2"/> with <see cref="Filter.Blend"/>.</summary>
    Blend,

    /// <summary>The input thinned (eroded) or, with <see cref="Filter.Dilate"/>, fattened by <see cref="Filter.Radius"/>.</summary>
    Morphology,

    /// <summary>Each channel of the input mapped by its function in <see cref="Filter.Transfer"/>.</summary>
    ComponentTransfer,

    /// <summary>Perlin noise as described by <see cref="Filter.Noise"/>, in the subregion; it takes no input.</summary>
    Turbulence,

    /// <summary>
    /// <see cref="Filter.In"/>'s pixels moved by <see cref="Filter.In2"/>'s <see cref="Filter.XChannel"/> and
    /// <see cref="Filter.YChannel"/>, each from 0 to 1 centred on 0.5, times <see cref="Filter.Scale"/>.
    /// </summary>
    DisplacementMap,

    /// <summary>The input's <see cref="Filter.Source"/> rectangle repeated across the subregion.</summary>
    Tile,

    /// <summary>
    /// The input convolved with <see cref="Filter.Kernel"/>, as feConvolveMatrix does: divided by
    /// <see cref="Filter.Divisor"/>, plus <see cref="Filter.Bias"/>, edges read by <see cref="Filter.EdgeMode"/>.
    /// </summary>
    ConvolveMatrix,

    /// <summary><see cref="Filter.Image"/> drawn into <see cref="Filter.Destination"/>; it takes no input.</summary>
    Image,

    /// <summary>
    /// Diffuse lighting of the input's alpha as a height map (feDiffuseLighting): <see cref="Filter.Light"/> in
    /// <see cref="Filter.Color"/>, with <see cref="Filter.SurfaceScale"/> and <see cref="Filter.LightingConstant"/> (kd).
    /// </summary>
    DiffuseLighting,

    /// <summary>
    /// Specular lighting of the input's alpha as a height map (feSpecularLighting): <see cref="Filter.Light"/> in
    /// <see cref="Filter.Color"/>, with <see cref="Filter.SurfaceScale"/>, <see cref="Filter.LightingConstant"/> (ks)
    /// and <see cref="Filter.Shininess"/>.
    /// </summary>
    SpecularLighting,
}

/// <summary>How a filter primitive reads pixels past the edges of its input.</summary>
public enum EdgeMode
{
    /// <summary>As transparent black.</summary>
    None,

    /// <summary>As the nearest edge pixel.</summary>
    Duplicate,

    /// <summary>From the opposite edge.</summary>
    Wrap,
}

/// <summary>The light sources of the lighting primitives (https://drafts.csswg.org/filter-effects-1/#LightSourceDefinitions).</summary>
public enum LightKind
{
    /// <summary>A light infinitely far away, shining along <see cref="Light.Direction"/>.</summary>
    Distant,

    /// <summary>A light at <see cref="Light.Position"/>.</summary>
    Point,

    /// <summary>
    /// A light at <see cref="Light.Position"/> pointing at <see cref="Light.Target"/>, its intensity falling off with
    /// <see cref="Light.Exponent"/> and cut off outside <see cref="Light.ConeAngle"/> when given.
    /// </summary>
    Spot,
}

/// <summary>
/// A light source in the layer's coordinates, z towards the viewer. <paramref name="Direction"/> is the unit vector
/// from the surface towards a distant light.
/// </summary>
public sealed record Light(LightKind Kind, Vector3 Direction = default, Vector3 Position = default, Vector3 Target = default,
                           float Exponent = 1, float? ConeAngle = null);

/// <summary>Where a filter primitive takes an input from.</summary>
public enum FilterSource
{
    /// <summary>The result of the primitive before it; the layer's content for the first.</summary>
    Previous,

    /// <summary>What was drawn into the layer.</summary>
    SourceGraphic,

    /// <summary>The alpha of what was drawn into the layer, as black.</summary>
    SourceAlpha,

    /// <summary>The result of an earlier primitive of the list, by its index.</summary>
    Result,
}

/// <summary>
/// A filter primitive's input. A <see cref="FilterSource.Result"/> whose index is not that of an earlier primitive is
/// read as <see cref="FilterSource.SourceGraphic"/>.
/// </summary>
public readonly record struct FilterInput(FilterSource Source, int Index = 0);

/// <summary>The Porter-Duff operators of feComposite, and its arithmetic one (https://drafts.csswg.org/filter-effects-1/#feCompositeElement).</summary>
public enum CompositeOperator
{
    Over,
    In,
    Out,
    Atop,
    Xor,

    /// <summary>k1·i1·i2 + k2·i1 + k3·i2 + k4 on premultiplied channels, with <see cref="Filter.Coefficients"/> k1 to k4.</summary>
    Arithmetic,
}

/// <summary>The kinds of component transfer function (https://drafts.csswg.org/filter-effects-1/#feComponentTransferElement).</summary>
public enum TransferKind
{
    Identity,
    Table,
    Discrete,
    Linear,
    Gamma,
}

/// <summary>
/// A component transfer function on straight-alpha channel values from 0 to 1: a table or discrete function of
/// <paramref name="Values"/>, slope × C + intercept, or amplitude × C^exponent + offset.
/// </summary>
public sealed record TransferFunction(TransferKind Kind, IReadOnlyList<float>? Values = null, float Slope = 1, float Intercept = 0,
                                      float Amplitude = 1, float Exponent = 1, float Offset = 0);

/// <summary>
/// Perlin noise (https://drafts.csswg.org/filter-effects-1/#feTurbulenceElement): turbulence, or fractal noise when
/// <paramref name="Fractal"/>, with its base frequency along each axis, octaves and seed; with <paramref name="Stitch"/>
/// it tiles seamlessly across the subregion.
/// </summary>
public sealed record Noise(Vector2 BaseFrequency, int Octaves = 1, float Seed = 0, bool Fractal = false, bool Stitch = false);

/// <summary>A colour channel, for displacement maps.</summary>
public enum ColorChannel
{
    R,
    G,
    B,
    A,
}

/// <summary>
/// One filter primitive (https://drafts.csswg.org/filter-effects-1/#FilterPrimitivesOverview), with results clamped.
/// By default a primitive takes the result of the one before it, so a list is a chain, as CSS filter functions are;
/// <see cref="In"/>, <see cref="In2"/> and <see cref="Inputs"/> make it a graph, as SVG filter elements are. The
/// list's result is its last primitive's. A colour matrix has 20 values, row by row: R', G', B' and A' from R, G, B, A
/// and 1, on straight-alpha colours from 0 to 1. Lengths and rectangles are CSS pixels in the layer's coordinates.
/// </summary>
public sealed record Filter(FilterKind Kind, float StdDeviation = 0, IReadOnlyList<float>? Matrix = null, Vector2 Offset = default, Rgba Color = default)
{
    /// <summary>The input, for every kind that takes one.</summary>
    public FilterInput In { get; init; }

    /// <summary>The second input: what <see cref="In"/> is composited or blended onto, or the displacement map.</summary>
    public FilterInput In2 { get; init; }

    /// <summary>The inputs of a merge.</summary>
    public IReadOnlyList<FilterInput>? Inputs { get; init; }

    /// <summary>Where the result is kept (the rest is transparent); null for everywhere.</summary>
    public RectF? Subregion { get; init; }

    /// <summary>Whether the primitive works on linear-light colours (color-interpolation-filters: linearRGB) instead of sRGB ones.</summary>
    public bool LinearRgb { get; init; }

    /// <summary>For a blur or drop shadow: the standard deviations along x and y, instead of <see cref="StdDeviation"/> for both.</summary>
    public Vector2? Deviations { get; init; }

    /// <summary>For a composite: its operator.</summary>
    public CompositeOperator Operator { get; init; }

    /// <summary>For an arithmetic composite: k1, k2, k3 and k4.</summary>
    public IReadOnlyList<float>? Coefficients { get; init; }

    /// <summary>For a blend: its mode.</summary>
    public BlendMode Blend { get; init; }

    /// <summary>For a morphology: the radii along x and y.</summary>
    public Vector2 Radius { get; init; }

    /// <summary>For a morphology: fatten rather than thin.</summary>
    public bool Dilate { get; init; }

    /// <summary>For a component transfer: the functions for R, G, B and A; a missing one is the identity.</summary>
    public IReadOnlyList<TransferFunction>? Transfer { get; init; }

    /// <summary>For turbulence: the noise.</summary>
    public Noise? Noise { get; init; }

    /// <summary>For a displacement map: how far a channel value of 1 moves a pixel.</summary>
    public float Scale { get; init; }

    /// <summary>For a displacement map: the channel of <see cref="In2"/> that moves pixels along x.</summary>
    public ColorChannel XChannel { get; init; } = ColorChannel.A;

    /// <summary>For a displacement map: the channel of <see cref="In2"/> that moves pixels along y.</summary>
    public ColorChannel YChannel { get; init; } = ColorChannel.A;

    /// <summary>For a tile: the rectangle of the input that is repeated.</summary>
    public RectF? Source { get; init; }

    /// <summary>
    /// For a convolution: the kernel matrix, <see cref="KernelColumns"/> by <see cref="KernelRows"/>, row by row, as
    /// feConvolveMatrix's kernelMatrix.
    /// </summary>
    public IReadOnlyList<float>? Kernel { get; init; }

    /// <summary>For a convolution: the kernel's columns.</summary>
    public int KernelColumns { get; init; }

    /// <summary>For a convolution: the kernel's rows.</summary>
    public int KernelRows { get; init; }

    /// <summary>For a convolution: the kernel's column over the pixel it computes.</summary>
    public int TargetX { get; init; }

    /// <summary>For a convolution: the kernel's row over the pixel it computes.</summary>
    public int TargetY { get; init; }

    /// <summary>For a convolution: what the sum is divided by.</summary>
    public float Divisor { get; init; } = 1;

    /// <summary>For a convolution: what is added to the result.</summary>
    public float Bias { get; init; }

    /// <summary>For a convolution: how pixels past the input's edges are read.</summary>
    public EdgeMode EdgeMode { get; init; }

    /// <summary>For a convolution: leave alpha as it is and convolve only the colour.</summary>
    public bool PreserveAlpha { get; init; }

    /// <summary>For an image: the image.</summary>
    public Imaging.IImageHandle? Image { get; init; }

    /// <summary>For an image: where it is drawn.</summary>
    public RectF Destination { get; init; }

    /// <summary>For lighting: the light.</summary>
    public Light? Light { get; init; }

    /// <summary>For lighting: the height of an opaque pixel of the input's alpha.</summary>
    public float SurfaceScale { get; init; } = 1;

    /// <summary>For lighting: kd for diffuse lighting, ks for specular lighting.</summary>
    public float LightingConstant { get; init; } = 1;

    /// <summary>For specular lighting: the exponent of the specular term.</summary>
    public float Shininess { get; init; } = 1;
}

public enum PathVerb
{
    MoveTo,
    LineTo,
    CubicTo,
    Close,
}

/// <summary>One path command; <see cref="CubicTo"/> uses all three points, the others only the first.</summary>
public readonly record struct PathCommand(PathVerb Verb, Vector2 P1 = default, Vector2 P2 = default, Vector2 P3 = default);

/// <summary>A path of lines and cubic curves in CSS pixels.</summary>
public sealed class PathData
{
    private readonly List<PathCommand> _commands;

    public PathData() => _commands = [];

    // For a path whose size is known, made for every box painted (border rings): the command list never grows.
    internal PathData(int capacity) => _commands = new(capacity);

    public IReadOnlyList<PathCommand> Commands => _commands;

    public PathData MoveTo(float x, float y) => Add(new(PathVerb.MoveTo, new(x, y)));

    public PathData LineTo(float x, float y) => Add(new(PathVerb.LineTo, new(x, y)));

    public PathData CubicTo(Vector2 control1, Vector2 control2, Vector2 end) => Add(new(PathVerb.CubicTo, control1, control2, end));

    public PathData Close() => Add(new(PathVerb.Close));

    /// <summary>Adds a closed rounded rectangle, clockwise from the end of the top-left corner.</summary>
    public PathData AddRoundedRect(in RoundedRect shape)
    {
        // Cubic approximation of a quarter ellipse.
        const float K = 0.5522848f;
        var (r, c) = (shape.Rect, shape.Radii);
        // Square corners: the four sides alone, the same shape without four empty curves.
        if (c.IsZero)
            return MoveTo(r.X, r.Y).LineTo(r.Right, r.Y).LineTo(r.Right, r.Bottom).LineTo(r.X, r.Bottom).Close();
        MoveTo(r.X + c.TopLeft.X, r.Y);
        LineTo(r.Right - c.TopRight.X, r.Y);
        CubicTo(new(r.Right - c.TopRight.X * (1 - K), r.Y), new(r.Right, r.Y + c.TopRight.Y * (1 - K)), new(r.Right, r.Y + c.TopRight.Y));
        LineTo(r.Right, r.Bottom - c.BottomRight.Y);
        CubicTo(new(r.Right, r.Bottom - c.BottomRight.Y * (1 - K)), new(r.Right - c.BottomRight.X * (1 - K), r.Bottom), new(r.Right - c.BottomRight.X, r.Bottom));
        LineTo(r.X + c.BottomLeft.X, r.Bottom);
        CubicTo(new(r.X + c.BottomLeft.X * (1 - K), r.Bottom), new(r.X, r.Bottom - c.BottomLeft.Y * (1 - K)), new(r.X, r.Bottom - c.BottomLeft.Y));
        LineTo(r.X, r.Y + c.TopLeft.Y);
        CubicTo(new(r.X, r.Y + c.TopLeft.Y * (1 - K)), new(r.X + c.TopLeft.X * (1 - K), r.Y), new(r.X + c.TopLeft.X, r.Y));
        return Close();
    }

    private PathData Add(PathCommand command)
    {
        _commands.Add(command);
        return this;
    }
}

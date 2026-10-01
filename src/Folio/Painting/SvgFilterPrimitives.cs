using System.Numerics;
using Folio.Css;
using Folio.Dom;
using Folio.Style;
using Folio.Svg;

namespace Folio.Painting;

/// <summary>
/// A filter chain as filter primitives (https://drafts.csswg.org/filter-effects-1/#FilterProperty): filter functions as
/// their equivalents, and SVG filter elements (https://drafts.csswg.org/filter-effects-1/#FilterElement) as graphs of
/// their primitives, in the filtered element's user space.
/// </summary>
// ponytail: an feImage of an element draws nothing, kernelUnitLength is ignored, and baseFrequency is not scaled by
// primitiveUnits="objectBoundingBox".
internal static class SvgFilterPrimitives
{
    /// <summary>How many primitives one filter element may have; later ones are left out.</summary>
    public const int MaxPrimitives = 256;

    /// <summary>The highest numOctaves used; more octaves add nothing visible at screen resolution.</summary>
    public const int MaxOctaves = 10;

    /// <summary>The largest morphology radius used, in user units: the work grows with the radius.</summary>
    public const float MaxRadius = 256;

    /// <summary>
    /// The most values a convolution kernel may have; a larger one passes its input through. The work grows with the
    /// kernel times the pixels it covers.
    /// </summary>
    public const int MaxKernelSize = 256;

    /// <summary>
    /// The filters of a chain, applied in order, each to the result of the one before. A filter that leaves nothing to
    /// render is a transparent flood.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Filter>> Of(SvgFilterChain chain) =>
        [.. chain.Filters.Select(f => f.Reference is { } reference
            ? Built.GetValue(reference, r => Element(r.Element, r.Bounds, r.Viewport, r.Images))
            : (IReadOnlyList<Filter>)[FilterPrimitives.Of(f.Function, chain.CurrentColor)])];

    // Each filter element's primitives for one use in a layout, built the first time it is painted and kept as long as
    // that layout's reference is.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SvgFilterReference, IReadOnlyList<Filter>> Built = [];

    private static readonly Filter[] Nothing = [new Filter(FilterKind.Flood)];

    // A filter element's primitives: its region from x, y, width and height in filterUnits (the bounding box by
    // default, from -10% to 120%), each primitive's subregion in primitiveUnits (user space by default) within it.
    private static IReadOnlyList<Filter> Element(ElementNode filter, SvgRect? bounds, Vector2 viewport, Imaging.ImageLoader? images)
    {
        var fontSize = filter.ComputedStyle()?.Font.Size ?? 16;
        bool BoxUnits(string name, bool byDefault) => filter.GetAttribute(name)?.Trim() is { } units
            ? units == "objectBoundingBox" || units != "userSpaceOnUse" && byDefault
            : byDefault;
        var (regionInBox, primitivesInBox) = (BoxUnits("filterUnits", true), BoxUnits("primitiveUnits", false));
        if ((regionInBox || primitivesInBox) && bounds is not { Width: > 0, Height: > 0 })
            return Nothing;
        var box = bounds ?? default;

        // A coordinate or size: fractions of the bounding box (numbers or percentages) in bounding box units, user
        // units otherwise, percentages there taken of the viewport.
        float Coordinate(ElementNode element, string name, string? fallback, SvgAxis axis, bool inBox, float otherwise)
        {
            var text = element.GetAttribute(name) ?? fallback;
            if (text is null)
                return otherwise;
            if (!inBox)
                return SvgGeometry.Length(text, axis, viewport, fontSize, otherwise);
            if (SvgGeometry.ParseLength(text, fontSize) is not { } length)
                return otherwise;
            var fraction = length.Percent ? length.Value / 100 : length.Value;
            return axis == SvgAxis.Horizontal ? fraction * box.Width : fraction * box.Height;
        }
        var region = new SvgRect(
            Coordinate(filter, "x", "-10%", SvgAxis.Horizontal, regionInBox, 0) + (regionInBox ? box.X : 0),
            Coordinate(filter, "y", "-10%", SvgAxis.Vertical, regionInBox, 0) + (regionInBox ? box.Y : 0),
            Coordinate(filter, "width", "120%", SvgAxis.Horizontal, regionInBox, 0),
            Coordinate(filter, "height", "120%", SvgAxis.Vertical, regionInBox, 0));
        // Bounding box fractions can overflow to infinities, which render nothing.
        if (!(region.Width > 0 && region.Height > 0) || !Finite(region.X, region.Y, region.Width, region.Height))
            return Nothing;

        var primitives = new List<Filter>();
        var results = new Dictionary<string, int>(StringComparer.Ordinal);
        int? transparent = null;
        for (var child = filter.FirstChild; child is not null && primitives.Count < MaxPrimitives; child = child.NextSibling)
        {
            if (child is not ElementNode { Name.Namespace: var ns, LocalName: var name } element || ns != Namespaces.Svg || !name.StartsWith("fe", StringComparison.Ordinal))
                continue;
            var style = element.ComputedStyle();
            var previous = primitives.Count == 0 ? new FilterInput(FilterSource.SourceGraphic) : new FilterInput(FilterSource.Result, primitives.Count - 1);

            // in and in2: the source, its alpha, a named result, or the previous result when missing or unknown.
            // BackgroundImage, BackgroundAlpha, FillPaint and StrokePaint are transparent.
            FilterInput Input(ElementNode e, string attribute)
            {
                switch (e.GetAttribute(attribute)?.Trim())
                {
                    case "SourceGraphic":
                        return new FilterInput(FilterSource.SourceGraphic);
                    case "SourceAlpha":
                        return new FilterInput(FilterSource.SourceAlpha);
                    case "BackgroundImage" or "BackgroundAlpha" or "FillPaint" or "StrokePaint":
                        if (transparent is null)
                        {
                            transparent = primitives.Count;
                            primitives.Add(new Filter(FilterKind.Flood));
                        }
                        return new FilterInput(FilterSource.Result, transparent.Value);
                    case { Length: > 0 } result when results.TryGetValue(result, out var index):
                        return new FilterInput(FilterSource.Result, index);
                    default:
                        return previous;
                }
            }
            float Length(float value, SvgAxis axis) =>
                !primitivesInBox ? value : (axis == SvgAxis.Horizontal ? value * box.Width : value * box.Height) is var length && float.IsFinite(length) ? length : 0;
            List<float> Numbers(string attribute, params float[] fallback) =>
                element.GetAttribute(attribute) is { } text && SvgGeometry.Numbers(text) is { Count: > 0 } numbers ? numbers : [.. fallback];
            float Number(string attribute, float fallback) => Numbers(attribute, fallback)[0];
            (float X, float Y) Pair(string attribute, float fallback)
            {
                var numbers = Numbers(attribute, fallback);
                return (numbers[0], numbers.Count > 1 ? numbers[1] : numbers[0]);
            }
            Rgba Flood()
            {
                var color = (style?.SvgStop.FloodColor ?? CssColor.Black).Resolve(style?.Inherited.Color ?? CssColor.Black);
                return new Rgba(color.R, color.G, color.B, color.A * (style?.SvgStop.FloodOpacity ?? 1));
            }

            // The subregion: x, y, width and height in primitiveUnits, the filter region for those missing, within it.
            float Edge(string attribute, SvgAxis axis, float otherwise) =>
                element.GetAttribute(attribute) is null ? otherwise : Coordinate(element, attribute, null, axis, primitivesInBox, otherwise) + (primitivesInBox && attribute is "x" or "y" ? (axis == SvgAxis.Horizontal ? box.X : box.Y) : 0);
            var (x, y) = (Edge("x", SvgAxis.Horizontal, region.X), Edge("y", SvgAxis.Vertical, region.Y));
            var (w, h) = (Edge("width", SvgAxis.Horizontal, region.Width), Edge("height", SvgAxis.Vertical, region.Height));
            var (left, top) = (Math.Max(x, region.X), Math.Max(y, region.Y));
            var (right, bottom) = (Math.Min(x + w, region.X + region.Width), Math.Min(y + h, region.Y + region.Height));
            var subregion = new RectF(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
            if (!Finite(subregion.X, subregion.Y, subregion.Width, subregion.Height))
                subregion = new RectF(region.X, region.Y, 0, 0);

            // A light source's position: user units, or fractions of the bounding box, z of its normalised diagonal.
            Vector3 Position(Vector3 p) => !primitivesInBox ? p
                : new(box.X + p.X * box.Width, box.Y + p.Y * box.Height, p.Z * MathF.Sqrt((box.Width * box.Width + box.Height * box.Height) / 2));
            Filter? Lighting(FilterKind kind, float constant, float shininess = 1)
            {
                if (LightSource(element, Position) is not { } light)
                    return null;
                var color = (style?.SvgStop.LightingColor ?? new CssColor(1, 1, 1, 1)).Resolve(style?.Inherited.Color ?? CssColor.Black);
                return new Filter(kind, Color: new Rgba(color.R, color.G, color.B, 1))
                {
                    Light = light, SurfaceScale = Number("surfaceScale", 1), LightingConstant = Math.Max(0, constant), Shininess = shininess,
                };
            }

            var input = Input(element, "in");
            var primitive = name switch
            {
                "feGaussianBlur" => Pair("stdDeviation", 0) is var (sx, sy)
                    ? new Filter(FilterKind.Blur) { Deviations = new(Math.Max(0, Length(sx, SvgAxis.Horizontal)), Math.Max(0, Length(sy, SvgAxis.Vertical))) }
                    : null,
                "feOffset" => new Filter(FilterKind.Offset, Offset: new(Length(Number("dx", 0), SvgAxis.Horizontal), Length(Number("dy", 0), SvgAxis.Vertical))),
                "feFlood" => new Filter(FilterKind.Flood, Color: Flood()),
                "feDropShadow" => Pair("stdDeviation", 2) is var (dx, dy)
                    ? new Filter(FilterKind.DropShadow, Offset: new(Length(Number("dx", 2), SvgAxis.Horizontal), Length(Number("dy", 2), SvgAxis.Vertical)), Color: Flood())
                    {
                        Deviations = new(Math.Max(0, Length(dx, SvgAxis.Horizontal)), Math.Max(0, Length(dy, SvgAxis.Vertical))),
                    }
                    : null,
                "feComposite" => Composite(element, Input(element, "in2"), Numbers),
                "feMerge" => new Filter(FilterKind.Merge)
                {
                    Inputs = [.. element.Children.OfType<ElementNode>().Where(n => n.LocalName == "feMergeNode" && n.Name.Namespace == Namespaces.Svg).Select(n => Input(n, "in"))],
                },
                "feBlend" => new Filter(FilterKind.Blend) { Blend = Blend(element.GetAttribute("mode")), In2 = Input(element, "in2") },
                "feColorMatrix" => new Filter(FilterKind.ColorMatrix, Matrix: ColorMatrix(element)),
                "feComponentTransfer" => new Filter(FilterKind.ComponentTransfer) { Transfer = Transfer(element) },
                "feMorphology" => Pair("radius", 0) is var (rx, ry) && rx > 0 && ry > 0
                    ? new Filter(FilterKind.Morphology) { Radius = new(Math.Min(Length(rx, SvgAxis.Horizontal), MaxRadius), Math.Min(Length(ry, SvgAxis.Vertical), MaxRadius)), Dilate = element.GetAttribute("operator")?.Trim() == "dilate" }
                    : null,
                "feTurbulence" => Pair("baseFrequency", 0) is var (fx, fy) && fx >= 0 && fy >= 0
                    ? new Filter(FilterKind.Turbulence)
                    {
                        Noise = new Noise(new(fx, fy), Math.Clamp((int)Number("numOctaves", 1), 0, MaxOctaves), MathF.Round(Number("seed", 0)),
                            element.GetAttribute("type")?.Trim() == "fractalNoise", element.GetAttribute("stitchTiles")?.Trim() == "stitch"),
                    }
                    : new Filter(FilterKind.Flood),
                "feDisplacementMap" => new Filter(FilterKind.DisplacementMap)
                {
                    Scale = Length(Number("scale", 0), SvgAxis.Horizontal), In2 = Input(element, "in2"),
                    XChannel = Channel(element.GetAttribute("xChannelSelector")), YChannel = Channel(element.GetAttribute("yChannelSelector")),
                },
                // The input's subregion repeated; the source and its alpha span the filter region.
                "feTile" => new Filter(FilterKind.Tile)
                {
                    Source = input is { Source: FilterSource.Result, Index: var i } && i < primitives.Count && primitives[i].Subregion is { } r
                        ? r : new RectF(region.X, region.Y, region.Width, region.Height),
                },
                "feConvolveMatrix" => Convolution(element, Numbers),
                "feImage" => Image(element, images, subregion),
                "feDiffuseLighting" => Lighting(FilterKind.DiffuseLighting, Number("diffuseConstant", 1)),
                "feSpecularLighting" => Lighting(FilterKind.SpecularLighting, Number("specularConstant", 1), Math.Clamp(Number("specularExponent", 1), 1, 128)),
                _ => null,
            } ?? new Filter(FilterKind.Offset); // what an unsupported or disabled primitive does: its input, unchanged

            // An image is in sRGB and a tile only moves pixels, so neither works in linear light.
            var linear = style?.Svg.ColorInterpolationFilters != ColorInterpolation.Srgb
                && primitive.Kind is not (FilterKind.Offset or FilterKind.Flood or FilterKind.Morphology or FilterKind.Tile or FilterKind.Image);
            primitives.Add(primitive with { In = input, Subregion = subregion, LinearRgb = linear });
            if (element.GetAttribute("result")?.Trim() is { Length: > 0 } resultName)
                results[resultName] = primitives.Count - 1;
        }
        // A filter element with no primitives leaves nothing to render.
        return primitives.Count > 0 ? primitives : Nothing;
    }

    private static Filter Composite(ElementNode element, FilterInput in2, Func<string, float[], List<float>> numbers)
    {
        var op = element.GetAttribute("operator")?.Trim() switch
        {
            "in" => CompositeOperator.In,
            "out" => CompositeOperator.Out,
            "atop" => CompositeOperator.Atop,
            "xor" => CompositeOperator.Xor,
            "arithmetic" => CompositeOperator.Arithmetic,
            _ => CompositeOperator.Over,
        };
        float K(string name) => numbers(name, [0])[0];
        return new Filter(FilterKind.Composite)
        {
            Operator = op, In2 = in2, Coefficients = op == CompositeOperator.Arithmetic ? [K("k1"), K("k2"), K("k3"), K("k4")] : null,
        };
    }

    // feConvolveMatrix (https://drafts.csswg.org/filter-effects-1/#feConvolveMatrixElement): an order of positive
    // integers (3 by default) and a kernel of that many values, or the input passes through. The divisor defaults to
    // the kernel's sum (1 when that is 0), as it does when given as 0; the target to the kernel's middle; the edge mode
    // to duplicate.
    private static Filter? Convolution(ElementNode element, Func<string, float[], List<float>> numbers)
    {
        var order = numbers("order", [3]);
        var (ox, oy) = (order[0], order.Count > 1 ? order[1] : order[0]);
        if (ox < 1 || oy < 1 || ox != MathF.Floor(ox) || oy != MathF.Floor(oy) || ox * oy > MaxKernelSize)
            return null;
        var (columns, rows) = ((int)ox, (int)oy);
        var kernel = numbers("kernelMatrix", []);
        if (kernel.Count != columns * rows)
            return null;
        var sum = kernel.Sum();
        var divisor = numbers("divisor", [0])[0] is var d && d != 0 ? d : sum != 0 ? sum : 1;
        int Target(string name, int size) => element.GetAttribute(name) is null ? size / 2 : (int)numbers(name, [0])[0];
        return new Filter(FilterKind.ConvolveMatrix)
        {
            Kernel = kernel, KernelColumns = columns, KernelRows = rows, TargetX = Target("targetX", columns), TargetY = Target("targetY", rows),
            Divisor = divisor, Bias = numbers("bias", [0])[0],
            EdgeMode = element.GetAttribute("edgeMode")?.Trim() switch { "wrap" => EdgeMode.Wrap, "none" => EdgeMode.None, _ => EdgeMode.Duplicate },
            PreserveAlpha = element.GetAttribute("preserveAlpha")?.Trim() == "true",
        };
    }

    // feImage (https://drafts.csswg.org/filter-effects-1/#feImageElement): its image fitted into the subregion by
    // preserveAspectRatio; transparent when it does not load or names an element.
    private static Filter Image(ElementNode element, Imaging.ImageLoader? images, RectF subregion)
    {
        if ((element.GetAttribute("href") ?? element.GetAttribute("xlink:href"))?.Trim() is not { Length: > 0 } href || href.StartsWith('#')
            || images?.Load(href, "feImage") is not { } image)
            return new Filter(FilterKind.Flood);
        var fit = SvgGeometry.ViewBoxTransform(new SvgRect(0, 0, image.Width, image.Height), element.GetAttribute("preserveAspectRatio"),
            new SvgRect(subregion.X, subregion.Y, subregion.Width, subregion.Height));
        return new Filter(FilterKind.Image) { Image = image, Destination = new RectF(fit.M31, fit.M32, image.Width * fit.M11, image.Height * fit.M22) };
    }

    // The light source of a lighting primitive: its first feDistantLight, fePointLight or feSpotLight child
    // (https://drafts.csswg.org/filter-effects-1/#LightSourceDefinitions); without one the input passes through.
    private static Light? LightSource(ElementNode element, Func<Vector3, Vector3> position)
    {
        var light = element.Children.OfType<ElementNode>()
            .FirstOrDefault(n => n.Name.Namespace == Namespaces.Svg && n.LocalName is "feDistantLight" or "fePointLight" or "feSpotLight");
        if (light is null)
            return null;
        float N(string name, float fallback) => light.GetAttribute(name) is { } text && SvgGeometry.Numbers(text) is [var n, ..] ? n : fallback;
        Vector3 At(string x, string y, string z) => position(new(N(x, 0), N(y, 0), N(z, 0)));
        var (azimuth, elevation) = (N("azimuth", 0) * MathF.PI / 180, N("elevation", 0) * MathF.PI / 180);
        var result = light.LocalName switch
        {
            "feDistantLight" => new Light(LightKind.Distant,
                Direction: new(MathF.Cos(azimuth) * MathF.Cos(elevation), MathF.Sin(azimuth) * MathF.Cos(elevation), MathF.Sin(elevation))),
            "fePointLight" => new Light(LightKind.Point, Position: At("x", "y", "z")),
            _ => new Light(LightKind.Spot, Position: At("x", "y", "z"), Target: At("pointsAtX", "pointsAtY", "pointsAtZ"),
                Exponent: N("specularExponent", 1), ConeAngle: light.GetAttribute("limitingConeAngle") is null ? null : Math.Abs(N("limitingConeAngle", 90))),
        };
        // Bounding box fractions can overflow to infinities: such a light lights nothing in particular, so there is none.
        return Finite(result.Direction.X, result.Direction.Y, result.Direction.Z, result.Position.X, result.Position.Y, result.Position.Z,
            result.Target.X, result.Target.Y, result.Target.Z) ? result : null;
    }

    private static bool Finite(params ReadOnlySpan<float> values)
    {
        foreach (var value in values)
        {
            if (!float.IsFinite(value))
                return false;
        }
        return true;
    }

    private static readonly Dictionary<string, Painting.BlendMode> BlendModes = new(StringComparer.Ordinal)
    {
        ["normal"] = Painting.BlendMode.Normal, ["multiply"] = Painting.BlendMode.Multiply, ["screen"] = Painting.BlendMode.Screen,
        ["overlay"] = Painting.BlendMode.Overlay, ["darken"] = Painting.BlendMode.Darken, ["lighten"] = Painting.BlendMode.Lighten,
        ["color-dodge"] = Painting.BlendMode.ColorDodge, ["color-burn"] = Painting.BlendMode.ColorBurn, ["hard-light"] = Painting.BlendMode.HardLight,
        ["soft-light"] = Painting.BlendMode.SoftLight, ["difference"] = Painting.BlendMode.Difference, ["exclusion"] = Painting.BlendMode.Exclusion,
        ["hue"] = Painting.BlendMode.Hue, ["saturation"] = Painting.BlendMode.Saturation, ["color"] = Painting.BlendMode.Color,
        ["luminosity"] = Painting.BlendMode.Luminosity,
    };

    private static Painting.BlendMode Blend(string? mode) => BlendModes.GetValueOrDefault(mode?.Trim() ?? "", Painting.BlendMode.Normal);

    // feColorMatrix: a full matrix of 20 values, saturate, hueRotate or luminanceToAlpha; values that do not fit the
    // type make it the identity (https://drafts.csswg.org/filter-effects-1/#feColorMatrixElement).
    private static float[] ColorMatrix(ElementNode element)
    {
        var values = element.GetAttribute("values") is { } text ? SvgGeometry.Numbers(text) : new List<float>();
        float[] identity = [1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0];
        return element.GetAttribute("type")?.Trim() switch
        {
            "saturate" => values.Count == 0 ? identity : FilterPrimitives.ColorMatrix("saturate", Math.Max(0, values[0])),
            "hueRotate" => FilterPrimitives.ColorMatrix("hue-rotate", values.Count == 0 ? 0 : values[0]),
            "luminanceToAlpha" => [.. FilterPrimitives.LuminanceToAlpha[0].Matrix!],
            _ => values.Count == 20 ? [.. values] : identity,
        };
    }

    // feComponentTransfer: the last feFuncR, feFuncG, feFuncB and feFuncA child, each the identity when missing.
    private static TransferFunction[] Transfer(ElementNode element)
    {
        var functions = new TransferFunction[4];
        foreach (var child in element.Children.OfType<ElementNode>())
        {
            var channel = child.LocalName switch { "feFuncR" => 0, "feFuncG" => 1, "feFuncB" => 2, "feFuncA" => 3, _ => -1 };
            if (channel < 0 || child.Name.Namespace != Namespaces.Svg)
                continue;
            float Value(string name, float fallback) => child.GetAttribute(name) is { } text && SvgGeometry.Numbers(text) is [var n, ..] ? n : fallback;
            var values = child.GetAttribute("tableValues") is { } table ? SvgGeometry.Numbers(table) : new List<float>();
            var kind = child.GetAttribute("type")?.Trim() switch
            {
                "table" when values.Count > 0 => TransferKind.Table,
                "discrete" when values.Count > 0 => TransferKind.Discrete,
                "linear" => TransferKind.Linear,
                "gamma" => TransferKind.Gamma,
                _ => TransferKind.Identity,
            };
            functions[channel] = new TransferFunction(kind, values, Value("slope", 1), Value("intercept", 0), Value("amplitude", 1), Value("exponent", 1), Value("offset", 0));
        }
        for (var i = 0; i < functions.Length; i++)
            functions[i] ??= new TransferFunction(TransferKind.Identity);
        return functions;
    }

    private static ColorChannel Channel(string? selector) => selector?.Trim() switch
    {
        "R" => ColorChannel.R,
        "G" => ColorChannel.G,
        "B" => ColorChannel.B,
        _ => ColorChannel.A,
    };
}

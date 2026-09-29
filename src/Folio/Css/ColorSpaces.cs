namespace Folio.Css;

/// <summary>Colour spaces of CSS Color 4 that <c>color-mix()</c> can interpolate in.</summary>
internal enum ColorSpace
{
    Srgb,
    SrgbLinear,
    Lab,
    Oklab,
    Lch,
    Oklch,
    XyzD50,
    XyzD65,
    Hsl,
    Hwb,
}

internal enum HueInterpolation
{
    Shorter,
    Longer,
    Increasing,
    Decreasing,
}

/// <summary>
/// Conversions between the CSS Color 4 colour spaces (https://www.w3.org/TR/css-color-4/#color-conversion-code) and
/// gamut mapping into sRGB (https://www.w3.org/TR/css-color-4/#css-gamut-mapping).
/// </summary>
internal static class ColorSpaces
{
    // ---------------------------------------------------------------- to sRGB

    public static (double R, double G, double B) LabToSrgb(double l, double a, double b) =>
        Gamma(XyzD65ToSrgbLinear(D50ToD65(LabToXyzD50(l, a, b))));

    public static (double R, double G, double B) OklabToSrgb(double l, double a, double b) =>
        Gamma(XyzD65ToSrgbLinear(OklabToXyzD65(l, a, b)));

    public static (double A, double B) PolarToRectangular(double chroma, double hueDegrees)
    {
        var h = hueDegrees * Math.PI / 180;
        return (chroma * Math.Cos(h), chroma * Math.Sin(h));
    }

    /// <summary>Maps a colour into the sRGB gamut, preserving lightness and hue (the CSS Color 4 algorithm).</summary>
    public static (double R, double G, double B) GamutMapToSrgb((double R, double G, double B) rgb)
    {
        if (InGamut(rgb))
            return rgb;
        var (l, a, b) = SrgbToOklab(rgb);
        if (l >= 1)
            return (1, 1, 1);
        if (l <= 0)
            return (0, 0, 0);

        const double jnd = 0.02, epsilon = 0.0001;
        var hue = Math.Atan2(b, a);
        var chroma = Math.Sqrt(a * a + b * b);
        (double, double, double) Current(double c) => OklabToSrgb(l, c * Math.Cos(hue), c * Math.Sin(hue));

        var clipped = Clip(Current(chroma));
        if (DeltaEOk(clipped, (l, a, b)) < jnd)
            return clipped;

        double min = 0, max = chroma;
        var minInGamut = true;
        while (max - min > epsilon)
        {
            var c = (min + max) / 2;
            var current = Current(c);
            if (minInGamut && InGamut(current))
            {
                min = c;
                continue;
            }
            clipped = Clip(current);
            var e = DeltaEOk(clipped, (l, c * Math.Cos(hue), c * Math.Sin(hue)));
            if (e < jnd)
            {
                if (jnd - e < epsilon)
                    return clipped;
                minInGamut = false;
                min = c;
            }
            else
            {
                max = c;
            }
        }
        return clipped;
    }

    private static bool InGamut((double R, double G, double B) c) =>
        c.R is >= -1e-6 and <= 1 + 1e-6 && c.G is >= -1e-6 and <= 1 + 1e-6 && c.B is >= -1e-6 and <= 1 + 1e-6;

    private static (double, double, double) Clip((double R, double G, double B) c) =>
        (Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1));

    private static double DeltaEOk((double R, double G, double B) rgb, (double L, double A, double B) lab)
    {
        var other = SrgbToOklab(rgb);
        return Math.Sqrt(Math.Pow(other.L - lab.L, 2) + Math.Pow(other.A - lab.A, 2) + Math.Pow(other.B - lab.B, 2));
    }

    // ---------------------------------------------------------------- between spaces

    /// <summary>Converts sRGB (0..1) into a space's components (hues in degrees, NaN for an achromatic hue).</summary>
    public static (double, double, double) FromSrgb(ColorSpace space, (double R, double G, double B) rgb) => space switch
    {
        ColorSpace.Srgb => rgb,
        ColorSpace.SrgbLinear => Linear(rgb),
        ColorSpace.XyzD65 => SrgbLinearToXyzD65(Linear(rgb)),
        ColorSpace.XyzD50 => D65ToD50(SrgbLinearToXyzD65(Linear(rgb))),
        ColorSpace.Lab => XyzD50ToLab(D65ToD50(SrgbLinearToXyzD65(Linear(rgb)))),
        ColorSpace.Oklab => SrgbToOklab(rgb),
        ColorSpace.Lch => ToPolar(XyzD50ToLab(D65ToD50(SrgbLinearToXyzD65(Linear(rgb)))), 0.0015),
        ColorSpace.Oklch => ToPolar(SrgbToOklab(rgb), 0.000004),
        ColorSpace.Hsl => SrgbToHsl(rgb),
        _ => SrgbToHwb(rgb),
    };

    public static (double R, double G, double B) ToSrgb(ColorSpace space, (double X, double Y, double Z) c) => space switch
    {
        ColorSpace.Srgb => c,
        ColorSpace.SrgbLinear => Gamma(c),
        ColorSpace.XyzD65 => Gamma(XyzD65ToSrgbLinear(c)),
        ColorSpace.XyzD50 => Gamma(XyzD65ToSrgbLinear(D50ToD65(c))),
        ColorSpace.Lab => LabToSrgb(c.X, c.Y, c.Z),
        ColorSpace.Oklab => OklabToSrgb(c.X, c.Y, c.Z),
        ColorSpace.Lch => LchToSrgb(c.X, c.Y, NoNaN(c.Z)),
        ColorSpace.Oklch => OklchToSrgb(c.X, c.Y, NoNaN(c.Z)),
        ColorSpace.Hsl => HslToSrgb(NoNaN(c.X), c.Y, c.Z),
        _ => HwbToSrgb(NoNaN(c.X), c.Y, c.Z),
    };

    public static (double R, double G, double B) LchToSrgb(double l, double chroma, double hue)
    {
        var (a, b) = PolarToRectangular(chroma, hue);
        return LabToSrgb(l, a, b);
    }

    public static (double R, double G, double B) OklchToSrgb(double l, double chroma, double hue)
    {
        var (a, b) = PolarToRectangular(chroma, hue);
        return OklabToSrgb(l, a, b);
    }

    public static bool IsPolar(ColorSpace space) => space is ColorSpace.Lch or ColorSpace.Oklch or ColorSpace.Hsl or ColorSpace.Hwb;

    /// <summary>Index of the hue component in a polar space's triple.</summary>
    public static int HueIndex(ColorSpace space) => space is ColorSpace.Hsl or ColorSpace.Hwb ? 0 : 2;

    private static double NoNaN(double hue) => double.IsNaN(hue) ? 0 : hue;

    private static (double, double, double) ToPolar((double L, double A, double B) lab, double achromatic)
    {
        var chroma = Math.Sqrt(lab.A * lab.A + lab.B * lab.B);
        var hue = chroma < achromatic ? double.NaN : (Math.Atan2(lab.B, lab.A) * 180 / Math.PI + 360) % 360;
        return (lab.L, chroma, hue);
    }

    // ---------------------------------------------------------------- building blocks

    private static double Linear(double c) => Math.Abs(c) <= 0.04045 ? c / 12.92 : Math.Sign(c) * Math.Pow((Math.Abs(c) + 0.055) / 1.055, 2.4);

    private static double Gamma(double c) => Math.Abs(c) > 0.0031308 ? Math.Sign(c) * (1.055 * Math.Pow(Math.Abs(c), 1 / 2.4) - 0.055) : 12.92 * c;

    private static (double, double, double) Linear((double R, double G, double B) c) => (Linear(c.R), Linear(c.G), Linear(c.B));

    private static (double, double, double) Gamma((double R, double G, double B) c) => (Gamma(c.R), Gamma(c.G), Gamma(c.B));

    private static (double, double, double) Multiply(double[,] m, (double X, double Y, double Z) v) =>
    (
        m[0, 0] * v.X + m[0, 1] * v.Y + m[0, 2] * v.Z,
        m[1, 0] * v.X + m[1, 1] * v.Y + m[1, 2] * v.Z,
        m[2, 0] * v.X + m[2, 1] * v.Y + m[2, 2] * v.Z
    );

    private static readonly double[,] LinearSrgbToXyz =
    {
        { 0.41239079926595934, 0.357584339383878, 0.1804807884018343 },
        { 0.21263900587151027, 0.715168678767756, 0.07219231536073371 },
        { 0.01933081871559182, 0.11919477979462598, 0.9505321522496607 },
    };

    private static readonly double[,] XyzToLinearSrgb =
    {
        { 3.2409699419045226, -1.537383177570094, -0.4986107602930034 },
        { -0.9692436362808796, 1.8759675015077202, 0.04155505740717559 },
        { 0.05563007969699366, -0.20397695888897652, 1.0569715142428786 },
    };

    private static readonly double[,] D50ToD65Matrix =
    {
        { 0.955473421488075, -0.02309845494876471, 0.06325924320057072 },
        { -0.0283697093338637, 1.0099953980813041, 0.021041441191917323 },
        { 0.012314014864481998, -0.020507649298898964, 1.330365926242124 },
    };

    private static readonly double[,] D65ToD50Matrix =
    {
        { 1.0479297925449969, 0.022946870601609652, -0.05019226628920524 },
        { 0.02962780877005599, 0.9904344267538799, -0.017073799063418826 },
        { -0.009243040646204504, 0.015055191490298152, 0.7518742814281371 },
    };

    private static readonly double[,] XyzToLms =
    {
        { 0.8190224379967030, 0.3619062600528904, -0.1288737815209879 },
        { 0.0329836539323885, 0.9292868615863434, 0.0361446663506424 },
        { 0.0481771893596242, 0.2642395317527308, 0.6335478284694309 },
    };

    private static readonly double[,] LmsToOklab =
    {
        { 0.2104542683093140, 0.7936177747023054, -0.0040720430116193 },
        { 1.9779985324311684, -2.4285922420485799, 0.4505937096174110 },
        { 0.0259040424655478, 0.7827717124575296, -0.8086757549230774 },
    };

    private static readonly double[,] OklabToLms =
    {
        { 1.0000000000000000, 0.3963377773761749, 0.2158037573099136 },
        { 1.0000000000000000, -0.1055613458156586, -0.0638541728258133 },
        { 1.0000000000000000, -0.0894841775298119, -1.2914855480194092 },
    };

    private static readonly double[,] LmsToXyz =
    {
        { 1.2268798758459243, -0.5578149944602171, 0.2813910456659647 },
        { -0.0405757452148008, 1.1122868032803170, -0.0717110580655164 },
        { -0.0763729366746601, -0.4214933324022432, 1.5869240198367816 },
    };

    private static (double, double, double) SrgbLinearToXyzD65((double, double, double) c) => Multiply(LinearSrgbToXyz, c);

    private static (double, double, double) XyzD65ToSrgbLinear((double, double, double) c) => Multiply(XyzToLinearSrgb, c);

    private static (double, double, double) D50ToD65((double, double, double) c) => Multiply(D50ToD65Matrix, c);

    private static (double, double, double) D65ToD50((double, double, double) c) => Multiply(D65ToD50Matrix, c);

    private static readonly (double X, double Y, double Z) D50White = (0.3457 / 0.3585, 1.0, (1.0 - 0.3457 - 0.3585) / 0.3585);
    private const double Kappa = 24389.0 / 27;
    private const double Epsilon = 216.0 / 24389;

    private static (double, double, double) LabToXyzD50(double l, double a, double b)
    {
        var f1 = (l + 16) / 116;
        var f0 = a / 500 + f1;
        var f2 = f1 - b / 200;
        var x = Math.Pow(f0, 3) > Epsilon ? Math.Pow(f0, 3) : (116 * f0 - 16) / Kappa;
        var y = l > Kappa * Epsilon ? Math.Pow((l + 16) / 116, 3) : l / Kappa;
        var z = Math.Pow(f2, 3) > Epsilon ? Math.Pow(f2, 3) : (116 * f2 - 16) / Kappa;
        return (x * D50White.X, y * D50White.Y, z * D50White.Z);
    }

    private static (double, double, double) XyzD50ToLab((double X, double Y, double Z) c)
    {
        static double F(double v) => v > Epsilon ? Math.Cbrt(v) : (Kappa * v + 16) / 116;
        var (f0, f1, f2) = (F(c.X / D50White.X), F(c.Y / D50White.Y), F(c.Z / D50White.Z));
        return (116 * f1 - 16, 500 * (f0 - f1), 200 * (f1 - f2));
    }

    private static (double, double, double) OklabToXyzD65(double l, double a, double b)
    {
        var (lp, mp, sp) = Multiply(OklabToLms, (l, a, b));
        return Multiply(LmsToXyz, (lp * lp * lp, mp * mp * mp, sp * sp * sp));
    }

    private static (double L, double A, double B) SrgbToOklab((double R, double G, double B) rgb)
    {
        var (lms0, lms1, lms2) = Multiply(XyzToLms, SrgbLinearToXyzD65(Linear(rgb)));
        return Multiply(LmsToOklab, (Math.Cbrt(lms0), Math.Cbrt(lms1), Math.Cbrt(lms2)));
    }

    // https://www.w3.org/TR/css-color-4/#hsl-to-rgb (saturation and lightness 0..1)
    public static (double, double, double) HslToSrgb(double hue, double saturation, double lightness)
    {
        hue = (hue % 360 + 360) % 360;
        double F(double n)
        {
            var k = (n + hue / 30) % 12;
            var a = saturation * Math.Min(lightness, 1 - lightness);
            return lightness - a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1));
        }
        return (F(0), F(8), F(4));
    }

    // https://www.w3.org/TR/css-color-4/#hwb-to-rgb (whiteness and blackness 0..1)
    public static (double, double, double) HwbToSrgb(double hue, double white, double black)
    {
        if (white + black >= 1)
        {
            var gray = white / (white + black);
            return (gray, gray, gray);
        }
        var (r, g, b) = HslToSrgb(hue, 1, 0.5);
        var scale = 1 - white - black;
        return (r * scale + white, g * scale + white, b * scale + white);
    }

    // https://www.w3.org/TR/css-color-4/#rgb-to-hsl
    private static (double, double, double) SrgbToHsl((double R, double G, double B) c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        var min = Math.Min(c.R, Math.Min(c.G, c.B));
        var lightness = (min + max) / 2;
        var d = max - min;
        double hue = double.NaN, saturation = 0;
        if (d != 0)
        {
            saturation = lightness is 0 or 1 ? 0 : (max - lightness) / Math.Min(lightness, 1 - lightness);
            hue = max == c.R ? (c.G - c.B) / d + (c.G < c.B ? 6 : 0) : max == c.G ? (c.B - c.R) / d + 2 : (c.R - c.G) / d + 4;
            hue *= 60;
        }
        return (hue, saturation, lightness);
    }

    private static (double, double, double) SrgbToHwb((double R, double G, double B) c)
    {
        var (hue, _, _) = SrgbToHsl(c);
        var white = Math.Min(c.R, Math.Min(c.G, c.B));
        var black = 1 - Math.Max(c.R, Math.Max(c.G, c.B));
        return (white + black >= 1 ? double.NaN : hue, white, black);
    }
}

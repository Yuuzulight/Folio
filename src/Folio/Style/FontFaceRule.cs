using System.Globalization;
using System.Text.RegularExpressions;
using Folio.Css;
using Folio.Resources;

namespace Folio.Style;

/// <summary>https://www.w3.org/TR/css-fonts-4/#font-display-desc</summary>
internal enum FontDisplay
{
    Auto,
    Block,
    Swap,
    Fallback,
    Optional,
}

/// <summary>One entry of an <c>@font-face</c> <c>src</c> list: a URL (resolved) or a <c>local()</c> face name.</summary>
internal sealed record FontFaceSource(string? Url, string? Local);

/// <summary>
/// An <c>@font-face</c> rule (https://www.w3.org/TR/css-fonts-4/#font-face-rule). A null weight, style or stretch
/// is <c>auto</c>: the face's own value. Ranges are inclusive.
/// </summary>
internal sealed partial record FontFaceRule(
    string Family,
    IReadOnlyList<FontFaceSource> Sources,
    (int Min, int Max)? Weight,
    FontStyle? Style,
    (float Min, float Max)? Stretch,
    IReadOnlyList<(int First, int Last)> UnicodeRange,
    FontDisplay Display)
{
    /// <summary>The descriptors of an <c>@font-face</c> block; null without a usable <c>font-family</c> and <c>src</c>.</summary>
    /// <param name="baseUrl">What relative <c>src</c> URLs resolve against: the stylesheet's URL.</param>
    public static FontFaceRule? Parse(string source, AtRule rule, string? baseUrl)
    {
        string? family = null;
        List<FontFaceSource>? sources = null;
        (int, int)? weight = null;
        FontStyle? style = null;
        (float, float)? stretch = null;
        List<(int, int)> ranges = [(0, 0x10FFFF)];
        var display = FontDisplay.Auto;

        // Later descriptors override earlier ones; invalid ones are ignored.
        foreach (var declaration in rule.Declarations)
        {
            var r = new ValueReader(source, declaration.Value);
            switch (declaration.Name)
            {
                case "font-family":
                    if ((r.String() ?? Words(r)) is { } name && r.AtEnd)
                        family = name;
                    break;
                case "src":
                    if (SourceList(r, baseUrl) is { } list)
                        sources = list;
                    break;
                case "font-weight":
                    if (Range(r, WeightValue) is { } w)
                        weight = ((int)w.Min, (int)w.Max);
                    else if (Auto(declaration.Value, source))
                        weight = null;
                    break;
                case "font-style":
                    if (StyleValue(r) is { } s && r.AtEnd)
                        style = s;
                    break;
                case "font-stretch" or "font-width":
                    if (Range(r, StretchValue) is { } st)
                        stretch = st;
                    else if (Auto(declaration.Value, source))
                        stretch = null;
                    break;
                case "unicode-range":
                    if (declaration.Value.Count > 0 && UnicodeRanges(source[declaration.Value[0].Start..declaration.Value[^1].End]) is { } parsed)
                        ranges = parsed;
                    break;
                case "font-display":
                    if (r.Keyword("auto", "block", "swap", "fallback", "optional") is { } d && r.AtEnd)
                        display = Enum.Parse<FontDisplay>(d, ignoreCase: true);
                    break;
            }
        }
        return family is null || sources is null ? null : new FontFaceRule(family, sources, weight, style, stretch, ranges, display);
    }

    private static bool Auto(List<ComponentValue> value, string source) =>
        value.Count > 0 && source[value[0].Start..value[^1].End].Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static string? Words(ValueReader r)
    {
        var words = new List<string>();
        while (r.Ident() is { } word)
            words.Add(word);
        return words.Count == 0 ? null : string.Join(' ', words);
    }

    // [ url() [format()]? [tech()]? | local(<family-name>) ]#; entries with unsupported formats or techs are skipped.
    private static List<FontFaceSource>? SourceList(ValueReader r, string? baseUrl)
    {
        var sources = new List<FontFaceSource>();
        do
        {
            if (r.Function("local") is { } local)
            {
                if ((local.String() ?? Words(local)) is { } name && local.AtEnd)
                    sources.Add(new FontFaceSource(null, name));
            }
            else if (r.Url() is { } url)
            {
                var supported = true;
                if (r.Function("format") is { } format)
                {
                    var name = (format.String() ?? format.Ident())?.ToLowerInvariant();
                    supported = name is "woff" or "woff2" or "truetype" or "opentype" && format.AtEnd;
                }
                if (r.Function("tech") is { } tech)
                {
                    while (tech.Ident() is { } t)
                    {
                        supported &= t.ToLowerInvariant() is "features-opentype" or "features-aat" or "variations";
                        tech.Comma();
                    }
                }
                if (supported && ResourceLoader.Resolve(baseUrl, url) is { } resolved)
                    sources.Add(new FontFaceSource(resolved, null));
            }
            else
            {
                return null;
            }
        } while (r.Comma());
        return r.AtEnd && sources.Count > 0 ? sources : null;
    }

    // One value or "min max" (swapped when reversed, as the spec says).
    private static (float Min, float Max)? Range(ValueReader r, Func<ValueReader, float?> value)
    {
        if (value(r) is not { } first)
            return null;
        var second = value(r) ?? first;
        return r.AtEnd ? (Math.Min(first, second), Math.Max(first, second)) : null;
    }

    private static float? WeightValue(ValueReader r) => r.Keyword("normal", "bold") switch
    {
        "normal" => 400,
        "bold" => 700,
        _ => r.Number() is { } n and >= 1 and <= 1000 ? n : null,
    };

    private static float? StretchValue(ValueReader r)
    {
        if (r.Keyword("ultra-condensed", "extra-condensed", "condensed", "semi-condensed", "normal", "semi-expanded", "expanded", "extra-expanded", "ultra-expanded") is { } keyword)
        {
            return keyword switch
            {
                "ultra-condensed" => 50, "extra-condensed" => 62.5f, "condensed" => 75, "semi-condensed" => 87.5f, "normal" => 100,
                "semi-expanded" => 112.5f, "expanded" => 125, "extra-expanded" => 150, _ => 200,
            };
        }
        return r.LengthPercentage(allowPercent: true, nonNegative: true) is PercentageValue p ? p.Percent : null;
    }

    // normal | italic | oblique <angle>{0,2}; the angle is not used for matching yet.
    private static FontStyle? StyleValue(ValueReader r) => r.Keyword("normal", "italic", "oblique") switch
    {
        "normal" => FontStyle.Normal,
        "italic" => FontStyle.Italic,
        "oblique" => SkipAngles(r),
        _ => null,
    };

    private static FontStyle SkipAngles(ValueReader r)
    {
        r.Rest();
        return FontStyle.Oblique;
    }

    /// <summary>https://www.w3.org/TR/css-fonts-4/#unicode-range-desc: <c>U+X</c>, <c>U+X-Y</c> and <c>U+X??</c>, comma-separated.</summary>
    public static List<(int First, int Last)>? UnicodeRanges(string text)
    {
        var ranges = new List<(int, int)>();
        foreach (var part in text.Split(','))
        {
            var match = UnicodeRangePattern().Match(part.Trim());
            if (!match.Success)
                return null;
            var start = match.Groups[1].Value;
            int first, last;
            if (start.Contains('?'))
            {
                if (match.Groups[2].Success || start.TrimEnd('?').Contains('?'))
                    return null;
                first = int.Parse(start.Replace('?', '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                last = int.Parse(start.Replace('?', 'F'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else
            {
                first = int.Parse(start, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                last = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture) : first;
            }
            if (last > 0x10FFFF || first > last)
                return null;
            ranges.Add((first, last));
        }
        return ranges;
    }

    [GeneratedRegex(@"^[uU]\+([0-9a-fA-F?]{1,6})(?:-([0-9a-fA-F]{1,6}))?$")]
    private static partial Regex UnicodeRangePattern();

    public bool Covers(int codePoint)
    {
        foreach (var (first, last) in UnicodeRange)
        {
            if (codePoint >= first && codePoint <= last)
                return true;
        }
        return false;
    }
}

using Folio.Style;

namespace Folio.Css;

/// <summary>A specified <c>animation-name</c> item: a keyframes name, or null for <c>none</c>.</summary>
internal readonly record struct KeyframesName(string? Name);

/// <summary>
/// The animation properties M1 needs to snap animations to their end state
/// (docs/study/04-cascade-and-computed-values.md): <c>animation-name</c>, <c>animation-iteration-count</c>,
/// <c>animation-direction</c>, <c>animation-fill-mode</c>, and the <c>animation</c> shorthand, which also accepts
/// durations, delays, easing functions and play states.
/// </summary>
// ponytail: duration, delay, easing and play state are read by the shorthand but not kept; they matter once there is
// a timeline (M2), and their longhands arrive with it.
internal static class AnimationProperties
{
    public static IEnumerable<Property> Rows =>
    [
        Properties.Layers<KeyframesName, string?>(PropertyId.AnimationName, "animation-name", "none", Name, (x, _) => x.Name,
            s => s.Animation.Names, (b, v) => b.Animation = b.Animation with { Names = v }),
        Properties.Layers<float, float>(PropertyId.AnimationIterationCount, "animation-iteration-count", "1", IterationCount, (x, _) => x,
            s => s.Animation.IterationCounts, (b, v) => b.Animation = b.Animation with { IterationCounts = v }),
        Properties.Layers<AnimationDirection, AnimationDirection>(PropertyId.AnimationDirection, "animation-direction", "normal", Direction, (x, _) => x,
            s => s.Animation.Directions, (b, v) => b.Animation = b.Animation with { Directions = v }),
        Properties.Layers<AnimationFillMode, AnimationFillMode>(PropertyId.AnimationFillMode, "animation-fill-mode", "none", FillMode, (x, _) => x,
            s => s.Animation.FillModes, (b, v) => b.Animation = b.Animation with { FillModes = v }),
    ];

    private static readonly PropertyId[] Longhands =
        [PropertyId.AnimationName, PropertyId.AnimationIterationCount, PropertyId.AnimationDirection, PropertyId.AnimationFillMode];

    public static IEnumerable<(string Name, Properties.Shorthand Shorthand)> Shorthands =>
    [
        ("animation", new Properties.Shorthand(Longhands, Shorthand)),
        ("-webkit-animation", new Properties.Shorthand(Longhands, Shorthand)),
        Alias("-webkit-animation-name", PropertyId.AnimationName),
        Alias("-webkit-animation-iteration-count", PropertyId.AnimationIterationCount),
        Alias("-webkit-animation-direction", PropertyId.AnimationDirection),
        Alias("-webkit-animation-fill-mode", PropertyId.AnimationFillMode),
    ];

    private static (string, Properties.Shorthand) Alias(string name, PropertyId id) =>
        (name, new Properties.Shorthand([id], r => Properties.Get(id).Parse(r) is { } value ? [(id, value)] : null));

    // none | <custom-ident> | <string> (https://www.w3.org/TR/css-animations-1/#animation-name)
    private static KeyframesName? Name(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new KeyframesName(null);
        return KeyframesNameOf(r) is { } name ? new KeyframesName(name) : null;
    }

    /// <summary>A keyframes name as <c>@keyframes</c> and <c>animation-name</c> write it: a custom identifier or a string.</summary>
    public static string? KeyframesNameOf(ValueReader r)
    {
        var mark = r.Mark;
        if (r.Ident() is { } ident)
        {
            if (ident.ToLowerInvariant() is not ("none" or "initial" or "inherit" or "unset" or "revert" or "revert-layer" or "default"))
                return ident;
            r.Reset(mark);
            return null;
        }
        return r.String();
    }

    // infinite | <number [0,∞]>
    private static float? IterationCount(ValueReader r) =>
        r.Keyword("infinite") is not null ? float.PositiveInfinity : r.Number(nonNegative: true);

    private static AnimationDirection? Direction(ValueReader r) => r.Keyword("normal", "reverse", "alternate", "alternate-reverse") switch
    {
        "normal" => AnimationDirection.Normal,
        "reverse" => AnimationDirection.Reverse,
        "alternate" => AnimationDirection.Alternate,
        "alternate-reverse" => AnimationDirection.AlternateReverse,
        _ => null,
    };

    private static AnimationFillMode? FillMode(ValueReader r) => r.Keyword("none", "forwards", "backwards", "both") switch
    {
        "none" => AnimationFillMode.None,
        "forwards" => AnimationFillMode.Forwards,
        "backwards" => AnimationFillMode.Backwards,
        "both" => AnimationFillMode.Both,
        _ => null,
    };

    // <single-animation>#: each part at most once, in any order. A keyword another part accepts goes to that part
    // before it can be a name (https://www.w3.org/TR/css-animations-1/#animation).
    private static List<(PropertyId, CssValue)>? Shorthand(ValueReader r)
    {
        var (names, counts, directions, fills) = (new List<KeyframesName>(), new List<float>(), new List<AnimationDirection>(), new List<AnimationFillMode>());
        do
        {
            KeyframesName? name = null;
            float? count = null;
            AnimationDirection? direction = null;
            AnimationFillMode? fill = null;
            var (times, easing, playState) = (0, false, false);
            while (!r.AtEnd && !r.PeekComma())
            {
                if (times < 2 && Time(r))
                    times++;
                else if (!easing && Easing(r))
                    easing = true;
                else if (count is null && IterationCount(r) is { } c)
                    count = c;
                else if (direction is null && Direction(r) is { } d)
                    direction = d;
                else if (fill is null && FillMode(r) is { } f)
                    fill = f;
                else if (!playState && r.Keyword("running", "paused") is not null)
                    playState = true;
                else if (name is null && Name(r) is { } n)
                    name = n;
                else
                    return null;
            }
            names.Add(name ?? new KeyframesName(null));
            counts.Add(count ?? 1);
            directions.Add(direction ?? AnimationDirection.Normal);
            fills.Add(fill ?? AnimationFillMode.None);
        }
        while (r.Comma());
        return r.AtEnd
            ?
            [
                (PropertyId.AnimationName, new LayerListValue<KeyframesName>(names)),
                (PropertyId.AnimationIterationCount, new LayerListValue<float>(counts)),
                (PropertyId.AnimationDirection, new LayerListValue<AnimationDirection>(directions)),
                (PropertyId.AnimationFillMode, new LayerListValue<AnimationFillMode>(fills)),
            ]
            : null;
    }

    // <time>: seconds or milliseconds (a duration or a delay; the shorthand only needs to consume them).
    private static bool Time(ValueReader r)
    {
        var mark = r.Mark;
        if (r.Dimension() is { Unit: var unit } && unit.ToLowerInvariant() is "s" or "ms")
            return true;
        r.Reset(mark);
        return false;
    }

    // <easing-function>: a keyword or one of the easing functions (consumed, not interpreted).
    private static bool Easing(ValueReader r) =>
        r.Keyword("linear", "ease", "ease-in", "ease-out", "ease-in-out", "step-start", "step-end") is not null
        || r.Function("cubic-bezier") is not null || r.Function("steps") is not null || r.Function("linear") is not null;

    /// <summary>
    /// A keyframe selector list (https://www.w3.org/TR/css-animations-1/#typedef-keyframe-selector): <c>from</c>,
    /// <c>to</c> or percentages, as offsets from 0 to 1; null if any item is invalid (the keyframe is then ignored).
    /// </summary>
    public static List<float>? KeyframeOffsets(ValueReader r)
    {
        var offsets = new List<float>();
        do
        {
            if (r.Keyword("from", "to") is { } keyword)
                offsets.Add(keyword == "from" ? 0 : 1);
            else if (r.LengthPercentage() is PercentageValue { Percent: >= 0 and <= 100 } percent)
                offsets.Add(percent.Percent / 100);
            else
                return null;
        }
        while (r.Comma());
        return r.AtEnd ? offsets : null;
    }
}

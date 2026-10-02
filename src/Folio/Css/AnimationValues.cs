using Folio.Style;

namespace Folio.Css;

/// <summary>A specified <c>animation-name</c> item: a keyframes name, or null for <c>none</c>.</summary>
internal readonly record struct KeyframesName(string? Name);

/// <summary>A specified easing function item.</summary>
internal readonly record struct EasingItem(Easing Easing);

/// <summary>A specified <c>transition-property</c> item: a property name, <c>all</c> or <c>none</c>, lowercased.</summary>
internal readonly record struct TransitionTarget(string Name);

/// <summary>
/// The animation properties (https://www.w3.org/TR/css-animations-1/#animation-definition) and the transition
/// properties (https://www.w3.org/TR/css-transitions-1/#transitions), with their shorthands and <c>-webkit-</c> names.
/// Each longhand is a comma-separated list.
/// </summary>
internal static class AnimationProperties
{
    public static Properties.LazyRow[] Rows =>
    [
        new(PropertyId.AnimationName, "animation-name", static () => Properties.Layers<KeyframesName, string?>(PropertyId.AnimationName, "animation-name", "none", Name, (x, _) => x.Name,
            s => s.Animation.Names, (b, v) => b.Animation = b.Animation with { Names = v })),
        new(PropertyId.AnimationIterationCount, "animation-iteration-count", static () => Properties.Layers<float, float>(PropertyId.AnimationIterationCount, "animation-iteration-count", "1", IterationCount, (x, _) => x,
            s => s.Animation.IterationCounts, (b, v) => b.Animation = b.Animation with { IterationCounts = v })),
        new(PropertyId.AnimationDirection, "animation-direction", static () => Properties.Layers<AnimationDirection, AnimationDirection>(PropertyId.AnimationDirection, "animation-direction", "normal", Direction, (x, _) => x,
            s => s.Animation.Directions, (b, v) => b.Animation = b.Animation with { Directions = v })),
        new(PropertyId.AnimationFillMode, "animation-fill-mode", static () => Properties.Layers<AnimationFillMode, AnimationFillMode>(PropertyId.AnimationFillMode, "animation-fill-mode", "none", FillMode, (x, _) => x,
            s => s.Animation.FillModes, (b, v) => b.Animation = b.Animation with { FillModes = v })),
        new(PropertyId.AnimationDuration, "animation-duration", static () => Properties.Layers<float, float>(PropertyId.AnimationDuration, "animation-duration", "0s", Duration, (x, _) => x,
            s => s.Animation.Durations, (b, v) => b.Animation = b.Animation with { Durations = v })),
        new(PropertyId.AnimationDelay, "animation-delay", static () => Properties.Layers<float, float>(PropertyId.AnimationDelay, "animation-delay", "0s", r => Time(r), (x, _) => x,
            s => s.Animation.Delays, (b, v) => b.Animation = b.Animation with { Delays = v })),
        new(PropertyId.AnimationTimingFunction, "animation-timing-function", static () => Properties.Layers<EasingItem, Easing>(PropertyId.AnimationTimingFunction, "animation-timing-function", "ease", EasingOf, (x, _) => x.Easing,
            s => s.Animation.TimingFunctions, (b, v) => b.Animation = b.Animation with { TimingFunctions = v })),
        new(PropertyId.AnimationPlayState, "animation-play-state", static () => Properties.Layers<AnimationPlayState, AnimationPlayState>(PropertyId.AnimationPlayState, "animation-play-state", "running", PlayState, (x, _) => x,
            s => s.Animation.PlayStates, (b, v) => b.Animation = b.Animation with { PlayStates = v })),
        new(PropertyId.TransitionProperty, "transition-property", static () => new Property<IReadOnlyList<string>>(PropertyId.TransitionProperty, "transition-property", false, "all", TransitionProperty,
            (v, _) => ((LayerListValue<TransitionTarget>)v).Items.Select(x => x.Name).ToList(),
            s => s.Transition.Properties, (b, v) => b.Transition = b.Transition with { Properties = v })),
        new(PropertyId.TransitionDuration, "transition-duration", static () => Properties.Layers<float, float>(PropertyId.TransitionDuration, "transition-duration", "0s", r => Time(r, nonNegative: true), (x, _) => x,
            s => s.Transition.Durations, (b, v) => b.Transition = b.Transition with { Durations = v })),
        new(PropertyId.TransitionTimingFunction, "transition-timing-function", static () => Properties.Layers<EasingItem, Easing>(PropertyId.TransitionTimingFunction, "transition-timing-function", "ease", EasingOf, (x, _) => x.Easing,
            s => s.Transition.TimingFunctions, (b, v) => b.Transition = b.Transition with { TimingFunctions = v })),
        new(PropertyId.TransitionDelay, "transition-delay", static () => Properties.Layers<float, float>(PropertyId.TransitionDelay, "transition-delay", "0s", r => Time(r), (x, _) => x,
            s => s.Transition.Delays, (b, v) => b.Transition = b.Transition with { Delays = v })),
        new(PropertyId.TransitionBehavior, "transition-behavior", static () => Properties.Layers<TransitionBehavior, TransitionBehavior>(PropertyId.TransitionBehavior, "transition-behavior", "normal", Behavior, (x, _) => x,
            s => s.Transition.Behaviors, (b, v) => b.Transition = b.Transition with { Behaviors = v })),
    ];

    private static readonly PropertyId[] AnimationLonghands =
    [
        PropertyId.AnimationName, PropertyId.AnimationDuration, PropertyId.AnimationTimingFunction, PropertyId.AnimationDelay,
        PropertyId.AnimationIterationCount, PropertyId.AnimationDirection, PropertyId.AnimationFillMode, PropertyId.AnimationPlayState,
    ];

    private static readonly PropertyId[] TransitionLonghands =
    [
        PropertyId.TransitionProperty, PropertyId.TransitionDuration, PropertyId.TransitionTimingFunction, PropertyId.TransitionDelay,
        PropertyId.TransitionBehavior,
    ];

    /// <summary>Whether a property is one of the animation or transition longhands, which keyframes ignore.</summary>
    public static bool IsAnimationOrTransition(PropertyId id) => AnimationLonghands.Contains(id) || TransitionLonghands.Contains(id);

    public static IEnumerable<(string Name, Properties.Shorthand Shorthand)> Shorthands =>
    [
        ("animation", new Properties.Shorthand(AnimationLonghands, Animation)),
        ("-webkit-animation", new Properties.Shorthand(AnimationLonghands, Animation)),
        ("transition", new Properties.Shorthand(TransitionLonghands, Transition)),
        ("-webkit-transition", new Properties.Shorthand(TransitionLonghands, Transition)),
        .. AnimationLonghands.Concat(TransitionLonghands).Where(id => id != PropertyId.TransitionBehavior)
            .Select(id => Alias("-webkit-" + Properties.Get(id).Name, id)),
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
            if (!IsReserved(ident))
                return ident;
            r.Reset(mark);
            return null;
        }
        return r.String();
    }

    private static bool IsReserved(string ident) =>
        ident.ToLowerInvariant() is "none" or "initial" or "inherit" or "unset" or "revert" or "revert-layer" or "default";

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

    private static AnimationPlayState? PlayState(ValueReader r) => r.Keyword("running", "paused") switch
    {
        "running" => AnimationPlayState.Running,
        "paused" => AnimationPlayState.Paused,
        _ => null,
    };

    private static TransitionBehavior? Behavior(ValueReader r) => r.Keyword("normal", "allow-discrete") switch
    {
        "normal" => TransitionBehavior.Normal,
        "allow-discrete" => TransitionBehavior.AllowDiscrete,
        _ => null,
    };

    // auto | <time [0s,∞]>: auto is 0s for CSS animations (https://drafts.csswg.org/css-animations-2/#animation-duration).
    private static float? Duration(ValueReader r) => r.Keyword("auto") is not null ? 0 : Time(r, nonNegative: true);

    /// <summary>A &lt;time&gt; in seconds (https://www.w3.org/TR/css-values-4/#time).</summary>
    private static float? Time(ValueReader r, bool nonNegative = false)
    {
        var mark = r.Mark;
        if (r.Dimension() is { } d && d.Unit.ToLowerInvariant() is "s" or "ms" && (!nonNegative || d.Value >= 0))
            return d.Unit.Equals("ms", StringComparison.OrdinalIgnoreCase) ? d.Value / 1000 : d.Value;
        r.Reset(mark);
        return null;
    }

    private static EasingItem? EasingOf(ValueReader r) => EasingFunction(r) is { } easing ? new EasingItem(easing) : null;

    /// <summary>An &lt;easing-function&gt; (https://www.w3.org/TR/css-easing-2/#typedef-easing-function); null if invalid.</summary>
    public static Easing? EasingFunction(ValueReader r)
    {
        switch (r.Keyword("linear", "ease", "ease-in", "ease-out", "ease-in-out", "step-start", "step-end"))
        {
            case "linear": return Easing.Linear;
            case "ease": return Easing.Ease;
            case "ease-in": return new CubicBezierEasing(0.42, 0, 1, 1);
            case "ease-out": return new CubicBezierEasing(0, 0, 0.58, 1);
            case "ease-in-out": return new CubicBezierEasing(0.42, 0, 0.58, 1);
            case "step-start": return new StepsEasing(1, StepPosition.JumpStart);
            case "step-end": return new StepsEasing(1, StepPosition.JumpEnd);
        }
        var mark = r.Mark;
        Easing? easing = null;
        if (r.Function("cubic-bezier") is { } bezier)
            easing = CubicBezier(bezier);
        else if (r.Function("steps") is { } steps)
            easing = Steps(steps);
        else if (r.Function("linear") is { } linear)
            easing = Linear(linear);
        if (easing is null)
            r.Reset(mark);
        return easing;
    }

    // cubic-bezier(<number [0,1]>, <number>, <number [0,1]>, <number>)
    private static Easing? CubicBezier(ValueReader r)
    {
        var p = new float[4];
        for (var i = 0; i < 4; i++)
        {
            if (i > 0 && !r.Comma() || r.Number() is not { } n || i % 2 == 0 && n is < 0 or > 1)
                return null;
            p[i] = n;
        }
        return r.AtEnd ? new CubicBezierEasing(p[0], p[1], p[2], p[3]) : null;
    }

    // steps(<integer>, <step-position>?): jump-none needs at least two steps.
    private static Easing? Steps(ValueReader r)
    {
        if (r.Integer() is not { } count || count < 1)
            return null;
        var position = StepPosition.JumpEnd;
        if (r.Comma())
        {
            switch (r.Keyword("jump-start", "jump-end", "jump-none", "jump-both", "start", "end"))
            {
                case "jump-start" or "start": position = StepPosition.JumpStart; break;
                case "jump-end" or "end": position = StepPosition.JumpEnd; break;
                case "jump-none": position = StepPosition.JumpNone; break;
                case "jump-both": position = StepPosition.JumpBoth; break;
                default: return null;
            }
        }
        return r.AtEnd && !(position == StepPosition.JumpNone && count < 2) ? new StepsEasing(count, position) : null;
    }

    // linear([<number> && <percentage>{0,2}]#): a stop with two percentages is two control points; at least two in all.
    private static Easing? Linear(ValueReader r)
    {
        var stops = new List<(double Output, double? Input)>();
        do
        {
            float? output = null;
            var inputs = new List<double>();
            while (!r.AtEnd && !r.PeekComma())
            {
                if (output is null && r.Number() is { } n)
                    output = n;
                else if (inputs.Count < 2 && r.LengthPercentage() is PercentageValue p)
                    inputs.Add(p.Percent / 100);
                else
                    return null;
            }
            if (output is not { } value)
                return null;
            if (inputs.Count == 0)
                stops.Add((value, null));
            foreach (var input in inputs)
                stops.Add((value, input));
        }
        while (r.Comma());
        return r.AtEnd && stops.Count >= 2 ? LinearEasing.FromStops(stops) : null;
    }

    // none | [all | <custom-ident>]#: none only on its own.
    private static CssValue? TransitionProperty(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return r.AtEnd ? new LayerListValue<TransitionTarget>([new TransitionTarget("none")]) : null;
        return BackgroundParsing.List(r, Target);
    }

    private static TransitionTarget? Target(ValueReader r)
    {
        var mark = r.Mark;
        if (r.Ident() is { } ident && !IsReserved(ident))
            return new TransitionTarget(ident.ToLowerInvariant());
        r.Reset(mark);
        return null;
    }

    // <single-animation>#: each part at most once, in any order; the first time is the duration and the second the delay.
    // A keyword another part accepts goes to that part before it can be a name (https://www.w3.org/TR/css-animations-1/#animation).
    private static List<(PropertyId, CssValue)>? Animation(ValueReader r)
    {
        var (names, durations, easings, delays) = (new List<KeyframesName>(), new List<float>(), new List<EasingItem>(), new List<float>());
        var (counts, directions, fills, states) = (new List<float>(), new List<AnimationDirection>(), new List<AnimationFillMode>(), new List<AnimationPlayState>());
        do
        {
            KeyframesName? name = null;
            float? duration = null, delay = null, count = null;
            EasingItem? easing = null;
            AnimationDirection? direction = null;
            AnimationFillMode? fill = null;
            AnimationPlayState? state = null;
            while (!r.AtEnd && !r.PeekComma())
            {
                if (duration is null && Duration(r) is { } d)
                    duration = d;
                else if (delay is null && duration is not null && Time(r) is { } t)
                    delay = t;
                else if (easing is null && EasingOf(r) is { } e)
                    easing = e;
                else if (count is null && IterationCount(r) is { } c)
                    count = c;
                else if (direction is null && Direction(r) is { } dir)
                    direction = dir;
                else if (fill is null && FillMode(r) is { } f)
                    fill = f;
                else if (state is null && PlayState(r) is { } s)
                    state = s;
                else if (name is null && Name(r) is { } n)
                    name = n;
                else
                    return null;
            }
            names.Add(name ?? new KeyframesName(null));
            durations.Add(duration ?? 0);
            easings.Add(easing ?? new EasingItem(Easing.Ease));
            delays.Add(delay ?? 0);
            counts.Add(count ?? 1);
            directions.Add(direction ?? AnimationDirection.Normal);
            fills.Add(fill ?? AnimationFillMode.None);
            states.Add(state ?? AnimationPlayState.Running);
        }
        while (r.Comma());
        return r.AtEnd
            ?
            [
                (PropertyId.AnimationName, new LayerListValue<KeyframesName>(names)),
                (PropertyId.AnimationDuration, new LayerListValue<float>(durations)),
                (PropertyId.AnimationTimingFunction, new LayerListValue<EasingItem>(easings)),
                (PropertyId.AnimationDelay, new LayerListValue<float>(delays)),
                (PropertyId.AnimationIterationCount, new LayerListValue<float>(counts)),
                (PropertyId.AnimationDirection, new LayerListValue<AnimationDirection>(directions)),
                (PropertyId.AnimationFillMode, new LayerListValue<AnimationFillMode>(fills)),
                (PropertyId.AnimationPlayState, new LayerListValue<AnimationPlayState>(states)),
            ]
            : null;
    }

    // <single-transition>#: [none | <single-transition-property>] || <time> || <easing-function> || <time> ||
    // <transition-behavior-value>, none only in a list of one (https://www.w3.org/TR/css-transitions-1/#transition-shorthand-property).
    private static List<(PropertyId, CssValue)>? Transition(ValueReader r)
    {
        var (targets, durations, easings, delays, behaviors) =
            (new List<TransitionTarget>(), new List<float>(), new List<EasingItem>(), new List<float>(), new List<TransitionBehavior>());
        do
        {
            TransitionTarget? target = null;
            float? duration = null, delay = null;
            EasingItem? easing = null;
            TransitionBehavior? behavior = null;
            while (!r.AtEnd && !r.PeekComma())
            {
                if (duration is null && Time(r, nonNegative: true) is { } d)
                    duration = d;
                else if (delay is null && duration is not null && Time(r) is { } t)
                    delay = t;
                else if (easing is null && EasingOf(r) is { } e)
                    easing = e;
                else if (behavior is null && Behavior(r) is { } b)
                    behavior = b;
                else if (target is null && r.Keyword("none") is not null)
                    target = new TransitionTarget("none");
                else if (target is null && Target(r) is { } p)
                    target = p;
                else
                    return null;
            }
            targets.Add(target ?? new TransitionTarget("all"));
            durations.Add(duration ?? 0);
            easings.Add(easing ?? new EasingItem(Easing.Ease));
            delays.Add(delay ?? 0);
            behaviors.Add(behavior ?? TransitionBehavior.Normal);
        }
        while (r.Comma());
        if (!r.AtEnd || targets.Count > 1 && targets.Any(t => t.Name == "none"))
            return null;
        return
        [
            (PropertyId.TransitionProperty, new LayerListValue<TransitionTarget>(targets)),
            (PropertyId.TransitionDuration, new LayerListValue<float>(durations)),
            (PropertyId.TransitionTimingFunction, new LayerListValue<EasingItem>(easings)),
            (PropertyId.TransitionDelay, new LayerListValue<float>(delays)),
            (PropertyId.TransitionBehavior, new LayerListValue<TransitionBehavior>(behaviors)),
        ];
    }

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

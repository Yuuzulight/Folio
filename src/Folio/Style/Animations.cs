using Folio.Css;
using Folio.Dom;

namespace Folio.Style;

internal enum AnimationDirection { Normal, Reverse, Alternate, AlternateReverse }

internal enum AnimationFillMode { None, Forwards, Backwards, Both }

internal enum AnimationPlayState { Running, Paused }

internal enum TransitionBehavior { Normal, AllowDiscrete }

/// <summary>
/// The animation properties (not inherited): one entry per <c>animation-name</c>, the other lists repeating when
/// shorter. A null name is <c>none</c>. Durations and delays are in seconds.
/// </summary>
internal sealed record AnimationGroup(IReadOnlyList<string?> Names, IReadOnlyList<float> IterationCounts,
                                      IReadOnlyList<AnimationDirection> Directions, IReadOnlyList<AnimationFillMode> FillModes)
{
    public static AnimationGroup Initial { get; } = new([null], [1], [AnimationDirection.Normal], [AnimationFillMode.None]);

    public IReadOnlyList<float> Durations { get; init; } = [0];

    public IReadOnlyList<float> Delays { get; init; } = [0];

    public IReadOnlyList<Easing> TimingFunctions { get; init; } = [Easing.Ease];

    public IReadOnlyList<AnimationPlayState> PlayStates { get; init; } = [AnimationPlayState.Running];
}

/// <summary>
/// The transition properties (not inherited), one entry per <c>transition-property</c> item (a lowercased property
/// name, <c>all</c> or <c>none</c>). Durations and delays are in seconds. Transitions start in M3, when
/// styles can change.
/// </summary>
internal sealed record TransitionGroup(IReadOnlyList<string> Properties, IReadOnlyList<float> Durations, IReadOnlyList<Easing> TimingFunctions,
                                       IReadOnlyList<float> Delays, IReadOnlyList<TransitionBehavior> Behaviors)
{
    public static TransitionGroup Initial { get; } = new(["all"], [0], [Easing.Ease], [0], [TransitionBehavior.Normal]);
}

/// <summary>One keyframe block of a <c>@keyframes</c> rule: its offsets (0 to 1) and declarations.</summary>
internal sealed record Keyframe(IReadOnlyList<float> Offsets, IReadOnlyList<CascadeDeclaration> Declarations)
{
    /// <summary>The block's <c>animation-timing-function</c>: the easing from this keyframe to the next; null for the animation's own.</summary>
    public Easing? Easing { get; init; }
}

/// <summary>
/// CSS animations sampled at a time (https://www.w3.org/TR/css-animations-1/, timing from
/// https://www.w3.org/TR/web-animations-1/#timing-model): each animation's keyframes are computed in the element's
/// context and interpolated per property, later animations winning. Without a time the document is settled, as the
/// conformance references are: every animation runs with no duration and no delay, so one that fills forwards shows
/// where it ends and any other has no effect.
/// </summary>
// ponytail: every animation starts at time 0 and a paused one stays at its start; animations that start or pause
// later wait for style changes (M3). A property that depends on another animated one (em on an animated font-size)
// uses the keyframes' values of both, not the interpolated one.
internal static class Animations
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<Keyframe>, Dictionary<PropertyId, List<(float Offset, Keyframe? Block)>>> ByProperty = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<Keyframe>, Dictionary<string, List<(float Offset, Keyframe? Block)>>> ByCustomProperty = [];

    /// <summary>
    /// The element's style with its animations applied at <paramref name="time"/> (seconds on the document timeline;
    /// null for the settled document), or null when no animation has an effect.
    /// </summary>
    /// <param name="keyframeStyle">The element's style with a keyframe block's declarations in the animation origin of the cascade.</param>
    /// <param name="styleWith">The element's style with these declarations in the animation origin, for animated custom properties.</param>
    /// <param name="registered">The registered custom properties, whose values interpolate by their syntax.</param>
    public static ComputedStyle? Sample(ComputedStyle underlying, ComputedStyle parent, IReadOnlyDictionary<string, List<Keyframe>> keyframes,
                                        double? time, Func<Keyframe, ComputedStyle> keyframeStyle, Dictionary<object, object>? groups = null,
                                        Func<IReadOnlyList<CascadeDeclaration>, ComputedStyle>? styleWith = null,
                                        IReadOnlyDictionary<string, RegisteredProperty>? registered = null)
    {
        var group = underlying.Animation;
        if (ReferenceEquals(group, AnimationGroup.Initial) || keyframes.Count == 0)
            return null;
        // Each animation's keyframes, progress and easing.
        var running = new List<(List<Keyframe> Blocks, double Progress, Easing Easing)>();
        for (var i = 0; i < group.Names.Count; i++)
        {
            if (group.Names[i] is not { } name || !keyframes.TryGetValue(name, out var blocks)
                || Progress(time is { } t && group.PlayStates[i % group.PlayStates.Count] == AnimationPlayState.Running ? t : 0,
                    time is null ? 0 : group.Durations[i % group.Durations.Count], time is null ? 0 : group.Delays[i % group.Delays.Count],
                    group.IterationCounts[i % group.IterationCounts.Count], group.Directions[i % group.Directions.Count],
                    group.FillModes[i % group.FillModes.Count]) is not { } progress)
                continue;
            running.Add((blocks, progress, group.TimingFunctions[i % group.TimingFunctions.Count]));
        }

        // Custom properties first (https://www.w3.org/TR/css-properties-values-api-1/#animation-behavior-of-custom-properties):
        // registered ones interpolate by their syntax, others flip half way. The element's style is then computed again
        // with their values, so everything that uses them with var() follows.
        var custom = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (blocks, progress, easing) in running)
        {
            foreach (var (name, frames) in ByCustomProperty.GetValue(blocks, PerCustomProperty))
            {
                var (from, to, p) = Around(frames, progress, easing);
                var (a, b) = (Style(from.Block).Custom.GetValueOrDefault(name), Style(to.Block).Custom.GetValueOrDefault(name));
                custom[name] = a is not null && b is not null && registered?.GetValueOrDefault(name) is { } registration
                    && registration.Syntax.Interpolate(a, b, p) is { } value ? value : p < 0.5 ? a : b;
            }
        }
        if (custom.Count > 0 && styleWith is not null)
            underlying = styleWith([.. custom.Select(c => new CascadeDeclaration(default, null, c.Key,
                new CustomProperties.Declared(c.Value, c.Value is null ? CssWideKeyword.Unset : null), false))]);

        StyleBuilder? builder = null;
        foreach (var (blocks, progress, easing) in running)
        {
            foreach (var (id, frames) in ByProperty.GetValue(blocks, PerProperty))
            {
                var (from, to, p) = Around(frames, progress, easing);
                Properties.Get(id).Interpolate(builder ??= StyleBuilder.From(underlying, parent), Style(from.Block), Style(to.Block), p);
            }
        }
        return builder?.Build(groups) ?? (custom.Count > 0 && styleWith is not null ? underlying : null);

        ComputedStyle Style(Keyframe? block) => block is null ? underlying : keyframeStyle(block);
    }

    // The keyframes around the progress (at or past the last one, the last pair) and the eased progress between them.
    private static ((float Offset, Keyframe? Block) From, (float Offset, Keyframe? Block) To, double Progress) Around(
        List<(float Offset, Keyframe? Block)> frames, double progress, Easing easing)
    {
        var a = 0;
        while (a < frames.Count - 2 && frames[a + 1].Offset <= progress)
            a++;
        var (from, to) = (frames[a], frames[a + 1]);
        var local = (progress - from.Offset) / (to.Offset - from.Offset);
        return (from, to, (from.Block?.Easing ?? easing).Apply(local));
    }

    /// <summary>Whether any animation of the style still changes after <paramref name="time"/>: it runs and has not ended.</summary>
    public static bool IsRunning(AnimationGroup group, IReadOnlyDictionary<string, List<Keyframe>> keyframes, double time)
    {
        for (var i = 0; i < group.Names.Count; i++)
        {
            var (duration, iterations) = (group.Durations[i % group.Durations.Count], group.IterationCounts[i % group.IterationCounts.Count]);
            if (group.Names[i] is { } name && keyframes.ContainsKey(name) && group.PlayStates[i % group.PlayStates.Count] == AnimationPlayState.Running
                && duration > 0 && iterations > 0 && time < group.Delays[i % group.Delays.Count] + duration * iterations)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Properties whose animation only changes painting (docs/study/14-invalidation.md): no box, line or position
    /// depends on them, and they are not inherited, so a frame restyles only the animated element and rebuilds the
    /// display list from the last layout.
    /// </summary>
    private static readonly HashSet<PropertyId> PaintOnly =
    [
        PropertyId.Opacity, PropertyId.Transform, PropertyId.Translate, PropertyId.Rotate, PropertyId.Scale, PropertyId.TransformOrigin,
        PropertyId.Filter, PropertyId.BackdropFilter, PropertyId.BackgroundColor, PropertyId.BackgroundPosition, PropertyId.BoxShadow,
        PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor, PropertyId.OutlineColor,
    ];

    /// <summary>Whether every property the style's animations animate is paint-only.</summary>
    public static bool AnimatesPaintOnly(AnimationGroup group, IReadOnlyDictionary<string, List<Keyframe>> keyframes) =>
        group.Names.All(name => name is null || !keyframes.TryGetValue(name, out var blocks)
            || ByProperty.GetValue(blocks, PerProperty).Keys.All(PaintOnly.Contains) && ByCustomProperty.GetValue(blocks, PerCustomProperty).Count == 0);

    // Each animated property's keyframes in offset order, the last block at an offset winning, with the underlying value
    // (null) at 0 and 1 where no block sets the property (https://www.w3.org/TR/css-animations-1/#keyframes).
    private static Dictionary<PropertyId, List<(float Offset, Keyframe? Block)>> PerProperty(List<Keyframe> blocks) =>
        Frames(blocks, block => block.Declarations.Where(d => d.CustomName is null).Select(d => d.Id));

    // The same for custom properties, by name.
    private static Dictionary<string, List<(float Offset, Keyframe? Block)>> PerCustomProperty(List<Keyframe> blocks) =>
        Frames(blocks, block => block.Declarations.Where(d => d.CustomName is not null).Select(d => d.CustomName!));

    private static Dictionary<TKey, List<(float Offset, Keyframe? Block)>> Frames<TKey>(List<Keyframe> blocks, Func<Keyframe, IEnumerable<TKey>> keys)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, List<(float Offset, Keyframe? Block)>>();
        foreach (var block in blocks)
        {
            foreach (var id in keys(block).Distinct())
            {
                if (!result.TryGetValue(id, out var frames))
                    result[id] = frames = [];
                foreach (var offset in block.Offsets)
                {
                    frames.RemoveAll(f => f.Offset == offset);
                    frames.Add((offset, block));
                }
            }
        }
        foreach (var frames in result.Values)
        {
            frames.Sort((x, y) => x.Offset.CompareTo(y.Offset));
            if (frames[0].Offset > 0)
                frames.Insert(0, (0, null));
            if (frames[^1].Offset < 1)
                frames.Add((1, null));
        }
        return result;
    }

    /// <summary>
    /// The iteration progress of an animation at a local time, after direction and before keyframe easing
    /// (https://www.w3.org/TR/web-animations-1/#calculating-the-directed-progress); null when it has no effect then.
    /// Times are in seconds.
    /// </summary>
    public static double? Progress(double time, double duration, double delay, double iterations, AnimationDirection direction, AnimationFillMode fill)
    {
        var activeDuration = duration == 0 || iterations == 0 ? 0 : duration * iterations;
        var end = Math.Max(delay + activeDuration, 0);
        var (fillsBackwards, fillsForwards) = (fill is AnimationFillMode.Backwards or AnimationFillMode.Both, fill is AnimationFillMode.Forwards or AnimationFillMode.Both);
        double activeTime;
        var (before, after) = (false, false);
        if (time < Math.Max(Math.Min(delay, end), 0))
        {
            if (!fillsBackwards)
                return null;
            (before, activeTime) = (true, Math.Max(time - delay, 0));
        }
        else if (time >= Math.Max(Math.Min(delay + activeDuration, end), 0))
        {
            if (!fillsForwards)
                return null;
            (after, activeTime) = (true, Math.Max(Math.Min(time - delay, activeDuration), 0));
        }
        else
        {
            activeTime = time - delay;
        }

        var overall = duration == 0 ? (before ? 0 : iterations) : activeTime / duration;
        var simple = double.IsInfinity(overall) ? 0 : overall % 1;
        if (simple == 0 && !before && activeTime == activeDuration && iterations != 0)
            simple = 1;
        var current = after && double.IsInfinity(iterations) ? double.PositiveInfinity : simple == 1 ? Math.Floor(overall) - 1 : Math.Floor(overall);
        var forwards = direction switch
        {
            AnimationDirection.Normal => true,
            AnimationDirection.Reverse => false,
            _ => double.IsInfinity(current) || (current + (direction == AnimationDirection.AlternateReverse ? 1 : 0)) % 2 == 0,
        };
        return forwards ? simple : 1 - simple;
    }
}

/// <summary>
/// An element with animations, as the last full style pass left it: what a frame needs to sample it again without
/// restyling the document. Keyframe styles are computed once and kept.
/// </summary>
internal sealed class AnimatedElement(ElementNode element, ComputedStyle underlying, ComputedStyle parent,
                                      IReadOnlyDictionary<string, List<Keyframe>> keyframes, Func<IReadOnlyList<CascadeDeclaration>, ComputedStyle> styleWith,
                                      IReadOnlyDictionary<string, RegisteredProperty>? registered = null)
{
    private readonly Dictionary<Keyframe, ComputedStyle> _keyframeStyles = [];

    public ElementNode Element { get; } = element;

    /// <summary>The style without animations.</summary>
    public ComputedStyle Underlying { get; } = underlying;

    public bool PaintOnly { get; } = Animations.AnimatesPaintOnly(underlying.Animation, keyframes);

    public bool IsRunning(double time) => Animations.IsRunning(Underlying.Animation, keyframes, time);

    /// <summary>The style at a time (null for the settled document).</summary>
    public ComputedStyle Sample(double? time, Dictionary<object, object>? groups = null) =>
        Animations.Sample(Underlying, parent, keyframes, time, KeyframeStyle, groups, styleWith, registered) ?? Underlying;

    private ComputedStyle KeyframeStyle(Keyframe block)
    {
        if (!_keyframeStyles.TryGetValue(block, out var style))
            _keyframeStyles[block] = style = styleWith(block.Declarations);
        return style;
    }
}

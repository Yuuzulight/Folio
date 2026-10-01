using Folio.Css;

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

    /// <summary>
    /// The element's style with its animations applied at <paramref name="time"/> (seconds on the document timeline;
    /// null for the settled document), or null when no animation has an effect.
    /// </summary>
    /// <param name="styleWith">The element's style with declarations added to the animation origin of the cascade.</param>
    public static ComputedStyle? Sample(ComputedStyle underlying, ComputedStyle parent, IReadOnlyDictionary<string, List<Keyframe>> keyframes,
                                        double? time, Func<IReadOnlyList<CascadeDeclaration>, ComputedStyle> styleWith, Dictionary<object, object>? groups = null)
    {
        var group = underlying.Animation;
        if (ReferenceEquals(group, AnimationGroup.Initial) || keyframes.Count == 0)
            return null;
        StyleBuilder? builder = null;
        Dictionary<Keyframe, ComputedStyle>? styles = null;
        for (var i = 0; i < group.Names.Count; i++)
        {
            if (group.Names[i] is not { } name || !keyframes.TryGetValue(name, out var blocks)
                || Progress(time is { } t && group.PlayStates[i % group.PlayStates.Count] == AnimationPlayState.Running ? t : 0,
                    time is null ? 0 : group.Durations[i % group.Durations.Count], time is null ? 0 : group.Delays[i % group.Delays.Count],
                    group.IterationCounts[i % group.IterationCounts.Count], group.Directions[i % group.Directions.Count],
                    group.FillModes[i % group.FillModes.Count]) is not { } progress)
                continue;
            var easing = group.TimingFunctions[i % group.TimingFunctions.Count];
            foreach (var (id, frames) in ByProperty.GetValue(blocks, PerProperty))
            {
                // The keyframes around the progress; at or past the last one, the last pair.
                var a = 0;
                while (a < frames.Count - 2 && frames[a + 1].Offset <= progress)
                    a++;
                var (from, to) = (frames[a], frames[a + 1]);
                var local = (progress - from.Offset) / (to.Offset - from.Offset);
                Properties.Get(id).Interpolate(builder ??= StyleBuilder.From(underlying, parent), Style(from.Block), Style(to.Block),
                    (from.Block?.Easing ?? easing).Apply(local));
            }
        }
        return builder?.Build(groups);

        ComputedStyle Style(Keyframe? block)
        {
            if (block is null)
                return underlying;
            styles ??= [];
            if (!styles.TryGetValue(block, out var style))
                styles[block] = style = styleWith(block.Declarations);
            return style;
        }
    }

    // Each animated property's keyframes in offset order, the last block at an offset winning, with the underlying value
    // (null) at 0 and 1 where no block sets the property (https://www.w3.org/TR/css-animations-1/#keyframes).
    private static Dictionary<PropertyId, List<(float Offset, Keyframe? Block)>> PerProperty(List<Keyframe> blocks)
    {
        var result = new Dictionary<PropertyId, List<(float Offset, Keyframe? Block)>>();
        foreach (var block in blocks)
        {
            foreach (var id in block.Declarations.Select(d => d.Id).Distinct())
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

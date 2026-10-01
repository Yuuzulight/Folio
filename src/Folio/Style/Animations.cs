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
/// M1's animations (docs/study/04-cascade-and-computed-values.md): an animation that fills forwards is resolved to
/// its end state, so a page whose content fades in is drawn as it looks once the animation has run. Other animations,
/// and transitions, have no effect until there is a timeline.
/// </summary>
// ponytail: an iteration count that is not a whole number would end between keyframes; those animations are
// ignored rather than interpolated, until interpolation arrives with the timeline (M2).
internal static class Animations
{
    /// <summary>
    /// The declarations of the keyframes each animation ends on, for the cascade's animation origin: in the order of
    /// <c>animation-name</c>, so a later animation wins; null when no animation fills forwards.
    /// </summary>
    public static List<CascadeDeclaration>? EndState(AnimationGroup group, IReadOnlyDictionary<string, List<Keyframe>> keyframes)
    {
        if (ReferenceEquals(group, AnimationGroup.Initial) || keyframes.Count == 0)
            return null;
        List<CascadeDeclaration>? declarations = null;
        for (var i = 0; i < group.Names.Count; i++)
        {
            if (group.Names[i] is not { } name || !keyframes.TryGetValue(name, out var blocks)
                || EndOffset(group.IterationCounts[i % group.IterationCounts.Count], group.Directions[i % group.Directions.Count],
                    group.FillModes[i % group.FillModes.Count]) is not { } offset)
                continue;
            // Keyframes at the same offset merge, later ones winning (https://www.w3.org/TR/css-animations-1/#keyframes).
            foreach (var block in blocks)
            {
                if (block.Offsets.Contains(offset))
                    (declarations ??= []).AddRange(block.Declarations);
            }
        }
        return declarations;
    }

    /// <summary>
    /// The keyframe offset a finished animation holds (https://www.w3.org/TR/web-animations-1/#calculating-the-directed-progress):
    /// the end of its last iteration, run in that iteration's direction; null if it does not fill forwards or never ends.
    /// </summary>
    public static float? EndOffset(float iterations, AnimationDirection direction, AnimationFillMode fill)
    {
        if (fill is not (AnimationFillMode.Forwards or AnimationFillMode.Both) || float.IsInfinity(iterations) || iterations != MathF.Floor(iterations))
            return null;
        // With no iterations the animation ends where the first one would start.
        var last = Math.Max(0, (int)iterations - 1);
        var forwards = direction switch
        {
            AnimationDirection.Normal => true,
            AnimationDirection.Reverse => false,
            AnimationDirection.Alternate => last % 2 == 0,
            _ => last % 2 == 1,
        };
        return iterations == 0 ? (forwards ? 0 : 1) : (forwards ? 1 : 0);
    }
}

using System.Globalization;
using Folio.Css;

namespace Folio.Style;

/// <summary>
/// One computed filter function (https://drafts.csswg.org/filter-effects-1/#supported-filter-functions): blur and
/// drop-shadow lengths in px (<see cref="Amount"/> holding the standard deviation), percentages as numbers, hue-rotate
/// in degrees. A drop shadow's offset is (<see cref="X"/>, <see cref="Y"/>) and its colour <see cref="Color"/>.
/// </summary>
internal sealed record FilterFunction(string Name, float Amount, float X = 0, float Y = 0, CssColor Color = default)
{
    public override string ToString() => Name switch
    {
        "blur" => $"blur({N(Amount)}px)",
        "hue-rotate" => $"hue-rotate({N(Amount)}deg)",
        "drop-shadow" => $"drop-shadow({Color} {N(X)}px {N(Y)}px {N(Amount)}px)",
        _ => $"{Name}({N(Amount)})",
    };

    private static string N(float value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);
}

/// <summary>A computed filter list; empty is <c>none</c>.</summary>
internal sealed class FilterList(IReadOnlyList<FilterFunction> functions) : IEquatable<FilterList>
{
    public static FilterList None { get; } = new([]);

    public IReadOnlyList<FilterFunction> Functions { get; } = functions;

    public bool IsNone => Functions.Count == 0;

    public bool Equals(FilterList? other) => other is not null && Functions.SequenceEqual(other.Functions);

    public override bool Equals(object? obj) => Equals(obj as FilterList);

    public override int GetHashCode() => Functions.Count == 0 ? 0 : HashCode.Combine(Functions.Count, Functions[0]);

    public override string ToString() => IsNone ? "none" : string.Join(" ", Functions);
}

/// <summary>Graphic effects (not inherited): <c>filter</c> and <c>backdrop-filter</c>.</summary>
internal sealed record EffectsGroup(FilterList Filter, FilterList BackdropFilter)
{
    public static EffectsGroup Initial { get; } = new(FilterList.None, FilterList.None);
}

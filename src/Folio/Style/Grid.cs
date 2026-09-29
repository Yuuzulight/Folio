namespace Folio.Style;

internal enum TrackKind { Length, Flex, MinContent, MaxContent, Auto, FitContent }

/// <summary>A track breadth (https://www.w3.org/TR/css-grid-1/#typedef-track-breadth): a length-percentage, a flex factor, or a keyword.</summary>
internal readonly record struct TrackBreadth(TrackKind Kind, LengthPercentage Length = default, float Flex = 0)
{
    public bool IsIntrinsic => Kind is TrackKind.MinContent or TrackKind.MaxContent or TrackKind.Auto or TrackKind.FitContent;

    public override string ToString() => Kind switch
    {
        TrackKind.Length => Length.ToString(),
        TrackKind.Flex => $"{Flex.ToString(System.Globalization.CultureInfo.InvariantCulture)}fr",
        TrackKind.MinContent => "min-content",
        TrackKind.MaxContent => "max-content",
        TrackKind.FitContent => $"fit-content({Length})",
        _ => "auto",
    };
}

/// <summary>A track sizing function: its minimum and maximum (a single breadth is both; fr alone is minmax(auto, fr)).</summary>
internal readonly record struct TrackSize(TrackBreadth Min, TrackBreadth Max)
{
    public static TrackSize Auto => new(new TrackBreadth(TrackKind.Auto), new TrackBreadth(TrackKind.Auto));

    public override string ToString() => Max.Kind == TrackKind.FitContent || Min == Max || Min.Kind == TrackKind.Auto && Max.Kind == TrackKind.Flex
        ? Max.ToString()
        : $"minmax({Min}, {Max})";
}

/// <summary>
/// <c>repeat(auto-fill | auto-fit, ...)</c> in a track list: inserted before track <see cref="Index"/>, repeated as
/// often as fits; <see cref="Names"/> has one more entry than <see cref="Tracks"/>.
/// </summary>
internal sealed record AutoRepeat(int Index, bool Fit, IReadOnlyList<TrackSize> Tracks, IReadOnlyList<IReadOnlyList<string>> Names)
{
    public bool Equals(AutoRepeat? other) => other is not null && Index == other.Index && Fit == other.Fit && Tracks.SequenceEqual(other.Tracks)
        && Names.Zip(other.Names).All(p => p.First.SequenceEqual(p.Second));

    public override int GetHashCode() => HashCode.Combine(Index, Fit, Tracks.Count);
}

/// <summary>
/// A computed track list: the tracks (integer repeat() expanded), the names of each line (one more than the tracks),
/// and an automatic repetition if any.
/// </summary>
internal sealed record TrackList(IReadOnlyList<TrackSize> Tracks, IReadOnlyList<IReadOnlyList<string>> LineNames, AutoRepeat? Repeat = null)
{
    public static TrackList None { get; } = new([], [[]]);

    public override string ToString()
    {
        if (Tracks.Count == 0 && Repeat is null)
            return "none";
        var parts = new List<string>();
        for (var i = 0; i <= Tracks.Count; i++)
        {
            if (Repeat is { } r && r.Index == i)
                parts.Add($"repeat({(r.Fit ? "auto-fit" : "auto-fill")}, {new TrackList(r.Tracks, r.Names)})");
            if (LineNames[i].Count > 0)
                parts.Add($"[{string.Join(" ", LineNames[i])}]");
            if (i < Tracks.Count)
                parts.Add(Tracks[i].ToString());
        }
        return string.Join(" ", parts);
    }

    public bool Equals(TrackList? other) => other is not null && Tracks.SequenceEqual(other.Tracks)
        && LineNames.Count == other.LineNames.Count && LineNames.Zip(other.LineNames).All(p => p.First.SequenceEqual(p.Second))
        && Equals(Repeat, other.Repeat);

    public override int GetHashCode() => Tracks.Count;
}

/// <summary>A named grid area: its lines, 0-based from the explicit grid's first line.</summary>
internal readonly record struct GridArea(int RowStart, int RowEnd, int ColumnStart, int ColumnEnd);

/// <summary>A computed <c>grid-template-areas</c>: its rows as written, and the rectangle of each name.</summary>
internal sealed record GridAreas(IReadOnlyList<string> RowStrings, int Rows, int Columns, IReadOnlyDictionary<string, GridArea> Areas)
{
    public static GridAreas None { get; } = new([], 0, 0, new Dictionary<string, GridArea>());

    public override string ToString() => RowStrings.Count == 0 ? "none" : string.Join(" ", RowStrings.Select(r => $"\"{r}\""));

    public bool Equals(GridAreas? other) => other is not null && RowStrings.SequenceEqual(other.RowStrings);

    public override int GetHashCode() => RowStrings.Count;
}

internal enum GridLineKind { Auto, Line, Span }

/// <summary>
/// A grid-placement value (https://www.w3.org/TR/css-grid-1/#line-placement): auto, a line number (negative counts
/// from the end), a span, or a named line or area (Number 0 with a Name).
/// </summary>
internal readonly record struct GridLine(GridLineKind Kind, int Number = 0, string? Name = null)
{
    public static GridLine Auto => default;

    public override string ToString() => Kind switch
    {
        GridLineKind.Line => Name is null ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : Number == 0 ? Name : $"{Number} {Name}",
        GridLineKind.Span => "span " + (Name is null ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : Number == 1 ? Name : $"{Number} {Name}"),
        _ => "auto",
    };
}

/// <summary>Grid layout properties (not inherited); the gaps are shared with flex layout in <see cref="FlexGroup"/>.</summary>
internal sealed record GridGroup(
    TrackList TemplateColumns, TrackList TemplateRows, IReadOnlyList<TrackSize> AutoColumns, IReadOnlyList<TrackSize> AutoRows,
    bool AutoFlowColumn, bool Dense, GridLine RowStart, GridLine RowEnd, GridLine ColumnStart, GridLine ColumnEnd,
    ItemAlign JustifyItems, ItemAlign JustifySelf, GridAreas Areas)
{
    public static GridGroup Initial { get; } = new(TrackList.None, TrackList.None, [TrackSize.Auto], [TrackSize.Auto], false, false,
        GridLine.Auto, GridLine.Auto, GridLine.Auto, GridLine.Auto, ItemAlign.Normal, ItemAlign.Auto, GridAreas.None);

    public bool Equals(GridGroup? other) => other is not null && TemplateColumns.Equals(other.TemplateColumns) && TemplateRows.Equals(other.TemplateRows)
        && AutoColumns.SequenceEqual(other.AutoColumns) && AutoRows.SequenceEqual(other.AutoRows) && AutoFlowColumn == other.AutoFlowColumn
        && Dense == other.Dense && RowStart == other.RowStart && RowEnd == other.RowEnd && ColumnStart == other.ColumnStart
        && ColumnEnd == other.ColumnEnd && JustifyItems == other.JustifyItems && JustifySelf == other.JustifySelf && Areas.Equals(other.Areas);

    public override int GetHashCode() => HashCode.Combine(TemplateColumns.Tracks.Count, TemplateRows.Tracks.Count, RowStart, ColumnStart);
}

using Folio.Style;

namespace Folio.Css;

/// <summary>A specified track breadth: lengths are computed later.</summary>
internal sealed record TrackBreadthSpecified(TrackKind Kind, CssValue? Length = null, float Flex = 0);

internal sealed record TrackSizeSpecified(TrackBreadthSpecified Min, TrackBreadthSpecified Max);

/// <summary>A specified repeat(auto-fill | auto-fit, ...), inserted before track Index.</summary>
internal sealed record AutoRepeatSpecified(int Index, bool Fit, IReadOnlyList<TrackSizeSpecified> Tracks, IReadOnlyList<IReadOnlyList<string>> Names);

/// <summary>A specified track list with integer repeat() expanded, the names of each line, and an automatic repetition.</summary>
internal sealed record TrackListValue(IReadOnlyList<TrackSizeSpecified> Tracks, IReadOnlyList<IReadOnlyList<string>> LineNames,
                                      AutoRepeatSpecified? Repeat = null) : CssValue;

internal sealed record GridAreasValue(GridAreas Areas) : CssValue;

internal sealed record TrackSizesValue(IReadOnlyList<TrackSizeSpecified> Sizes) : CssValue;

internal sealed record GridLineValue(GridLine Line) : CssValue;

internal sealed record GridAutoFlowValue(bool Column, bool Dense) : CssValue;

/// <summary>Grammars of the grid properties (https://www.w3.org/TR/css-grid-1/).</summary>
internal static class GridParsing
{
    /// <summary>At most this many tracks from repeat() (study 08 caps the grid at 1000 tracks per axis).</summary>
    public const int MaxTracks = 1000;

    public static CssValue? TrackList(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new TrackListValue([], [[]]);
        var tracks = new List<TrackSizeSpecified>();
        var names = new List<IReadOnlyList<string>>();
        var pending = new List<string>();
        AutoRepeatSpecified? autoRepeat = null;
        if (!ReadTracks(r, tracks, names, pending, allowRepeat: true, ref autoRepeat) || tracks.Count == 0 && autoRepeat is null)
            return null;
        names.Add(pending);
        // With an automatic repetition, every track must have a fixed size (css-grid-1 <auto-track-list>).
        if (autoRepeat is not null && !tracks.All(Fixed))
            return null;
        return new TrackListValue(tracks, names, autoRepeat);
    }

    // A <fixed-size>: a fixed breadth as its minimum or maximum, never flexible at the minimum.
    private static bool Fixed(TrackSizeSpecified size) =>
        size.Min.Kind == TrackKind.Length && size.Max.Kind != TrackKind.FitContent || size.Max.Kind == TrackKind.Length && size.Min.Kind != TrackKind.Flex;

    private static bool ReadTracks(ValueReader r, List<TrackSizeSpecified> tracks, List<IReadOnlyList<string>> names, List<string> pending, bool allowRepeat)
    {
        AutoRepeatSpecified? none = null;
        return ReadTracks(r, tracks, names, pending, allowRepeat, ref none, allowAuto: false);
    }

    private static bool ReadTracks(ValueReader r, List<TrackSizeSpecified> tracks, List<IReadOnlyList<string>> names, List<string> pending,
                                   bool allowRepeat, ref AutoRepeatSpecified? autoRepeat, bool allowAuto = true)
    {
        while (!r.AtEnd)
        {
            if (r.LineNames() is { } lineNames)
            {
                pending.AddRange(lineNames);
                continue;
            }
            if (allowRepeat && allowAuto && autoRepeat is null && r.Copy().Function("repeat") is { } peek && peek.Keyword("auto-fill", "auto-fit") is { } kind)
            {
                var repeatArgs = r.Function("repeat")!;
                repeatArgs.Keyword("auto-fill", "auto-fit");
                if (!repeatArgs.Comma())
                    return false;
                var (innerTracks, innerNames, innerPending) = (new List<TrackSizeSpecified>(), new List<IReadOnlyList<string>>(), new List<string>());
                if (!ReadTracks(repeatArgs, innerTracks, innerNames, innerPending, allowRepeat: false) || innerTracks.Count == 0 || !innerTracks.All(Fixed))
                    return false;
                innerNames[0] = [.. pending, .. innerNames[0]];
                innerNames.Add(innerPending);
                autoRepeat = new AutoRepeatSpecified(tracks.Count, kind == "auto-fit", innerTracks, innerNames);
                pending.Clear();
                continue;
            }
            if (allowRepeat && r.Function("repeat") is { } repeat)
            {
                if (repeat.Integer() is not ({ } count and >= 1) || !repeat.Comma())
                    return false;
                var (innerTracks, innerNames, innerPending) = (new List<TrackSizeSpecified>(), new List<IReadOnlyList<string>>(), new List<string>());
                if (!ReadTracks(repeat, innerTracks, innerNames, innerPending, allowRepeat: false) || innerTracks.Count == 0)
                    return false;
                for (var i = 0; i < count && tracks.Count < MaxTracks; i++)
                {
                    for (var t = 0; t < innerTracks.Count; t++)
                    {
                        names.Add(t == 0 ? [.. pending, .. innerNames[0]] : innerNames[t]);
                        tracks.Add(innerTracks[t]);
                        if (t == 0)
                            pending.Clear();
                    }
                    pending.AddRange(innerPending);
                }
                continue;
            }
            if (TrackSize(r) is not { } size)
                return false;
            names.Add([.. pending]);
            pending.Clear();
            tracks.Add(size);
        }
        return true;
    }

    public static CssValue? TrackSizes(ValueReader r)
    {
        var sizes = new List<TrackSizeSpecified>();
        while (!r.AtEnd)
        {
            if (TrackSize(r) is not { } size)
                return null;
            sizes.Add(size);
        }
        return sizes.Count > 0 ? new TrackSizesValue(sizes) : null;
    }

    /// <summary>One track size, or null (nothing consumed) when the next value is not one.</summary>
    public static TrackSizeSpecified? OneTrackSize(ValueReader r) => TrackSize(r);

    // <track-size> = <track-breadth> | minmax(<inflexible-breadth>, <track-breadth>) | fit-content(<length-percentage>)
    private static TrackSizeSpecified? TrackSize(ValueReader r)
    {
        if (r.Function("minmax") is { } minmax)
        {
            return Breadth(minmax, allowFlex: false) is { } min && minmax.Comma() && Breadth(minmax, allowFlex: true) is { } max && minmax.AtEnd
                ? new TrackSizeSpecified(min, max)
                : null;
        }
        if (r.Function("fit-content") is { } fit)
        {
            return fit.LengthPercentage(nonNegative: true) is { } limit && fit.AtEnd
                ? new TrackSizeSpecified(new TrackBreadthSpecified(TrackKind.Auto), new TrackBreadthSpecified(TrackKind.FitContent, limit))
                : null;
        }
        return Breadth(r, allowFlex: true) switch
        {
            { Kind: TrackKind.Flex } flex => new TrackSizeSpecified(new TrackBreadthSpecified(TrackKind.Auto), flex),
            { } breadth => new TrackSizeSpecified(breadth, breadth),
            null => null,
        };
    }

    private static TrackBreadthSpecified? Breadth(ValueReader r, bool allowFlex)
    {
        if (r.Keyword("min-content", "max-content", "auto") is { } keyword)
            return new TrackBreadthSpecified(keyword switch { "min-content" => TrackKind.MinContent, "max-content" => TrackKind.MaxContent, _ => TrackKind.Auto });
        if (allowFlex && r.Flex() is { } flex)
            return new TrackBreadthSpecified(TrackKind.Flex, Flex: flex);
        return r.LengthPercentage(nonNegative: true) is { } length ? new TrackBreadthSpecified(TrackKind.Length, length) : null;
    }

    // <grid-line> = auto | <custom-ident> | [ <integer> && <custom-ident>? ] | [ span && [ <integer> || <custom-ident> ] ]
    public static CssValue? Line(ValueReader r)
    {
        if (r.Keyword("auto") is not null)
            return new GridLineValue(GridLine.Auto);
        bool span = false;
        int? number = null;
        string? name = null;
        while (!r.AtEnd)
        {
            if (!span && r.Keyword("span") is not null)
                span = true;
            else if (number is null && r.Integer() is { } n)
                number = n;
            else if (name is null && r.Ident() is { } ident && !ident.Equals("auto", StringComparison.OrdinalIgnoreCase)
                     && !ident.Equals("span", StringComparison.OrdinalIgnoreCase))
                name = ident;
            else
                return null;
        }
        if (span)
            return number is <= 0 || number is null && name is null ? null : new GridLineValue(new GridLine(GridLineKind.Span, number ?? 1, name));
        if (number is 0 || number is null && name is null)
            return null;
        return new GridLineValue(new GridLine(GridLineKind.Line, number ?? 0, name));
    }

    public static CssValue? AutoFlow(ValueReader r)
    {
        bool? column = null;
        var dense = false;
        while (!r.AtEnd)
        {
            if (column is null && r.Keyword("row", "column") is { } axis)
                column = axis == "column";
            else if (!dense && r.Keyword("dense") is not null)
                dense = true;
            else
                return null;
        }
        return column is null && !dense ? null : new GridAutoFlowValue(column ?? false, dense);
    }

    public static CssValue? Areas(ValueReader r)
    {
        if (r.Keyword("none") is not null)
            return new GridAreasValue(GridAreas.None);
        var rows = new List<string>();
        while (r.String() is { } row)
            rows.Add(row);
        return rows.Count > 0 && r.AtEnd && BuildAreas(rows) is { } areas ? new GridAreasValue(areas) : null;
    }

    /// <summary>
    /// The areas of a grid-template-areas value (https://www.w3.org/TR/css-grid-1/#grid-template-areas-property): every
    /// row the same number of cells, each name a filled rectangle; null when invalid.
    /// </summary>
    public static GridAreas? BuildAreas(List<string> rows)
    {
        var cells = rows.Select(Cells).ToList();
        if (cells.Any(c => c is null) || cells[0]!.Count == 0 || cells.Any(c => c!.Count != cells[0]!.Count))
            return null;
        var areas = new Dictionary<string, GridArea>(StringComparer.Ordinal);
        for (var row = 0; row < cells.Count; row++)
        {
            for (var column = 0; column < cells[row]!.Count; column++)
            {
                if (cells[row]![column] is not { } name)
                    continue;
                areas[name] = areas.TryGetValue(name, out var a)
                    ? new GridArea(Math.Min(a.RowStart, row), Math.Max(a.RowEnd, row + 1), Math.Min(a.ColumnStart, column), Math.Max(a.ColumnEnd, column + 1))
                    : new GridArea(row, row + 1, column, column + 1);
            }
        }
        foreach (var (name, area) in areas)
        {
            for (var row = area.RowStart; row < area.RowEnd; row++)
            {
                for (var column = area.ColumnStart; column < area.ColumnEnd; column++)
                {
                    if (cells[row]![column] != name)
                        return null;
                }
            }
        }
        return new GridAreas(cells.Select(c => string.Join(" ", c!.Select(n => n ?? "."))).ToList(), cells.Count, cells[0]!.Count, areas);
    }

    // A row string's cells: names, or null for a run of dots; null when it holds anything else.
    private static List<string?>? Cells(string row)
    {
        var cells = new List<string?>();
        for (var i = 0; i < row.Length;)
        {
            var c = row[i];
            if (c is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                i++;
            }
            else if (c == '.')
            {
                while (i < row.Length && row[i] == '.')
                    i++;
                cells.Add(null);
            }
            else if (IsNameChar(c))
            {
                var start = i;
                while (i < row.Length && IsNameChar(row[i]))
                    i++;
                cells.Add(row[start..i]);
            }
            else
            {
                return null;
            }
        }
        return cells;

        static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' || c >= 0x80;
    }

    public static TrackSize Compute(TrackSizeSpecified size, ComputeContext context) => new(Compute(size.Min, context), Compute(size.Max, context));

    private static TrackBreadth Compute(TrackBreadthSpecified breadth, ComputeContext context) =>
        new(breadth.Kind, breadth.Length is null ? default : context.LengthPercentage(breadth.Length, nonNegative: true), breadth.Flex);

    /// <summary>A start or end value reused for an omitted end (a lone custom-ident repeats, anything else is auto).</summary>
    public static CssValue Omitted(CssValue start) =>
        start is GridLineValue { Line: { Kind: GridLineKind.Line, Number: 0, Name: not null } } ? start : new GridLineValue(GridLine.Auto);
}

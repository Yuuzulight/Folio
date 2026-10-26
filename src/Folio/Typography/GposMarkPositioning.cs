namespace Folio.Typography;

/// <summary>
/// The bounded mark-to-base and mark-to-mark subset of GPOS used by Folio's simple Hebrew shaper.
/// Unsupported lookup formats are ignored and remain on the complex differential path.
/// </summary>
internal sealed class GposMarkPositioning
{
    private const int MarkToBase = 4;
    private const int MarkToMark = 6;
    private const int Extension = 9;
    private readonly FontData _gpos;
    private readonly int[][] _subtables;
    private readonly int[][] _markToMark;

    private GposMarkPositioning(FontData gpos, int[][] subtables, int[][] markToMark) =>
        (_gpos, _subtables, _markToMark) = (gpos, subtables, markToMark);

    public static GposMarkPositioning? Read(FontData gpos)
    {
        try
        {
            var featureList = gpos.U16(6);
            var lookupList = gpos.U16(8);
            var featureCount = gpos.U16(featureList);
            var wanted = new SortedSet<int>();
            for (var i = 0; i < featureCount; i++)
            {
                var feature = featureList + 2 + 6 * i;
                var tag = gpos.Tag(feature);
                if (tag is not ("mark" or "mkmk"))
                    continue;
                var table = featureList + gpos.U16(feature + 4);
                for (var j = 0; j < gpos.U16(table + 2); j++)
                    wanted.Add(gpos.U16(table + 4 + 2 * j));
            }

            // If a font does not expose mark in the selected script's feature set, there is no safe
            // assumption that every mark lookup is appropriate for this simple run.
            if (wanted.Count == 0)
                return null;

            var lookups = gpos.U16(lookupList);
            var subtables = new List<int[]>();
            var markToMark = new List<int[]>();
            foreach (var index in wanted)
            {
                if ((uint)index >= lookups)
                    continue;
                var lookup = lookupList + gpos.U16(lookupList + 2 + 2 * index);
                var type = gpos.U16(lookup);
                var offsets = new List<int>();
                for (var s = 0; s < gpos.U16(lookup + 4); s++)
                {
                    var subtable = lookup + gpos.U16(lookup + 6 + 2 * s);
                    if (type == MarkToBase)
                        offsets.Add(subtable);
                    else if (type == Extension && gpos.U16(subtable) == 1 && gpos.U16(subtable + 2) == MarkToBase)
                        offsets.Add(checked(subtable + (int)gpos.U32(subtable + 4)));
                }
                if (offsets.Count > 0)
                    subtables.Add([.. offsets]);

                var markOffsets = new List<int>();
                for (var s = 0; s < gpos.U16(lookup + 4); s++)
                {
                    var subtable = lookup + gpos.U16(lookup + 6 + 2 * s);
                    if (type == MarkToMark)
                        markOffsets.Add(subtable);
                    else if (type == Extension && gpos.U16(subtable) == 1 && gpos.U16(subtable + 2) == MarkToMark)
                        markOffsets.Add(checked(subtable + (int)gpos.U32(subtable + 4)));
                }
                if (markOffsets.Count > 0)
                    markToMark.Add([.. markOffsets]);
            }
            return subtables.Count > 0 || markToMark.Count > 0
                ? new GposMarkPositioning(gpos, [.. subtables], [.. markToMark])
                : null;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    public (int X, int Y)? Offset(ushort baseGlyph, ushort markGlyph)
    {
        try
        {
            foreach (var lookup in _subtables)
            {
                foreach (var subtable in lookup)
                {
                    if (Apply(subtable, baseGlyph, markGlyph) is { } offset)
                        return offset;
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            // A malformed optional table must not make a document fail to layout.
        }
        return null;
    }

    public (int X, int Y)? MarkOffset(ushort previousMark, ushort markGlyph)
    {
        try
        {
            foreach (var lookup in _markToMark)
                foreach (var subtable in lookup)
                    if (ApplyMarkToMark(subtable, previousMark, markGlyph) is { } offset)
                        return offset;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
        }
        return null;
    }

    private (int X, int Y)? Apply(int subtable, ushort baseGlyph, ushort markGlyph)
    {
        if (_gpos.U16(subtable) != 1)
            return null;
        var markIndex = CoverageIndex(subtable + _gpos.U16(subtable + 2), markGlyph);
        var baseIndex = CoverageIndex(subtable + _gpos.U16(subtable + 4), baseGlyph);
        if (markIndex < 0 || baseIndex < 0)
            return null;
        var classCount = _gpos.U16(subtable + 6);
        var markArray = subtable + _gpos.U16(subtable + 8);
        var baseArray = subtable + _gpos.U16(subtable + 10);
        if ((uint)markIndex >= _gpos.U16(markArray) || (uint)baseIndex >= _gpos.U16(baseArray))
            return null;
        var markRecord = markArray + 2 + 4 * markIndex;
        var markClass = _gpos.U16(markRecord);
        if (markClass >= classCount)
            return null;
        var markAnchorOffset = _gpos.U16(markRecord + 2);
        if (markAnchorOffset == 0)
            return null;
        var baseRecord = baseArray + 2 + 2 * classCount * baseIndex;
        var baseAnchorOffset = _gpos.U16(baseRecord + 2 * markClass);
        if (baseAnchorOffset == 0)
            return null;
        var markAnchor = Anchor(markArray + markAnchorOffset);
        var baseAnchor = Anchor(baseArray + baseAnchorOffset);
        return (baseAnchor.X - markAnchor.X, baseAnchor.Y - markAnchor.Y);
    }

    private (int X, int Y)? ApplyMarkToMark(int subtable, ushort previousMark, ushort markGlyph)
    {
        if (_gpos.U16(subtable) != 1)
            return null;
        var mark1Index = CoverageIndex(subtable + _gpos.U16(subtable + 2), previousMark);
        var mark2Index = CoverageIndex(subtable + _gpos.U16(subtable + 4), markGlyph);
        if (mark1Index < 0 || mark2Index < 0)
            return null;
        var classCount = _gpos.U16(subtable + 6);
        var mark1Array = subtable + _gpos.U16(subtable + 8);
        var mark2Array = subtable + _gpos.U16(subtable + 10);
        if ((uint)mark1Index >= _gpos.U16(mark1Array) || (uint)mark2Index >= _gpos.U16(mark2Array))
            return null;
        var mark1Record = mark1Array + 2 + 4 * mark1Index;
        var markClass = _gpos.U16(mark1Record);
        if (markClass >= classCount)
            return null;
        var mark1AnchorOffset = _gpos.U16(mark1Record + 2);
        if (mark1AnchorOffset == 0)
            return null;
        var mark2Record = mark2Array + 2 + 2 * classCount * mark2Index;
        var mark2AnchorOffset = _gpos.U16(mark2Record + 2 * markClass);
        if (mark2AnchorOffset == 0)
            return null;
        var mark1Anchor = Anchor(mark1Array + mark1AnchorOffset);
        var mark2Anchor = Anchor(mark2Array + mark2AnchorOffset);
        return (mark1Anchor.X - mark2Anchor.X, mark1Anchor.Y - mark2Anchor.Y);
    }

    private (int X, int Y) Anchor(int offset)
    {
        if (_gpos.U16(offset) != 1)
            throw new InvalidDataException("Unsupported GPOS anchor format.");
        return (_gpos.S16(offset + 2), _gpos.S16(offset + 4));
    }

    private int CoverageIndex(int coverage, ushort glyph)
    {
        var format = _gpos.U16(coverage);
        var count = _gpos.U16(coverage + 2);
        if (format == 1)
        {
            var (low, high) = (0, count - 1);
            while (low <= high)
            {
                var mid = (low + high) / 2;
                var value = _gpos.U16(coverage + 4 + 2 * mid);
                if (value == glyph) return mid;
                if (value < glyph) low = mid + 1; else high = mid - 1;
            }
        }
        else if (format == 2)
        {
            var (low, high) = (0, count - 1);
            while (low <= high)
            {
                var mid = (low + high) / 2;
                var range = coverage + 4 + 6 * mid;
                var start = _gpos.U16(range);
                var end = _gpos.U16(range + 2);
                if (glyph < start) high = mid - 1;
                else if (glyph > end) low = mid + 1;
                else return _gpos.U16(range + 4) + glyph - start;
            }
        }
        return -1;
    }
}

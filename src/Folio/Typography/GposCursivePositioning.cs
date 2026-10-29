namespace Folio.Typography;

/// <summary>Folio-owned GPOS cursive attachment (lookup type 3).</summary>
internal sealed class GposCursivePositioning
{
    private const int Cursive = 3;
    private const int Extension = 9;
    private readonly FontData _gpos;
    private readonly int[][] _subtables;

    private GposCursivePositioning(FontData gpos, int[][] subtables) => (_gpos, _subtables) = (gpos, subtables);

    public static GposCursivePositioning? Read(FontData gpos)
    {
        try
        {
            var featureList = gpos.U16(6);
            var lookupList = gpos.U16(8);
            var wanted = new SortedSet<int>();
            var featureCount = gpos.U16(featureList);
            for (var i = 0; i < featureCount; i++)
            {
                var feature = featureList + 2 + 6 * i;
                if (gpos.Tag(feature) != "curs") continue;
                var table = featureList + gpos.U16(feature + 4);
                for (var j = 0; j < gpos.U16(table + 2); j++) wanted.Add(gpos.U16(table + 4 + 2 * j));
            }
            var lookups = gpos.U16(lookupList);
            var result = new List<int[]>();
            foreach (var index in wanted)
            {
                if ((uint)index >= lookups) continue;
                var lookup = lookupList + gpos.U16(lookupList + 2 + 2 * index);
                var type = gpos.U16(lookup);
                var offsets = new List<int>();
                for (var s = 0; s < gpos.U16(lookup + 4); s++)
                {
                    var subtable = lookup + gpos.U16(lookup + 6 + 2 * s);
                    if (type == Cursive) offsets.Add(subtable);
                    else if (type == Extension && gpos.U16(subtable) == 1 && gpos.U16(subtable + 2) == Cursive)
                        offsets.Add(checked(subtable + (int)gpos.U32(subtable + 4)));
                }
                if (offsets.Count > 0) result.Add([.. offsets]);
            }
            return result.Count == 0 ? null : new GposCursivePositioning(gpos, [.. result]);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException) { return null; }
    }

    public (int X, int Y)? Placement(ushort previousGlyph, ushort glyph)
    {
        try
        {
            foreach (var lookup in _subtables)
                foreach (var subtable in lookup)
                {
                    if (_gpos.U16(subtable) != 1) continue;
                    var previous = CoverageIndex(subtable + _gpos.U16(subtable + 2), previousGlyph);
                    var current = CoverageIndex(subtable + _gpos.U16(subtable + 2), glyph);
                    if (previous < 0 || current < 0) continue;
                    var count = _gpos.U16(subtable + 4);
                    if ((uint)previous >= count || (uint)current >= count) continue;
                    var prevRecord = subtable + _gpos.U16(subtable + 6) + 4 * previous;
                    var currentRecord = subtable + _gpos.U16(subtable + 6) + 4 * current;
                    var exit = _gpos.U16(prevRecord + 2);
                    var entry = _gpos.U16(currentRecord);
                    if (exit == 0 || entry == 0) continue;
                    var exitAnchor = Anchor(subtable + exit);
                    var entryAnchor = Anchor(subtable + entry);
                    return (entryAnchor.X - exitAnchor.X, entryAnchor.Y - exitAnchor.Y);
                }
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException) { }
        return null;
    }

    private (int X, int Y) Anchor(int offset)
    {
        if (_gpos.U16(offset) != 1) throw new InvalidDataException("Unsupported GPOS anchor format.");
        return (_gpos.S16(offset + 2), _gpos.S16(offset + 4));
    }

    private int CoverageIndex(int coverage, ushort glyph)
    {
        var format = _gpos.U16(coverage);
        var count = _gpos.U16(coverage + 2);
        if (format == 1)
        {
            for (var i = 0; i < count; i++) if (_gpos.U16(coverage + 4 + 2 * i) == glyph) return i;
        }
        else if (format == 2)
        {
            for (var i = 0; i < count; i++)
            {
                var range = coverage + 4 + 6 * i;
                var start = _gpos.U16(range); var end = _gpos.U16(range + 2);
                if (glyph >= start && glyph <= end) return _gpos.U16(range + 4) + glyph - start;
            }
        }
        return -1;
    }
}

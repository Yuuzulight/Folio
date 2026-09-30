namespace Folio.Typography;

/// <summary>
/// Pair kerning from the GPOS table (https://learn.microsoft.com/en-us/typography/opentype/spec/gpos): the pair
/// adjustment lookups (type 2, formats 1 and 2, also behind extension lookups) of the <c>kern</c> feature, for
/// <see cref="SimpleShaper"/>. Reads the table on demand; offsets are checked on every read, and a malformed table
/// kerns nothing rather than failing layout.
/// </summary>
// ponytail: only the first glyph's x-advance is applied (what kerning uses); placements, the second glyph's value
// record and device tables are left to the complex shaper.
internal sealed class GposKerning
{
    private const int PairAdjustment = 2;
    private const int Extension = 9;

    private readonly FontData _gpos;
    private readonly int[][] _subtables; // per kern lookup, in lookup order: subtable offsets into the GPOS table

    private GposKerning(FontData gpos, int[][] subtables) => (_gpos, _subtables) = (gpos, subtables);

    /// <summary>The kern lookups of a GPOS table, or null when it has none (or cannot be read).</summary>
    public static GposKerning? Read(FontData gpos)
    {
        try
        {
            var features = gpos.U16(6);
            var lookupList = gpos.U16(8);
            var lookups = new SortedSet<int>();
            foreach (var feature in KernFeatures(gpos, gpos.U16(4), features))
            {
                var table = features + gpos.U16(features + 2 + 6 * feature + 4);
                for (var i = 0; i < gpos.U16(table + 2); i++)
                    lookups.Add(gpos.U16(table + 4 + 2 * i));
            }

            var subtables = new List<int[]>();
            var lookupCount = gpos.U16(lookupList);
            foreach (var index in lookups)
            {
                if (index >= lookupCount)
                    continue;
                var lookup = lookupList + gpos.U16(lookupList + 2 + 2 * index);
                var type = gpos.U16(lookup);
                var offsets = new List<int>();
                for (var s = 0; s < gpos.U16(lookup + 4); s++)
                {
                    var subtable = lookup + gpos.U16(lookup + 6 + 2 * s);
                    if (type == Extension && gpos.U16(subtable) == 1 && gpos.U16(subtable + 2) == PairAdjustment)
                        offsets.Add(checked(subtable + (int)gpos.U32(subtable + 4)));
                    else if (type == PairAdjustment)
                        offsets.Add(subtable);
                }
                if (offsets.Count > 0)
                    subtables.Add([.. offsets]);
            }
            return subtables.Count > 0 ? new GposKerning(gpos, [.. subtables]) : null;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return null;
        }
    }

    // The kern features of the Latin script's default language system, else the default script's, else every kern
    // feature in the list.
    // ponytail: Greek and Cyrillic runs use the Latin choice too; per-script features come with script itemisation.
    private static IEnumerable<int> KernFeatures(FontData gpos, int scriptList, int featureList)
    {
        var featureCount = gpos.U16(featureList);
        bool IsKern(int feature) => feature < featureCount && gpos.Tag(featureList + 2 + 6 * feature) == "kern";
        foreach (var tag in (string[])["latn", "DFLT"])
        {
            for (var i = 0; i < gpos.U16(scriptList); i++)
            {
                var record = scriptList + 2 + 6 * i;
                if (gpos.Tag(record) != tag)
                    continue;
                var script = scriptList + gpos.U16(record + 4);
                int langSys = gpos.U16(script);
                if (langSys == 0)
                    continue;
                langSys += script;
                var found = new List<int>();
                var required = gpos.U16(langSys + 2);
                if (required != 0xFFFF && IsKern(required))
                    found.Add(required);
                for (var f = 0; f < gpos.U16(langSys + 4); f++)
                {
                    if (IsKern(gpos.U16(langSys + 6 + 2 * f)))
                        found.Add(gpos.U16(langSys + 6 + 2 * f));
                }
                if (found.Count > 0)
                    return found;
            }
        }
        return Enumerable.Range(0, featureCount).Where(IsKern);
    }

    /// <summary>The x-advance adjustment of <paramref name="left"/> before <paramref name="right"/>, in font units.</summary>
    public int Kerning(ushort left, ushort right)
    {
        try
        {
            var total = 0;
            foreach (var lookup in _subtables)
            {
                // A lookup applies its first subtable that handles the pair.
                foreach (var subtable in lookup)
                {
                    if (Apply(subtable, left, right) is { } value)
                    {
                        total += value;
                        break;
                    }
                }
            }
            return total;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return 0;
        }
    }

    private int? Apply(int subtable, ushort left, ushort right)
    {
        var format = _gpos.U16(subtable);
        var coverage = CoverageIndex(subtable + _gpos.U16(subtable + 2), left);
        if (coverage < 0)
            return null;
        var (format1, format2) = (_gpos.U16(subtable + 4), _gpos.U16(subtable + 6));
        var size1 = 2 * System.Numerics.BitOperations.PopCount((uint)format1 & 0xFF);
        var size2 = 2 * System.Numerics.BitOperations.PopCount((uint)format2 & 0xFF);
        if (format == 1)
        {
            // Pair sets per covered first glyph, their records sorted by second glyph.
            if (coverage >= _gpos.U16(subtable + 8))
                return null;
            var set = subtable + _gpos.U16(subtable + 10 + 2 * coverage);
            var recordSize = 2 + size1 + size2;
            var (low, high) = (0, _gpos.U16(set) - 1);
            while (low <= high)
            {
                var mid = (low + high) / 2;
                var record = set + 2 + recordSize * mid;
                var second = _gpos.U16(record);
                if (second == right)
                    return XAdvance(record + 2, format1);
                if (second < right)
                    low = mid + 1;
                else
                    high = mid - 1;
            }
            return null;
        }
        if (format == 2)
        {
            // A value per pair of glyph classes.
            var class1 = ClassOf(subtable + _gpos.U16(subtable + 8), left);
            var class2 = ClassOf(subtable + _gpos.U16(subtable + 10), right);
            var (class1Count, class2Count) = (_gpos.U16(subtable + 12), _gpos.U16(subtable + 14));
            if (class1 >= class1Count || class2 >= class2Count)
                return null;
            return XAdvance(subtable + 16 + (class1 * class2Count + class2) * (size1 + size2), format1);
        }
        return null;
    }

    private int XAdvance(int valueRecord, ushort format) =>
        (format & 0x4) == 0 ? 0 : _gpos.S16(valueRecord + 2 * System.Numerics.BitOperations.PopCount((uint)format & 0x3));

    // https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2#coverage-table
    private int CoverageIndex(int coverage, ushort glyph)
    {
        var format = _gpos.U16(coverage);
        var (low, high) = (0, _gpos.U16(coverage + 2) - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (format == 1)
            {
                var g = _gpos.U16(coverage + 4 + 2 * mid);
                if (g == glyph)
                    return mid;
                (low, high) = g < glyph ? (mid + 1, high) : (low, mid - 1);
            }
            else if (format == 2)
            {
                var range = coverage + 4 + 6 * mid;
                if (glyph < _gpos.U16(range))
                    high = mid - 1;
                else if (glyph > _gpos.U16(range + 2))
                    low = mid + 1;
                else
                    return _gpos.U16(range + 4) + glyph - _gpos.U16(range);
            }
            else
            {
                return -1;
            }
        }
        return -1;
    }

    // https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2#class-definition-table: glyphs not listed are class 0.
    private int ClassOf(int classDef, ushort glyph)
    {
        var format = _gpos.U16(classDef);
        if (format == 1)
        {
            var start = _gpos.U16(classDef + 2);
            return glyph >= start && glyph - start < _gpos.U16(classDef + 4) ? _gpos.U16(classDef + 6 + 2 * (glyph - start)) : 0;
        }
        if (format != 2)
            return 0;
        var (low, high) = (0, _gpos.U16(classDef + 2) - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var range = classDef + 4 + 6 * mid;
            if (glyph < _gpos.U16(range))
                high = mid - 1;
            else if (glyph > _gpos.U16(range + 2))
                low = mid + 1;
            else
                return _gpos.U16(range + 4);
        }
        return 0;
    }
}

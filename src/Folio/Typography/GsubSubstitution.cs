using System.Collections.Concurrent;

namespace Folio.Typography;

/// <summary>
/// Single substitutions from the GSUB table (https://learn.microsoft.com/en-us/typography/opentype/spec/gsub, lookup
/// type 1 in both formats, also behind extension lookups) for the features text asks for, such as <c>tnum</c> for
/// tabular figures. Reads the table on demand with checked offsets; a malformed table substitutes nothing.
/// </summary>
// ponytail: only single substitutions; ligatures and contextual features stay with the complex shaper.
internal sealed class GsubSubstitution
{
    private const int Single = 1;
    private const int Extension = 7;

    private readonly FontData _gsub;
    private readonly ConcurrentDictionary<string, int[]> _subtables = new(StringComparer.Ordinal);

    private GsubSubstitution(FontData gsub) => _gsub = gsub;

    public static GsubSubstitution? Read(FontData gsub) => gsub.Length >= 10 ? new GsubSubstitution(gsub) : null;

    /// <summary>The glyph after the single substitutions of the features, applied in lookup order.</summary>
    public ushort Substitute(ushort glyph, string features)
    {
        try
        {
            foreach (var subtable in _subtables.GetOrAdd(features, Subtables))
            {
                if (Apply(subtable, glyph) is { } replaced)
                    glyph = replaced;
            }
            return glyph;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return glyph;
        }
    }

    // The single-substitution subtables of the space-separated feature tags' lookups, in lookup order, for the Latin
    // script's default language system, else the default script's, else any feature with that tag.
    private int[] Subtables(string features)
    {
        try
        {
            var tags = features.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var (scripts, featureList, lookupList) = (_gsub.U16(4), _gsub.U16(6), _gsub.U16(8));
            var featureCount = _gsub.U16(featureList);
            bool Wanted(int feature) => feature < featureCount && tags.Contains(_gsub.Tag(featureList + 2 + 6 * feature));
            var features2 = LanguageFeatures(scripts).Where(Wanted).ToList();
            if (features2.Count == 0)
                features2 = Enumerable.Range(0, featureCount).Where(Wanted).ToList();
            var lookups = new SortedSet<int>();
            foreach (var feature in features2)
            {
                var table = featureList + _gsub.U16(featureList + 2 + 6 * feature + 4);
                for (var i = 0; i < _gsub.U16(table + 2); i++)
                    lookups.Add(_gsub.U16(table + 4 + 2 * i));
            }
            var subtables = new List<int>();
            foreach (var index in lookups.Where(i => i < _gsub.U16(lookupList)))
            {
                var lookup = lookupList + _gsub.U16(lookupList + 2 + 2 * index);
                var type = _gsub.U16(lookup);
                for (var s = 0; s < _gsub.U16(lookup + 4); s++)
                {
                    var subtable = lookup + _gsub.U16(lookup + 6 + 2 * s);
                    if (type == Extension && _gsub.U16(subtable) == 1 && _gsub.U16(subtable + 2) == Single)
                        subtables.Add(checked(subtable + (int)_gsub.U32(subtable + 4)));
                    else if (type == Single)
                        subtables.Add(subtable);
                }
            }
            return [.. subtables];
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return [];
        }
    }

    // The feature indices of the Latin script's default language system, else the default script's.
    private IEnumerable<int> LanguageFeatures(int scriptList)
    {
        foreach (var tag in (string[])["latn", "DFLT"])
        {
            for (var i = 0; i < _gsub.U16(scriptList); i++)
            {
                var record = scriptList + 2 + 6 * i;
                if (_gsub.Tag(record) != tag)
                    continue;
                var script = scriptList + _gsub.U16(record + 4);
                int langSys = _gsub.U16(script);
                if (langSys == 0)
                    continue;
                langSys += script;
                var found = new List<int>();
                for (var f = 0; f < _gsub.U16(langSys + 4); f++)
                    found.Add(_gsub.U16(langSys + 6 + 2 * f));
                return found;
            }
        }
        return [];
    }

    private ushort? Apply(int subtable, ushort glyph)
    {
        var index = CoverageIndex(subtable + _gsub.U16(subtable + 2), glyph);
        if (index < 0)
            return null;
        return _gsub.U16(subtable) switch
        {
            1 => (ushort)((glyph + _gsub.S16(subtable + 4)) & 0xFFFF),
            2 when index < _gsub.U16(subtable + 4) => _gsub.U16(subtable + 6 + 2 * index),
            _ => null,
        };
    }

    // https://learn.microsoft.com/en-us/typography/opentype/spec/chapter2#coverage-table
    private int CoverageIndex(int coverage, ushort glyph)
    {
        var format = _gsub.U16(coverage);
        var (low, high) = (0, _gsub.U16(coverage + 2) - 1);
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (format == 1)
            {
                var g = _gsub.U16(coverage + 4 + 2 * mid);
                if (g == glyph)
                    return mid;
                (low, high) = g < glyph ? (mid + 1, high) : (low, mid - 1);
            }
            else if (format == 2)
            {
                var range = coverage + 4 + 6 * mid;
                if (glyph < _gsub.U16(range))
                    high = mid - 1;
                else if (glyph > _gsub.U16(range + 2))
                    low = mid + 1;
                else
                    return _gsub.U16(range + 4) + glyph - _gsub.U16(range);
            }
            else
            {
                return -1;
            }
        }
        return -1;
    }
}

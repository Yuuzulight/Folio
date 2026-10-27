using System.Collections.Concurrent;

namespace Folio.Typography;

/// <summary>
/// Single substitutions from the GSUB table (https://learn.microsoft.com/en-us/typography/opentype/spec/gsub, lookup
/// type 1 in both formats, also behind extension lookups) for the features text asks for, such as <c>tnum</c> for
/// tabular figures. Reads the table on demand with checked offsets; a malformed table substitutes nothing.
/// </summary>
// ponytail: only single substitutions; ligatures and contextual features stay with the complex shaper, which gets every
// run with a ligature in it (StartsLigature).
internal sealed class GsubSubstitution
{
    private const int Single = 1;
    private const int Ligature = 4;
    private const int Extension = 7;

    private readonly FontData _gsub;
    private readonly ConcurrentDictionary<string, int[]> _subtables = new(StringComparer.Ordinal);

    // The coverage tables of the ligature substitutions text gets by default, read on first use.
    private int[]? _ligatureCoverages;

    private GsubSubstitution(FontData gsub) => _gsub = gsub;

    /// <summary>
    /// Whether a glyph starts a ligature of the features on by default (liga, clig and rlig: lookup type 4), which only
    /// the complex shaper forms.
    /// </summary>
    public bool StartsLigature(ushort glyph)
    {
        try
        {
            _ligatureCoverages ??= [.. Subtables("liga clig rlig", Ligature).Select(s => s + _gsub.U16(s + 2))];
            foreach (var coverage in _ligatureCoverages)
            {
                if (CoverageIndex(coverage, glyph) >= 0)
                    return true;
            }
            return false;
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return false;
        }
    }

    public static GsubSubstitution? Read(FontData gsub) => gsub.Length >= 10 ? new GsubSubstitution(gsub) : null;

    /// <summary>The glyph after the single substitutions of the features, applied in lookup order.</summary>
    public ushort Substitute(ushort glyph, string features)
    {
        try
        {
            // Looked up first: GetOrAdd with the method group would make a delegate for every glyph.
            if (!_subtables.TryGetValue(features, out var subtables))
                subtables = _subtables.GetOrAdd(features, f => Subtables(f, Single));
            foreach (var subtable in subtables)
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

    /// <summary>Applies script-aware single and format-3 contextual substitutions to a glyph run.</summary>
    public ushort[] Substitute(ushort[] glyphs, string features, string scriptTag)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        var result = (ushort[])glyphs.Clone();
        try
        {
            foreach (var lookup in LookupIndices(features, scriptTag))
                for (var position = 0; position < result.Length; position++)
                    ApplyLookup(result, position, lookup, 0);
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            return glyphs;
        }
        return result;
    }

    // The subtables of one lookup type (single or ligature substitutions) of the space-separated feature tags' lookups, in
    // lookup order, for the Latin script's default language system, else the default script's, else any feature with
    // that tag.
    private int[] Subtables(string features, int kind)
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
                    if (type == Extension && _gsub.U16(subtable) == 1 && _gsub.U16(subtable + 2) == kind)
                        subtables.Add(checked(subtable + (int)_gsub.U32(subtable + 4)));
                    else if (type == kind)
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

    private int[] LookupIndices(string features, string scriptTag)
    {
        try
        {
            var scripts = _gsub.U16(4);
            var featureList = _gsub.U16(6);
            var lookupList = _gsub.U16(8);
            var featureCount = _gsub.U16(featureList);
            bool Wanted(int feature) => feature < featureCount && features.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(_gsub.Tag(featureList + 2 + 6 * feature), StringComparer.Ordinal);
            var featureIndices = LanguageFeatures(scripts, scriptTag).Where(Wanted).ToList();
            if (featureIndices.Count == 0)
                featureIndices = Enumerable.Range(0, featureCount).Where(Wanted).ToList();
            var lookups = new SortedSet<int>();
            foreach (var feature in featureIndices)
            {
                var table = featureList + _gsub.U16(featureList + 2 + 6 * feature + 4);
                for (var i = 0; i < _gsub.U16(table + 2); i++)
                    lookups.Add(_gsub.U16(table + 4 + 2 * i));
            }
            return [.. lookups.Where(i => i < _gsub.U16(lookupList))];
        }
        catch (Exception e) when (e is InvalidDataException or OverflowException)
        {
            return [];
        }
    }

    private bool ApplyLookup(ushort[] glyphs, int position, int lookupIndex, int depth)
    {
        if (depth > 8 || position < 0 || position >= glyphs.Length)
            return false;
        var lookupList = _gsub.U16(8);
        var lookup = lookupList + _gsub.U16(lookupList + 2 + 2 * lookupIndex);
        var type = _gsub.U16(lookup);
        var count = _gsub.U16(lookup + 4);
        var changed = false;
        for (var i = 0; i < count; i++)
        {
            var subtable = lookup + _gsub.U16(lookup + 6 + 2 * i);
            if (type == Extension && _gsub.U16(subtable) == 1)
            {
                var targetType = _gsub.U16(subtable + 2);
                var target = checked(subtable + (int)_gsub.U32(subtable + 4));
                changed |= ApplySubtable(glyphs, position, targetType, target, depth);
            }
            else
                changed |= ApplySubtable(glyphs, position, type, subtable, depth);
        }
        return changed;
    }

    private bool ApplySubtable(ushort[] glyphs, int position, int type, int subtable, int depth)
    {
        if (type == 1)
        {
            var replacement = Apply(subtable, glyphs[position]);
            if (replacement is not { } value || value == glyphs[position]) return false;
            glyphs[position] = value;
            return true;
        }
        if (type is 5 or 6)
            return ApplyContext(glyphs, position, type, subtable, depth + 1);
        return false;
    }

    private bool ApplyContext(ushort[] glyphs, int position, int type, int subtable, int depth)
    {
        if (_gsub.U16(subtable) != 3) return false;
        var cursor = subtable + 2;
        var backtrack = Array.Empty<int>();
        if (type == 6)
        {
            var count = _gsub.U16(cursor); cursor += 2;
            backtrack = new int[count];
            for (var i = 0; i < count; i++, cursor += 2) backtrack[i] = _gsub.U16(cursor);
        }
        var inputCount = _gsub.U16(cursor);
        // Contextual format 3 places SubstCount immediately after GlyphCount;
        // chain contextual format 3 reaches InputGlyphCount here directly.
        cursor += type == 5 ? 4 : 2;
        var input = new int[inputCount];
        for (var i = 0; i < inputCount; i++, cursor += 2) input[i] = _gsub.U16(cursor);
        var lookahead = Array.Empty<int>();
        if (type == 6)
        {
            var count = _gsub.U16(cursor); cursor += 2;
            lookahead = new int[count];
            for (var i = 0; i < count; i++, cursor += 2) lookahead[i] = _gsub.U16(cursor);
        }
        // Contextual format 3 stores SubstCount before the coverage offsets (at +4); chain
        // contextual format 3 stores it after the lookahead coverage offsets.
        var recordCount = type == 5 ? _gsub.U16(subtable + 4) : _gsub.U16(cursor);
        if (type == 6)
            cursor += 2;
        if (position + inputCount > glyphs.Length || position < backtrack.Length || position + inputCount + lookahead.Length > glyphs.Length)
            return false;
        for (var i = 0; i < backtrack.Length; i++)
            if (CoverageIndex(subtable + backtrack[i], glyphs[position - 1 - i]) < 0) return false;
        for (var i = 0; i < input.Length; i++)
            if (CoverageIndex(subtable + input[i], glyphs[position + i]) < 0) return false;
        for (var i = 0; i < lookahead.Length; i++)
            if (CoverageIndex(subtable + lookahead[i], glyphs[position + inputCount + i]) < 0) return false;
        var changed = false;
        for (var i = 0; i < recordCount; i++, cursor += 4)
        {
            var sequenceIndex = _gsub.U16(cursor);
            var referencedLookup = _gsub.U16(cursor + 2);
            if (sequenceIndex < inputCount)
                changed |= ApplyLookup(glyphs, position + sequenceIndex, referencedLookup, depth);
        }
        return changed;
    }

    // The feature indices of the Latin script's default language system, else the default script's.
    private IEnumerable<int> LanguageFeatures(int scriptList, string? preferredTag = null)
    {
        foreach (var tag in (new[] { preferredTag, "latn", "DFLT" }).Where(static tag => tag is not null).Select(static tag => tag!))
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
            2 when index < _gsub.U16(subtable + 4) => SingleReplacement(subtable, index),
            _ => null,
        };
    }

    private ushort? SingleReplacement(int subtable, int index)
    {
        // SingleSubst format 2 stores the replacement glyph IDs inline, after
        // GlyphCount; unlike a multiple substitution table it has no offsets.
        return _gsub.U16(subtable + 6 + 2 * index);
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

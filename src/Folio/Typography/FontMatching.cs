using System.Globalization;
using System.Text;

namespace Folio.Typography;

/// <summary>The properties font style matching compares.</summary>
internal readonly record struct FaceTraits(int Weight, FaceStyle Style, float Stretch);

/// <summary>Font style matching within one family (https://www.w3.org/TR/css-fonts-4/#font-style-matching).</summary>
internal static class FontMatcher
{
    /// <summary>Narrows by stretch, then style, then weight, as CSS Fonts 4 §5.2 step 4 orders them.</summary>
    public static T? Match<T>(IReadOnlyList<T> faces, Func<T, FaceTraits> traits, float stretch, FaceStyle style, int weight) where T : class
    {
        if (faces.Count == 0)
            return null;

        var bestStretch = StretchMatch(faces.Select(f => traits(f).Stretch).Distinct().ToList(), stretch);
        var candidates = faces.Where(f => traits(f).Stretch == bestStretch).ToList();

        FaceStyle[] order = style switch
        {
            FaceStyle.Italic => [FaceStyle.Italic, FaceStyle.Oblique, FaceStyle.Normal],
            FaceStyle.Oblique => [FaceStyle.Oblique, FaceStyle.Italic, FaceStyle.Normal],
            _ => [FaceStyle.Normal, FaceStyle.Oblique, FaceStyle.Italic],
        };
        var bestStyle = order.First(s => candidates.Any(f => traits(f).Style == s));
        candidates = candidates.Where(f => traits(f).Style == bestStyle).ToList();

        var bestWeight = WeightMatch(candidates.Select(f => traits(f).Weight).Distinct().ToList(), weight);
        return candidates.First(f => traits(f).Weight == bestWeight);
    }

    // Stretch: at or below normal, prefer narrower values (descending) then wider; above normal, wider then narrower.
    private static float StretchMatch(List<float> available, float desired)
    {
        if (available.Contains(desired))
            return desired;
        var narrower = available.Where(v => v < desired).OrderByDescending(v => v);
        var wider = available.Where(v => v > desired).OrderBy(v => v);
        return (desired <= 100 ? narrower.Concat(wider) : wider.Concat(narrower)).First();
    }

    // https://www.w3.org/TR/css-fonts-4/#font-style-matching, font-weight.
    private static int WeightMatch(List<int> available, int desired)
    {
        if (available.Contains(desired))
            return desired;
        if (desired is >= 400 and <= 500)
        {
            var upTo500 = available.Where(w => w > desired && w <= 500).OrderBy(w => w);
            var lighter = available.Where(w => w < desired).OrderByDescending(w => w);
            var heavier = available.Where(w => w > 500).OrderBy(w => w);
            return upTo500.Concat(lighter).Concat(heavier).First();
        }
        return desired < 400
            ? available.Where(w => w < desired).OrderByDescending(w => w).Concat(available.Where(w => w > desired).OrderBy(w => w)).First()
            : available.Where(w => w > desired).OrderBy(w => w).Concat(available.Where(w => w < desired).OrderByDescending(w => w)).First();
    }
}

/// <summary>
/// Faces grouped by family, with generic family mapping and per-cluster fallback (docs/study/11-text.md, font matching
/// and fallback steps 1). Tests fill it from bundled font files only, so text never depends on system fonts.
/// </summary>
internal sealed class FontCollection
{
    private readonly Dictionary<string, List<FontFace>> _families = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Generic family → families to try, in order (host-configurable; defaults from study 11).</summary>
    public Dictionary<string, string[]> GenericFamilies { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sans-serif"] = ["Segoe UI"],
        ["system-ui"] = ["Segoe UI"],
        ["serif"] = ["Times New Roman"],
        ["monospace"] = ["Cascadia Mono", "Consolas"],
        ["emoji"] = ["Segoe UI Emoji"],
    };

    public IEnumerable<string> Families => _families.Keys;

    public void Add(FontFace face)
    {
        if (!_families.TryGetValue(face.Family, out var faces))
            _families[face.Family] = faces = [];
        faces.Add(face);
    }

    /// <summary>Adds every face of the font files (.ttf, .otf, .ttc) in a folder; unreadable files are skipped.</summary>
    public static FontCollection FromFolder(string folder)
    {
        var collection = new FontCollection();
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc").Order())
        {
            var bytes = File.ReadAllBytes(file);
            for (var index = 0; FontFace.Parse(bytes, index) is { } face; index++)
                collection.Add(face);
        }
        return collection;
    }

    /// <summary>The best face of a family (generic families resolve through <see cref="GenericFamilies"/>).</summary>
    public FontFace? Match(string family, FaceStyle style, int weight, float stretch)
    {
        foreach (var name in Resolve(family))
        {
            if (_families.TryGetValue(name, out var faces)
                && FontMatcher.Match(faces, f => new FaceTraits(f.Weight, f.Style, f.Stretch), stretch, style, weight) is { } face)
                return face;
        }
        return null;
    }

    /// <summary>
    /// Fallback per grapheme cluster: the first family in the list whose matched face maps every code point of the
    /// cluster. Null means none does; script and system fallback come later (study 11, steps 2 and 3).
    /// </summary>
    public FontFace? FaceForCluster(IReadOnlyList<string> families, FaceStyle style, int weight, float stretch, ReadOnlySpan<char> cluster)
    {
        foreach (var family in families)
        {
            if (Match(family, style, weight, stretch) is { } face && CoversAll(face, cluster))
                return face;
        }
        return null;
    }

    private static bool CoversAll(FontFace face, ReadOnlySpan<char> cluster)
    {
        foreach (var rune in cluster.EnumerateRunes())
        {
            // Variation selectors and joiners are handled by the shaper, not the cmap.
            if (rune.Value is 0x200D or (>= 0xFE00 and <= 0xFE0F))
                continue;
            if (!face.Covers(rune.Value))
                return false;
        }
        return true;
    }

    private IEnumerable<string> Resolve(string family) =>
        GenericFamilies.TryGetValue(family, out var mapped) ? mapped : [family];
}

/// <summary>A run of glyphs from one face at one size (docs/study/11-text.md, ShapedRun).</summary>
internal sealed class ShapedRun(FontFace face, float size, ushort[] glyphs, int[] clusters, float[] advances)
{
    public FontFace Face { get; } = face;
    public float Size { get; } = size;
    public ushort[] Glyphs { get; } = glyphs;

    /// <summary>For each glyph, the UTF-16 index of the character it came from.</summary>
    public int[] Clusters { get; } = clusters;

    /// <summary>Advances in CSS px, kerning included.</summary>
    public float[] Advances { get; } = advances;

    public float Width => Advances.Sum();
}

/// <summary>
/// Folio's shaper for simple scripts (docs/study/11-text.md, shaping option B): one glyph per character from the
/// cmap, advances from hmtx, kerning from the kern table. GPOS/GSUB kerning and ligatures come with the layout tables.
/// </summary>
internal static class SimpleShaper
{
    /// <summary>
    /// Whether the text can be shaped here: every character mapped by the face, only simple scripts, and no
    /// combining marks. Everything else goes to the complex shaper.
    /// </summary>
    // ponytail: scripts are recognised by code point ranges until the Unicode Script tables are generated (study 11).
    public static bool CanShape(ReadOnlySpan<char> text, FontFace face)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!IsSimpleScript(rune.Value) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                return false;
            if (!face.Covers(rune.Value))
                return false;
        }
        return true;
    }

    public static ShapedRun Shape(string text, int start, int length, FontFace face, float size)
    {
        var glyphs = new List<ushort>(length);
        var clusters = new List<int>(length);
        for (var i = start; i < start + length;)
        {
            var rune = Rune.GetRuneAt(text, i);
            glyphs.Add(face.GlyphFor(rune.Value));
            clusters.Add(i);
            i += rune.Utf16SequenceLength;
        }

        var scale = size / face.UnitsPerEm;
        var advances = new float[glyphs.Count];
        for (var g = 0; g < glyphs.Count; g++)
        {
            var units = face.Advance(glyphs[g]);
            if (g + 1 < glyphs.Count)
                units += face.Kerning(glyphs[g], glyphs[g + 1]);
            advances[g] = units * scale;
        }
        return new ShapedRun(face, size, [.. glyphs], [.. clusters], advances);
    }

    private static bool IsSimpleScript(int c) => c switch
    {
        <= 0x024F => true,                       // Basic Latin through Latin Extended-B
        >= 0x0370 and <= 0x052F => true,         // Greek, Cyrillic, Cyrillic Supplement
        >= 0x1E00 and <= 0x1FFF => true,         // Latin Extended Additional, Greek Extended
        >= 0x2000 and <= 0x218F => true,         // punctuation, super/subscripts, currency, letterlike, number forms
        >= 0x2C60 and <= 0x2C7F => true,         // Latin Extended-C
        >= 0xA720 and <= 0xA7FF => true,         // Latin Extended-D
        0xFFFD => true,
        _ => false,
    };
}

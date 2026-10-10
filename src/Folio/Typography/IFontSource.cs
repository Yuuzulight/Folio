namespace Folio.Typography;

/// <summary>
/// Where fonts come from (docs/dependencies.md, system services): the families a source offers, the font data of a
/// family, and a family for a character no other font covers (fallback step 3, docs/study/11-text.md).
/// </summary>
/// <remarks>
/// Folio parses and validates every face it is given; a source only finds data. Families are opened on first use.
/// <para>
/// Folio keeps the parsed faces per source instance, shared by every document that uses the instance:
/// <see cref="OpenFamily"/> is called at most once per family (compared ignoring case) for the life of the instance, not
/// once per document, and may be called from any thread. A call that throws is not remembered. A source whose fonts
/// change (fonts installed or removed) shows the change only through a new instance. <see cref="MatchCharacter"/> is
/// still asked once per document.
/// </para>
/// </remarks>
public interface IFontSource
{
    /// <summary>The faces of a family (any case), as font data and face index; empty when the source has no such family.</summary>
    /// <remarks>Called at most once per family for the life of this instance; the result is shared by every document using it.</remarks>
    IReadOnlyList<IFontHandle> OpenFamily(string family);

    /// <summary>A family with a glyph for the code point, preferring one close to the given traits; null when none has one.</summary>
    string? MatchCharacter(int codePoint, int weight, bool italic);
}

/// <summary>An <see cref="IFontSource"/> over the font files (.ttf, .otf, .ttc) of one folder, read on first use.</summary>
public sealed class FontFolderSource(string folder) : IFontSource
{
    private readonly Lazy<Dictionary<string, List<IFontHandle>>> _families = new(() => Read(folder));

    public IReadOnlyList<IFontHandle> OpenFamily(string family) =>
        _families.Value.TryGetValue(family, out var faces) ? faces : [];

    public string? MatchCharacter(int codePoint, int weight, bool italic) =>
        _families.Value.FirstOrDefault(f => f.Value.OfType<FontFace>().Any(face => face.Covers(codePoint))).Key;

    private static Dictionary<string, List<IFontHandle>> Read(string folder)
    {
        var families = new Dictionary<string, List<IFontHandle>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
            return families;
        foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc").Order())
        {
            var bytes = File.ReadAllBytes(file);
            for (var index = 0; FontFace.Parse(bytes, index) is { } face; index++)
            {
                if (!families.TryGetValue(face.Family, out var faces))
                    families[face.Family] = faces = [];
                faces.Add(face);
            }
        }
        return families;
    }
}

/// <summary>How a document finds fonts: the source, and the families generic names stand for.</summary>
public sealed record FontSettings
{
    /// <summary>No fonts: text is measured with fallback metrics and not drawn.</summary>
    public static FontSettings Default { get; } = new();

    /// <summary>Where installed fonts come from; null for none.</summary>
    /// <remarks>
    /// Parsed faces are kept per source instance and shared across documents (see <see cref="IFontSource"/>), so reuse one
    /// instance across documents, and make a new one when the fonts it offers change.
    /// </remarks>
    public IFontSource? Source { get; init; }

    /// <summary>Generic family → families to try in order (study 11 defaults, for Windows).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GenericFamilies { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sans-serif"] = ["Segoe UI"],
            ["system-ui"] = ["Segoe UI"],
            ["serif"] = ["Times New Roman"],
            ["monospace"] = ["Cascadia Mono", "Consolas"],
            ["cursive"] = ["Comic Sans MS"],
            ["fantasy"] = ["Impact"],
            ["emoji"] = ["Segoe UI Emoji"],
        };
}

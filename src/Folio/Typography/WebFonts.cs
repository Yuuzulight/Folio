using Folio.Resources;
using Folio.Style;

namespace Folio.Typography;

/// <summary>A face loaded for an <c>@font-face</c> rule: the font and the traits the rule gives it.</summary>
internal sealed record WebFace(FontFace Face, FontFaceRule Rule)
{
    /// <summary>The traits to match against a desired style: ranges clamp towards the desired value (Fonts 4 §5.2).</summary>
    public FaceTraits TraitsFor(int weight, float stretch) => new(
        Rule.Weight is { } w ? Math.Clamp(weight, w.Min, w.Max) : Face.Weight,
        Rule.Style switch { FontStyle.Normal => FaceStyle.Normal, FontStyle.Italic => FaceStyle.Italic, FontStyle.Oblique => FaceStyle.Oblique, _ => Face.Style },
        Rule.Stretch is { } s ? Math.Clamp(stretch, s.Min, s.Max) : Face.Stretch);
}

/// <summary>
/// Loads the faces of <c>@font-face</c> rules into a <see cref="FontCollection"/> (docs/study/11-text.md): the first
/// <c>src</c> entry that loads and parses wins. URLs go through the document's <see cref="ResourceLoader"/>, so only
/// what the host allows is ever read; <c>local()</c> names a family of the host's font source.
/// </summary>
internal static class WebFonts
{
    public static void Load(IEnumerable<FontFaceRule> rules, FontCollection fonts, ResourceLoader loader, Action<string>? report = null)
    {
        foreach (var rule in rules)
        {
            fonts.DeclareWebFamily(rule.Family);
            var face = rule.Sources.Select(source => Open(source, fonts, loader, report)).FirstOrDefault(f => f is not null);
            if (face is not null)
                fonts.AddWebFace(new WebFace(face, rule));
            else
                report?.Invoke($"No source of the font \"{rule.Family}\" could be used; the next family in the list is used instead.");
        }
    }

    private static FontFace? Open(FontFaceSource source, FontCollection fonts, ResourceLoader loader, Action<string>? report)
    {
        if (source.Local is { } local)
            return fonts.MatchInstalled(local);
        var response = loader.Load(new ResourceRequest(source.Url!, ResourceKind.Font));
        if (!response.Succeeded)
        {
            report?.Invoke($"Font {Shorten(source.Url!)} was not loaded: {response.Error}");
            return null;
        }
        var face = WebFontDecoder.Decode(response.Data) is { } sfnt ? FontFace.Parse(sfnt) : null;
        if (face is null)
            report?.Invoke($"Font {Shorten(source.Url!)} is not a usable WOFF, WOFF 2, TrueType or OpenType font.");
        return face;
    }

    private static string Shorten(string url) => url.Length > 80 ? url[..77] + "..." : url;
}

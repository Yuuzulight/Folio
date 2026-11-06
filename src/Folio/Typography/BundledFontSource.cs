
namespace Folio.Typography;

/// <summary>Trusted, on-demand font assets shipped with the isolated content host.</summary>
public sealed class BundledFontSource : IFontSource
{
    private readonly string _directory;

    /// <summary>Uses the licensed font assets copied beside a consuming application by Folio's package.</summary>
    public BundledFontSource() : this(Path.Combine(AppContext.BaseDirectory, "fonts")) { }

    /// <summary>Uses an explicitly deployed font directory; unknown families return no faces.</summary>
    public BundledFontSource(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    private static readonly IReadOnlyDictionary<string, string[]> Families = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["Source Sans 3"] = ["SourceSans3-Regular.ttf", "SourceSans3-Bold.ttf", "SourceSans3-It.ttf"],
        ["Source Code Pro"] = ["SourceCodePro-Regular.ttf", "SourceCodePro-Bold.ttf", "SourceCodePro-It.ttf", "SourceCodePro-BoldIt.ttf"],
        ["Source Serif 4"] = ["SourceSerif4-Regular.ttf", "SourceSerif4-Bold.ttf", "SourceSerif4-It.ttf", "SourceSerif4-BoldIt.ttf"],
        ["Noto Sans Arabic"] = ["NotoSansArabic-Regular.ttf", "NotoSansArabic-Bold.ttf"],
        ["Noto Sans Hebrew"] = ["NotoSansHebrew-Regular.ttf", "NotoSansHebrew-Bold.ttf"],
        ["Noto Sans JP"] = ["NotoSansJP-Regular.otf", "NotoSansJP-Bold.otf"],
        ["Noto Sans SC"] = ["NotoSansSC-Regular.otf", "NotoSansSC-Bold.otf"],
        ["Noto Color Emoji"] = ["NotoColorEmoji.ttf"],
    };

    // FontCollection caches parsed families per source. Reuse this source across
    // navigations; opening one family never scans or parses the other files.
    public static FontSettings Settings { get; } = new()
    {
        Source = new BundledFontSource(),
        GenericFamilies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["sans-serif"] = ["Source Sans 3"],
            ["system-ui"] = ["Source Sans 3"],
            ["serif"] = ["Source Serif 4"],
            ["monospace"] = ["Source Code Pro"],
            ["cursive"] = ["Source Sans 3"],
            ["fantasy"] = ["Source Sans 3"],
            ["emoji"] = ["Noto Color Emoji"],
        }
    };

    public IReadOnlyList<IFontHandle> OpenFamily(string family) => Families.TryGetValue(family, out var files)
        ? files.Select(file => (IFontHandle)new FontData(File.ReadAllBytes(Path.Combine(_directory, file)))).ToArray()
        : [];

    public string? MatchCharacter(int codePoint, int weight, bool italic) => codePoint switch
    {
        >= 0x0590 and <= 0x05ff => "Noto Sans Hebrew",
        >= 0x0600 and <= 0x08ff or >= 0xfb50 and <= 0xfdff or >= 0xfe70 and <= 0xfeff => "Noto Sans Arabic",
        >= 0x3040 and <= 0x30ff or >= 0x31f0 and <= 0x31ff or >= 0xac00 and <= 0xd7af => "Noto Sans JP",
        >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af => "Noto Sans SC",
        >= 0x1f000 and <= 0x1faff or >= 0x2600 and <= 0x27bf => "Noto Color Emoji",
        _ => "Source Sans 3",
    };

    private sealed record FontData(ReadOnlyMemory<byte> Data) : IFontHandle
    {
        public int FaceIndex => 0;
    }
}

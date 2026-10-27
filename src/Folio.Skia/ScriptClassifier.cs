namespace Folio.Skia;

/// <summary>Conservative script labels used by the shaping replacement gate.</summary>
internal static class ScriptClassifier
{
    public static string Classify(ReadOnlySpan<char> text)
    {
        string? script = null;
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            var current = value switch
            {
                >= 0x0590 and <= 0x05FF => "Hebrew",
                >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF => "Arabic",
                >= 0x0900 and <= 0x0DFF => "Indic",
                >= 0x0E00 and <= 0x0E7F or >= 0x0E80 and <= 0x0EFF => "Southeast Asian",
                >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x9FFF => "CJK",
                >= 0x0370 and <= 0x03FF => "Greek",
                >= 0x0400 and <= 0x052F => "Cyrillic",
                _ => null,
            };
            if (current is null)
                continue;
            if (script is not null && !string.Equals(script, current, StringComparison.Ordinal))
                return "Mixed";
            script = current;
        }
        return script ?? "Latin";
    }
}

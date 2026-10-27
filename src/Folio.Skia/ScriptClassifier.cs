namespace Folio.Skia;

/// <summary>Conservative script labels used by the shaping replacement gate.</summary>
internal static class ScriptClassifier
{
    public static string Classify(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x0590 and <= 0x05FF) return "Hebrew";
            if (value is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF) return "Arabic";
            if (value is >= 0x0900 and <= 0x0DFF) return "Indic";
            if (value is >= 0x0E00 and <= 0x0E7F or >= 0x0E80 and <= 0x0EFF) return "Southeast Asian";
            if (value is >= 0x3040 and <= 0x30FF or >= 0x3400 and <= 0x9FFF) return "CJK";
            if (value is >= 0x0370 and <= 0x03FF) return "Greek";
            if (value is >= 0x0400 and <= 0x052F) return "Cyrillic";
        }
        return "Latin";
    }
}

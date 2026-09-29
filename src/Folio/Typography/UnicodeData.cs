namespace Folio.Typography;

/// <summary>Lookups over the generated Unicode tables (UnicodeData.g.cs).</summary>
internal static partial class UnicodeData
{
    /// <summary>The paired bracket of a bracket character and whether it opens (Bidi_Paired_Bracket, UAX #9).</summary>
    public static (int Pair, bool Opens)? Bracket(int codePoint)
    {
        var i = BracketCodePoints.BinarySearch(codePoint);
        return i >= 0 ? (BracketPairs[i], BracketIsOpen[i]) : null;
    }

    /// <summary>The Bidi_Mirroring_Glyph of a code point, or null when it has none.</summary>
    public static int? Mirror(int codePoint)
    {
        var i = MirrorCodePoints.BinarySearch(codePoint);
        return i >= 0 ? Mirrors[i] : null;
    }

    // The value of the range holding the code point; ranges are given by their first code points, the first being 0.
    private static byte Lookup(ReadOnlySpan<int> starts, ReadOnlySpan<byte> values, int codePoint)
    {
        if ((uint)codePoint >= 0x110000)
            return 0;
        var i = starts.BinarySearch(codePoint);
        return values[i >= 0 ? i : ~i - 1];
    }
}

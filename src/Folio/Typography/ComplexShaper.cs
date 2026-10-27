using System.Globalization;
using System.Numerics;
using System.Text;

namespace Folio.Typography;

/// <summary>
/// Folio's in-house complex script shaper (docs/dependencies.md, Towards all-Folio):
/// supports Hebrew (base consonants and combining niqqud marks positioning) and
/// Arabic (Unicode cursive joining forms: isolated, initial, medial, final, and mark positioning).
/// </summary>
public sealed class ComplexShaper : ITextShaper
{
    public static readonly ComplexShaper Instance = new();

    public ShapedText Shape(string text, int start, int length, IFontHandle font, float size, bool rightToLeft, string? language)
    {
        if (length == 0 || font is not FontFace face)
            return new ShapedText([], [], [], []);

        var runes = new List<(Rune Rune, int Index)>();
        for (var i = start; i < start + length;)
        {
            var r = Rune.GetRuneAt(text, i);
            runes.Add((r, i));
            i += r.Utf16SequenceLength;
        }

        var glyphs = new List<ushort>(runes.Count);
        var clusters = new List<int>(runes.Count);
        var advances = new List<float>(runes.Count);
        var offsets = new List<Vector2>(runes.Count);

        var scale = size / face.UnitsPerEm;

        if (IsArabic(runes))
        {
            ShapeArabic(runes, face, scale, glyphs, clusters, advances, offsets);
        }
        else if (IsHebrew(runes))
        {
            ShapeHebrew(runes, face, scale, glyphs, clusters, advances, offsets);
        }
        else
        {
            // Fallback simple 1:1 mapping
            foreach (var (rune, index) in runes)
            {
                var glyph = face.GlyphFor(rune.Value);
                glyphs.Add(glyph);
                clusters.Add(index);
                advances.Add(face.Advance(glyph) * scale);
                offsets.Add(Vector2.Zero);
            }
        }

        return new ShapedText([.. glyphs], [.. clusters], [.. advances], [.. offsets]);
    }

    private static bool IsArabic(List<(Rune Rune, int Index)> runes)
    {
        foreach (var (r, _) in runes)
        {
            if (r.Value is (>= 0x0600 and <= 0x06FF) or (>= 0xFB50 and <= 0xFDFF) or (>= 0xFE70 and <= 0xFEFF))
                return true;
        }
        return false;
    }

    private static bool IsHebrew(List<(Rune Rune, int Index)> runes)
    {
        foreach (var (r, _) in runes)
        {
            if (r.Value is >= 0x0590 and <= 0x05FF)
                return true;
        }
        return false;
    }

    private enum ArabicJoining
    {
        NonJoining,
        Transparent,
        RightJoining,
        LeftJoining,
        DualJoining,
    }

    private enum ArabicForm
    {
        Isolated,
        Final,
        Initial,
        Medial,
    }

    private static ArabicJoining GetArabicJoining(int c) => c switch
    {
        // Transparent marks (Tashkeel)
        >= 0x064B and <= 0x0655 => ArabicJoining.Transparent,
        0x0670 => ArabicJoining.Transparent,
        // Right-joining only
        0x0622 or 0x0623 or 0x0624 or 0x0625 or 0x0627 => ArabicJoining.RightJoining,
        0x062F or 0x0630 or 0x0631 or 0x0632 => ArabicJoining.RightJoining,
        0x0648 => ArabicJoining.RightJoining,
        0x0649 => ArabicJoining.RightJoining,
        // Dual-joining
        >= 0x0626 and <= 0x062E => ArabicJoining.DualJoining,
        >= 0x0633 and <= 0x0647 => ArabicJoining.DualJoining,
        0x064A => ArabicJoining.DualJoining,
        _ => ArabicJoining.NonJoining,
    };

    private static void ShapeArabic(
        List<(Rune Rune, int Index)> runes, FontFace face, float scale,
        List<ushort> glyphs, List<int> clusters, List<float> advances, List<Vector2> offsets)
    {
        var forms = new ArabicForm[runes.Count];

        ushort? previousBaseGlyph = null;
        int? previousBaseCluster = null;
        ushort? previousMarkGlyph = null;
        var previousMarkOffset = Vector2.Zero;

        for (var i = 0; i < runes.Count; i++)
        {
            var joining = GetArabicJoining(runes[i].Rune.Value);
            if (joining is ArabicJoining.NonJoining or ArabicJoining.Transparent)
            {
                forms[i] = ArabicForm.Isolated;
                continue;
            }

            // Look backward for preceding non-transparent
            var canJoinRight = false;
            for (var prev = i - 1; prev >= 0; prev--)
            {
                var prevJ = GetArabicJoining(runes[prev].Rune.Value);
                if (prevJ == ArabicJoining.Transparent) continue;
                if (prevJ is ArabicJoining.DualJoining or ArabicJoining.LeftJoining)
                    canJoinRight = true;
                break;
            }

            // Look forward for succeeding non-transparent
            var canJoinLeft = false;
            for (var next = i + 1; next < runes.Count; next++)
            {
                var nextJ = GetArabicJoining(runes[next].Rune.Value);
                if (nextJ == ArabicJoining.Transparent) continue;
                if (nextJ is ArabicJoining.DualJoining or ArabicJoining.RightJoining)
                    canJoinLeft = true;
                break;
            }

            if (joining == ArabicJoining.RightJoining)
            {
                forms[i] = canJoinRight ? ArabicForm.Final : ArabicForm.Isolated;
            }
            else // Dual-joining
            {
                if (canJoinRight && canJoinLeft)
                    forms[i] = ArabicForm.Medial;
                else if (canJoinRight)
                    forms[i] = ArabicForm.Final;
                else if (canJoinLeft)
                    forms[i] = ArabicForm.Initial;
                else
                    forms[i] = ArabicForm.Isolated;
            }
        }

        for (var i = 0; i < runes.Count; i++)
        {
            var (rune, cluster) = runes[i];
            var code = rune.Value;
            var joining = GetArabicJoining(code);

            if (joining == ArabicJoining.Transparent)
            {
                // Arabic tashkeel: advance = 0, positioned through Folio-owned GPOS anchors.
                var glyph = face.GlyphFor(code);
                glyphs.Add(glyph);
                clusters.Add(previousBaseCluster ?? cluster);
                advances.Add(0f);
                var relativeToPreviousMark = false;
                (int X, int Y)? markOffset = null;
                if (previousMarkGlyph is { } previousMark)
                {
                    markOffset = face.MarkToMarkOffset(previousMark, glyph);
                    relativeToPreviousMark = markOffset is not null;
                }
                if (markOffset is null && previousBaseGlyph is { } previousBase)
                    markOffset = face.MarkOffset(previousBase, glyph);
                if (markOffset is { } position)
                {
                    var offset = new Vector2(position.X * scale, -position.Y * scale);
                    if (relativeToPreviousMark)
                        offset += previousMarkOffset;
                    offsets.Add(offset);
                    previousMarkOffset = offset;
                }
                else
                {
                    offsets.Add(Vector2.Zero);
                    previousMarkOffset = Vector2.Zero;
                }
                previousMarkGlyph = glyph;
                continue;
            }

            var form = forms[i];
            var mappedCode = MapArabicPresentationForm(code, form);
            var mappedGlyph = face.Covers(mappedCode) ? face.GlyphFor(mappedCode) : face.GlyphFor(code);

            glyphs.Add(mappedGlyph);
            clusters.Add(cluster);
            advances.Add(face.Advance(mappedGlyph) * scale);
            offsets.Add(Vector2.Zero);
            previousBaseGlyph = mappedGlyph;
            previousBaseCluster = cluster;
            previousMarkGlyph = null;
            previousMarkOffset = Vector2.Zero;
        }
    }

    private static int MapArabicPresentationForm(int code, ArabicForm form)
    {
        // Unicode Arabic Presentation Forms-B mappings (0xFE80..0xFEFC)
        return code switch
        {
            0x0627 => form == ArabicForm.Final ? 0xFE8E : 0xFE8D, // ALEF
            0x0628 => form switch { ArabicForm.Final => 0xFE90, ArabicForm.Initial => 0xFE91, ArabicForm.Medial => 0xFE92, _ => 0xFE8F }, // BEH
            0x062A => form switch { ArabicForm.Final => 0xFE96, ArabicForm.Initial => 0xFE97, ArabicForm.Medial => 0xFE98, _ => 0xFE95 }, // TEH
            0x062B => form switch { ArabicForm.Final => 0xFE9A, ArabicForm.Initial => 0xFE9B, ArabicForm.Medial => 0xFE9C, _ => 0xFE99 }, // THEH
            0x062C => form switch { ArabicForm.Final => 0xFE9E, ArabicForm.Initial => 0xFE9F, ArabicForm.Medial => 0xFEA0, _ => 0xFE9D }, // JEEM
            0x062D => form switch { ArabicForm.Final => 0xFEA2, ArabicForm.Initial => 0xFEA3, ArabicForm.Medial => 0xFEA4, _ => 0xFEA1 }, // HAH
            0x062E => form switch { ArabicForm.Final => 0xFEA6, ArabicForm.Initial => 0xFEA7, ArabicForm.Medial => 0xFEA8, _ => 0xFEA5 }, // KHAH
            0x062F => form == ArabicForm.Final ? 0xFEAA : 0xFEA9, // DAL
            0x0630 => form == ArabicForm.Final ? 0xFEAC : 0xFEAB, // THAL
            0x0631 => form == ArabicForm.Final ? 0xFEAE : 0xFEAD, // REH
            0x0632 => form == ArabicForm.Final ? 0xFEB0 : 0xFEAF, // ZAIN
            0x0633 => form switch { ArabicForm.Final => 0xFEB2, ArabicForm.Initial => 0xFEB3, ArabicForm.Medial => 0xFEB4, _ => 0xFEB1 }, // SEEN
            0x0634 => form switch { ArabicForm.Final => 0xFEB6, ArabicForm.Initial => 0xFEB7, ArabicForm.Medial => 0xFEB8, _ => 0xFEB5 }, // SHEEN
            0x0635 => form switch { ArabicForm.Final => 0xFEBA, ArabicForm.Initial => 0xFEBB, ArabicForm.Medial => 0xFEBC, _ => 0xFEB9 }, // SAD
            0x0636 => form switch { ArabicForm.Final => 0xFEBE, ArabicForm.Initial => 0xFEBF, ArabicForm.Medial => 0xFEC0, _ => 0xFEBD }, // DAD
            0x0637 => form switch { ArabicForm.Final => 0xFEC2, ArabicForm.Initial => 0xFEC3, ArabicForm.Medial => 0xFEC4, _ => 0xFEC1 }, // TAH
            0x0638 => form switch { ArabicForm.Final => 0xFEC6, ArabicForm.Initial => 0xFEC7, ArabicForm.Medial => 0xFEC8, _ => 0xFEC5 }, // ZAH
            0x0639 => form switch { ArabicForm.Final => 0xFECA, ArabicForm.Initial => 0xFECB, ArabicForm.Medial => 0xFECC, _ => 0xFEC9 }, // AIN
            0x063A => form switch { ArabicForm.Final => 0xFECE, ArabicForm.Initial => 0xFECF, ArabicForm.Medial => 0xFED0, _ => 0xFECD }, // GHAIN
            0x0641 => form switch { ArabicForm.Final => 0xFED2, ArabicForm.Initial => 0xFED3, ArabicForm.Medial => 0xFED4, _ => 0xFED1 }, // FEH
            0x0642 => form switch { ArabicForm.Final => 0xFED6, ArabicForm.Initial => 0xFED7, ArabicForm.Medial => 0xFED8, _ => 0xFED5 }, // QAF
            0x0643 => form switch { ArabicForm.Final => 0xFEDA, ArabicForm.Initial => 0xFEDB, ArabicForm.Medial => 0xFEDC, _ => 0xFED9 }, // KAF
            0x0644 => form switch { ArabicForm.Final => 0xFEDE, ArabicForm.Initial => 0xFEDF, ArabicForm.Medial => 0xFEE0, _ => 0xFEDD }, // LAM
            0x0645 => form switch { ArabicForm.Final => 0xFEE2, ArabicForm.Initial => 0xFEE3, ArabicForm.Medial => 0xFEE4, _ => 0xFEE1 }, // MEEM
            0x0646 => form switch { ArabicForm.Final => 0xFEE6, ArabicForm.Initial => 0xFEE7, ArabicForm.Medial => 0xFEE8, _ => 0xFEE5 }, // NOON
            0x0647 => form switch { ArabicForm.Final => 0xFEEA, ArabicForm.Initial => 0xFEEB, ArabicForm.Medial => 0xFEEC, _ => 0xFEE9 }, // HEH
            0x0648 => form == ArabicForm.Final ? 0xFEEE : 0xFEED, // WAW
            0x064A => form switch { ArabicForm.Final => 0xFEF2, ArabicForm.Initial => 0xFEF3, ArabicForm.Medial => 0xFEF4, _ => 0xFEF1 }, // YEH
            _ => code,
        };
    }

    private static void ShapeHebrew(
        List<(Rune Rune, int Index)> runes, FontFace face, float scale,
        List<ushort> glyphs, List<int> clusters, List<float> advances, List<Vector2> offsets)
    {
        // HarfBuzz emits Hebrew combining marks in the visual attachment order
        // (the mark sequence is reversed for the RTL run). Keep the source
        // cluster on every mark while matching that ordering for the Folio path.
        var orderedRunes = new List<(Rune Rune, int Index)>(runes.Count);
        for (var i = 0; i < runes.Count;)
        {
            orderedRunes.Add(runes[i]);
            var end = i + 1;
            while (end < runes.Count && IsHebrewMark(runes[end].Rune))
                end++;
            for (var mark = end - 1; mark > i; mark--)
                orderedRunes.Add(runes[mark] with { Index = runes[i].Index });
            i = end;
        }

        ushort? previousBaseGlyph = null;
        int? previousBaseCluster = null;
        ushort? previousMarkGlyph = null;
        var previousMarkOffset = Vector2.Zero;

        for (var i = 0; i < orderedRunes.Count; i++)
        {
            var (rune, cluster) = orderedRunes[i];
            var code = rune.Value;
            var glyph = face.GlyphFor(code);
            glyphs.Add(glyph);

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                clusters.Add(previousBaseCluster ?? cluster);
                // Hebrew mark (Niqqud): advance = 0, placed using the face's GPOS
                // mark-to-base or mark-to-mark anchor. Mark-to-mark offsets are
                // relative to the preceding mark, so retain the accumulated offset.
                advances.Add(0f);
                var relativeToPreviousMark = false;
                (int X, int Y)? markOffset = null;
                if (previousMarkGlyph is { } previousMark)
                {
                    markOffset = face.MarkToMarkOffset(previousMark, glyph);
                    relativeToPreviousMark = markOffset is not null;
                }
                if (markOffset is null && previousBaseGlyph is { } previousBase)
                    markOffset = face.MarkOffset(previousBase, glyph);
                if (markOffset is { } position)
                {
                    var offset = new Vector2(position.X * scale, -position.Y * scale);
                    if (relativeToPreviousMark)
                        offset += previousMarkOffset;
                    offsets.Add(offset);
                    previousMarkOffset = offset;
                }
                else
                {
                    offsets.Add(Vector2.Zero);
                    previousMarkOffset = Vector2.Zero;
                }
                previousMarkGlyph = glyph;
            }
            else
            {
                advances.Add(face.Advance(glyph) * scale);
                offsets.Add(Vector2.Zero);
                clusters.Add(cluster);
                previousBaseGlyph = glyph;
                previousBaseCluster = cluster;
                previousMarkGlyph = null;
                previousMarkOffset = Vector2.Zero;
            }
        }
    }

    private static bool IsHebrewMark(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

}

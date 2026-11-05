using Folio.Typography;
using System.Runtime.CompilerServices;

namespace Folio.Painting;

/// <summary>Preparation and isolation of a font's hinting programs. Production callers supply device pixels per em.</summary>
internal static class FontHinting
{
    internal const long CacheBudget = 1024 * 1024;
    private static readonly ConditionalWeakTable<FontFace, FontCache> Fonts = new();

    // One bounded cache per live face, shared across surfaces. It retains no external font/native resources.
    // Failed programs are recorded as failures, not as fitted outlines. A malformed font still gets its complete
    // unhinted outline, while diagnostics and tests can distinguish fallback from successful grid fitting.
    internal static bool TryGet(FontFace face, ushort glyph, float pixelsPerEm, out PathData? path, out string? error)
    {
        path = null; error = null;
        if (face.RawTable("glyf") is null && face.RawTable("CFF ") is null && face.RawTable("CFF2") is null) return false;
        return Fonts.GetOrCreateValue(face).TryGet(face, glyph, pixelsPerEm, out path, out error);
    }

    internal static long CacheBytes(FontFace face) => Fonts.TryGetValue(face, out var cache) ? cache.Bytes : 0;

    private sealed class FontCache
    {
        private readonly Dictionary<int, (TrueTypeMachine? Machine, string? Error, long Bytes)> _sizes = [];
        private readonly Dictionary<(int Size, ushort Glyph), (PathData? Path, string? Error, long Bytes)> _glyphs = [];
        private long _bytes;
        internal long Bytes { get { lock (_sizes) return _bytes; } }

        internal bool TryGet(FontFace face, ushort glyph, float size, out PathData? path, out string? error)
        {
            path = null; error = null;
            if (!float.IsFinite(size) || size <= 0 || size > 4096) return false;
            var key = (int)MathF.Round(size * 64);
            if (key == 0) return false;
            lock (_sizes)
            {
                if (_glyphs.TryGetValue((key, glyph), out var existing))
                { path = existing.Path; error = existing.Error; return error is null; }
                if (face.RawTable("glyf") is null)
                {
                    try { path = GlyphOutlines.GetHintedCff(face, glyph, key / 64f); }
                    catch (Exception e) when (e is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
                    { error = e.Message; }
                    Remember(path, error);
                    return error is null;
                }
                if (!_sizes.TryGetValue(key, out var prepared))
                {
                    try
                    {
                        var machine = Prepare(face, key / 64f);
                        prepared = (machine, null, machine.RetainedBytes);
                    }
                    catch (Exception e) when (e is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
                    { prepared = (null, e.Message, 2048); }
                    Trim(prepared.Bytes);
                    if (prepared.Bytes <= CacheBudget) { _sizes[key] = prepared; _bytes += prepared.Bytes; }
                }
                error = prepared.Error;
                if (prepared.Machine is not null)
                {
                    try { path = GlyphOutlines.GetHinted(face, glyph, prepared.Machine); }
                    catch (Exception e) when (e is InvalidDataException or OverflowException or ArgumentException or IndexOutOfRangeException)
                    { error = e.Message; }
                }
                Remember(path, error);
                return error is null;

                void Remember(PathData? storedPath, string? storedError)
                {
                    var bytes = 2048L + (storedPath?.Commands.Count ?? 0) * 48L;
                    Trim(bytes);
                    if (bytes <= CacheBudget)
                    { _glyphs[(key, glyph)] = (storedPath, storedError, bytes); _bytes += bytes; }
                }
            }
        }

        private void Trim(long incoming)
        {
            while (_bytes + incoming > CacheBudget && (_glyphs.Count > 0 || _sizes.Count > 0))
            {
                if (_glyphs.Count > 0)
                { var item = _glyphs.First(); _bytes -= item.Value.Bytes; _glyphs.Remove(item.Key); }
                else
                { var item = _sizes.First(); _bytes -= item.Value.Bytes; _sizes.Remove(item.Key); }
            }
        }
    }

    internal static TrueTypeMachine Prepare(FontFace face, float pixelsPerEm)
    {
        var maxp = face.RawTable("maxp") ?? throw new InvalidDataException("Missing TrueType profile.");
        if (maxp.Length < 32 || maxp.U32(0) != 0x00010000) throw new InvalidDataException("Invalid TrueType profile.");
        var cvt = face.RawTable("cvt ");
        if (cvt is { } table && (table.Length % 2 != 0 || table.Length / 2 > TrueTypeMachine.MemoryLimit))
            throw new InvalidDataException("Invalid hinting control value table.");
        var values = new short[(cvt?.Length ?? 0) / 2];
        for (var i = 0; i < values.Length; i++) values[i] = cvt!.Value.S16(2 * i);
        var machine = new TrueTypeMachine(pixelsPerEm, face.UnitsPerEm, maxp.U16(18), maxp.U16(16), values);
        if (face.RawTable("fpgm") is { } fontProgram) machine.Execute(Program(fontProgram), definitions: true);
        // The CVT program has a fresh operand stack and graphics state; only font definitions and storage survive.
        machine.StartGlyph(new([], []), new());
        if (face.RawTable("prep") is { } prep) machine.Execute(Program(prep), definitions: true);
        return machine;
    }

    private static byte[] Program(FontData table) => table.Length <= 65_536
        ? table.Span.ToArray() : throw new InvalidDataException("Font hint program exceeds host limits.");
}

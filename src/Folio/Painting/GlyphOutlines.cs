using System.Numerics;
using Folio.Typography;

namespace Folio.Painting;

/// <summary>
/// Glyph outlines in font units (y up) as paths of lines and cubic curves: TrueType <c>glyf</c> outlines, with composite
/// glyphs, and CFF Type 2 charstrings. A glyph that is missing, empty or malformed has no path (null) or an empty one.
/// TrueType (https://learn.microsoft.com/typography/opentype/spec/glyf), CFF (Adobe Technical Notes 5176 and 5177).
/// </summary>
internal static class GlyphOutlines
{
    private const int MaxCompositeDepth = 8;
    private const int MaxCompositeParts = 256;

    internal static PathData GetHintedCff(FontFace face, ushort glyph, float pixelsPerEm)
    {
        if (glyph >= face.GlyphCount) throw new InvalidDataException("Invalid CFF glyph id.");
        if (face.RawTable("CFF ") is { } cff)
            return CffFont.For(face, cff, false).Outline(glyph, pixelsPerEm / face.UnitsPerEm);
        if (face.RawTable("CFF2") is { } cff2)
            return CffFont.For(face, cff2, true).Outline(glyph, pixelsPerEm / face.UnitsPerEm);
        throw new InvalidDataException("No CFF outline.");
    }

    // A distinct entry point keeps unhinted font-unit outlines reusable. Failure is explicit: a renderer must fall
    // back to the complete original outline, never cache partially modified points as successfully fitted text.
    internal static PathData GetHinted(FontFace face, ushort glyph, TrueTypeMachine prepared)
    {
        if (glyph >= face.GlyphCount || face.RawTable("glyf") is not { } glyf || face.RawTable("loca") is not { } loca)
            throw new InvalidDataException("No TrueType glyph outline.");
        var (start, end) = GlyphRange(face, loca, glyph);
        if (end <= start) return new PathData();
        var contours = new Contours();
        var parts = 0;
        var budget = new HintBudget();
        Collect(face, glyf, loca, glyph, contours, 0, ref parts, prepared, budget);
        var shift = contours.Phantoms?[0].X ?? 0;
        var scale = prepared.PixelsPerEm / face.UnitsPerEm;
        var fitted = contours.Points.Select(p => new Point((p.X - shift) * scale, p.Y * scale, p.OnCurve)).ToList();
        var path = new PathData(); var first = 0;
        foreach (var last in contours.Ends) { AddContour(path, fitted, first, last); first = last + 1; }
        return path;
    }

    private sealed class HintBudget { internal int Remaining = TrueTypeMachine.InstructionLimit; }

    private static void Fit(FontFace face, ushort glyph, FontData data, Contours contours, TrueTypeMachine prepared, HintBudget budget)
    {
        if (contours.Points.Count > TrueTypeMachine.PointLimit - 4) throw new InvalidDataException("Too many hinting points.");
        var points = new Vector2[contours.Points.Count + 4];
        var flags = new bool[points.Length];
        var scale = prepared.PixelsPerEm * 64 / face.UnitsPerEm;
        for (var i = 0; i < contours.Points.Count; i++)
        {
            var p = contours.Points[i];
            points[i] = new Vector2(MathF.Round(p.X * scale), MathF.Round(p.Y * scale)); flags[i] = p.OnCurve;
        }
        var left = data.S16(2) - (LeftSideBearing(face, glyph) ?? data.S16(2));
        points[^4] = new Vector2(MathF.Round(left * scale), 0);
        points[^3] = new Vector2(MathF.Round((left + face.Advance(glyph)) * scale), 0);
        // Vertical phantom points are defined even in a horizontally laid-out glyph.
        points[^2] = new Vector2(0, MathF.Round(face.Ascent * scale));
        points[^1] = new Vector2(0, MathF.Round(face.Descent * scale));
        if (contours.Phantoms is { } inherited)
            for (var i = 0; i < 4; i++) points[points.Length - 4 + i] = inherited[i] * scale;
        var zone = new TrueTypeMachine.Zone(points, contours.Ends.ToArray(), flags);
        var machine = prepared.Fork();
        machine.StartGlyph(zone, prepared.Graphics, budget.Remaining);
        if (!prepared.Graphics.Inhibit) machine.Execute(contours.Instructions);
        budget.Remaining -= machine.InstructionsUsed;
        if (budget.Remaining < 0) throw new InvalidDataException("Composite hinting instruction budget exhausted.");
        for (var i = 0; i < contours.Points.Count; i++)
            contours.Points[i] = new Point(zone.Current[i].X / scale, zone.Current[i].Y / scale, zone.OnCurve[i]);
        contours.Phantoms = zone.Current[^4..].Select(p => p / scale).ToArray();
    }

    public static PathData? Get(FontFace face, ushort glyph)
    {
        try
        {
            if (glyph >= face.GlyphCount)
                return null;
            if (face.RawTable("glyf") is { } glyf && face.RawTable("loca") is { } loca)
                return TrueType(face, glyf, loca, glyph);
            if (face.RawTable("CFF ") is { } cff)
                return CffFont.For(face, cff, cff2: false).Outline(glyph);
            if (face.RawTable("CFF2") is { } cff2)
                return CffFont.For(face, cff2, cff2: true).Outline(glyph);
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    // ---- TrueType ----

    private readonly record struct Point(float X, float Y, bool OnCurve);

    private sealed class Contours
    {
        public readonly List<Point> Points = [];
        public readonly List<int> Ends = [];
        public byte[] Instructions = [];
        public Vector2[]? Phantoms;
    }

    private static PathData TrueType(FontFace face, FontData glyf, FontData loca, ushort glyph)
    {
        var contours = new Contours();
        var parts = 0;
        Collect(face, glyf, loca, glyph, contours, 0, ref parts);

        // The outline's origin is the glyph's first phantom point, xMin - lsb (the glyf and hmtx tables): a glyph whose header
        // xMin is not its left side bearing is moved so that they agree.
        var shift = 0f;
        var (glyphStart, glyphEnd) = GlyphRange(face, loca, glyph);
        if (glyphEnd > glyphStart && LeftSideBearing(face, glyph) is { } lsb)
            shift = glyf.S16(glyphStart + 2) - lsb;
        if (shift != 0)
        {
            for (var i = 0; i < contours.Points.Count; i++)
                contours.Points[i] = contours.Points[i] with { X = contours.Points[i].X - shift };
        }

        var path = new PathData();
        var start = 0;
        foreach (var end in contours.Ends)
        {
            AddContour(path, contours.Points, start, end);
            start = end + 1;
        }
        return path;
    }

    private static int? LeftSideBearing(FontFace face, ushort glyph)
    {
        if (face.RawTable("hmtx") is not { } hmtx || face.RawTable("hhea") is not { } hhea)
            return null;
        int metrics = hhea.U16(34);
        var position = glyph < metrics ? 4 * glyph + 2 : 4 * metrics + 2 * (glyph - metrics);
        return position + 2 <= hmtx.Length ? hmtx.S16(position) : null;
    }

    private static (int Start, int End) GlyphRange(FontFace face, FontData loca, ushort glyph)
    {
        var longOffsets = face.RawTable("head")!.Value.S16(50) != 0;
        if (longOffsets)
            return ((int)loca.U32(4 * glyph), (int)loca.U32(4 * (glyph + 1)));
        return (2 * loca.U16(2 * glyph), 2 * loca.U16(2 * (glyph + 1)));
    }

    private static void Collect(FontFace face, FontData glyf, FontData loca, ushort glyph, Contours output, int depth, ref int parts,
        TrueTypeMachine? prepared = null, HintBudget? budget = null)
    {
        if (glyph >= face.GlyphCount || depth > MaxCompositeDepth)
            throw new InvalidDataException("Invalid composite glyph or excessive depth.");
        var (start, end) = GlyphRange(face, loca, glyph);
        if (end <= start)
            return; // an empty glyph, such as a space
        var data = glyf.Slice(start, end - start);
        var contourCount = data.S16(0);
        if (contourCount >= 0)
            Simple(data, contourCount, output);
        else
            Composite(face, glyf, loca, data, output, depth, ref parts, prepared, budget);
        if (prepared is not null) Fit(face, glyph, data, output, prepared, budget!);
    }

    private static void Simple(FontData data, int contourCount, Contours output)
    {
        if (contourCount == 0)
        {
            output.Instructions = data.Bytes(12, data.U16(10)).ToArray();
            return;
        }
        var position = 10;
        var ends = new int[contourCount];
        for (var i = 0; i < contourCount; i++, position += 2)
            ends[i] = data.U16(position);
        var pointCount = ends[^1] + 1;
        var instructionLength = data.U16(position);
        output.Instructions = data.Bytes(position + 2, instructionLength).ToArray();
        position += 2 + instructionLength;

        var flags = new byte[pointCount];
        for (var i = 0; i < pointCount;)
        {
            var flag = data.U8(position++);
            flags[i++] = flag;
            if ((flag & 8) == 0)
                continue;
            var repeat = data.U8(position++);
            while (repeat-- > 0 && i < pointCount)
                flags[i++] = flag;
        }

        var x = new int[pointCount];
        var value = 0;
        for (var i = 0; i < pointCount; i++)
        {
            var flag = flags[i];
            if ((flag & 2) != 0)
            {
                var delta = data.U8(position++);
                value += (flag & 16) != 0 ? delta : -delta;
            }
            else if ((flag & 16) == 0)
            {
                value += data.S16(position);
                position += 2;
            }
            x[i] = value;
        }
        var baseIndex = output.Points.Count;
        value = 0;
        for (var i = 0; i < pointCount; i++)
        {
            var flag = flags[i];
            if ((flag & 4) != 0)
            {
                var delta = data.U8(position++);
                value += (flag & 32) != 0 ? delta : -delta;
            }
            else if ((flag & 32) == 0)
            {
                value += data.S16(position);
                position += 2;
            }
            output.Points.Add(new Point(x[i], value, (flags[i] & 1) != 0));
        }
        foreach (var end in ends)
            output.Ends.Add(baseIndex + end);
    }

    private static void Composite(FontFace face, FontData glyf, FontData loca, FontData data, Contours output, int depth, ref int parts,
        TrueTypeMachine? prepared = null, HintBudget? budget = null)
    {
        var position = 10;
        var instructions = false;
        while (true)
        {
            if (++parts > MaxCompositeParts)
                throw new InvalidDataException("Too many composite parts.");
            var flags = data.U16(position);
            instructions |= (flags & 0x100) != 0;
            var component = data.U16(position + 2);
            position += 4;

            int arg1, arg2;
            if ((flags & 1) != 0)
            {
                // ARG_1_AND_2_ARE_WORDS
                if ((flags & 2) != 0)
                {
                    arg1 = data.S16(position);
                    arg2 = data.S16(position + 2);
                }
                else
                {
                    arg1 = data.U16(position);
                    arg2 = data.U16(position + 2);
                }
                position += 4;
            }
            else
            {
                if ((flags & 2) != 0)
                {
                    arg1 = (sbyte)data.U8(position);
                    arg2 = (sbyte)data.U8(position + 1);
                }
                else
                {
                    arg1 = data.U8(position);
                    arg2 = data.U8(position + 1);
                }
                position += 2;
            }

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & 8) != 0)
            {
                a = d = data.S16(position) / 16384f;
                position += 2;
            }
            else if ((flags & 0x40) != 0)
            {
                a = data.S16(position) / 16384f;
                d = data.S16(position + 2) / 16384f;
                position += 4;
            }
            else if ((flags & 0x80) != 0)
            {
                a = data.S16(position) / 16384f;
                b = data.S16(position + 2) / 16384f;
                c = data.S16(position + 4) / 16384f;
                d = data.S16(position + 6) / 16384f;
                position += 8;
            }

            var part = new Contours();
            Collect(face, glyf, loca, component, part, depth + 1, ref parts, prepared, budget);

            // x' = a x + c y + dx, y' = b x + d y + dy.
            float dx, dy;
            if ((flags & 2) != 0)
            {
                dx = arg1;
                dy = arg2;
                if ((flags & 0x800) != 0 && (flags & 0x1000) == 0)
                {
                    // SCALED_COMPONENT_OFFSET: the offset is transformed too.
                    (dx, dy) = (a * dx + c * dy, b * dx + d * dy);
                }
                if (prepared is not null && (flags & 4) != 0)
                {
                    var scale = prepared.PixelsPerEm / face.UnitsPerEm;
                    dx = MathF.Floor(dx * scale + .5f) / scale;
                    dy = MathF.Floor(dy * scale + .5f) / scale;
                }
            }
            else
            {
                // The offset makes point arg2 of the component land on point arg1 of the glyph so far.
                if (arg1 >= output.Points.Count || arg2 >= part.Points.Count)
                    throw new InvalidDataException("A composite point match outside the glyph.");
                var target = output.Points[arg1];
                var source = part.Points[arg2];
                dx = target.X - (a * source.X + c * source.Y);
                dy = target.Y - (b * source.X + d * source.Y);
            }

            var offset = output.Points.Count;
            foreach (var p in part.Points)
                output.Points.Add(new Point(a * p.X + c * p.Y + dx, b * p.X + d * p.Y + dy, p.OnCurve));
            foreach (var end in part.Ends)
                output.Ends.Add(offset + end);
            if (prepared is not null && (flags & 0x200) != 0 && part.Phantoms is { } phantoms)
                output.Phantoms = phantoms.Select(p => new Vector2(a * p.X + c * p.Y, b * p.X + d * p.Y)).ToArray();
            if (output.Points.Count > TrueTypeMachine.PointLimit - 4) throw new InvalidDataException("Composite point limit exceeded.");

            if ((flags & 0x20) == 0)
                break;
        }
        if (instructions) output.Instructions = data.Bytes(position + 2, data.U16(position)).ToArray();
    }

    // One closed contour of on- and off-curve points: quadratic curves between off-curve points have an implied on-curve
    // point at their midpoint. Quadratics are written as the equivalent cubics.
    private static void AddContour(PathData path, List<Point> points, int first, int last)
    {
        var count = last - first + 1;
        if (count < 2)
            return;
        Point At(int i) => points[first + ((i % count) + count) % count];

        // Start at an on-curve point, or at the midpoint of the first two off-curve ones.
        var startIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (At(i).OnCurve)
            {
                startIndex = i;
                break;
            }
        }
        Vector2 current;
        int next;
        if (startIndex >= 0)
        {
            current = new Vector2(At(startIndex).X, At(startIndex).Y);
            next = startIndex + 1;
        }
        else
        {
            var a = At(0);
            var b = At(count - 1);
            current = new Vector2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            startIndex = count - 1;
            next = 0;
        }
        var origin = current;
        path.MoveTo(current.X, current.Y);

        for (var step = 0; step < count; step++)
        {
            var p = At(next);
            next++;
            if (p.OnCurve)
            {
                path.LineTo(p.X, p.Y);
                current = new Vector2(p.X, p.Y);
                continue;
            }

            var control = new Vector2(p.X, p.Y);
            Vector2 end;
            var q = At(next);
            if (q.OnCurve)
            {
                end = new Vector2(q.X, q.Y);
                next++;
                step++;
            }
            else
                end = new Vector2((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            path.CubicTo(current + 2f / 3f * (control - current), end + 2f / 3f * (control - end), end);
            current = end;
            if (step >= count - 1)
                break;
        }
        if (current != origin)
            path.LineTo(origin.X, origin.Y);
        path.Close();
    }

    // ---- CFF / CFF2 ----

    /// <summary>A CFF font: its charstrings, subroutines and, for CID-keyed fonts, the font dictionaries per glyph.</summary>
    private sealed class CffFont
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FontFace, CffFont> Cache = [];

        private readonly FontData _data;
        private readonly Index _charStrings;
        private readonly Index _globalSubrs;
        private readonly bool _cff2;
        private readonly Index[] _localSubrs;
        private readonly int _fdSelectFormat;
        private readonly int _fdSelectOffset;

        public static CffFont For(FontFace face, FontData cff, bool cff2) => Cache.GetValue(face, _ => new CffFont(cff, cff2));

        private readonly record struct Index(FontData Data, int Count, int OffsetSize, int OffsetsStart, int DataStart, int End)
        {
            public static Index Read(FontData data, int position, bool cff2 = false)
            {
                var count32 = cff2 ? data.U32(position) : data.U16(position);
                if (count32 > int.MaxValue)
                    throw new InvalidDataException("A CFF index is too large.");
                var count = (int)count32;
                if (count == 0)
                {
                    var empty = position + (cff2 ? 4 : 2);
                    return new Index(data, 0, 0, empty, empty, empty);
                }
                var countBytes = cff2 ? 4 : 2;
                var offsetSize = data.U8(position + countBytes);
                if (offsetSize is < 1 or > 4)
                    throw new InvalidDataException("A CFF index with an offset size outside 1 to 4.");
                var offsetsStart = position + countBytes + 1;
                var dataStart = offsetsStart + (count + 1) * offsetSize - 1;
                var end = dataStart + (int)Offset(data, offsetsStart, offsetSize, count);
                return new Index(data, count, offsetSize, offsetsStart, dataStart, end);
            }

            private static uint Offset(FontData data, int start, int size, int i)
            {
                uint value = 0;
                for (var k = 0; k < size; k++)
                    value = value << 8 | data.U8(start + i * size + k);
                return value;
            }

            public FontData Item(int i)
            {
                if (i < 0 || i >= Count)
                    throw new InvalidDataException("A CFF index item out of range.");
                var from = (int)Offset(Data, OffsetsStart, OffsetSize, i);
                var to = (int)Offset(Data, OffsetsStart, OffsetSize, i + 1);
                return Data.Slice(DataStart + from, to - from);
            }
        }

        private sealed class Dict
        {
            public readonly Dictionary<int, double[]> Entries = [];

            public static Dict Parse(FontData data)
            {
                var dict = new Dict();
                var operands = new List<double>();
                var position = 0;
                while (position < data.Length)
                {
                    var b = data.U8(position++);
                    if (b <= 21)
                    {
                        int op = b;
                        if (b == 12)
                            op = 0x0C00 | data.U8(position++);
                        dict.Entries[op] = [.. operands];
                        operands.Clear();
                    }
                    else if (b == 28)
                    {
                        operands.Add(data.S16(position));
                        position += 2;
                    }
                    else if (b == 29)
                    {
                        operands.Add((int)data.U32(position));
                        position += 4;
                    }
                    else if (b == 30)
                    {
                        // A real number in nibbles.
                        var text = new System.Text.StringBuilder();
                        for (var done = false; !done && position < data.Length;)
                        {
                            var packed = data.U8(position++);
                            foreach (var nibble in new[] { packed >> 4, packed & 15 })
                            {
                                if (nibble == 15)
                                {
                                    done = true;
                                    break;
                                }
                                text.Append(nibble switch { 10 => ".", 11 => "E", 12 => "E-", 14 => "-", _ => nibble.ToString() });
                            }
                        }
                        operands.Add(double.TryParse(text.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real) ? real : 0);
                    }
                    else if (b is >= 32 and <= 246)
                        operands.Add(b - 139);
                    else if (b is >= 247 and <= 250)
                        operands.Add((b - 247) * 256 + data.U8(position++) + 108);
                    else if (b is >= 251 and <= 254)
                        operands.Add(-(b - 251) * 256 - data.U8(position++) - 108);
                }
                return dict;
            }

            public double? Number(int op) => Entries.TryGetValue(op, out var v) && v.Length > 0 ? v[0] : null;
        }

        private readonly double[] _defaultWidth;
        private readonly double[] _nominalWidth;
        private readonly Dict[] _hintParameters;

        private CffFont(FontData data, bool cff2)
        {
            _data = data;
            _cff2 = cff2;
            Dict top;
            int globalSubrsOffset;
            if (cff2)
            {
                var headerSize = data.U8(2);
                var topLength = data.U16(3);
                top = Dict.Parse(data.Slice(headerSize, topLength));
                globalSubrsOffset = headerSize + topLength;
            }
            else
            {
                var headerSize = data.U8(2);
                var nameIndex = Index.Read(data, headerSize);
                var topIndex = Index.Read(data, nameIndex.End);
                var stringIndex = Index.Read(data, topIndex.End);
                globalSubrsOffset = stringIndex.End;
                top = Dict.Parse(topIndex.Item(0));
            }
            _globalSubrs = Index.Read(data, globalSubrsOffset, cff2);
            if (top.Number(17) is not { } charStringsOffset)
                throw new InvalidDataException("A CFF font without charstrings.");
            _charStrings = Index.Read(data, (int)charStringsOffset, cff2);

            if (top.Entries.ContainsKey(0x0C24) && top.Number(0x0C24) is { } fdArrayOffset)
            {
                // CID-keyed: one font dictionary, with its own private dictionary, per range of glyphs.
                var fdArray = Index.Read(data, (int)fdArrayOffset, cff2);
                if (fdArray.Count is < 1 or > 256)
                    throw new InvalidDataException("CFF font dictionary count exceeds host limits.");
                _localSubrs = new Index[fdArray.Count];
                _defaultWidth = new double[fdArray.Count];
                _nominalWidth = new double[fdArray.Count];
                _hintParameters = new Dict[fdArray.Count];
                for (var i = 0; i < fdArray.Count; i++)
                (_localSubrs[i], _defaultWidth[i], _nominalWidth[i], _hintParameters[i]) = ReadPrivate(Dict.Parse(fdArray.Item(i)));
                if (top.Number(0x0C25) is { } fdSelect)
                {
                    _fdSelectOffset = (int)fdSelect;
                    _fdSelectFormat = data.U8(_fdSelectOffset);
                }
            }
            else
            {
                _localSubrs = new Index[1];
                _defaultWidth = new double[1];
                _nominalWidth = new double[1];
                _hintParameters = new Dict[1];
                (_localSubrs[0], _defaultWidth[0], _nominalWidth[0], _hintParameters[0]) = ReadPrivate(top);
            }
        }

        private (Index Subrs, double DefaultWidth, double NominalWidth, Dict Hints) ReadPrivate(Dict owner)
        {
            if (!owner.Entries.TryGetValue(18, out var priv) || priv.Length < 2)
                return (default, 0, 0, new());
            var size = (int)priv[0];
            var offset = (int)priv[1];
            if (size == 0)
                return (default, 0, 0, new());
            var dict = Dict.Parse(_data.Slice(offset, size));
            var subrs = dict.Number(19) is { } subrsOffset ? Index.Read(_data, offset + (int)subrsOffset, _cff2) : default;
            return (subrs, dict.Number(20) ?? 0, dict.Number(21) ?? 0, dict);
        }

        private int FontDictionaryFor(int glyph)
        {
            if (_fdSelectOffset == 0)
                return 0;
            var position = _fdSelectOffset;
            if (_fdSelectFormat == 0)
                return _data.U8(position + 1 + glyph);
            if (_fdSelectFormat == 3)
            {
                var rangeCount = _data.U16(position + 1);
                for (var i = 0; i < rangeCount; i++)
                {
                    var first = _data.U16(position + 3 + 3 * i);
                    var next = _data.U16(position + 3 + 3 * (i + 1));
                    if (glyph >= first && glyph < next)
                        return _data.U8(position + 5 + 3 * i);
                }
            }
            return 0;
        }

        public PathData Outline(int glyph, float scale = 0)
        {
            var fd = FontDictionaryFor(glyph);
            if ((uint)fd >= (uint)_localSubrs.Length) throw new InvalidDataException("Invalid CFF font dictionary selector.");
            var parameters = _hintParameters[fd];
            var hints = scale > 0 ? new CffHintMap(scale,
                parameters.Entries.GetValueOrDefault(6), parameters.Entries.GetValueOrDefault(7),
                (float)(parameters.Number(0x0C0B) ?? 1), (float)(parameters.Number(0x0C09) ?? .039625),
                (float)(parameters.Number(0x0C0A) ?? 7)) : null;
            var run = new Interpreter(this, _localSubrs[fd], _cff2, hints);
            run.Run(_charStrings.Item(glyph), 0);
            return run.Finish();
        }

        private static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

        // The Type 2 charstring machine (Adobe Technical Note 5177).
        private sealed class Interpreter(CffFont font, Index local, bool cff2, CffHintMap? hints)
        {
            private readonly double[] _stack = new double[96];
            private int _depth;
            private readonly PathData _path = new();
            private float _x, _y;
            private bool _open;
            private int _stems;
            private bool _widthSeen;
            private bool _ended;
            private int _operations;
            private readonly double[] _transient = new double[32];

            public PathData Finish()
            {
                Close();
                return _path;
            }

            private void Close()
            {
                if (_open)
                    _path.Close();
                _open = false;
            }

            private void Push(double v)
            {
                if (_depth >= _stack.Length)
                    throw new InvalidDataException("A charstring stack overflow.");
                _stack[_depth++] = v;
            }

            private void MoveTo(float dx, float dy)
            {
                Close();
                _x += dx;
                _y += dy;
                var point = hints?.Map(new(_x, _y)) ?? new Vector2(_x, _y);
                _path.MoveTo(point.X, point.Y);
                _open = true;
            }

            private void LineTo(float dx, float dy)
            {
                _x += dx;
                _y += dy;
                var point = hints?.Map(new(_x, _y)) ?? new Vector2(_x, _y);
                _path.LineTo(point.X, point.Y);
            }

            private void CurveTo(float dx1, float dy1, float dx2, float dy2, float dx3, float dy3)
            {
                var p1 = new Vector2(_x + dx1, _y + dy1);
                var p2 = new Vector2(p1.X + dx2, p1.Y + dy2);
                var p3 = new Vector2(p2.X + dx3, p2.Y + dy3);
                _path.CubicTo(hints?.Map(p1) ?? p1, hints?.Map(p2) ?? p2, hints?.Map(p3) ?? p3);
                (_x, _y) = (p3.X, p3.Y);
            }

            // The first stack-clearing operator may carry the glyph's width as an extra first operand.
            private void TakeWidth(bool extra)
            {
                if (cff2)
                {
                    _widthSeen = true;
                    return;
                }
                if (!_widthSeen && extra && _depth > 0)
                {
                    for (var i = 1; i < _depth; i++)
                        _stack[i - 1] = _stack[i];
                    _depth--;
                }
                _widthSeen = true;
            }

            public void Run(FontData code, int depth)
            {
                if (depth > 10)
                    throw new InvalidDataException("Charstring subroutines nested too deeply.");
                var position = 0;
                while (position < code.Length && !_ended)
                {
                    // A budget for the whole glyph: subroutines may call each other many times over.
                    if (++_operations > 200_000)
                        throw new InvalidDataException("A charstring that runs too long.");
                    var b = code.U8(position++);
                    if (b >= 32 || b == 28)
                    {
                        if (b == 28)
                        {
                            Push(code.S16(position));
                            position += 2;
                        }
                        else if (b <= 246)
                            Push(b - 139);
                        else if (b <= 250)
                            Push((b - 247) * 256 + code.U8(position++) + 108);
                        else if (b <= 254)
                            Push(-(b - 251) * 256 - code.U8(position++) - 108);
                        else
                        {
                            Push((int)code.U32(position) / 65536.0);
                            position += 4;
                        }
                        continue;
                    }

                    switch (b)
                    {
                        case 1: // hstem
                        case 3: // vstem
                        case 18: // hstemhm
                        case 23: // vstemhm
                            TakeWidth((_depth & 1) == 1);
                            hints?.AddStems(b is 1 or 18, _stack.AsSpan(0, _depth));
                            _stems += _depth / 2;
                            if (_stems > 96) throw new InvalidDataException("Too many CFF hints.");
                            _depth = 0;
                            break;
                        case 19: // hintmask
                        case 20: // cntrmask
                            TakeWidth((_depth & 1) == 1);
                            hints?.AddStems(false, _stack.AsSpan(0, _depth));
                            _stems += _depth / 2;
                            if (_stems > 96) throw new InvalidDataException("Too many CFF hints.");
                            _depth = 0;
                            if (hints is not null)
                            {
                                var mask = code.Bytes(position, (_stems + 7) / 8);
                                if (b == 20) hints.CounterMask(mask); else hints.Mask(mask);
                            }
                            position += (_stems + 7) / 8;
                            break;
                        case 21: // rmoveto
                            TakeWidth(_depth > 2);
                            MoveTo((float)_stack[0], (float)_stack[1]);
                            _depth = 0;
                            break;
                        case 22: // hmoveto
                            TakeWidth(_depth > 1);
                            MoveTo((float)_stack[0], 0);
                            _depth = 0;
                            break;
                        case 4: // vmoveto
                            TakeWidth(_depth > 1);
                            MoveTo(0, (float)_stack[0]);
                            _depth = 0;
                            break;
                        case 5: // rlineto
                            for (var i = 0; i + 1 < _depth; i += 2)
                                LineTo((float)_stack[i], (float)_stack[i + 1]);
                            _depth = 0;
                            break;
                        case 6: // hlineto
                        case 7: // vlineto
                        {
                            var horizontal = b == 6;
                            for (var i = 0; i < _depth; i++, horizontal = !horizontal)
                                LineTo(horizontal ? (float)_stack[i] : 0, horizontal ? 0 : (float)_stack[i]);
                            _depth = 0;
                            break;
                        }
                        case 8: // rrcurveto
                            for (var i = 0; i + 5 < _depth; i += 6)
                                CurveTo((float)_stack[i], (float)_stack[i + 1], (float)_stack[i + 2], (float)_stack[i + 3], (float)_stack[i + 4], (float)_stack[i + 5]);
                            _depth = 0;
                            break;
                        case 24: // rcurveline
                        {
                            var i = 0;
                            for (; i + 7 < _depth; i += 6)
                                CurveTo((float)_stack[i], (float)_stack[i + 1], (float)_stack[i + 2], (float)_stack[i + 3], (float)_stack[i + 4], (float)_stack[i + 5]);
                            LineTo((float)_stack[i], (float)_stack[i + 1]);
                            _depth = 0;
                            break;
                        }
                        case 25: // rlinecurve
                        {
                            var i = 0;
                            for (; i + 7 < _depth; i += 2)
                                LineTo((float)_stack[i], (float)_stack[i + 1]);
                            CurveTo((float)_stack[i], (float)_stack[i + 1], (float)_stack[i + 2], (float)_stack[i + 3], (float)_stack[i + 4], (float)_stack[i + 5]);
                            _depth = 0;
                            break;
                        }
                        case 26: // vvcurveto
                        {
                            var i = 0;
                            var dx1 = 0f;
                            if ((_depth & 1) == 1)
                                dx1 = (float)_stack[i++];
                            for (; i + 3 < _depth; i += 4)
                            {
                                CurveTo(dx1, (float)_stack[i], (float)_stack[i + 1], (float)_stack[i + 2], 0, (float)_stack[i + 3]);
                                dx1 = 0;
                            }
                            _depth = 0;
                            break;
                        }
                        case 27: // hhcurveto
                        {
                            var i = 0;
                            var dy1 = 0f;
                            if ((_depth & 1) == 1)
                                dy1 = (float)_stack[i++];
                            for (; i + 3 < _depth; i += 4)
                            {
                                CurveTo((float)_stack[i], dy1, (float)_stack[i + 1], (float)_stack[i + 2], (float)_stack[i + 3], 0);
                                dy1 = 0;
                            }
                            _depth = 0;
                            break;
                        }
                        case 30: // vhcurveto
                        case 31: // hvcurveto
                        {
                            var horizontal = b == 31;
                            var i = 0;
                            while (i + 3 < _depth)
                            {
                                var last = _depth - i == 5 ? (float)_stack[i + 4] : 0f;
                                if (horizontal)
                                    CurveTo((float)_stack[i], 0, (float)_stack[i + 1], (float)_stack[i + 2], last, (float)_stack[i + 3]);
                                else
                                    CurveTo(0, (float)_stack[i], (float)_stack[i + 1], (float)_stack[i + 2], (float)_stack[i + 3], last);
                                horizontal = !horizontal;
                                i += 4;
                            }
                            _depth = 0;
                            break;
                        }
                        case 10: // callsubr
                        case 29: // callgsubr
                        {
                            if (_depth < 1)
                                throw new InvalidDataException("A subroutine call without an index.");
                            var index = b == 10 ? local : font._globalSubrs;
                            var number = (int)_stack[--_depth] + Bias(index.Count);
                            Run(index.Item(number), depth + 1);
                            break;
                        }
                        case 11: // return
                            return;
                        case 14: // endchar
                            TakeWidth(_depth is 1 or 5);
                            Close();
                            _ended = true;
                            break;
                        case 12:
                            Escape(code.U8(position++));
                            break;
                        default:
                            _depth = 0; // reserved operators clear the stack
                            break;
                    }
                }
            }

            private void Escape(int op)
            {
                switch (op)
                {
                    case 34: // hflex
                        CurveTo((float)_stack[0], 0, (float)_stack[1], (float)_stack[2], (float)_stack[3], 0);
                        CurveTo((float)_stack[4], 0, (float)_stack[5], -(float)_stack[2], (float)_stack[6], 0);
                        _depth = 0;
                        break;
                    case 35: // flex
                        CurveTo((float)_stack[0], (float)_stack[1], (float)_stack[2], (float)_stack[3], (float)_stack[4], (float)_stack[5]);
                        CurveTo((float)_stack[6], (float)_stack[7], (float)_stack[8], (float)_stack[9], (float)_stack[10], (float)_stack[11]);
                        _depth = 0;
                        break;
                    case 36: // hflex1
                        CurveTo((float)_stack[0], (float)_stack[1], (float)_stack[2], (float)_stack[3], (float)_stack[4], 0);
                        CurveTo((float)_stack[5], 0, (float)_stack[6], (float)_stack[7], (float)_stack[8], -(float)(_stack[1] + _stack[3] + _stack[7]));
                        _depth = 0;
                        break;
                    case 37: // flex1
                    {
                        var dx = _stack[0] + _stack[2] + _stack[4] + _stack[6] + _stack[8];
                        var dy = _stack[1] + _stack[3] + _stack[5] + _stack[7] + _stack[9];
                        CurveTo((float)_stack[0], (float)_stack[1], (float)_stack[2], (float)_stack[3], (float)_stack[4], (float)_stack[5]);
                        if (Math.Abs(dx) > Math.Abs(dy))
                            CurveTo((float)_stack[6], (float)_stack[7], (float)_stack[8], (float)_stack[9], (float)_stack[10], -(float)dy);
                        else
                            CurveTo((float)_stack[6], (float)_stack[7], (float)_stack[8], (float)_stack[9], -(float)dx, (float)_stack[10]);
                        _depth = 0;
                        break;
                    }
                    case 9: _stack[_depth - 1] = Math.Abs(_stack[_depth - 1]); break;
                    case 10: _stack[_depth - 2] += _stack[_depth - 1]; _depth--; break;
                    case 11: _stack[_depth - 2] -= _stack[_depth - 1]; _depth--; break;
                    case 12: _stack[_depth - 2] /= _stack[_depth - 1]; _depth--; break;
                    case 14: _stack[_depth - 1] = -_stack[_depth - 1]; break;
                    case 16: // blend (CFF2): keep the default-instance values.
                    {
                        // The operand stack contains n base values, followed by
                        // n * k deltas, followed by n (the number of blends).
                        // The variation store is not selected yet, so the
                        // default instance contributes zero region deltas. Keep
                        // the bases while validating the complete operand shape;
                        // silently dropping the stack would corrupt the path.
                        if (_depth == 0)
                            throw new InvalidDataException("A CFF2 blend without a count.");
                        var count = (int)_stack[_depth - 1];
                        if (count <= 0 || count > _stack.Length || _depth < count + 1 || (_depth - count - 1) % count != 0)
                            throw new InvalidDataException("A CFF2 blend has an invalid operand count.");
                        _depth = count;
                        break;
                    }
                    case 24: _stack[_depth - 2] *= _stack[_depth - 1]; _depth--; break;
                    case 26: _stack[_depth - 1] = Math.Sqrt(_stack[_depth - 1]); break;
                    case 18: _depth--; break;
                    case 27: _stack[_depth] = _stack[_depth - 1]; _depth++; break;
                    case 28: (_stack[_depth - 1], _stack[_depth - 2]) = (_stack[_depth - 2], _stack[_depth - 1]); break;
                    case 20: _transient[(int)_stack[_depth - 1] & 31] = _stack[_depth - 2]; _depth -= 2; break;
                    case 21: _stack[_depth - 1] = _transient[(int)_stack[_depth - 1] & 31]; break;
                    case 22:
                    {
                        // ifelse: s1 s2 v1 v2 -> s1 if v1 <= v2, else s2
                        var result = _stack[_depth - 2] <= _stack[_depth - 1] ? _stack[_depth - 4] : _stack[_depth - 3];
                        _depth -= 3;
                        _stack[_depth - 1] = result;
                        break;
                    }
                    default:
                        _depth = 0;
                        break;
                }
            }
        }
    }
}

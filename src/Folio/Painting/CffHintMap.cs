using System.Numerics;

namespace Folio.Painting;

/// <summary>
/// Type 2 grayscale grid fitting: decode delta-encoded stem hints, select the active hint set, fit horizontal
/// edges and interpolate the intervening outline. X coordinates retain natural subpixel positioning.
/// Hint declaration and mask semantics: Adobe Technical Note 5177, section 4.3.
/// </summary>
internal sealed class CffHintMap
{
    private readonly record struct Stem(bool Horizontal, float Low, float High, bool Ghost);
    private readonly record struct Edge(float Original, float Fitted);
    private readonly record struct Placement(int Id, float Low, float High, float FittedLow, float FittedHigh);
    private readonly List<Stem> _stems = [];
    private readonly float _scale;
    private readonly List<(float Low, float High, bool Bottom)> _blues = [];
    private readonly float _blueFuzz, _blueScale, _blueShift;
    private byte[]? _mask;
    private readonly List<byte[]> _counters = [];
    private Edge[]? _edges;
    private Edge[]? _xEdges;

    internal CffHintMap(float scale, double[]? blues = null, double[]? otherBlues = null,
        float blueFuzz = 1, float blueScale = .039625f, float blueShift = 7)
    {
        if (!float.IsFinite(scale) || scale <= 0 || !float.IsFinite(blueFuzz) || blueFuzz < 0
            || !float.IsFinite(blueScale) || blueScale < 0 || !float.IsFinite(blueShift) || blueShift < 0)
            throw new InvalidDataException("Invalid CFF hint scale or alignment zone parameters.");
        _scale = scale; _blueFuzz = blueFuzz; _blueScale = blueScale; _blueShift = blueShift;
        Blues(blues, false); Blues(otherBlues, true);
    }

    private void Blues(double[]? deltas, bool bottom)
    {
        if (deltas is null) return;
        if ((deltas.Length & 1) != 0 || deltas.Length > 14) throw new InvalidDataException("Invalid CFF alignment zones.");
        var value = 0d;
        for (var i = 0; i < deltas.Length; i += 2)
        {
            value += deltas[i]; var low = value; value += deltas[i + 1];
            if (!double.IsFinite(value) || value < low || Math.Abs(value) > 1_000_000 || Math.Abs(low) > 1_000_000)
                throw new InvalidDataException("Invalid CFF alignment zone coordinates.");
            _blues.Add(((float)low, (float)value, bottom || i == 0));
        }
    }

    internal void AddStems(bool horizontal, ReadOnlySpan<double> values)
    {
        if (values.Length > 0 && (_mask is not null || _counters.Count != 0))
            throw new InvalidDataException("CFF stems must be declared before hint/counter masks.");
        if ((values.Length & 1) != 0 || _stems.Count + values.Length / 2 > 96)
            throw new InvalidDataException("CFF stem count exceeds the Type 2 limit.");
        var position = 0d;
        for (var i = 0; i < values.Length; i += 2)
        {
            position += values[i]; var width = values[i + 1];
            if (!double.IsFinite(position) || !double.IsFinite(width) || Math.Abs(position) > 1_000_000 || Math.Abs(width) > 1_000_000)
                throw new InvalidDataException("Invalid CFF stem coordinates.");
            if (width < 0 && width is not (-20 or -21)) throw new InvalidDataException("Invalid CFF ghost hint width.");
            // -20 gives a top edge at y; -21 gives a bottom edge at y-21.
            var low = width == -21 ? position - 21 : position;
            var high = width < 0 ? low : position + width;
            _stems.Add(new(horizontal, (float)low, (float)high, width < 0));
            position += width;
        }
        _edges = _xEdges = null;
    }

    internal void Mask(ReadOnlySpan<byte> mask)
    {
        ValidateMask(mask);
        _mask = mask.ToArray(); _edges = _xEdges = null;
    }

    internal void CounterMask(ReadOnlySpan<byte> mask)
    {
        ValidateMask(mask);
        if (_counters.Count == 96) throw new InvalidDataException("Too many CFF counter groups.");
        _counters.Add(mask.ToArray());
        _edges = _xEdges = null;
    }

    private void ValidateMask(ReadOnlySpan<byte> mask)
    {
        if (_stems.Count == 0) throw new InvalidDataException("CFF hint mask without stems.");
        if (mask.Length != (_stems.Count + 7) / 8) throw new InvalidDataException("Invalid CFF hint mask length.");
        if (_stems.Count % 8 != 0 && (mask[^1] & ((1 << (8 - _stems.Count % 8)) - 1)) != 0)
            throw new InvalidDataException("Nonzero unused CFF hint mask bits.");
    }

    internal Vector2 Map(Vector2 point)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) throw new InvalidDataException("Nonfinite CFF outline.");
        _edges ??= Edges();
        _xEdges ??= Edges(horizontal: false);
        var y = Interpolate(point.Y * _scale, _edges);
        var x = Interpolate(point.X * _scale, _xEdges);
        // Emit the same 26.6 device-coordinate precision as the TrueType fitter. Keeping arbitrary floating point
        // residue here otherwise makes tiny curve-edge differences depend on the sequence of charstring operations.
        return new Vector2(MathF.Round(x * 64) / 64, MathF.Round(y * 64) / 64);
    }

    private static float Interpolate(float value, Edge[] edges)
    {
        if (edges.Length != 0)
        {
            var lower = edges[0];
            if (value <= lower.Original) value += lower.Fitted - lower.Original;
            else if (value >= edges[^1].Original) value += edges[^1].Fitted - edges[^1].Original;
            else
            {
                for (var i = 1; i < edges.Length; i++)
                {
                    var upper = edges[i];
                    if (value <= upper.Original)
                    {
                        value = lower.Fitted + (value - lower.Original) * (upper.Fitted - lower.Fitted) / (upper.Original - lower.Original);
                        break;
                    }
                    lower = upper;
                }
            }
        }
        return value;
    }

    private Edge[] Edges(bool horizontal = true)
    {
        var placements = new List<Placement>();
        for (var i = 0; i < _stems.Count; i++)
        {
            var stem = _stems[i];
            if (stem.Horizontal != horizontal || (_mask is not null && (_mask[i / 8] & (128 >> (i % 8))) == 0)) continue;
            var low = stem.Low * _scale; var high = stem.High * _scale;
            var width = high - low;
            float fittedLow, fittedHigh;
            if (stem.Ghost) fittedLow = fittedHigh = Grid(low);
            else if (!horizontal)
            {
                // Optical compensation for thin vertical strokes in grayscale CFF text. This conventional Adobe
                // darkening curve is piecewise linear in device pixels; it tapers to zero for already-heavy stems.
                // https://freetype.org/freetype2/docs/reference/ft2-properties.html#darkening-parameters
                var fittedWidth = Math.Max(1, Grid((width + Darkening(width)) * 4) / 4);
                fittedLow = Grid(low * 4) / 4; fittedHigh = fittedLow + fittedWidth;
            }
            else
            {
                // Grayscale stems use quarter-pixel widths above one pixel. Anchor the edge already nearest a
                // grid line, rather than moving both edges onto a centre phase and softening both sides of a stem.
                var fittedWidth = Math.Max(1, Grid(width * 4) / 4);
                if (Math.Abs(low - Grid(low)) <= Math.Abs(high - Grid(high)))
                { fittedLow = Grid(low); fittedHigh = fittedLow + fittedWidth; }
                else
                { fittedHigh = Grid(high); fittedLow = fittedHigh - fittedWidth; }
            }
            if (horizontal)
            {
                if (Blue(stem.Low, true) is { } bottom) { fittedHigh += bottom - fittedLow; fittedLow = bottom; }
                else if (Blue(stem.High, false) is { } top) { fittedLow += top - fittedHigh; fittedHigh = top; }
            }
            placements.Add(new(i, low, high, fittedLow, fittedHigh));
        }
        placements.Sort((a, b) => a.Low.CompareTo(b.Low));
        var gaps = new float[placements.Count];
        for (var i = 1; i < placements.Count; i++)
        {
            if (placements[i].Low < placements[i - 1].High)
                throw new InvalidDataException("Overlapping CFF hints require a hint mask.");
            gaps[i] = placements[i].FittedLow <= placements[i - 1].FittedHigh
                ? placements[i].Low - placements[i - 1].High : 0;
        }
        // Counter hints do not change the active hint set. In declaration priority order, protect the original
        // whitespace between the selected stems. Constraints only increase, so lower-priority groups cannot erase
        // a protected counter. Distribute a deficit over intervening gaps rather than moving just one contour.
        foreach (var mask in _counters)
        {
            var previous = -1;
            for (var current = 0; current < placements.Count; current++)
            {
                var id = placements[current].Id;
                if ((mask[id / 8] & (128 >> (id % 8))) == 0) continue;
                if (previous >= 0)
                {
                    var target = placements[current].Low - placements[previous].High;
                    var available = 0f;
                    for (var p = previous + 1; p <= current; p++)
                    {
                        available += gaps[p];
                        if (p < current) available += placements[p].FittedHigh - placements[p].FittedLow;
                    }
                    var share = Math.Max(0, target - available) / (current - previous);
                    for (var p = previous + 1; p <= current; p++) gaps[p] += share;
                }
                previous = current;
            }
        }
        var offsets = new float[placements.Count];
        for (var i = 1; i < placements.Count; i++)
            offsets[i] = offsets[i - 1] + placements[i - 1].FittedHigh - placements[i - 1].FittedLow + gaps[i];
        // Independent rounding can collide in a dense glyph. Solve the ordered stem positions jointly, preserving
        // their widths and counters rather than letting a later edge reverse or erase the preceding one. Pool adjacent
        // violating positions; the resulting translation minimises squared displacement from the desired grid fits.
        var blocks = new List<(int First, int Count, double Sum)>();
        for (var i = 0; i < placements.Count; i++)
        {
            blocks.Add((i, 1, placements[i].FittedLow - offsets[i]));
            while (blocks.Count >= 2)
            {
                var b = blocks[^1]; var a = blocks[^2];
                if (a.Sum / a.Count <= b.Sum / b.Count) break;
                blocks.RemoveRange(blocks.Count - 2, 2);
                blocks.Add((a.First, a.Count + b.Count, a.Sum + b.Sum));
            }
        }
        var result = new List<Edge>();
        foreach (var block in blocks)
            for (var i = block.First; i < block.First + block.Count; i++)
            {
                var p = placements[i]; var low = (float)(block.Sum / block.Count) + offsets[i];
                result.Add(new(p.Low, low));
                if (p.High != p.Low) result.Add(new(p.High, low + p.FittedHigh - p.FittedLow));
            }
        // Touching stems can share an edge. Identical anchors are emitted only once so interpolation never divides by zero.
        for (var i = result.Count - 1; i > 0; i--)
            if (result[i].Original == result[i - 1].Original) result.RemoveAt(i);
        return result.ToArray();
    }

    private float? Blue(float edge, bool bottom)
    {
        foreach (var zone in _blues)
        {
            if (zone.Bottom != bottom || edge < zone.Low - _blueFuzz || edge > zone.High + _blueFuzz) continue;
            var reference = bottom ? zone.High : zone.Low;
            var overshoot = Math.Abs(edge - reference);
            var fitted = Grid(reference * _scale);
            if (_scale >= _blueScale && overshoot >= _blueShift)
                fitted += (bottom ? -1 : 1) * Math.Max(1, Grid(overshoot * _scale));
            return fitted;
        }
        return null;
    }

    private static float Grid(float value) => MathF.Floor(value + .5f);

    internal static float Darkening(float width) => width <= .5f ? .4f : width < 1 ? .4f - (width - .5f) * .25f
        : width <= 1.667f ? .275f : width < 2.333f ? .275f * (2.333f - width) / (2.333f - 1.667f) : 0;
}

using System.Numerics;

namespace Folio.Painting;

internal sealed partial class TrueTypeMachine
{
    internal sealed class Zone
    {
        internal readonly Vector2[] Current, Original;
        internal readonly bool[] TouchedX, TouchedY, OnCurve;
        internal readonly int[] Ends;
        internal Zone(Vector2[] points, int[] ends, bool[]? onCurve = null)
        {
            if (points.Length > PointLimit || ends.Length > PointLimit) throw Fault("Too many glyph points.");
            var previous = -1;
            foreach (var end in ends)
            {
                if (end <= previous || end >= points.Length) throw Fault("Invalid contour endpoint.");
                previous = end;
            }
            if (onCurve is not null && onCurve.Length != points.Length) throw Fault("Invalid curve flags.");
            Current = (Vector2[])points.Clone(); Original = (Vector2[])points.Clone(); Ends = (int[])ends.Clone();
            TouchedX = new bool[points.Length]; TouchedY = new bool[points.Length];
            OnCurve = onCurve is null ? new bool[points.Length] : (bool[])onCurve.Clone();
        }
    }

    private static int ZoneIndex(int index) => index is 0 or 1 ? index : throw Fault("Invalid point zone.");
    private Zone ZoneAt(int index) => index == 0 ? Twilight : Glyph;
    private static Vector2 Unit(Vector2 vector)
    {
        if (!float.IsFinite(vector.X) || !float.IsFinite(vector.Y) || vector.LengthSquared() < 1e-10f)
            throw Fault("Invalid hint vector.");
        return Vector2.Normalize(vector);
    }
    private int Coordinate(Zone zone, int point, bool original = false) =>
        Number(Vector2.Dot((original ? zone.Original : zone.Current)[Index(point, zone.Current.Length)],
            original ? Graphics.Dual : Graphics.Projection));
    private int Distance(Zone a, int ap, Zone b, int bp, bool original = false) =>
        Number(Vector2.Dot((original ? a.Original : a.Current)[Index(ap, a.Current.Length)]
            - (original ? b.Original : b.Current)[Index(bp, b.Current.Length)], original ? Graphics.Dual : Graphics.Projection));

    private void Move(Zone zone, int point, double projectedDistance)
    {
        Index(point, zone.Current.Length);
        var dot = Vector2.Dot(Graphics.Freedom, Graphics.Projection);
        if (Math.Abs(dot) < 1e-6f) throw Fault("Perpendicular freedom and projection vectors.");
        Shift(zone, point, Graphics.Freedom * (float)(projectedDistance / dot));
    }
    private void Shift(Zone zone, int point, Vector2 delta)
    {
        Step(); Index(point, zone.Current.Length);
        var value = zone.Current[point] + delta;
        zone.Current[point] = new Vector2(Number(value.X), Number(value.Y));
        if (Graphics.Freedom.X != 0) zone.TouchedX[point] = true;
        if (Graphics.Freedom.Y != 0) zone.TouchedY[point] = true;
    }
    private void SetLineVector(int op)
    {
        var p2 = Pop(); var p1 = Pop();
        var a = ZoneAt(Graphics.Zp1); var b = ZoneAt(Graphics.Zp2);
        Index(p1, a.Current.Length); Index(p2, b.Current.Length);
        Vector2 Vector(bool original)
        {
            var v = (original ? a.Original[p1] : a.Current[p1]) - (original ? b.Original[p2] : b.Current[p2]);
            if ((op & 1) != 0) v = new Vector2(-v.Y, v.X);
            return Unit(v);
        }
        if (op is 0x08 or 0x09) Graphics.Freedom = Vector(false);
        else { Graphics.Projection = Vector(false); Graphics.Dual = op >= 0x86 ? Vector(true) : Graphics.Projection; }
    }
    private void AbsoluteMove(int op, bool indirect)
    {
        var value = indirect ? Cvt[Index(Pop(), Cvt.Length)] : 0;
        var p = Pop(); var z = ZoneAt(Graphics.Zp0); Index(p, z.Current.Length);
        if (indirect && Graphics.Zp0 == 0)
        {
            z.Original[p] = z.Current[p] = Graphics.Freedom * value;
        }
        var current = Coordinate(z, p);
        if (!indirect) value = current;
        if ((op & 1) != 0)
        {
            if (indirect && Math.Abs((long)value - current) > Graphics.CvtCutIn) value = current;
            value = Round(value);
        }
        Move(z, p, value - (double)current);
        Graphics.Rp0 = Graphics.Rp1 = p;
    }
    private void RelativeMove(int op)
    {
        var indirect = op >= 0xE0;
        var value = indirect ? Cvt[Index(Pop(), Cvt.Length)] : 0;
        var p = Pop(); var z = ZoneAt(Graphics.Zp1); var reference = ZoneAt(Graphics.Zp0);
        var original = Distance(z, p, reference, Graphics.Rp0, true);
        if (!indirect) value = original;
        else if (Graphics.AutoFlip && (value < 0) != (original < 0)) value = -value;
        if (Math.Abs(Math.Abs((long)value) - Graphics.SingleWidth) < Graphics.SingleWidthCutIn)
            value = value < 0 ? -Graphics.SingleWidth : Graphics.SingleWidth;
        if (indirect && Graphics.Zp1 == 0)
        {
            z.Original[p] = z.Current[p] = reference.Original[Index(Graphics.Rp0, reference.Current.Length)] + Graphics.Freedom * value;
            original = value;
        }
        if ((op & 4) != 0)
        {
            if (indirect && Graphics.Zp0 == Graphics.Zp1 && Math.Abs((long)value - original) > Graphics.CvtCutIn) value = original;
            value = Round(value);
        }
        if ((op & 8) != 0)
            value = original >= 0 ? Math.Max(value, Graphics.MinimumDistance) : Math.Min(value, -Graphics.MinimumDistance);
        Move(z, p, value - (double)Distance(z, p, reference, Graphics.Rp0));
        Graphics.Rp1 = Graphics.Rp0; Graphics.Rp2 = p;
        if ((op & 16) != 0) Graphics.Rp0 = p;
    }
    private void StackRelativeMove(int op)
    {
        var value = Pop(); var p = Pop(); var z = ZoneAt(Graphics.Zp1); var r = ZoneAt(Graphics.Zp0);
        Index(p, z.Current.Length); Index(Graphics.Rp0, r.Current.Length);
        if (Graphics.Zp1 == 0) z.Original[p] = z.Current[p] = r.Original[Graphics.Rp0] + Graphics.Freedom * value;
        Move(z, p, value - (double)Distance(z, p, r, Graphics.Rp0));
        Graphics.Rp1 = Graphics.Rp0; Graphics.Rp2 = p;
        if ((op & 1) != 0) Graphics.Rp0 = p;
    }
    private void AlignReference()
    {
        var n = Graphics.Loop;
        for (var i = 0; i < n; i++)
        {
            var p = Pop(); var z = ZoneAt(Graphics.Zp1);
            Move(z, p, -Distance(z, p, ZoneAt(Graphics.Zp0), Graphics.Rp0));
        }
        Graphics.Loop = 1;
    }
    private void AlignPoints()
    {
        var p1 = Pop(); var p2 = Pop(); var a = ZoneAt(Graphics.Zp1); var b = ZoneAt(Graphics.Zp0);
        var d = Distance(a, p1, b, p2) / 2d;
        Move(a, p1, -d); Move(b, p2, d);
    }

    private void IntersectLines()
    {
        var a0 = Pop(); var a1 = Pop(); var b0 = Pop(); var b1 = Pop(); var target = Pop();
        var a = ZoneAt(Graphics.Zp0); var b = ZoneAt(Graphics.Zp1); var z = ZoneAt(Graphics.Zp2);
        var p = a.Current[Index(a0, a.Current.Length)]; var q = b.Current[Index(b0, b.Current.Length)];
        var u = a.Current[Index(a1, a.Current.Length)] - p; var v = b.Current[Index(b1, b.Current.Length)] - q;
        Index(target, z.Current.Length);
        var determinant = (double)u.X * v.Y - (double)u.Y * v.X;
        Vector2 intersection;
        if (Math.Abs(determinant) < 1e-9) intersection = (p + p + u + q + q + v) / 4;
        else
        {
            var t = ((double)(q.X - p.X) * v.Y - (double)(q.Y - p.Y) * v.X) / determinant;
            intersection = new Vector2(Number(p.X + u.X * t), Number(p.Y + u.Y * t));
        }
        z.Current[target] = new Vector2(Number(intersection.X), Number(intersection.Y));
        z.TouchedX[target] = z.TouchedY[target] = true;
    }
    private void Untouch()
    {
        var p = Pop(); var z = ZoneAt(Graphics.Zp0); Index(p, z.Current.Length);
        if (Graphics.Freedom.X != 0) z.TouchedX[p] = false;
        if (Graphics.Freedom.Y != 0) z.TouchedY[p] = false;
    }
    private (Zone Zone, int Point, int Distance) ReferenceShift(int op)
    {
        var zone = ZoneAt((op & 1) != 0 ? Graphics.Zp0 : Graphics.Zp1);
        var point = (op & 1) != 0 ? Graphics.Rp1 : Graphics.Rp2;
        return (zone, point, Coordinate(zone, point) - Number(Vector2.Dot(zone.Original[point], Graphics.Projection)));
    }
    private void ShiftPoints(int op)
    {
        var reference = ReferenceShift(op);
        var n = Graphics.Loop;
        for (var i = 0; i < n; i++) Move(ZoneAt(Graphics.Zp2), Pop(), reference.Distance);
        Graphics.Loop = 1;
    }
    private void ShiftContour(int op)
    {
        var reference = ReferenceShift(op); var contour = Pop(); var z = ZoneAt(Graphics.Zp2);
        int first, last;
        if (Graphics.Zp2 == 0) { if (contour != 0) throw Fault("Invalid twilight contour."); first = 0; last = z.Current.Length - 1; }
        else { Index(contour, z.Ends.Length); first = contour == 0 ? 0 : z.Ends[contour - 1] + 1; last = z.Ends[contour]; }
        for (var p = first; p <= last; p++)
            if (z != reference.Zone || p != reference.Point) Move(z, p, reference.Distance);
    }
    private void ShiftZone(int op)
    {
        var reference = ReferenceShift(op); var zone = ZoneIndex(Pop()); var z = ZoneAt(zone);
        var n = zone == 0 ? z.Current.Length : z.Ends.Length == 0 ? 0 : z.Ends[^1] + 1;
        for (var p = 0; p < n; p++)
            if (z != reference.Zone || p != reference.Point) Move(z, p, reference.Distance);
    }
    private void ShiftPixels()
    {
        var delta = Graphics.Freedom * Pop(); var n = Graphics.Loop;
        for (var i = 0; i < n; i++) Shift(ZoneAt(Graphics.Zp2), Pop(), delta);
        Graphics.Loop = 1;
    }
    private void InterpolatePoints()
    {
        var a = ZoneAt(Graphics.Zp0); var b = ZoneAt(Graphics.Zp1); var z = ZoneAt(Graphics.Zp2);
        var oldRange = Distance(b, Graphics.Rp2, a, Graphics.Rp1, true);
        var newRange = Distance(b, Graphics.Rp2, a, Graphics.Rp1);
        var n = Graphics.Loop;
        for (var i = 0; i < n; i++)
        {
            var p = Pop(); var oldDistance = Distance(z, p, a, Graphics.Rp1, true);
            var target = oldRange != 0 ? Number(oldDistance * (double)newRange / oldRange) : oldDistance;
            Move(z, p, target - (double)Distance(z, p, a, Graphics.Rp1));
        }
        Graphics.Loop = 1;
    }
    private void InterpolateUntouched(bool x)
    {
        var z = Glyph; var touched = x ? z.TouchedX : z.TouchedY;
        float Old(int i) => x ? z.Original[i].X : z.Original[i].Y;
        float Current(int i) => x ? z.Current[i].X : z.Current[i].Y;
        void Set(int i, double value)
        {
            Step(); var point = z.Current[i];
            z.Current[i] = x ? point with { X = Number(value) } : point with { Y = Number(value) };
        }
        var first = 0;
        foreach (var last in z.Ends)
        {
            var anchors = new List<int>();
            for (var i = first; i <= last; i++) { Step(); if (touched[i]) anchors.Add(i); }
            if (anchors.Count == 1)
            {
                var delta = Current(anchors[0]) - Old(anchors[0]);
                for (var i = first; i <= last; i++) if (!touched[i]) Set(i, Old(i) + delta);
            }
            else if (anchors.Count > 1)
            {
                for (var j = 0; j < anchors.Count; j++)
                {
                    var a = anchors[j]; var b = anchors[(j + 1) % anchors.Count];
                    var low = Old(a) <= Old(b) ? a : b; var high = low == a ? b : a;
                    for (var p = a == last ? first : a + 1; p != b; p = p == last ? first : p + 1)
                    {
                        var original = Old(p); double value;
                        if (Old(low) == Old(high))
                            value = Current(low) == Current(high) ? original + Current(low) - Old(low) : original;
                        else if (original <= Old(low)) value = original + Current(low) - Old(low);
                        else if (original >= Old(high)) value = original + Current(high) - Old(high);
                        else value = Current(low) + (original - Old(low)) * (double)(Current(high) - Current(low)) / (Old(high) - Old(low));
                        Set(p, value);
                    }
                }
            }
            first = last + 1;
        }
    }
    private void Delta(int op)
    {
        var n = Count(Pop(), StackLimit / 2);
        var band = op switch { 0x71 or 0x74 => 16, 0x72 or 0x75 => 32, _ => 0 };
        for (var i = 0; i < n; i++)
        {
            Step(); var index = Pop(); var argument = Pop();
            if ((uint)argument > 255) throw Fault("Invalid delta argument.");
            if (Number(PixelsPerEm) != Graphics.DeltaBase + band + (argument >> 4)) continue;
            var steps = (argument & 15) - 8; if (steps >= 0) steps++;
            var distance = steps * 64d / (1 << Graphics.DeltaShift);
            if (op is 0x73 or 0x74 or 0x75) Cvt[Index(index, Cvt.Length)] = Number(Cvt[index] + distance);
            else Move(ZoneAt(Graphics.Zp0), index, distance);
        }
    }
    private void FlipPoints()
    {
        var zone = ZoneAt(Graphics.Zp0);
        var n = Graphics.Loop;
        for (var i = 0; i < n; i++) { Step(); var p = Index(Pop(), zone.OnCurve.Length); zone.OnCurve[p] = !zone.OnCurve[p]; }
        Graphics.Loop = 1;
    }
    private void FlipRange(bool on)
    {
        var zone = ZoneAt(Graphics.Zp0);
        var high = Index(Pop(), zone.OnCurve.Length); var low = Index(Pop(), zone.OnCurve.Length);
        if (high < low) throw Fault("Invalid point range.");
        for (var p = low; p <= high; p++) { Step(); zone.OnCurve[p] = on; }
    }
}

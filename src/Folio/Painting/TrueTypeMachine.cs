using System.Numerics;

namespace Folio.Painting;

/// <summary>
/// Bounded TrueType instruction execution. Coordinates and CVT values are 26.6 pixels, stack values are signed
/// integers. No font-supplied limit can increase the host's limits. Invalid programs abort the entire glyph;
/// callers must never use a partially fitted outline. Based on the OpenType TrueType instruction specification:
/// https://learn.microsoft.com/typography/opentype/otspec180/tt_instructions
/// </summary>
internal sealed partial class TrueTypeMachine
{
    internal const int InstructionLimit = 200_000;
    internal const int StackLimit = 1024;
    internal const int MemoryLimit = 4096;
    internal const int PointLimit = 65_536;
    private readonly int[] _stack = new int[StackLimit];
    private int _top;
    private int _fuel = InstructionLimit;
    private int _allowance = InstructionLimit;
    private readonly Dictionary<int, byte[]> _functions = [];
    private readonly Dictionary<int, byte[]> _instructions = [];
    internal readonly int[] Storage;
    internal readonly int[] Cvt;
    internal readonly Zone Twilight;
    internal Zone Glyph = new([], []);
    internal State Graphics = new();
    internal float PixelsPerEm { get; }
    private readonly float _scale;
    internal int StackCount => _top;
    internal int InstructionsUsed => _allowance - _fuel;
    internal long RetainedBytes => 16_384L + Storage.Length * 4L + Cvt.Length * 4L
        + Twilight.Current.Length * 32L + _functions.Sum(f => 96L + f.Value.Length) + _instructions.Sum(f => 96L + f.Value.Length);

    private TrueTypeMachine(TrueTypeMachine prepared)
    {
        PixelsPerEm = prepared.PixelsPerEm; _scale = prepared._scale;
        Storage = (int[])prepared.Storage.Clone(); Cvt = (int[])prepared.Cvt.Clone();
        Twilight = new(prepared.Twilight.Original, [], prepared.Twilight.OnCurve);
        Array.Copy(prepared.Twilight.Current, Twilight.Current, Twilight.Current.Length);
        _functions = prepared._functions;
        _instructions = prepared._instructions;
        Graphics = prepared.Graphics.Copy();
    }

    internal TrueTypeMachine Fork() => new(this);

    internal sealed class State
    {
        internal Vector2 Projection = Vector2.UnitX, Dual = Vector2.UnitX, Freedom = Vector2.UnitX;
        internal int Zp0 = 1, Zp1 = 1, Zp2 = 1, Rp0, Rp1, Rp2, Loop = 1;
        internal int MinimumDistance = 64, CvtCutIn = 68, SingleWidthCutIn, SingleWidth;
        internal int DeltaBase = 9, DeltaShift = 3, RoundPeriod = 64, RoundPhase, RoundThreshold = 32;
        internal bool AutoFlip = true, Inhibit, IgnorePrep;
        internal State Copy() => (State)MemberwiseClone();
    }

    internal TrueTypeMachine(float pixelsPerEm, int unitsPerEm, int storage, int twilight, ReadOnlySpan<short> cvt)
    {
        if (!float.IsFinite(pixelsPerEm) || pixelsPerEm <= 0 || pixelsPerEm > 4096 || unitsPerEm <= 0
            || storage < 0 || storage > MemoryLimit || twilight < 0 || twilight > MemoryLimit || cvt.Length > MemoryLimit)
            throw Fault("Font hinting resources exceed host limits.");
        PixelsPerEm = MathF.Round(pixelsPerEm * 64) / 64;
        _scale = MathF.Round(PixelsPerEm * 64 / unitsPerEm * 65536) / 65536;
        Storage = new int[storage];
        Twilight = new(new Vector2[twilight], []);
        Cvt = new int[cvt.Length];
        for (var i = 0; i < cvt.Length; i++) Cvt[i] = Number(cvt[i] * _scale);
    }

    private static InvalidDataException Fault(string reason) => new("TrueType hinting: " + reason);
    private void Step() { if (--_fuel < 0) throw Fault("Instruction budget exhausted."); }
    private static int Index(int index, int length)
    {
        if ((uint)index >= (uint)length) throw Fault("Index outside allocated memory.");
        return index;
    }
    private static int Number(double value)
    {
        if (!double.IsFinite(value) || value < -0x10000000 || value > 0x10000000) throw Fault("Coordinate or arithmetic overflow.");
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
    internal int Pop() => _top > 0 ? _stack[--_top] : throw Fault("Stack underflow.");
    private void Push(int value)
    {
        if (_top == StackLimit) throw Fault("Stack overflow.");
        _stack[_top++] = value;
    }
    private int Count(int value, int maximum = MemoryLimit)
    {
        if (value < 0 || value > maximum) throw Fault("Invalid loop count.");
        return value;
    }
    private void Binary(Func<int, int, int> operation) { var b = Pop(); var a = Pop(); Push(operation(a, b)); }

    // Starting an independent glyph preserves prepared CVT/storage/functions, but not the operand stack, references,
    // loop count or zones. A fresh budget covers this glyph, including every nested function and point loop.
    internal void StartGlyph(Zone glyph, State prepared, int allowance = InstructionLimit)
    {
        Glyph = glyph;
        Graphics = prepared.IgnorePrep ? new State() : prepared.Copy();
        Graphics.Projection = Graphics.Dual = Graphics.Freedom = Vector2.UnitX;
        Graphics.Rp0 = Graphics.Rp1 = Graphics.Rp2 = 0;
        Graphics.Zp0 = Graphics.Zp1 = Graphics.Zp2 = 1;
        Graphics.Loop = 1;
        _top = 0;
        if (allowance < 0 || allowance > InstructionLimit) throw Fault("Invalid execution budget.");
        _allowance = _fuel = allowance;
    }

    internal void Execute(byte[] code, bool definitions = false) => Run(code, definitions, 0);

    private static int Next(byte[] code, int pc)
    {
        var op = code[pc++];
        var count = op is 0x40 or 0x41 ? (pc < code.Length ? code[pc++] : throw Fault("Truncated push."))
            : op is >= 0xB0 and <= 0xBF ? (op & 7) + 1 : 0;
        var bytes = count * (op == 0x41 || op >= 0xB8 ? 2 : 1);
        if ((long)pc + bytes > code.Length) throw Fault("Truncated push operands.");
        return pc + bytes;
    }

    private void Run(byte[] code, bool definitions, int depth)
    {
        if (depth >= 64 || code.Length > 65_536) throw Fault("Program or call depth exceeds host limits.");
        // Branches may target instruction boundaries only, never bytes inside a PUSH payload.
        var boundaries = new bool[code.Length + 1];
        var conditions = new Stack<bool>();
        for (var p = 0; p < code.Length; p = Next(code, p))
        {
            Step(); boundaries[p] = true;
            if (code[p] == 0x58)
            {
                if (conditions.Count == 64) throw Fault("Conditional nesting exceeds host limits.");
                conditions.Push(false);
            }
            else if (code[p] == 0x1B)
            {
                if (conditions.Count == 0 || conditions.Pop()) throw Fault("Unexpected ELSE.");
                conditions.Push(true);
            }
            else if (code[p] == 0x59)
            {
                if (conditions.Count == 0) throw Fault("Unexpected EIF.");
                conditions.Pop();
            }
        }
        if (conditions.Count != 0) throw Fault("Unterminated conditional.");
        boundaries[code.Length] = true;
        for (var pc = 0; pc < code.Length;)
        {
            Step();
            var instruction = pc;
            var op = code[pc++];
            if (op is 0x40 or 0x41 or >= 0xB0 and <= 0xBF)
            {
                var n = op is 0x40 or 0x41 ? code[pc++] : (op & 7) + 1;
                for (var j = 0; j < n; j++)
                {
                    var value = (int)code[pc++];
                    if (op == 0x41 || op >= 0xB8) value = (short)((value << 8) | code[pc++]);
                    Push(value);
                }
                continue;
            }
            if (op >= 0xC0) { RelativeMove(op); continue; }
            switch (op)
            {
                case 0x00: case 0x01:
                    Graphics.Projection = Graphics.Dual = Graphics.Freedom = (op & 1) == 0 ? Vector2.UnitY : Vector2.UnitX; break;
                case 0x02: case 0x03: Graphics.Projection = Graphics.Dual = (op & 1) == 0 ? Vector2.UnitY : Vector2.UnitX; break;
                case 0x04: case 0x05: Graphics.Freedom = (op & 1) == 0 ? Vector2.UnitY : Vector2.UnitX; break;
                case 0x06: case 0x07: case 0x08: case 0x09: case 0x86: case 0x87: SetLineVector(op); break;
                case 0x0A: case 0x0B:
                {
                    var y = (short)Pop(); var x = (short)Pop();
                    var v = Unit(new Vector2(x, y));
                    if (op == 0x0A) Graphics.Projection = Graphics.Dual = v; else Graphics.Freedom = v;
                    break;
                }
                case 0x0C: case 0x0D:
                {
                    var v = op == 0x0C ? Graphics.Projection : Graphics.Freedom;
                    Push(Number(v.X * 16384)); Push(Number(v.Y * 16384)); break;
                }
                case 0x0E: Graphics.Freedom = Graphics.Projection; break;
                case 0x0F: IntersectLines(); break;
                case 0x10: Graphics.Rp0 = Pop(); break;
                case 0x11: Graphics.Rp1 = Pop(); break;
                case 0x12: Graphics.Rp2 = Pop(); break;
                case 0x13: Graphics.Zp0 = ZoneIndex(Pop()); break;
                case 0x14: Graphics.Zp1 = ZoneIndex(Pop()); break;
                case 0x15: Graphics.Zp2 = ZoneIndex(Pop()); break;
                case 0x16: Graphics.Zp0 = Graphics.Zp1 = Graphics.Zp2 = ZoneIndex(Pop()); break;
                case 0x17: Graphics.Loop = Count(Pop()); break;
                case 0x18: SetRound(64, 0, 32); break;
                case 0x19: SetRound(64, 32, 32); break;
                case 0x1A: Graphics.MinimumDistance = Pop(); break;
                case 0x1B: pc = Skip(code, pc, false); break;
                case 0x1C: Jump(Pop()); break;
                case 0x1D: Graphics.CvtCutIn = Pop(); break;
                case 0x1E: Graphics.SingleWidthCutIn = Pop(); break;
                case 0x1F: Graphics.SingleWidth = Number(Pop() * _scale); break;
                case 0x20: { var v = Pop(); Push(v); Push(v); break; }
                case 0x21: Pop(); break;
                case 0x22: _top = 0; break;
                case 0x23: { var b = Pop(); var a = Pop(); Push(b); Push(a); break; }
                case 0x24: Push(_top); break;
                case 0x25: case 0x26:
                {
                    var n = Pop(); if (n <= 0 || n > _top) throw Fault("Invalid stack index.");
                    var at = _top - n; var value = _stack[at];
                    if (op == 0x26) { Array.Copy(_stack, at + 1, _stack, at, n - 1); _top--; }
                    Push(value); break;
                }
                case 0x27: AlignPoints(); break;
                case 0x29: Untouch(); break;
                case 0x2A: case 0x2B:
                {
                    var id = Pop(); var count = op == 0x2A ? Count(Pop()) : 1;
                    if (!_functions.TryGetValue(id, out var function)) throw Fault("Undefined function " + id + ".");
                    for (var i = 0; i < count; i++) { Step(); Run(function, false, depth + 1); }
                    break;
                }
                case 0x2C: case 0x89:
                {
                    if (!definitions) throw Fault("Definition outside font/CVT preparation.");
                    var id = Pop(); Index(id, op == 0x2C ? MemoryLimit : 256);
                    var end = pc;
                    while (end < code.Length && code[end] != 0x2D)
                    {
                        Step(); if (code[end] is 0x2C or 0x89) throw Fault("Nested function/instruction definition.");
                        end = Next(code, end);
                    }
                    if (end == code.Length) throw Fault("Unterminated function.");
                    (op == 0x2C ? _functions : _instructions)[id] = code[pc..end]; pc = end + 1; break;
                }
                case 0x2D: throw Fault("Unexpected function terminator.");
                case 0x2E: case 0x2F: AbsoluteMove(op, false); break;
                case 0x30: case 0x31: InterpolateUntouched(op == 0x31); break;
                case 0x32: case 0x33: ShiftPoints(op); break;
                case 0x34: case 0x35: ShiftContour(op); break;
                case 0x36: case 0x37: ShiftZone(op); break;
                case 0x38: ShiftPixels(); break;
                case 0x39: InterpolatePoints(); break;
                case 0x3A: case 0x3B: StackRelativeMove(op); break;
                case 0x3C: AlignReference(); break;
                case 0x3D: SetRound(32, 0, 16); break;
                case 0x3E: case 0x3F: AbsoluteMove(op, true); break;
                case 0x42: { var v = Pop(); Storage[Index(Pop(), Storage.Length)] = v; break; }
                case 0x43: Push(Storage[Index(Pop(), Storage.Length)]); break;
                case 0x44: case 0x70:
                {
                    var v = Pop(); Cvt[Index(Pop(), Cvt.Length)] = op == 0x70 ? Number(v * _scale) : v; break;
                }
                case 0x45: Push(Cvt[Index(Pop(), Cvt.Length)]); break;
                case 0x46: case 0x47:
                {
                    var p = Pop(); Push(Coordinate(ZoneAt(Graphics.Zp2), p, op == 0x47)); break;
                }
                case 0x48:
                {
                    var target = Pop(); var p = Pop(); var z = ZoneAt(Graphics.Zp2);
                    Move(z, p, target - Coordinate(z, p));
                    if (Graphics.Zp2 == 0) z.Original[p] = z.Current[p];
                    break;
                }
                case 0x49: case 0x4A:
                {
                    var a = Pop(); var b = Pop();
                    // 0x49 uses fitted coordinates, 0x4A original coordinates. Some published prose in the
                    // TrueType manuals reverses these flags; the instruction tables and font engines agree here.
                    Push(Distance(ZoneAt(Graphics.Zp0), b, ZoneAt(Graphics.Zp1), a, op == 0x4A)); break;
                }
                case 0x4B: Push(Number(PixelsPerEm)); break;
                case 0x4C: Push(Number(PixelsPerEm * 64)); break;
                case 0x4D: Graphics.AutoFlip = true; break;
                case 0x4E: Graphics.AutoFlip = false; break;
                case 0x4F: case 0x7E: case 0x7F: Pop(); break; // Retired debugger/angle-adjustment instructions.
                case 0x50: Binary((a, b) => a < b ? 1 : 0); break;
                case 0x51: Binary((a, b) => a <= b ? 1 : 0); break;
                case 0x52: Binary((a, b) => a > b ? 1 : 0); break;
                case 0x53: Binary((a, b) => a >= b ? 1 : 0); break;
                case 0x54: Binary((a, b) => a == b ? 1 : 0); break;
                case 0x55: Binary((a, b) => a != b ? 1 : 0); break;
                case 0x56: Push((Round(Pop()) & 127) == 64 ? 1 : 0); break;
                case 0x57: Push((Round(Pop()) & 127) == 0 ? 1 : 0); break;
                case 0x58: if (Pop() == 0) pc = Skip(code, pc, true); break;
                case 0x59: break;
                case 0x5A: Binary((a, b) => a != 0 && b != 0 ? 1 : 0); break;
                case 0x5B: Binary((a, b) => a != 0 || b != 0 ? 1 : 0); break;
                case 0x5C: Push(Pop() == 0 ? 1 : 0); break;
                case 0x5D: case 0x71: case 0x72: case 0x73: case 0x74: case 0x75: Delta(op); break;
                case 0x5E: Graphics.DeltaBase = Count(Pop(), 255); break;
                case 0x5F: Graphics.DeltaShift = Count(Pop(), 6); break;
                case 0x60: Binary((a, b) => Number((long)a + b)); break;
                case 0x61: Binary((a, b) => Number((long)a - b)); break;
                case 0x62: Binary((a, b) => b == 0 ? throw Fault("Division by zero.") : Number(a * 64d / b)); break;
                case 0x63: Binary((a, b) => Number((long)a * b / 64d)); break;
                case 0x64: Push(Number(Math.Abs((long)Pop()))); break;
                case 0x65: Push(Number(-(long)Pop())); break;
                case 0x66: Push(Pop() & ~63); break;
                case 0x67: Push(Number(Math.Ceiling(Pop() / 64d) * 64)); break;
                case >= 0x68 and <= 0x6B: Push(Round(Pop())); break;
                case >= 0x6C and <= 0x6F: { var v = Pop(); Push(v); break; }
                case 0x76: case 0x77: SuperRound(Pop(), op == 0x77); break;
                case 0x78: case 0x79: { var test = Pop(); var offset = Pop(); if ((test != 0) == (op == 0x78)) Jump(offset); break; }
                case 0x7A: SetRound(0, 0, 0); break;
                case 0x7C: SetRound(64, 0, 63); break;
                case 0x7D: SetRound(64, 0, 0); break;
                case 0x80: FlipPoints(); break;
                case 0x81: case 0x82: FlipRange(op == 0x81); break;
                case 0x85: case 0x8D: Pop(); break; // Scan conversion flags: grayscale coverage has no monochrome dropouts.
                case 0x88:
                {
                    var selector = Pop();
                    // Interpreter version 40, grayscale rendering (bit 12), no subpixel/LCD/rotation/stretch flags.
                    Push(((selector & 1) != 0 ? 40 : 0) | ((selector & 32) != 0 ? 1 << 12 : 0)); break;
                }
                case 0x8A: { var c = Pop(); var b = Pop(); var a = Pop(); Push(b); Push(c); Push(a); break; }
                case 0x8B: Binary(Math.Max); break;
                case 0x8C: Binary(Math.Min); break;
                case 0x8E:
                {
                    var selector = Pop(); var value = Pop();
                    if (selector == 1) Graphics.Inhibit = value == 1;
                    else if (selector == 2) Graphics.IgnorePrep = value == 2;
                    else throw Fault("Unsupported instruction control selector.");
                    break;
                }
                default:
                    if (_instructions.TryGetValue(op, out var implementation)) Run(implementation, false, depth + 1);
                    else throw Fault($"Unsupported opcode 0x{op:X2}.");
                    break;
            }

            void Jump(int offset)
            {
                var target = (long)instruction + offset;
                if (target < 0 || target > code.Length || !boundaries[target]) throw Fault("Jump outside instruction boundaries.");
                pc = (int)target;
            }
        }
    }

    private int Skip(byte[] code, int pc, bool otherwise)
    {
        var level = 0;
        while (pc < code.Length)
        {
            Step(); var op = code[pc]; var next = Next(code, pc);
            if (op == 0x58) level++;
            else if (op == 0x59) { if (level == 0) return next; level--; }
            else if (op == 0x1B && level == 0 && otherwise) return next;
            pc = next;
        }
        throw Fault("Unterminated conditional.");
    }

    private void SetRound(int period, int phase, int threshold)
    { Graphics.RoundPeriod = period; Graphics.RoundPhase = phase; Graphics.RoundThreshold = threshold; }
    private int Round(int value)
    {
        if (Graphics.RoundPeriod == 0) return value;
        var magnitude = Math.Abs((long)value);
        var rounded = Math.Max(Graphics.RoundPhase,
            Math.Floor((magnitude - Graphics.RoundPhase + Graphics.RoundThreshold) / (double)Graphics.RoundPeriod)
            * Graphics.RoundPeriod + Graphics.RoundPhase);
        return Number(value < 0 ? -rounded : rounded);
    }
    private void SuperRound(int selector, bool diagonal)
    {
        var period = (selector >> 6 & 3) switch { 0 => 32d, 1 => 64d, 2 => 128d, _ => throw Fault("Invalid round period.") };
        if (diagonal) period /= Math.Sqrt(2);
        SetRound(Number(period), Number((selector >> 4 & 3) * period / 4),
            Number((selector & 15) == 0 ? period - 1 : ((selector & 15) - 4) * period / 8));
    }
}

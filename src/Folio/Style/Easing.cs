using System.Globalization;

namespace Folio.Style;

internal enum StepPosition { JumpStart, JumpEnd, JumpNone, JumpBoth }

/// <summary>
/// An easing function (https://www.w3.org/TR/css-easing-2/): maps an input progress to an output progress. Inputs
/// outside 0 to 1 extrapolate, as keyframe easing can need.
/// </summary>
internal abstract record Easing
{
    public static Easing Linear { get; } = new LinearEasing([]);

    public static Easing Ease { get; } = new CubicBezierEasing(0.25, 0.1, 0.25, 1);

    /// <summary>A number as text for descriptions: parameters are read as floats, so a few decimals are enough.</summary>
    protected static string Text(double value) => Math.Round(value, 4).ToString(CultureInfo.InvariantCulture);

    /// <param name="before">The before flag of step easing: the input approaches from the before phase.</param>
    public abstract double Apply(double input, bool before = false);
}

/// <summary>cubic-bezier(x1, y1, x2, y2), and the ease keywords (https://www.w3.org/TR/css-easing-2/#cubic-bezier-easing-functions).</summary>
internal sealed record CubicBezierEasing(double X1, double Y1, double X2, double Y2) : Easing
{
    public override double Apply(double input, bool before = false)
    {
        if (input is 0 or 1)
            return input;
        // Outside 0 to 1 the curve continues along its end tangents.
        if (input < 0)
            return input * (X1 > 0 ? Y1 / X1 : Y1 == 0 && X2 > 0 ? Y2 / X2 : 0);
        if (input > 1)
            return 1 + (input - 1) * (X2 < 1 ? (Y2 - 1) / (X2 - 1) : Y2 == 1 && X1 < 1 ? (Y1 - 1) / (X1 - 1) : 0);
        return Coordinate(Parameter(input), Y1, Y2);
    }

    private static double Coordinate(double t, double p1, double p2) => ((1 - 3 * p2 + 3 * p1) * t + (3 * p2 - 6 * p1)) * t * t + 3 * p1 * t;

    // The curve parameter whose x is the input: Newton's method, then bisection if it does not converge (x is monotonic,
    // since x1 and x2 are in 0 to 1).
    private double Parameter(double x)
    {
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Coordinate(t, X1, X2) - x;
            if (Math.Abs(error) < 1e-7)
                return t;
            var slope = (3 * (1 - 3 * X2 + 3 * X1) * t + 2 * (3 * X2 - 6 * X1)) * t + 3 * X1;
            if (Math.Abs(slope) < 1e-6)
                break;
            t -= error / slope;
        }
        var (low, high) = (0.0, 1.0);
        t = x;
        for (var i = 0; i < 40 && high - low > 1e-9; i++)
        {
            if (Coordinate(t, X1, X2) < x)
                low = t;
            else
                high = t;
            t = (low + high) / 2;
        }
        return t;
    }

    public override string ToString() => $"cubic-bezier({Text(X1)}, {Text(Y1)}, {Text(X2)}, {Text(Y2)})";
}

/// <summary>steps(n, position), step-start and step-end (https://www.w3.org/TR/css-easing-2/#step-easing-functions).</summary>
internal sealed record StepsEasing(int Steps, StepPosition Position) : Easing
{
    public override double Apply(double input, bool before = false)
    {
        var scaled = input * Steps;
        var step = Math.Floor(scaled);
        if (Position is StepPosition.JumpStart or StepPosition.JumpBoth)
            step++;
        if (before && scaled == Math.Floor(scaled))
            step--;
        if (input >= 0 && step < 0)
            step = 0;
        var jumps = Position switch { StepPosition.JumpBoth => Steps + 1, StepPosition.JumpNone => Steps - 1, _ => Steps };
        if (input <= 1 && step > jumps)
            step = jumps;
        return step / jumps;
    }

    public override string ToString() => $"steps({Steps}, {Position switch
    {
        StepPosition.JumpStart => "jump-start", StepPosition.JumpNone => "jump-none", StepPosition.JumpBoth => "jump-both", _ => "jump-end",
    }})";
}

/// <summary>
/// linear() with its control points, inputs already filled in (https://www.w3.org/TR/css-easing-2/#the-linear-easing-function);
/// no points is the linear keyword.
/// </summary>
internal sealed record LinearEasing(IReadOnlyList<(double Input, double Output)> Points) : Easing
{
    public override double Apply(double input, bool before = false)
    {
        if (Points.Count < 2)
            return input;
        // The segment of the last point at or before the input; inputs before the first or after the last extend the end segments.
        var a = 0;
        for (var i = 0; i < Points.Count; i++)
        {
            if (Points[i].Input <= input)
                a = i;
        }
        if (a == Points.Count - 1)
            a--;
        var (from, to) = (Points[a], Points[a + 1]);
        return from.Input == to.Input ? to.Output : from.Output + (to.Output - from.Output) * (input - from.Input) / (to.Input - from.Input);
    }

    public override string ToString() => Points.Count == 0 ? "linear"
        : "linear(" + string.Join(", ", Points.Select(p => $"{Text(p.Output)} {Text(p.Input * 100)}%")) + ")";

    /// <summary>
    /// Fills in the inputs of linear()'s control points: the first defaults to 0 and the last to 1 (or the largest input
    /// before it), an input below an earlier one moves up to it, and runs without inputs spread evenly between their neighbours.
    /// </summary>
    public static LinearEasing FromStops(IReadOnlyList<(double Output, double? Input)> stops)
    {
        var inputs = stops.Select(s => s.Input).ToArray();
        inputs[0] ??= 0;
        inputs[^1] ??= Math.Max(1, inputs.Max() ?? 1);
        var largest = double.NegativeInfinity;
        for (var i = 0; i < inputs.Length; i++)
        {
            if (inputs[i] is { } input)
                inputs[i] = largest = Math.Max(largest, input);
        }
        for (var i = 1; i < inputs.Length; i++)
        {
            if (inputs[i] is not null)
                continue;
            var end = i;
            while (inputs[end] is null)
                end++;
            var (start, stop) = (inputs[i - 1]!.Value, inputs[end]!.Value);
            for (var j = i; j < end; j++)
                inputs[j] = start + (stop - start) * (j - i + 1) / (end - i + 1);
            i = end;
        }
        return new LinearEasing([.. stops.Select((s, i) => (inputs[i]!.Value, s.Output))]);
    }
}

using System.Runtime.CompilerServices;

namespace Folio.Scripting.Native;

/// <summary>A synchronous task budget shared by nested native calls and callbacks.</summary>
internal sealed class NativeExecutionBudget(object owner, ScriptLimits limits, Func<bool> timedOut, Func<bool> interrupted)
{
    [ThreadStatic] private static NativeExecutionBudget? _current;
    private readonly long _allocationStart = GC.GetAllocatedBytesForCurrentThread();
    private long _steps;
    private int _depth;
    private Exception? _failure;

    internal static Scope Begin(object owner, ScriptLimits limits, Func<bool> timedOut, Func<bool> interrupted)
    {
        var previous = _current;
        if (previous is null || !ReferenceEquals(previous.Owner, owner))
            _current = new(owner, limits, timedOut, interrupted);
        return new Scope(previous);
    }

    private object Owner => owner;

    internal static void Step()
    {
        if (_current is not { } budget) return;
        if (budget._failure is not null) throw budget._failure;
        budget._steps++;
        if (budget.Limits.MaxStatements is { } maximum && budget._steps > maximum) budget.Fail("Statements");
        if ((budget._steps & 63) == 0) budget.Check();
    }

    private ScriptLimits Limits => limits;

    internal static void CheckCurrent() => _current?.Check();

    internal static void CheckAllocation(long bytes)
    {
        if (_current is not { } budget) return;
        budget.Check();
        if (budget.Limits.MemoryLimitBytes is { } maximum
            && bytes > maximum - (GC.GetAllocatedBytesForCurrentThread() - budget._allocationStart))
            budget.Fail("Memory");
    }

    private void Check()
    {
        if (_failure is not null) throw _failure;
        if (interrupted())
        {
            _failure = new ScriptInterruptedException("The native script was interrupted.");
            throw _failure;
        }
        if (timedOut()) Fail("Timeout");
        if (limits.MemoryLimitBytes is { } maximum && GC.GetAllocatedBytesForCurrentThread() - _allocationStart > maximum)
            Fail("Memory");
    }

    internal static CallScope EnterCall()
    {
        EnsureStack();
        Step();
        var budget = _current;
        if (budget is not null)
        {
            if (budget.Limits.MaxRecursionDepth is { } maximum && budget._depth >= maximum)
                budget.Fail("Recursion");
            budget._depth++;
        }
        return new CallScope(budget);
    }

    internal static void EnsureStack()
    {
        if (!RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            if (_current is { } budget) budget.Fail("Recursion");
            throw new ScriptLimitExceededException("Recursion", "The native script exhausted its safe execution stack.");
        }
    }

    private void Fail(string name)
    {
        _failure = new ScriptLimitExceededException(name, $"The native script exceeded its {name.ToLowerInvariant()} limit.");
        throw _failure;
    }

    internal readonly struct Scope(NativeExecutionBudget? previous) : IDisposable
    {
        public void Dispose() => _current = previous;
    }

    internal readonly struct CallScope(NativeExecutionBudget? budget) : IDisposable
    {
        public void Dispose() { if (budget is not null) budget._depth--; }
    }
}

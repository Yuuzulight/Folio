using System.Runtime.CompilerServices;

namespace Folio.Scripting.Dom;

/// <summary>Accounts for pending message storage shared by an engine's ports and workers.</summary>
internal sealed class ScriptMessageBudget(long maximum)
{
    private static readonly ConditionalWeakTable<IScriptEngine, ScriptMessageBudget> Budgets = new();
    private readonly object _gate = new();
    private long _used;
    internal long Maximum { get; } = Math.Max(0, maximum);

    internal static ScriptMessageBudget For(IScriptEngine engine, long maximum) =>
        Budgets.GetValue(engine, _ => new ScriptMessageBudget(maximum));

    internal static void Reset(IScriptEngine engine) => Budgets.Remove(engine);

    internal Reservation Reserve(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > Maximum - _used)
                throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The pending message limit has been exceeded.");
            _used += bytes;
        }
        return new Reservation(this, bytes);
    }

    internal void Release(long bytes)
    {
        lock (_gate)
        {
            if (bytes < 0 || bytes > _used) throw new InvalidOperationException("Invalid message reservation release.");
            _used -= bytes;
        }
    }

    internal sealed class Reservation(ScriptMessageBudget budget, long bytes) : IDisposable
    {
        private ScriptMessageBudget? _budget = budget;
        internal ScriptMessageBudget Budget => _budget ?? throw new ObjectDisposedException(nameof(Reservation));
        internal long Bytes { get; } = bytes;

        // Ownership passes to a mailbox, whose aggregate reservation is released on drain/close.
        internal void Adopt() => _budget = null;

        public void Dispose() => Interlocked.Exchange(ref _budget, null)?.Release(Bytes);
    }
}

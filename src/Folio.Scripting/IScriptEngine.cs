namespace Folio.Scripting;

/// <summary>
/// Options for configuring a script engine instance, including testing hooks for determinism.
/// </summary>
public sealed record ScriptEngineOptions
{
    public static readonly ScriptEngineOptions Default = new();

    /// <summary>Seed for Math.random to ensure deterministic behavior across test runs.</summary>
    public int? RandomSeed { get; init; }

    /// <summary>Injected clock for Date evaluation to ensure reproducible timestamps in tests.</summary>
    public Func<DateTimeOffset>? Clock { get; init; }

    /// <summary>Initial script limits to apply to the engine.</summary>
    public ScriptLimits Limits { get; init; } = ScriptLimits.Default;
}

/// <summary>
/// Abstraction for a sandboxed JavaScript engine (dependencies.md, study 17).
/// Implemented by Jint in M4, and a native JIT-compiling engine in M5.
/// </summary>
public interface IScriptEngine : IDisposable
{
    /// <summary>Configure or update execution limits (time, statements, recursion, memory).</summary>
    void Configure(ScriptLimits limits);

    /// <summary>Evaluates a script snippet within the engine's global execution context.</summary>
    ScriptValue Evaluate(string source, string url = "eval");

    /// <summary>Calls a script function with a specified 'this' target and arguments.</summary>
    ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args);

    /// <summary>Wraps a host object with an <see cref="IHostObjectTemplate"/> without exposing CLR internals.</summary>
    ScriptValue CreateHostObject(IHostObjectTemplate template, object target);

    /// <summary>Sets a global property on the engine's global object.</summary>
    void SetGlobalProperty(string name, ScriptValue value);

    /// <summary>Gets a global property from the engine's global object.</summary>
    ScriptValue GetGlobalProperty(string name);

    /// <summary>Processes all queued microtasks (promises, queueMicrotask).</summary>
    void PerformMicrotaskCheckpoint();

    /// <summary>
    /// Interrupts any currently running script on another thread.
    /// Causes the evaluation to immediately fail with <see cref="ScriptInterruptedException"/>.
    /// </summary>
    void Interrupt();
}

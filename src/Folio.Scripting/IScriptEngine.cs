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
/// Implemented by the dependency-backed Jint engine for M4–M5. The interface
/// remains the replacement seam for the later Folio-owned engine.
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

    /// <summary>Extracts the underlying host target from a wrapped host object, if present.</summary>
    bool TryGetHostTarget(ScriptValue value, out object? target);

    /// <summary>Creates a JavaScript array containing the specified elements.</summary>
    ScriptValue CreateArray(ReadOnlySpan<ScriptValue> elements);

    /// <summary>Reads a script array for bindings that accept sequence arguments.</summary>
    bool TryGetArrayItems(ScriptValue value, out IReadOnlyList<ScriptValue> items) { items = []; return false; }
    bool TrySetArrayItem(ScriptValue value, int index, ScriptValue item) => false;

    /// <summary>Reads a property of an object (<c>undefined</c> when it has none, or the value is not an object).</summary>
    ScriptValue GetProperty(ScriptValue target, string name) => ScriptValue.Undefined;

    /// <summary>Creates a <c>Uint8ClampedArray</c> holding a copy of <paramref name="bytes"/>.</summary>
    ScriptValue CreateUint8ClampedArray(ReadOnlySpan<byte> bytes) => throw new NotSupportedException();

    /// <summary>Copies the elements of a <c>Uint8Array</c> or <c>Uint8ClampedArray</c> into <paramref name="bytes"/>; false for any other value.</summary>
    bool TryGetBytes(ScriptValue value, out byte[] bytes) { bytes = []; return false; }

    /// <summary>Writes <paramref name="bytes"/> into the elements of a <c>Uint8Array</c> or <c>Uint8ClampedArray</c> of the same length; false if it is not one.</summary>
    bool TrySetBytes(ScriptValue value, ReadOnlySpan<byte> bytes) => false;

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

/// <summary>Optional factory used by isolated execution contexts such as dedicated workers.</summary>
public interface IScriptEngineFactory
{
    IScriptEngine CreateIsolated();
}

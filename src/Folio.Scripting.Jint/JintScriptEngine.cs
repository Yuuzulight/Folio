using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Native.TypedArray;
using Jint.Runtime;
using Acornima.Ast;

namespace Folio.Scripting.Jint;

/// <summary>
/// Jint-backed implementation of <see cref="IScriptEngine"/> (study 17, dependencies.md).
/// Provides sandboxed ECMAScript evaluation with time, statement, recursion, and memory limits.
/// </summary>
public sealed class JintScriptEngine : IScriptEngine, IScriptEngineFactory, IHostScriptEvaluator
{
    private static readonly Dictionary<(string Source, string Url), Lazy<Prepared<Script>>> HostScripts = new();
    private const int MaxPreparedHostScripts = 16;
    private Engine? _engine;
    private CancellationTokenSource _cts = new();
    private ScriptLimits _limits = ScriptLimits.Default;
    private readonly ScriptEngineOptions _options;
    private bool _disposed;
    private int _evaluationDepth;
    private int _evaluationThreadId;
    private Func<bool>? _eventLoopPump;

    public JintScriptEngine() : this(ScriptEngineOptions.Default)
    {
    }

    public JintScriptEngine(ScriptEngineOptions options)
    {
        _options = options;
        _limits = options.Limits;
        RebuildEngine();
    }

    public IScriptEngine CreateIsolated() => new JintScriptEngine(_options);

    public void Configure(ScriptLimits limits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _limits = limits;
        RebuildEngine();
    }

    private void RebuildEngine()
    {
        _engine?.Dispose();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        _engine = new Engine(cfg =>
        {
            // Strict sandboxing: do NOT call AllowClr!
            cfg.CancellationToken(_cts.Token);
            cfg.Constraints.StackOverflowGuard = true;

            if (_limits.Timeout is { } timeout)
                cfg.TimeoutInterval(timeout);

            if (_limits.MaxStatements is { } maxStatements)
                cfg.MaxStatements(maxStatements);

            if (_limits.MaxRecursionDepth is { } maxRecursion)
                cfg.LimitRecursion(maxRecursion);

            if (_limits.MemoryLimitBytes is { } memBytes)
                cfg.LimitMemory(memBytes);

            if (_options.Clock is { } clock)
                cfg.TimeSystem = new InjectedTimeSystem(clock);
        });

        // Seeded Math.random if specified
        if (_options.RandomSeed is { } seed)
        {
            var rng = new Random(seed);
            _engine.SetValue("__folio_seed_random", (Func<double>)(() => rng.NextDouble()));
            _engine.Evaluate("Math.random = __folio_seed_random; delete globalThis.__folio_seed_random;");
        }
    }

    public ScriptValue Evaluate(string source, string url = "eval")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EvaluateOnCurrentThread(source, url);
    }

    ScriptValue IHostScriptEvaluator.EvaluateHostScript(string source, string url)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EvaluateOnCurrentThread(source, url, prepareHostScript: true);
    }

    private ScriptValue EvaluateOnCurrentThread(string source, string url, bool prepareHostScript = false)
    {
        var previousThread = Volatile.Read(ref _evaluationThreadId);
        if (previousThread == 0)
            Volatile.Write(ref _evaluationThreadId, Environment.CurrentManagedThreadId);
        try
        {
            Interlocked.Increment(ref _evaluationDepth);
            Lazy<Prepared<Script>>? prepared = null;
            // Only bounded, explicitly supplied host bootstrap scripts share parsed code.
            // Artifact scripts retain the normal parsing and execution limits.
            if (prepareHostScript && source.Length <= 128 * 1024)
            {
                lock (HostScripts)
                {
                    var key = (source, url);
                    if (!HostScripts.TryGetValue(key, out prepared) && HostScripts.Count < MaxPreparedHostScripts)
                    {
                        prepared = new Lazy<Prepared<Script>>(() => Engine.PrepareScript(source, url));
                        HostScripts.Add(key, prepared);
                    }
                }
            }
            var result = prepared is null ? _engine!.Evaluate(source, url) : _engine!.Evaluate(prepared.Value);
            return ToScriptValue(result);
        }
        catch (Exception ex)
        {
            throw TranslateException(ex);
        }
        finally
        {
            Interlocked.Decrement(ref _evaluationDepth);
            if (previousThread == 0)
                Volatile.Write(ref _evaluationThreadId, 0);
        }
    }

    public void RunHostTask(Action task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The host event loop and this engine share an owning thread. Async
        // functions return promises; their jobs run at the next checkpoint.
        task();
    }

    public void SetEventLoopPump(Func<bool> pump)
    {
        _eventLoopPump = pump ?? throw new ArgumentNullException(nameof(pump));
    }

    public bool IsEvaluationSuspended => Volatile.Read(ref _evaluationDepth) > 0;

    public ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CallOnCurrentThread(function, thisValue, args);
    }

    private ScriptValue CallOnCurrentThread(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args)
    {
        if (function.Raw is not JsValue jsCallable)
            throw new ArgumentException("Provided ScriptValue is not a callable function.", nameof(function));

        try
        {
            var jsThis = ToJsValue(_engine!, thisValue);
            var jsArgs = new JsValue[args.Length];
            for (var i = 0; i < args.Length; i++)
                jsArgs[i] = ToJsValue(_engine!, args[i]);

            var result = _engine!.Call(jsCallable, jsThis, jsArgs);
            return ToScriptValue(result);
        }
        catch (Exception ex)
        {
            throw TranslateException(ex);
        }
    }

    public ScriptValue CreateHostObject(IHostObjectTemplate template, object target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var hostObj = new JintHostObject(_engine!, template, target);
        return ScriptValue.FromObject(hostObj);
    }

    public bool TryGetHostTarget(ScriptValue value, out object? target)
    {
        if (value.Raw is JintHostObject jho)
        {
            target = jho.Target;
            return true;
        }
        target = null;
        return false;
    }

    public ScriptValue CreateArray(ReadOnlySpan<ScriptValue> elements)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = _engine!;
        var jsArray = engine.Intrinsics.Array.Construct(elements.Length);
        for (var i = 0; i < elements.Length; i++)
        {
            jsArray.Set(i, ToJsValue(engine, elements[i]));
        }
        return ToScriptValue(jsArray);
    }

    public ScriptValue GetProperty(ScriptValue target, string name) =>
        target.Raw is ObjectInstance obj ? ToScriptValue(obj.Get(name)) : ScriptValue.Undefined;

    public ScriptValue CreateUint8ClampedArray(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var engine = _engine!;
        var buffer = engine.Intrinsics.ArrayBuffer.Construct(bytes.ToArray());
        return ToScriptValue(engine.Intrinsics.Uint8ClampedArray.Construct(buffer, null, null));
    }

    public bool TryGetBytes(ScriptValue value, out byte[] bytes)
    {
        bytes = [];
        if (value.Raw is not JsValue js || !(js.IsUint8ClampedArray() || js.IsUint8Array()))
            return false;
        bytes = js.IsUint8ClampedArray() ? js.AsUint8ClampedArray() : js.AsUint8Array();
        return true;
    }

    public bool TrySetBytes(ScriptValue value, ReadOnlySpan<byte> bytes)
    {
        if (value.Raw is not JsTypedArray typed || !(((JsValue)typed).IsUint8ClampedArray() || ((JsValue)typed).IsUint8Array()) || typed.Length != bytes.Length)
            return false;
        for (var i = 0; i < bytes.Length; i++)
            typed.Set(i, (int)bytes[i]);
        return true;
    }

    public bool TryGetArrayBufferBytes(ScriptValue value, out byte[] bytes)
    {
        bytes = [];
        if (value.IsNullOrUndefined || value.IsFunction) return false;
        try
        {
            SetGlobalProperty("__folio_array_buffer", value);
            if (!string.Equals(Evaluate("Object.prototype.toString.call(__folio_array_buffer)").AsString(), "[object ArrayBuffer]", StringComparison.Ordinal))
            {
                SetGlobalProperty("__folio_array_buffer", ScriptValue.Undefined);
                return false;
            }
            var array = Evaluate("Array.from(new Uint8Array(__folio_array_buffer))");
            SetGlobalProperty("__folio_array_buffer", ScriptValue.Undefined);
            if (!TryGetArrayItems(array, out var items)) return false;
            bytes = items.Select(item => (byte)Math.Clamp(item.AsNumber(), 0, 255)).ToArray();
            return true;
        }
        catch
        {
            SetGlobalProperty("__folio_array_buffer", ScriptValue.Undefined);
            return false;
        }
    }

    public ScriptValue CreateArrayBuffer(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ToScriptValue(_engine!.Intrinsics.ArrayBuffer.Construct(bytes.ToArray()));
    }

    public bool TryDetachArrayBuffer(ScriptValue value)
    {
        try
        {
            SetGlobalProperty("__folio_transfer_buffer", value);
            Evaluate("__folio_transfer_buffer.transfer()");
            SetGlobalProperty("__folio_transfer_buffer", ScriptValue.Undefined);
            return true;
        }
        catch
        {
            SetGlobalProperty("__folio_transfer_buffer", ScriptValue.Undefined);
            return false;
        }
    }

    public bool TryGetArrayItems(ScriptValue value, out IReadOnlyList<ScriptValue> items)
    {
        if (value.Raw is ArrayInstance array)
        {
            var result = new ScriptValue[(int)array.Get("length").AsNumber()];
            for (var i = 0; i < result.Length; i++)
                result[i] = ToScriptValue(array.Get(i.ToString()));
            items = result;
            return true;
        }
        if (value.Raw is JsTypedArray typed)
        {
            var result = new ScriptValue[(int)typed.Get("length").AsNumber()];
            for (var i = 0; i < result.Length; i++)
                result[i] = ToScriptValue(typed.Get(i.ToString()));
            items = result;
            return true;
        }
        items = [];
        return false;
    }

    public bool TrySetArrayItem(ScriptValue value, int index, ScriptValue item)
    {
        if (index < 0) return false;
        var jsItem = ToJsValue(_engine!, item);
        if (value.Raw is ArrayInstance array)
        {
            array.Set(index, jsItem);
            return true;
        }
        if (value.Raw is JsTypedArray typed)
        {
            typed.Set(index, jsItem);
            return true;
        }
        return false;
    }

    public void SetGlobalProperty(string name, ScriptValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine!.SetValue(name, ToJsValue(_engine!, value));
    }

    public ScriptValue GetGlobalProperty(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ToScriptValue(_engine!.GetValue(name));
    }

    public void PerformMicrotaskCheckpoint()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _engine!.Advanced.ProcessTasks();
        }
        catch (Exception ex)
        {
            throw TranslateException(ex);
        }
    }

    public void Interrupt()
    {
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private Exception TranslateException(Exception ex)
    {
        return ex switch
        {
            ExecutionCanceledException or OperationCanceledException =>
                new ScriptInterruptedException("Script execution was interrupted."),

            TimeoutException =>
                new ScriptLimitExceededException("Timeout", "Script execution timed out."),

            StatementsCountOverflowException =>
                new ScriptLimitExceededException("Statements", "Script exceeded statement limit."),

            RecursionDepthOverflowException =>
                new ScriptLimitExceededException("Recursion", "Script exceeded maximum recursion depth."),

            MemoryLimitExceededException =>
                new ScriptLimitExceededException("Memory", "Script exceeded memory allocation limit."),

            JavaScriptException jse =>
                new ScriptRuntimeException(
                    jse.Message,
                    jse.JavaScriptStackTrace,
                    jse.Location.Start.Line,
                    jse.Location.Start.Column,
                    jse.Location.SourceFile),

            _ => ex,
        };
    }

    internal static ScriptValue ToScriptValue(JsValue value)
    {
        if (value.IsUndefined())
            return ScriptValue.Undefined;
        if (value.IsNull())
            return ScriptValue.Null;
        if (value.IsBoolean())
            return ScriptValue.FromBoolean(value.AsBoolean());
        if (value.IsNumber())
            return ScriptValue.FromNumber(value.AsNumber());
        if (value.IsString())
            return ScriptValue.FromString(value.AsString());
        if (value.IsArray())
            return ScriptValue.FromArray(value);
        if (value is Function or BindFunction)
            return ScriptValue.FromFunction(value);
        return ScriptValue.FromObject(value);
    }

    internal static JsValue ToJsValue(Engine engine, ScriptValue value)
    {
        return value.Kind switch
        {
            ScriptValueKind.Undefined => JsValue.Undefined,
            ScriptValueKind.Null => JsValue.Null,
            ScriptValueKind.Boolean => value.AsBoolean() ? JsBoolean.True : JsBoolean.False,
            ScriptValueKind.Number => new JsNumber(value.AsNumber()),
            ScriptValueKind.String => new JsString(value.AsString() ?? ""),
            ScriptValueKind.Object or ScriptValueKind.Array or ScriptValueKind.Function =>
                value.Raw is JsValue jv ? jv :
                value.Raw is JintHostObject jho ? jho :
                JsValue.Undefined,
            _ => JsValue.Undefined,
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        try { Dom.DomBindings.Dispose(this); }
        catch (ScriptInterruptedException) { }
        catch (ScriptLimitExceededException) { }
        catch (ScriptRuntimeException) { }
        finally
        {
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
            _engine?.Dispose();
            _engine = null;
        }
    }

    private sealed class InjectedTimeSystem : ITimeSystem
    {
        private readonly Func<DateTimeOffset> _clock;

        public InjectedTimeSystem(Func<DateTimeOffset> clock)
        {
            _clock = clock;
        }

        public DateTimeOffset GetUtcNow() => _clock();
        public TimeZoneInfo DefaultTimeZone => TimeZoneInfo.Utc;
        public TimeSpan GetUtcOffset(long epochMilliseconds) => TimeSpan.Zero;
        public bool TryParse(string date, out long epochMilliseconds)
        {
            if (DateTimeOffset.TryParse(date, out var dto))
            {
                epochMilliseconds = dto.ToUnixTimeMilliseconds();
                return true;
            }
            epochMilliseconds = 0;
            return false;
        }
    }
}

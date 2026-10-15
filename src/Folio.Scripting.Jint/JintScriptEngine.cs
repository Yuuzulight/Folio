using System.Collections.Concurrent;
using Jint;
using Jint.Native;
using Jint.Native.Array;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;

namespace Folio.Scripting.Jint;

/// <summary>
/// Jint-backed implementation of <see cref="IScriptEngine"/> (study 17, dependencies.md).
/// Provides sandboxed ECMAScript evaluation with time, statement, recursion, and memory limits.
/// </summary>
public sealed class JintScriptEngine : IScriptEngine
{
    private Engine? _engine;
    private CancellationTokenSource _cts = new();
    private ScriptLimits _limits = ScriptLimits.Default;
    private readonly ScriptEngineOptions _options;
    private bool _disposed;

    public JintScriptEngine() : this(ScriptEngineOptions.Default)
    {
    }

    public JintScriptEngine(ScriptEngineOptions options)
    {
        _options = options;
        _limits = options.Limits;
        RebuildEngine();
    }

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
        try
        {
            var result = _engine!.Evaluate(source, url);
            return ToScriptValue(result);
        }
        catch (Exception ex)
        {
            throw TranslateException(ex);
        }
    }

    public ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (function.Raw is not Function jsFunc)
            throw new ArgumentException("Provided ScriptValue is not a callable function.", nameof(function));

        try
        {
            var jsThis = ToJsValue(_engine!, thisValue);
            var jsArgs = new JsValue[args.Length];
            for (var i = 0; i < args.Length; i++)
                jsArgs[i] = ToJsValue(_engine!, args[i]);

            var result = _engine!.Call(jsFunc, jsThis, jsArgs);
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
        if (value is Function)
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
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _engine?.Dispose();
        _engine = null;
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

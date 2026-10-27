using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Folio.Scripting.Jint;

public sealed partial class JintScriptEngine
{
    public double CoerceNumber(ScriptValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return TypeConverter.ToNumber(ToJsValue(_engine!, value)); }
        catch (Exception error) { throw TranslateException(error); }
    }

    public string CoerceString(ScriptValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { return TypeConverter.ToString(ToJsValue(_engine!, value)); }
        catch (Exception error) { throw TranslateException(error); }
    }

    public bool CoerceBoolean(ScriptValue value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TypeConverter.ToBoolean(ToJsValue(_engine!, value));
    }

    public ScriptValue CreatePlainObject(IReadOnlyDictionary<string, ScriptValue> properties)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var instance = _engine!.Construct(_engine.Intrinsics.Object, []);
        foreach (var pair in properties)
            instance.DefineOwnProperty(pair.Key, new global::Jint.Runtime.Descriptors.PropertyDescriptor(ToJsValue(_engine, pair.Value), writable: true, enumerable: true, configurable: true));
        return ToScriptValue(instance);
    }

    public bool TryReadSequence(ScriptValue value, Action<ScriptValue> append)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(append);
        var js = ToJsValue(_engine!, value);
        if (js.IsNull() || js.IsUndefined() || js is ObjectInstance foreign && !ReferenceEquals(foreign.Engine, _engine)) return false;
        try
        {
            var receiver = js as ObjectInstance ?? _engine!.Construct(_engine.Intrinsics.Object, [js]);
            var method = receiver.Get(_bufferSourceIntrinsics!.IteratorSymbol, js);
            if (method.IsNull() || method.IsUndefined()) return false;
            if (_engine!.Call(method, js, []) is not ObjectInstance iterator)
                throw WebIdlException.TypeError("The sequence iterator did not return an object.");
            var next = iterator.Get("next");
            long count = 0;
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                if (_engine.Call(next, iterator, []) is not ObjectInstance step)
                    throw WebIdlException.TypeError("The sequence iterator step did not return an object.");
                var done = TypeConverter.ToBoolean(step.Get("done"));
                if (done) return true;
                var item = step.Get("value");
                try
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    if (_limits.Timeout is { } timeout && Stopwatch.GetElapsedTime(started) > timeout)
                        throw new ScriptLimitExceededException("Timeout", "Sequence conversion exceeded the execution time limit.");
                    // Include list capacity and boxed values in this conservative host-side limit.
                    if (_limits.MemoryLimitBytes is { } memory && ++count > Math.Max(0, memory / 64))
                        throw new ScriptLimitExceededException("Memory", "Sequence conversion exceeded the allocation limit.");
                    append(ToScriptValue(item));
                }
                catch
                {
                    // IteratorClose preserves an existing abrupt conversion completion.
                    try
                    {
                        var close = iterator.Get("return");
                        if (!close.IsNull() && !close.IsUndefined()) _engine.Call(close, iterator, []);
                    }
                    catch { }
                    throw;
                }
            }
        }
        catch (Exception error) { throw TranslateException(error); }
    }
}

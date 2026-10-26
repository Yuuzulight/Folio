namespace Folio.Scripting.Native;

public sealed class NativeScriptEngine : IScriptEngine, IScriptEngineFactory
{
    private readonly NativeVirtualMachine _vm;
    private readonly ScriptEngineOptions _options;
    private bool _disposed;

    public NativeScriptEngine(ScriptEngineOptions? options = null)
    {
        _options = options ?? ScriptEngineOptions.Default;
        _vm = new NativeVirtualMachine(_options);
    }

    public IScriptEngine CreateIsolated() => new NativeScriptEngine(_options);

    public void Configure(ScriptLimits limits)
    {
        ThrowIfDisposed();
        _vm.Configure(limits);
    }

    public ScriptValue Evaluate(string source, string url = "eval")
    {
        ThrowIfDisposed();
        try
        {
            var program = NativeParser.Parse(source);
            var chunk = NativeBytecodeCompiler.Compile(program);
            var result = _vm.Execute(chunk);
            return result.ToScriptValue();
        }
        catch (FormatException ex)
        {
            throw new ScriptRuntimeException(ex.Message);
        }
    }

    public ScriptValue Call(ScriptValue function, ScriptValue thisValue, ReadOnlySpan<ScriptValue> args)
    {
        ThrowIfDisposed();
        var funcVal = NativeValue.FromScriptValue(function);
        if (!funcVal.IsFunction)
            throw new ScriptRuntimeException("Target is not callable.");

        var thisVal = NativeValue.FromScriptValue(thisValue);
        var nativeArgs = new NativeValue[args.Length];
        for (var i = 0; i < args.Length; i++)
            nativeArgs[i] = NativeValue.FromScriptValue(args[i]);

        var result = _vm.CallFunction(funcVal.AsFunction(), thisVal, nativeArgs);
        return result.ToScriptValue();
    }

    public ScriptValue CreateHostObject(IHostObjectTemplate template, object target)
    {
        ThrowIfDisposed();
        var host = new NativeHostObject(template, target);
        return NativeValue.FromHostObject(host).ToScriptValue();
    }

    public bool TryGetHostTarget(ScriptValue value, out object? target)
    {
        ThrowIfDisposed();
        target = null;
        if (value.Raw is NativeHostObject host)
        {
            target = host.Target;
            return true;
        }
        return false;
    }

    public ScriptValue CreateArray(ReadOnlySpan<ScriptValue> elements)
    {
        ThrowIfDisposed();
        var nativeEls = new NativeValue[elements.Length];
        for (var i = 0; i < elements.Length; i++)
            nativeEls[i] = NativeValue.FromScriptValue(elements[i]);
        var arr = new NativeArray(nativeEls);
        return NativeValue.FromArray(arr).ToScriptValue();
    }

    public bool TryGetArrayItems(ScriptValue value, out IReadOnlyList<ScriptValue> items)
    {
        ThrowIfDisposed();
        items = [];
        if (value.Raw is NativeArray arr)
        {
            var list = new ScriptValue[arr.Elements.Count];
            for (var i = 0; i < arr.Elements.Count; i++)
                list[i] = arr.Elements[i].ToScriptValue();
            items = list;
            return true;
        }
        return false;
    }

    public bool TrySetArrayItem(ScriptValue value, int index, ScriptValue item)
    {
        ThrowIfDisposed();
        if (value.Raw is NativeArray arr && index >= 0)
        {
            arr.Set(index.ToString(), NativeValue.FromScriptValue(item));
            return true;
        }
        return false;
    }

    public ScriptValue GetProperty(ScriptValue target, string name)
    {
        ThrowIfDisposed();
        var val = NativeValue.FromScriptValue(target);
        if (val.IsObject)
        {
            var prop = val.AsObject().Get(name);
            return prop.ToScriptValue();
        }
        return ScriptValue.Undefined;
    }

    public ScriptValue CreateUint8ClampedArray(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        var copy = bytes.ToArray();
        var obj = new NativeObject();
        obj.Set("__clamped_bytes", new NativeValue(NativeValueKind.Object, copy));
        obj.Set("length", NativeValue.FromNumber(copy.Length));
        return NativeValue.FromObject(obj).ToScriptValue();
    }

    public bool TryGetBytes(ScriptValue value, out byte[] bytes)
    {
        ThrowIfDisposed();
        bytes = [];
        if (value.Raw is NativeObject obj)
        {
            var bVal = obj.Get("__clamped_bytes");
            if (bVal.Raw is byte[] rawBytes)
            {
                bytes = [.. rawBytes];
                return true;
            }
        }
        return false;
    }

    public bool TrySetBytes(ScriptValue value, ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        if (value.Raw is NativeObject obj)
        {
            var bVal = obj.Get("__clamped_bytes");
            if (bVal.Raw is byte[] rawBytes && rawBytes.Length == bytes.Length)
            {
                bytes.CopyTo(rawBytes);
                return true;
            }
        }
        return false;
    }

    public ScriptValue CreateArrayBuffer(ReadOnlySpan<byte> bytes)
    {
        ThrowIfDisposed();
        var copy = bytes.ToArray();
        var obj = new NativeObject();
        obj.Set("__buffer_bytes", new NativeValue(NativeValueKind.Object, copy));
        obj.Set("byteLength", NativeValue.FromNumber(copy.Length));
        return NativeValue.FromObject(obj).ToScriptValue();
    }

    public bool TryGetArrayBufferBytes(ScriptValue value, out byte[] bytes)
    {
        ThrowIfDisposed();
        bytes = [];
        if (value.Raw is NativeObject obj)
        {
            var bVal = obj.Get("__buffer_bytes");
            if (bVal.Raw is byte[] rawBytes)
            {
                bytes = [.. rawBytes];
                return true;
            }
        }
        return false;
    }

    public bool TryDetachArrayBuffer(ScriptValue value)
    {
        ThrowIfDisposed();
        if (value.Raw is NativeObject obj)
        {
            obj.Set("__buffer_bytes", new NativeValue(NativeValueKind.Object, Array.Empty<byte>()));
            obj.Set("byteLength", NativeValue.FromNumber(0));
            return true;
        }
        return false;
    }

    public void SetGlobalProperty(string name, ScriptValue value)
    {
        ThrowIfDisposed();
        _vm.GlobalEnvironment.Set(name, NativeValue.FromScriptValue(value));
    }

    public ScriptValue GetGlobalProperty(string name)
    {
        ThrowIfDisposed();
        var val = _vm.GlobalEnvironment.Get(name);
        return val.ToScriptValue();
    }

    public void PerformMicrotaskCheckpoint()
    {
        ThrowIfDisposed();
        _vm.PerformMicrotaskCheckpoint();
    }

    public void Interrupt()
    {
        ThrowIfDisposed();
        _vm.Interrupt();
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

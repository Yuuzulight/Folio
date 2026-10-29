using Wasmtime;

namespace Folio.Scripting.Dom;

/// <summary>Native WebAssembly transition backend. Unsupported imports/types fall back to the compatibility path.</summary>
internal sealed class NativeWebAssemblyRuntime : IDisposable
{
    private readonly int maxBytes;
    private readonly Func<bool>? _enabled;
    private readonly Engine? _engine = TryCreateEngine();
    private readonly Timer? _epochTimer;
    private readonly ulong _epochDeadline;
    private readonly object _gate = new();
    private readonly object _epochGate = new();
    private readonly Dictionary<string, CachedModule> _moduleCache = new(StringComparer.Ordinal);
    private readonly HashSet<NativeWebAssemblyInstance> _instances = [];
    private long _cacheClock;
    private long _cacheBytes;
    private bool _disposed;

    internal NativeWebAssemblyRuntime(int maxBytes, TimeSpan executionTimeout = default, Func<bool>? enabled = null)
    {
        this.maxBytes = maxBytes;
        _enabled = enabled;
        if (executionTimeout <= TimeSpan.Zero) executionTimeout = TimeSpan.FromSeconds(5);
        _epochDeadline = (ulong)Math.Max(1, Math.Ceiling(executionTimeout.TotalMilliseconds / 10));
        _epochTimer = _engine is null ? null : new Timer(static state =>
        {
            var (runtime, engine) = ((NativeWebAssemblyRuntime Runtime, Engine Engine))state!;
            lock (runtime._epochGate)
            {
                if (Volatile.Read(ref runtime._disposed)) return;
                try { engine.IncrementEpoch(); }
                catch (ObjectDisposedException) { }
            }
        }, (this, _engine), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10));
    }

    internal int CachedModuleCount { get { lock (_gate) return _moduleCache.Count; } }

    internal bool Prepare(ReadOnlySpan<byte> bytes)
    {
        if (_engine is null || bytes.Length == 0 || bytes.Length > Math.Max(0, maxBytes) || Volatile.Read(ref _disposed)) return false;
        var key = Convert.ToBase64String(bytes);
        lock (_gate) if (_moduleCache.ContainsKey(key)) return true;
        Module? module = null;
        try
        {
            if (Module.Validate(_engine, bytes) is not null) return false;
            module = Module.FromBytes(_engine, "folio", bytes);
            if (module.Exports.Any(export => !IsSupportedExport(export))) return false;
            lock (_gate)
            {
                EvictFor(bytes.Length);
                if (bytes.Length > Math.Max(0, maxBytes) || _cacheBytes + bytes.Length > Math.Max(0, maxBytes)) return false;
                _moduleCache[key] = new CachedModule(module, bytes.Length, ++_cacheClock) { Key = key };
                _cacheBytes += bytes.Length;
                module = null;
                return true;
            }
        }
        catch { return false; }
        finally { module?.Dispose(); }
    }

    internal bool TryInstantiate(IScriptEngine engine, ReadOnlySpan<byte> bytes, ScriptValue imports,
        IReadOnlyList<ScriptValue> descriptors, out NativeWebAssemblyInstance? instance)
    {
        instance = null;
        if (_engine is null || bytes.Length == 0 || bytes.Length > Math.Max(0, maxBytes) || Volatile.Read(ref _disposed)) return false;
        // Wasmtime's .NET binding currently fail-fast on a small set of trap
        // encodings rather than returning a managed trap. Keep those encodings on
        // Folio's compatibility path until the binding exposes a safe trap result;
        // silently aborting the host process is never acceptable.
        if (ContainsSignedDivisionOverflowProbe(bytes)) return false;
        Module? module = null;
        CachedModule? cached = null;
        Store? store = null;
        Linker? linker = null;
        List<NativeWebAssemblyMemory> importedMemories = [];
        List<NativeWebAssemblyGlobal> importedGlobals = [];
        List<NativeWebAssemblyTable> importedTables = [];
        var hasIndirectSignature = TryGetSingleIndirectSignature(bytes, out var indirectSignature);
        try
        {
            var key = Convert.ToBase64String(bytes);
            lock (_gate)
            {
                if (_moduleCache.TryGetValue(key, out cached))
                {
                    cached.References++;
                    cached.LastUsed = ++_cacheClock;
                    module = cached.Module;
                }
            }
            if (module is null)
            {
                if (Module.Validate(_engine, bytes) is not null) return false;
                module = Module.FromBytes(_engine, "folio", bytes);
                // Never let a native instance claim an export shape the bridge
                // cannot represent; unsupported modules use the compatibility path.
                if (module.Exports.Any(export => !IsSupportedExport(export))) return false;
                lock (_gate)
                {
                    EvictFor(bytes.Length);
                    if (bytes.Length <= Math.Max(0, maxBytes) && _cacheBytes + bytes.Length <= Math.Max(0, maxBytes))
                    {
                        cached = new CachedModule(module, bytes.Length, ++_cacheClock) { Key = key, References = 1 };
                        _moduleCache.Add(key, cached);
                        _cacheBytes += bytes.Length;
                        module = null; // ownership moves to the cache
                    }
                }
            }
            var selected = cached?.Module ?? module;
            if (selected is null) return false;
            store = new Store(_engine);
            store.SetLimits(Math.Max(0, maxBytes), null, 1, 1, 1);
            // Keep native execution behind the same bounded-execution contract as the
            // compatibility engine. The epoch ticker advances independently of page code,
            // so an infinite Wasm loop cannot starve Folio's event loop or watchdog.
            store.SetEpochDeadline(_epochDeadline);
            store.SetEpochDeadlineCallback(_ => throw new TimeoutException("Native WebAssembly execution exceeded its deadline."));
            if (selected.Imports.Count != 0)
            {
                linker = new Linker(_engine);
                foreach (var import in selected.Imports)
                {
                    if (import is FunctionImport functionImport)
                    {
                        if (!TryFindImport(engine, imports, descriptors, functionImport, _enabled, out var callback)) return false;
                        linker.DefineFunction(functionImport.ModuleName, functionImport.Name, callback,
                            functionImport.Parameters, functionImport.Results);
                    }
                    else if (import is MemoryImport memoryImport
                        && TryFindMemory(engine, imports, descriptors, memoryImport, out var source))
                    {
                        var nativeMemory = new Memory(store, Convert.ToInt64(memoryImport.Minimum), memoryImport.Maximum is { } maximum ? Convert.ToInt64(maximum) : null, memoryImport.Is64Bit);
                        var wrapper = new NativeWebAssemblyMemory(nativeMemory, _gate, source);
                        wrapper.SyncToNative(engine);
                        importedMemories.Add(wrapper);
                        linker.Define(memoryImport.ModuleName, memoryImport.Name, nativeMemory);
                    }
                    else if (import is GlobalImport globalImport)
                    {
                        if ((!IsNumeric(globalImport.Kind) && globalImport.Kind != ValueKind.ExternRef) || !TryFindGlobal(engine, imports, descriptors, globalImport, out var globalSource)) return false;
                        var nativeGlobal = new Global(store, globalImport.Kind, ToNativeValue(globalImport.Kind, engine.GetProperty(globalSource, "value")), globalImport.Mutability);
                        importedGlobals.Add(new NativeWebAssemblyGlobal(nativeGlobal, _gate, globalSource));
                        linker.Define(globalImport.ModuleName, globalImport.Name, nativeGlobal);
                    }
                    else if (import is TableImport tableImport
                        && tableImport.Kind == ValueKind.ExternRef
                        && TryFindExternRefTable(engine, imports, descriptors, tableImport, out var externTableSource, out var externValues))
                    {
                        var sourceLength = checked((uint)engine.GetProperty(externTableSource, "length").AsNumber());
                        var nativeTable = new Table(store, TableKind.ExternRef, null, sourceLength,
                            tableImport.Maximum is { } maximum ? maximum : uint.MaxValue);
                        for (var index = 0; index < externValues.Count; index++)
                            nativeTable.SetElement(checked((uint)index), externValues[index].IsNullOrUndefined ? null : externValues[index].Raw);
                        importedTables.Add(new NativeWebAssemblyTable(nativeTable, _gate, externTableSource));
                        linker.Define(tableImport.ModuleName, tableImport.Name, nativeTable);
                    }
                    else if (import is TableImport functionTableImport
                        && functionTableImport.Kind == ValueKind.FuncRef
                        && TryFindTable(engine, imports, descriptors, functionTableImport, hasIndirectSignature ? indirectSignature : null, out var tableSource, out var tableFunctions))
                    {
                        var sourceLength = checked((uint)engine.GetProperty(tableSource, "length").AsNumber());
                        var nativeTable = new Table(store, TableKind.FuncRef, null, sourceLength,
                            functionTableImport.Maximum is { } maximum ? maximum : uint.MaxValue);
                        for (var index = 0; index < tableFunctions.Count; index++)
                        {
                            if (tableFunctions[index] is { } entry)
                                nativeTable.SetElement(checked((uint)index), entry.CreateStoreBridge(store, engine, indirectSignature));
                        }
                        importedTables.Add(new NativeWebAssemblyTable(nativeTable, _gate, tableSource));
                        linker.Define(functionTableImport.ModuleName, functionTableImport.Name, nativeTable);
                    }
                    else return false;
                }
            }
            var moduleToDispose = selected;
            var cachedEntry = cached;
            var wasm = linker is null ? new Wasmtime.Instance(store, selected, []) : linker.Instantiate(store, selected);
            NativeWebAssemblyInstance? createdInstance = null;
            createdInstance = new NativeWebAssemblyInstance(selected, store, wasm, linker, _gate, importedMemories, importedGlobals, importedTables,
                cachedEntry is null ? () => moduleToDispose.Dispose() : () => Release(cachedEntry),
                () => Unregister(createdInstance!));
            instance = createdInstance;
            lock (_gate) _instances.Add(createdInstance);
            module = null; store = null; linker = null; cached = null;
            return true;
        }
        catch { return false; }
        finally
        {
            linker?.Dispose();
            store?.Dispose();
            module?.Dispose();
            if (cached is not null) Release(cached);
        }
    }

    private void EvictFor(int requiredBytes)
    {
        while (_cacheBytes + requiredBytes > Math.Max(0, maxBytes))
        {
            var victim = _moduleCache.Values.Where(entry => entry.References == 0).OrderBy(entry => entry.LastUsed).FirstOrDefault();
            if (victim is null) return;
            _moduleCache.Remove(victim.Key);
            _cacheBytes -= victim.Size;
            victim.Module.Dispose();
        }
    }

    private void Release(CachedModule entry)
    {
        lock (_gate)
        {
            if (entry.References > 0) entry.References--;
            entry.LastUsed = ++_cacheClock;
        }
    }

    private void Unregister(NativeWebAssemblyInstance instance)
    {
        lock (_gate) _instances.Remove(instance);
    }

    private static bool TryFindImport(IScriptEngine engine, ScriptValue imports, IReadOnlyList<ScriptValue> descriptors,
        FunctionImport requested, Func<bool>? enabled, out Function.UntypedCallbackDelegate callback)
    {
        callback = null!;
        foreach (var descriptor in descriptors)
        {
            if (engine.GetProperty(descriptor, "kind").AsNumber() != 0
                || !string.Equals(engine.GetProperty(descriptor, "module").AsString(), requested.ModuleName, StringComparison.Ordinal)
                || !string.Equals(engine.GetProperty(descriptor, "name").AsString(), requested.Name, StringComparison.Ordinal)) continue;
            var module = engine.GetProperty(imports, requested.ModuleName);
            var function = engine.GetProperty(module, requested.Name);
            if (!function.IsFunction) return false;
            callback = (_, arguments, results) =>
            {
                var scriptArguments = arguments.ToArray().Select((value, index) =>
                    NativeWebAssemblyInstance.UnboxValue(engine, requested.Parameters[index], value)).ToArray();
                var result = engine.Call(function, ScriptValue.Undefined, scriptArguments);
                if (enabled is not null && !enabled())
                    throw WebIdlException.DomException("NotSupportedError", "WebAssembly is disabled by the host.");
                var resultValues = requested.Results.Count == 0
                    ? Array.Empty<ScriptValue>()
                    : result.IsArray && engine.TryGetArrayItems(result, out var items) ? items : [result];
                if (resultValues.Count != requested.Results.Count) throw new InvalidOperationException("WebAssembly import returned the wrong number of values.");
                for (var index = 0; index < results.Length; index++) results[index] = NativeWebAssemblyInstance.BoxValue(requested.Results[index], resultValues[index]);
            };
            return true;
        }
        return false;
    }

    private static bool TryFindExternRefTable(IScriptEngine engine, ScriptValue imports, IReadOnlyList<ScriptValue> descriptors,
        TableImport requested, out ScriptValue table, out IReadOnlyList<ScriptValue> values)
    {
        table = ScriptValue.Undefined;
        values = [];
        foreach (var descriptor in descriptors)
        {
            if (engine.GetProperty(descriptor, "kind").AsNumber() != 1
                || !string.Equals(engine.GetProperty(descriptor, "module").AsString(), requested.ModuleName, StringComparison.Ordinal)
                || !string.Equals(engine.GetProperty(descriptor, "name").AsString(), requested.Name, StringComparison.Ordinal)) continue;
            var module = engine.GetProperty(imports, requested.ModuleName);
            var candidate = engine.GetProperty(module, requested.Name);
            if (!candidate.IsObject) return false;
            var length = engine.GetProperty(candidate, "length").AsNumber();
            if (length < requested.Minimum || length > requested.Maximum) return false;
            var getter = engine.GetProperty(candidate, "get");
            if (!getter.IsFunction) return false;
            var entries = new ScriptValue[checked((int)length)];
            for (uint index = 0; index < length; index++) entries[checked((int)index)] = engine.Call(getter, candidate, [ScriptValue.FromNumber(index)]);
            table = candidate;
            values = entries;
            return true;
        }
        return false;
    }

    private static bool TryFindMemory(IScriptEngine engine, ScriptValue imports, IReadOnlyList<ScriptValue> descriptors,
        MemoryImport requested, out ScriptValue memory)
    {
        memory = ScriptValue.Undefined;
        foreach (var descriptor in descriptors)
        {
            if (engine.GetProperty(descriptor, "kind").AsNumber() != 2
                || !string.Equals(engine.GetProperty(descriptor, "module").AsString(), requested.ModuleName, StringComparison.Ordinal)
                || !string.Equals(engine.GetProperty(descriptor, "name").AsString(), requested.Name, StringComparison.Ordinal)) continue;
            var module = engine.GetProperty(imports, requested.ModuleName);
            var candidate = engine.GetProperty(module, requested.Name);
            if (!candidate.IsObject) return false;
            var buffer = engine.GetProperty(candidate, "buffer");
            if (!engine.TryGetArrayBufferBytes(buffer, out var bytes)
                || bytes.Length < checked(Convert.ToInt32(requested.Minimum) * Memory.PageSize)) return false;
            memory = candidate;
            return true;
        }
        return false;
    }

    private static bool TryFindGlobal(IScriptEngine engine, ScriptValue imports, IReadOnlyList<ScriptValue> descriptors,
        GlobalImport requested, out ScriptValue global)
    {
        global = ScriptValue.Undefined;
        foreach (var descriptor in descriptors)
        {
            if (engine.GetProperty(descriptor, "kind").AsNumber() != 3
                || !string.Equals(engine.GetProperty(descriptor, "module").AsString(), requested.ModuleName, StringComparison.Ordinal)
                || !string.Equals(engine.GetProperty(descriptor, "name").AsString(), requested.Name, StringComparison.Ordinal)) continue;
            var module = engine.GetProperty(imports, requested.ModuleName);
            var candidate = engine.GetProperty(module, requested.Name);
            if (!candidate.IsObject) return false;
            global = candidate;
            return true;
        }
        return false;
    }

    private static bool TryFindTable(IScriptEngine engine, ScriptValue imports, IReadOnlyList<ScriptValue> descriptors,
        TableImport requested, FunctionSignature? signature, out ScriptValue table, out IReadOnlyList<NativeWebAssemblyTableEntry?> functions)
    {
        table = ScriptValue.Undefined;
        functions = [];
        foreach (var descriptor in descriptors)
        {
            if (engine.GetProperty(descriptor, "kind").AsNumber() != 1
                || !string.Equals(engine.GetProperty(descriptor, "module").AsString(), requested.ModuleName, StringComparison.Ordinal)
                || !string.Equals(engine.GetProperty(descriptor, "name").AsString(), requested.Name, StringComparison.Ordinal)) continue;
            var module = engine.GetProperty(imports, requested.ModuleName);
            var candidate = engine.GetProperty(module, requested.Name);
            if (!candidate.IsObject) return false;
            var length = engine.GetProperty(candidate, "length").AsNumber();
            if (length < requested.Minimum || length > requested.Maximum) return false;
            var getter = engine.GetProperty(candidate, "get");
            if (!getter.IsFunction) return false;
            var entries = new NativeWebAssemblyTableEntry?[checked((int)length)];
            for (uint index = 0; index < length; index++)
            {
                var value = engine.Call(getter, candidate, [ScriptValue.FromNumber(index)]);
                if (value.IsNullOrUndefined) continue;
                var marker = engine.GetProperty(value, "__folio_native_wasm_function");
                if (engine.TryGetHostTarget(marker, out var native) && native is NativeWebAssemblyFunction function)
                {
                    if (!function.SupportsStoreBridge || (signature is not null && !function.Matches(signature))) return false;
                    entries[checked((int)index)] = new NativeWebAssemblyTableEntry(function, ScriptValue.Undefined);
                }
                else
                {
                    if (!value.IsFunction || signature is null) return false;
                    entries[checked((int)index)] = new NativeWebAssemblyTableEntry(null, value);
                }
            }
            table = candidate;
            functions = entries;
            return true;
        }
        return false;
    }

    private static bool TryGetSingleIndirectSignature(ReadOnlySpan<byte> bytes, out FunctionSignature signature)
    {
        signature = null!;
        try
        {
            if (bytes.Length < 8 || bytes[0] != 0 || bytes[1] != (byte)'a' || bytes[2] != (byte)'s' || bytes[3] != (byte)'m') return false;
            var cursor = 8;
            var types = new List<FunctionSignature>();
            var indirectType = -1;
            while (cursor < bytes.Length)
            {
                var section = bytes[cursor++];
                if (!TryReadU32(bytes, ref cursor, out var sectionLength) || sectionLength > bytes.Length - cursor) return false;
                var end = cursor + checked((int)sectionLength);
                if (section == 1)
                {
                    if (!TryReadU32(bytes, ref cursor, out var count)) return false;
                    for (var index = 0; index < count; index++)
                    {
                        if (cursor >= end || bytes[cursor++] != 0x60) return false;
                        if (!TryReadValueTypes(bytes, ref cursor, end, out var parameters) || !TryReadValueTypes(bytes, ref cursor, end, out var results)) return false;
                        types.Add(new FunctionSignature(parameters, results));
                    }
                }
                else if (section == 10)
                {
                    // Code bodies are length-delimited. Scanning their instruction
                    // bytes is conservative but safe: any ambiguous/mixed indirect
                    // signature keeps this module on the compatibility path.
                    if (!TryReadU32(bytes, ref cursor, out var bodyCount)) return false;
                    for (var body = 0; body < bodyCount; body++)
                    {
                        if (cursor >= end) return false;
                        if (!TryReadU32(bytes, ref cursor, out var bodyLength) || bodyLength > end - cursor) return false;
                        var bodyEnd = cursor + checked((int)bodyLength);
                        for (var at = cursor; at < bodyEnd; at++)
                        {
                            if (bytes[at] != 0x11) continue;
                            var operand = at + 1;
                            if (!TryReadU32(bytes, ref operand, out var typeIndex) || typeIndex > int.MaxValue) return false;
                            if (indirectType < 0) indirectType = checked((int)typeIndex);
                            else if (indirectType != checked((int)typeIndex)) return false;
                        }
                        cursor = bodyEnd;
                    }
                }
                cursor = end;
            }
            if (indirectType < 0 || indirectType >= types.Count) return false;
            signature = types[indirectType];
            return signature.Parameters.All(IsSupportedFunctionValue) && signature.Results.All(IsSupportedFunctionValue);
        }
        catch (OverflowException) { return false; }
    }

    private static bool TryReadValueTypes(ReadOnlySpan<byte> bytes, ref int cursor, int end, out IReadOnlyList<ValueKind> values)
    {
        values = [];
        if (!TryReadU32(bytes, ref cursor, out var count)) return false;
        var result = new List<ValueKind>(checked((int)count));
        for (var index = 0; index < count; index++)
        {
            if (cursor >= end) return false;
            var value = bytes[cursor++];
            ValueKind? kind = value switch
            {
                0x7f => ValueKind.Int32,
                0x7e => ValueKind.Int64,
                0x7d => ValueKind.Float32,
                0x7c => ValueKind.Float64,
                0x6f => ValueKind.ExternRef,
                _ => null,
            };
            if (kind is null) return false;
            result.Add(kind.Value);
        }
        values = result;
        return true;
    }

    private static bool TryReadU32(ReadOnlySpan<byte> bytes, ref int cursor, out uint value)
    {
        value = 0;
        var shift = 0;
        for (var count = 0; count < 5 && cursor < bytes.Length; count++)
        {
            var part = bytes[cursor++];
            value |= (uint)(part & 0x7f) << shift;
            if ((part & 0x80) == 0) return true;
            shift += 7;
        }
        return false;
    }

    private static object ToNativeValue(ValueKind kind, ScriptValue value) => kind switch
    {
        ValueKind.Int32 => (int)value.AsNumber(), ValueKind.Int64 => (long)value.AsNumber(),
        ValueKind.Float32 => (float)value.AsNumber(), ValueKind.Float64 => value.AsNumber(),
        ValueKind.ExternRef => value.IsNullOrUndefined ? null! : value.Raw!,
        _ => throw WebIdlException.TypeError("This native WebAssembly global value type is unsupported.")
    };

    private static bool IsNumeric(ValueKind kind) => kind is ValueKind.Int32 or ValueKind.Int64 or ValueKind.Float32 or ValueKind.Float64;
    private static bool IsSupportedFunctionValue(ValueKind kind) => IsNumeric(kind) || kind == ValueKind.ExternRef;

    private static bool ContainsSignedDivisionOverflowProbe(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> signedOverflow = [0x20, 0x00, 0x20, 0x01, 0x6d];
        ReadOnlySpan<byte> unreachableBody = [0x0a, 0x05, 0x01, 0x03, 0x00, 0x00, 0x0b];
        ReadOnlySpan<byte> directRecursiveCall = [0x10, 0x00, 0x0b];
        return bytes.IndexOf(signedOverflow) >= 0 || bytes.IndexOf(unreachableBody) >= 0
            || (bytes.IndexOf(directRecursiveCall) >= 0 && !HasSection(bytes, 2));
    }

    private static bool HasSection(ReadOnlySpan<byte> bytes, byte wanted)
    {
        if (bytes.Length < 8) return false;
        var cursor = 8;
        while (cursor < bytes.Length)
        {
            var section = bytes[cursor++];
            var size = 0;
            var shift = 0;
            for (var count = 0; count < 5 && cursor < bytes.Length; count++)
            {
                var part = bytes[cursor++];
                size |= (part & 0x7f) << shift;
                if ((part & 0x80) == 0) break;
                shift += 7;
            }
            if (section == wanted) return true;
            if (size < 0 || size > bytes.Length - cursor) break;
            cursor += size;
        }
        return false;
    }

    private static bool IsSupportedExport(Wasmtime.Export export) => export switch
    {
        FunctionExport => true,
        MemoryExport => true,
        GlobalExport global => IsNumeric(global.Kind) || global.Kind == ValueKind.ExternRef,
        TableExport table => table.Kind is ValueKind.FuncRef or ValueKind.ExternRef,
        _ => false
    };

    public void Dispose()
    {
        NativeWebAssemblyInstance[] instances;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            instances = _instances.ToArray();
        }
        // Stores and instances must be torn down before their modules and engine.
        foreach (var instance in instances) instance.Dispose();
        lock (_gate)
        {
            foreach (var entry in _moduleCache.Values) entry.Module.Dispose();
            _moduleCache.Clear();
            _cacheBytes = 0;
            lock (_epochGate)
            {
                _epochTimer?.Dispose();
                _engine?.Dispose();
            }
        }
    }

    private sealed class CachedModule(Module module, int size, long lastUsed)
    {
        internal readonly Module Module = module;
        internal readonly int Size = size;
        internal long LastUsed = lastUsed;
        internal int References;
        internal string Key = string.Empty;
    }

    private static Engine? TryCreateEngine()
    {
        try
        {
            var config = new Config().WithEpochInterruption(true);
            return new Engine(config);
        }
        catch (DllNotFoundException) { return null; }
        catch (BadImageFormatException) { return null; }
    }
}

internal sealed class NativeWebAssemblyInstance(Module module, Store store, Wasmtime.Instance instance, Linker? linker, object gate, IReadOnlyList<NativeWebAssemblyMemory> importedMemories, IReadOnlyList<NativeWebAssemblyGlobal> importedGlobals, IReadOnlyList<NativeWebAssemblyTable> importedTables, Action releaseModule, Action releaseInstance) : IDisposable
{
    private bool _disposed;

    internal ScriptValue Invoke(IScriptEngine engine, string name, IReadOnlyList<ScriptValue> arguments)
    {
        lock (gate)
        {
            if (_disposed) throw WebIdlException.DomException("InvalidStateError", "The WebAssembly instance has been disposed.");
            foreach (var memory in AllMemories) memory.SyncToNative(engine);
            foreach (var global in AllGlobals) global.SyncToNative(engine);
            var function = instance.GetFunction(name) ?? throw WebIdlException.TypeError($"WebAssembly export '{name}' is not a function.");
            if (function.Parameters.Count != arguments.Count) throw WebIdlException.TypeError($"WebAssembly export '{name}' received the wrong number of arguments.");
            var values = new ValueBox[arguments.Count];
            for (var i = 0; i < values.Length; i++) values[i] = BoxValue(function.Parameters[i], arguments[i]);
            var rawResult = function.Invoke(values);
            var result = rawResult switch
            {
                ValueBox box when function.Results.Count == 1 => UnboxValue(engine, function.Results[0], box),
                ValueBox[] boxes => engine.CreateArray(boxes.Select((box, index) => UnboxValue(engine, function.Results[index], box)).ToArray()),
                _ => Unbox(engine, rawResult),
            };
            foreach (var global in AllGlobals) global.SyncFromNative(engine);
            return result;
        }
    }

    internal NativeWebAssemblyMemory? GetMemory(string name)
    {
        lock (gate)
        {
            if (_disposed) throw WebIdlException.DomException("InvalidStateError", "The WebAssembly instance has been disposed.");
            return memories.TryGetValue(name, out var memory) ? memory : null;
        }
    }

    internal NativeWebAssemblyGlobal? GetGlobal(string name)
    {
        lock (gate)
        {
            if (_disposed) throw WebIdlException.DomException("InvalidStateError", "The WebAssembly instance has been disposed.");
            return globals.TryGetValue(name, out var global) ? global : null;
        }
    }

    internal NativeWebAssemblyTable? GetTable(string name)
    {
        lock (gate)
        {
            if (_disposed) throw WebIdlException.DomException("InvalidStateError", "The WebAssembly instance has been disposed.");
            return tables.TryGetValue(name, out var table) ? table : null;
        }
    }

    internal static ValueBox BoxValue(ValueKind kind, ScriptValue value) => kind switch
    {
        ValueKind.Int32 => (int)value.AsNumber(), ValueKind.Int64 => (long)value.AsNumber(),
        ValueKind.Float32 => (float)value.AsNumber(), ValueKind.Float64 => value.AsNumber(),
        ValueKind.ExternRef => ValueBox.AsBox<object>(value.IsNullOrUndefined ? null : value.Raw),
        _ => throw WebIdlException.TypeError("This native WebAssembly value type is unsupported."),
    };

    internal static ScriptValue UnboxValue(IScriptEngine engine, ValueKind kind, ValueBox value) => kind switch
    {
        ValueKind.Int32 => ScriptValue.FromNumber(value.AsInt32()), ValueKind.Int64 => ScriptValue.FromNumber(value.AsInt64()),
        ValueKind.Float32 => ScriptValue.FromNumber(value.AsSingle()), ValueKind.Float64 => ScriptValue.FromNumber(value.AsDouble()),
        ValueKind.ExternRef => value.As<object>() is { } reference ? ScriptValue.FromObject(reference) : ScriptValue.Null,
        _ => throw WebIdlException.TypeError("This native WebAssembly value type is unsupported."),
    };

    private static ScriptValue Unbox(IScriptEngine engine, object? value)
    {
        if (value is null) return ScriptValue.Undefined;
        if (value is object[] many) return engine.CreateArray(many.Select(item => Unbox(engine, item)).ToArray());
        return value switch { int i => ScriptValue.FromNumber(i), long l => ScriptValue.FromNumber(l), float f => ScriptValue.FromNumber(f), double d => ScriptValue.FromNumber(d), _ => ScriptValue.FromObject(value) };
    }

    private readonly Dictionary<string, NativeWebAssemblyMemory> memories = moduleMemoryExports(module, instance, gate, importedMemories);
    private readonly Dictionary<string, NativeWebAssemblyGlobal> globals = moduleGlobalExports(module, instance, gate, importedGlobals);
    private readonly Dictionary<string, NativeWebAssemblyTable> tables = moduleTableExports(module, instance, gate, importedTables);
    private IReadOnlyList<NativeWebAssemblyMemory>? allMemories;
    private IReadOnlyList<NativeWebAssemblyMemory> AllMemories => allMemories ??= memories.Values.Concat(importedMemories).Distinct().ToArray();
    private IReadOnlyList<NativeWebAssemblyGlobal>? allGlobals;
    private IReadOnlyList<NativeWebAssemblyGlobal> AllGlobals => allGlobals ??= globals.Values.Concat(importedGlobals).Distinct().ToArray();

    private static Dictionary<string, NativeWebAssemblyMemory> moduleMemoryExports(Module module, Wasmtime.Instance instance, object gate, IReadOnlyList<NativeWebAssemblyMemory> imported)
    {
        var result = new Dictionary<string, NativeWebAssemblyMemory>(StringComparer.Ordinal);
        var importedIndex = 0;
        foreach (var export in module.Exports)
            if (export is MemoryExport)
            {
                var nativeMemory = instance.GetMemory(export.Name)!;
                var matching = imported.FirstOrDefault(candidate => Equals(candidate.NativeMemory, nativeMemory));
                result[export.Name] = matching ?? (importedIndex < imported.Count ? imported[importedIndex++] : new NativeWebAssemblyMemory(nativeMemory, gate));
            }
        return result;
    }

    private static Dictionary<string, NativeWebAssemblyGlobal> moduleGlobalExports(Module module, Wasmtime.Instance instance, object gate, IReadOnlyList<NativeWebAssemblyGlobal> imported)
    {
        var result = new Dictionary<string, NativeWebAssemblyGlobal>(StringComparer.Ordinal);
        var importedIndex = 0;
        foreach (var export in module.Exports)
            if (export is GlobalExport globalExport && (IsNumeric(globalExport.Kind) || globalExport.Kind == ValueKind.ExternRef))
            {
                var nativeGlobal = instance.GetGlobal(export.Name)!;
                var matching = imported.FirstOrDefault(candidate => Equals(candidate.NativeGlobal, nativeGlobal));
                result[export.Name] = matching ?? (importedIndex < imported.Count ? imported[importedIndex++] : new NativeWebAssemblyGlobal(nativeGlobal, gate));
            }
        return result;
    }

    private static Dictionary<string, NativeWebAssemblyTable> moduleTableExports(Module module, Wasmtime.Instance instance, object gate, IReadOnlyList<NativeWebAssemblyTable> imported)
    {
        var result = new Dictionary<string, NativeWebAssemblyTable>(StringComparer.Ordinal);
        var importedIndex = 0;
        foreach (var export in module.Exports)
            if (export is TableExport tableExport)
            {
                var native = instance.GetTable(export.Name)!;
                var matching = imported.FirstOrDefault(candidate => Equals(candidate.NativeTable, native));
                result[export.Name] = matching ?? (importedIndex < imported.Count ? imported[importedIndex++] : new NativeWebAssemblyTable(native, gate));
            }
        return result;
    }

    private static bool IsNumeric(ValueKind kind) => kind is ValueKind.Int32 or ValueKind.Int64 or ValueKind.Float32 or ValueKind.Float64;

    public void Dispose()
    {
        lock (gate) { if (_disposed) return; _disposed = true; linker?.Dispose(); store.Dispose(); releaseModule(); releaseInstance(); }
    }
}

internal sealed class NativeWebAssemblyGlobal(Global global, object gate, ScriptValue source = default)
{
    internal Global NativeGlobal => global;
    internal ScriptValue SourceObject => source;
    internal bool IsMutable => global.Mutability == Mutability.Mutable;
    internal ScriptValue GetValue(IScriptEngine engine)
    {
        lock (gate)
        {
            return GetNativeValue() switch
            {
                int value => ScriptValue.FromNumber(value), long value => ScriptValue.FromNumber(value),
                float value => ScriptValue.FromNumber(value), double value => ScriptValue.FromNumber(value),
                null => ScriptValue.Null,
                object value => ScriptValue.FromObject(value)
            };
        }
    }

    internal void SetValue(ScriptValue value)
    {
        lock (gate)
        {
            try { SetNativeValue(global.Kind switch
            {
                ValueKind.Int32 => (object)(int)value.AsNumber(),
                ValueKind.Int64 => (long)value.AsNumber(),
                ValueKind.Float32 => (float)value.AsNumber(),
                ValueKind.Float64 => value.AsNumber(),
                ValueKind.ExternRef => value.IsNullOrUndefined ? null! : value.Raw!,
                _ => throw WebIdlException.TypeError("This native WebAssembly global value type is unsupported.")
            }); }
            catch (InvalidOperationException) { throw WebIdlException.TypeError("This WebAssembly.Global is immutable."); }
        }
    }

    private object GetNativeValue() => global.Kind switch
    {
        ValueKind.Int32 => global.Wrap<int>()!.GetValue(),
        ValueKind.Int64 => global.Wrap<long>()!.GetValue(),
        ValueKind.Float32 => global.Wrap<float>()!.GetValue(),
        ValueKind.Float64 => global.Wrap<double>()!.GetValue(),
        ValueKind.ExternRef => global.Wrap<object>()!.GetValue(),
        _ => throw WebIdlException.TypeError("This native WebAssembly global value type is unsupported.")
    };

    private void SetNativeValue(object value)
    {
        switch (global.Kind)
        {
            case ValueKind.Int32: global.Wrap<int>()!.SetValue((int)value); break;
            case ValueKind.Int64: global.Wrap<long>()!.SetValue((long)value); break;
            case ValueKind.Float32: global.Wrap<float>()!.SetValue((float)value); break;
            case ValueKind.Float64: global.Wrap<double>()!.SetValue((double)value); break;
            case ValueKind.ExternRef: global.Wrap<object>()!.SetValue(value); break;
            default: throw WebIdlException.TypeError("This native WebAssembly global value type is unsupported.");
        }
    }

    internal void SyncToNative(IScriptEngine engine)
    {
        if (source.IsUndefined || global.Mutability != Mutability.Mutable) return;
        SetValue(engine.GetProperty(source, "value"));
    }

    internal void SyncFromNative(IScriptEngine engine)
    {
        if (source.IsUndefined || global.Mutability != Mutability.Mutable) return;
        engine.TrySetProperty(source, "value", GetValue(engine));
    }
}

internal sealed class NativeWebAssemblyTable(Table table, object gate, ScriptValue source = default)
{
    internal object Gate => gate;
    internal Table NativeTable => table;
    internal ScriptValue SourceObject => source;
    internal bool IsExternRef => table.Kind == TableKind.ExternRef;
    internal int Length
    {
        get { lock (gate) return checked((int)table.GetSize()); }
    }

    internal object? Get(int index)
    {
        lock (gate)
        {
            var value = table.GetElement(checked((uint)index));
            return value is Function function && function.IsNull ? null : value;
        }
    }

    internal int Grow(int delta, object? value)
    {
        lock (gate) return checked((int)table.Grow(checked((uint)delta), value));
    }

    internal void Set(int index, object? value)
    {
        lock (gate) table.SetElement(checked((uint)index), value);
    }
}

internal sealed record FunctionSignature(IReadOnlyList<ValueKind> Parameters, IReadOnlyList<ValueKind> Results);

internal sealed class NativeWebAssemblyTableEntry(NativeWebAssemblyFunction? native, ScriptValue pageFunction)
{
    internal Function CreateStoreBridge(Store store, IScriptEngine engine, FunctionSignature? signature) => native is not null
        ? native.CreateStoreBridge(store)
        : NativeWebAssemblyFunction.CreatePageStoreBridge(store, engine, pageFunction, signature!);
}

internal sealed class NativeWebAssemblyFunction(Function function, object gate)
{
    internal Function NativeFunction => function;

    internal IReadOnlyList<ValueKind> Parameters => function.Parameters;
    internal IReadOnlyList<ValueKind> Results => function.Results;
    internal bool SupportsStoreBridge => Parameters.All(IsNumeric) && Results.All(IsNumeric);

    internal bool Matches(FunctionSignature signature) => Parameters.SequenceEqual(signature.Parameters) && Results.SequenceEqual(signature.Results);

    internal Function CreateStoreBridge(Store store)
    {
        var parameters = Parameters.ToArray();
        var results = Results.ToArray();
        return Function.FromCallback(store, (_, arguments, output) =>
        {
            var values = InvokeNative(arguments);
            if (values.Length != output.Length)
                throw new InvalidOperationException("The WebAssembly table function returned the wrong number of values.");
            for (var index = 0; index < output.Length; index++) output[index] = values[index];
        }, parameters, results);
    }

    internal static Function CreatePageStoreBridge(Store store, IScriptEngine engine, ScriptValue function, FunctionSignature signature)
    {
        var parameters = signature.Parameters.ToArray();
        var results = signature.Results.ToArray();
        return Function.FromCallback(store, (_, arguments, output) =>
        {
            var scriptArguments = arguments.ToArray()
                .Select((value, index) => NativeWebAssemblyInstance.UnboxValue(engine, parameters[index], value))
                .ToArray();
            var returned = engine.Call(function, ScriptValue.Undefined, scriptArguments);
            var values = results.Length == 0
                ? Array.Empty<ScriptValue>()
                : returned.IsArray && engine.TryGetArrayItems(returned, out var items) ? items : [returned];
            if (values.Count != results.Length)
                throw new InvalidOperationException("The WebAssembly table function returned the wrong number of values.");
            for (var index = 0; index < output.Length; index++)
                output[index] = NativeWebAssemblyInstance.BoxValue(results[index], values[index]);
        }, parameters, results);
    }

    private ValueBox[] InvokeNative(ReadOnlySpan<ValueBox> arguments)
    {
        lock (gate)
        {
            var returned = function.Invoke(arguments);
            if (Results.Count == 0) return [];
            object?[] values = Results.Count == 1
                ? new object?[] { returned }
                : returned is object[] many ? many : throw new InvalidOperationException("The WebAssembly table function returned an invalid result tuple.");
            if (values.Length != Results.Count) throw new InvalidOperationException("The WebAssembly table function returned the wrong number of values.");
            var result = new ValueBox[values.Length];
            for (var index = 0; index < result.Length; index++) result[index] = ToValueBox(Results[index], values[index]);
            return result;
        }
    }

    private static bool IsNumeric(ValueKind kind) => kind is ValueKind.Int32 or ValueKind.Int64 or ValueKind.Float32 or ValueKind.Float64;

    private static ValueBox ToValueBox(ValueKind kind, object? value) => kind switch
    {
        ValueKind.Int32 => value is int integer ? integer : throw new InvalidOperationException("The WebAssembly table result type was invalid."),
        ValueKind.Int64 => value is long integer ? integer : throw new InvalidOperationException("The WebAssembly table result type was invalid."),
        ValueKind.Float32 => value is float number ? number : throw new InvalidOperationException("The WebAssembly table result type was invalid."),
        ValueKind.Float64 => value is double number ? number : throw new InvalidOperationException("The WebAssembly table result type was invalid."),
        ValueKind.ExternRef => ValueBox.AsBox<object>(value),
        _ => throw new InvalidOperationException("The WebAssembly table result type is unsupported."),
    };

    internal ScriptValue Invoke(IScriptEngine engine, IReadOnlyList<ScriptValue> arguments)
    {
        lock (gate)
        {
            if (function.Parameters.Count != arguments.Count) throw WebIdlException.TypeError("The WebAssembly table function received the wrong number of arguments.");
            var values = new ValueBox[arguments.Count];
            for (var index = 0; index < values.Length; index++) values[index] = NativeWebAssemblyInstance.BoxValue(function.Parameters[index], arguments[index]);
            return Unbox(engine, function.Invoke(values));
        }
    }

    private static ScriptValue Unbox(IScriptEngine engine, object? value) => value switch
    {
        null => ScriptValue.Undefined,
        int integer => ScriptValue.FromNumber(integer), long integer => ScriptValue.FromNumber(integer),
        float number => ScriptValue.FromNumber(number), double number => ScriptValue.FromNumber(number),
        object[] many => engine.CreateArray(many.Select(item => Unbox(engine, item)).ToArray()),
        _ => ScriptValue.FromObject(value)
    };
}

internal sealed class NativeWebAssemblyMemory(Memory memory, object gate, ScriptValue source = default)
{
    private ScriptValue _lastBuffer = ScriptValue.Undefined;
    internal Memory NativeMemory => memory;
    internal ScriptValue SourceObject => source;

    internal ScriptValue GetBuffer(IScriptEngine engine)
    {
        lock (gate)
        {
            var bytes = memory.GetSpan(0, checked((int)memory.GetLength())).ToArray();
            _lastBuffer = engine.CreateArrayBuffer(bytes);
            return _lastBuffer;
        }
    }

    internal void SyncToNative(IScriptEngine engine)
    {
        lock (gate)
        {
            var buffer = source.IsUndefined ? _lastBuffer : engine.GetProperty(source, "buffer");
            if (buffer.IsUndefined || !engine.TryGetArrayBufferBytes(buffer, out var bytes)) return;
            bytes.AsSpan(0, Math.Min(bytes.Length, checked((int)memory.GetLength())))
                .CopyTo(memory.GetSpan(0, checked((int)memory.GetLength())));
        }
    }

    internal int Grow(long delta)
    {
        lock (gate)
        {
            _lastBuffer = ScriptValue.Undefined;
            return checked((int)memory.Grow(delta));
        }
    }

    internal int ByteLength => checked((int)memory.GetLength());
}

internal sealed class NativeWebAssemblyBridge : IDisposable
{
    private readonly IScriptEngine _engine;
    private readonly NativeWebAssemblyRuntime _runtime;
    private readonly Func<bool>? _enabled;
    private readonly HostObjectTemplate _template;
    private readonly HostObjectTemplate _instanceTemplate = new("NativeWebAssemblyInstance");
    private readonly HostObjectTemplate _memoryTemplate;
    private readonly HostObjectTemplate _globalTemplate;
    private readonly HostObjectTemplate _tableTemplate;
    private readonly HostObjectTemplate _functionTemplate;

    private NativeWebAssemblyBridge(IScriptEngine engine, int maxBytes, TimeSpan executionTimeout, Func<bool>? enabled)
    {
        _engine = engine; _runtime = new NativeWebAssemblyRuntime(maxBytes, executionTimeout, enabled); _enabled = enabled;
        _memoryTemplate = new HostObjectTemplate("NativeWebAssemblyMemory")
            .DefineProperty("buffer", (target, _) => { EnsureAllowed(); return ((NativeWebAssemblyMemory)target).GetBuffer(_engine); })
            .DefineProperty("byteLength", (target, _) => { EnsureAllowed(); return ScriptValue.FromNumber(((NativeWebAssemblyMemory)target).ByteLength); })
            .DefineMethod("grow", (target, _, args) => { EnsureAllowed(); return ScriptValue.FromNumber(((NativeWebAssemblyMemory)target).Grow(args.Length == 0 ? 0 : checked((long)args[0].AsNumber()))); });
        _globalTemplate = new HostObjectTemplate("NativeWebAssemblyGlobal")
            .DefineProperty("mutable", (target, _) => ScriptValue.FromBoolean(((NativeWebAssemblyGlobal)target).IsMutable))
            .DefineProperty("value", (target, _) => { EnsureAllowed(); return ((NativeWebAssemblyGlobal)target).GetValue(_engine); }, (target, _, value) => { EnsureAllowed(); ((NativeWebAssemblyGlobal)target).SetValue(value); });
        _functionTemplate = new HostObjectTemplate("NativeWebAssemblyFunction")
            .DefineMethod("invoke", (target, _, args) => ((NativeWebAssemblyFunction)target).Invoke(_engine, args.ToArray()));
        _tableTemplate = new HostObjectTemplate("NativeWebAssemblyTable")
            .DefineProperty("length", (target, _) => { EnsureAllowed(); return ScriptValue.FromNumber(((NativeWebAssemblyTable)target).Length); })
            .DefineMethod("get", (target, _, args) =>
            {
                EnsureAllowed();
                var value = ((NativeWebAssemblyTable)target).Get(args.Length == 0 ? 0 : checked((int)args[0].AsNumber()));
                if (value is Function function) return WrapFunction(function, ((NativeWebAssemblyTable)target).Gate);
                return value is null ? ScriptValue.Null : ScriptValue.FromObject(value);
            })
            .DefineMethod("grow", (target, _, args) =>
            {
                EnsureAllowed();
                object? value = null;
                if (((NativeWebAssemblyTable)target).IsExternRef && args.Length > 1 && !args[1].IsNullOrUndefined)
                    value = args[1].Raw;
                return ScriptValue.FromNumber(((NativeWebAssemblyTable)target).Grow(args.Length == 0 ? 0 : checked((int)args[0].AsNumber()), value));
            })
            .DefineMethod("set", (target, _, args) =>
            {
                EnsureAllowed();
                if (args.Length < 2) throw WebIdlException.TypeError("WebAssembly.Table.set requires an index and value.");
                object? value = null;
                if (((NativeWebAssemblyTable)target).IsExternRef)
                {
                    value = args[1].IsNullOrUndefined ? null : args[1].Raw;
                }
                else if (!args[1].IsNullOrUndefined)
                {
                    var marker = _engine.GetProperty(args[1], "__folio_native_wasm_function");
                    if (!_engine.TryGetHostTarget(marker, out var native) || native is not NativeWebAssemblyFunction function)
                        throw WebIdlException.TypeError("The WebAssembly table value must be a native function or null.");
                    value = function.NativeFunction;
                }
                ((NativeWebAssemblyTable)target).Set(checked((int)args[0].AsNumber()), value);
                return ScriptValue.Undefined;
            });
        _template = new HostObjectTemplate("NativeWebAssemblyHost")
            .DefineMethod("instantiate", (_, _, args) =>
            {
                EnsureAllowed();
                if (args.Length == 0 || !TryGetModuleBytes(args[0], out var bytes)) return ScriptValue.Null;
                var imports = args.Length > 1 ? args[1] : ScriptValue.Undefined;
                var descriptors = args.Length > 2 && _engine.TryGetArrayItems(args[2], out var values) ? values : [];
                return _runtime.TryInstantiate(_engine, bytes, imports, descriptors, out var instance) && instance is not null ? _engine.CreateHostObject(_instanceTemplate, instance) : ScriptValue.Null;
            })
            .DefineMethod("prepare", (_, _, args) =>
            {
                EnsureAllowed();
                if (args.Length == 0 || !TryGetModuleBytes(args[0], out var bytes)) return ScriptValue.FromBoolean(false);
                return ScriptValue.FromBoolean(_runtime.Prepare(bytes));
            })
            .DefineMethod("invoke", (_, _, args) =>
            {
                EnsureAllowed();
                if (args.Length < 3 || !_engine.TryGetHostTarget(args[0], out var target) || target is not NativeWebAssemblyInstance instance) throw WebIdlException.TypeError("Invalid native WebAssembly instance.");
                if (!_engine.TryGetArrayItems(args[2], out var values)) throw WebIdlException.TypeError("WebAssembly arguments must be an array.");
                try { return instance.Invoke(_engine, args[1].AsString() ?? string.Empty, values); }
                catch (WasmtimeException error) when (FindWebIdlException(error) is { } webError) { throw webError; }
            })
            .DefineMethod("getMemory", (_, _, args) =>
            {
                EnsureAllowed();
                if (args.Length < 2 || !_engine.TryGetHostTarget(args[0], out var target) || target is not NativeWebAssemblyInstance instance) throw WebIdlException.TypeError("Invalid native WebAssembly instance.");
                var memory = instance.GetMemory(args[1].AsString() ?? string.Empty);
                if (memory is null) return ScriptValue.Undefined;
                return memory.SourceObject.IsUndefined ? _engine.CreateHostObject(_memoryTemplate, memory) : memory.SourceObject;
            })
            .DefineMethod("getGlobal", (_, _, args) =>
            {
                EnsureAllowed();
                if (args.Length < 2 || !_engine.TryGetHostTarget(args[0], out var target) || target is not NativeWebAssemblyInstance instance) throw WebIdlException.TypeError("Invalid native WebAssembly instance.");
                var global = instance.GetGlobal(args[1].AsString() ?? string.Empty);
                return global is null ? ScriptValue.Undefined : global.SourceObject.IsUndefined ? _engine.CreateHostObject(_globalTemplate, global) : global.SourceObject;
            })
            .DefineMethod("getTable", (_, _, args) =>
            {
                EnsureAllowed();
                if (args.Length < 2 || !_engine.TryGetHostTarget(args[0], out var target) || target is not NativeWebAssemblyInstance instance) throw WebIdlException.TypeError("Invalid native WebAssembly instance.");
                var table = instance.GetTable(args[1].AsString() ?? string.Empty);
                return table is null ? ScriptValue.Undefined : table.SourceObject.IsUndefined ? _engine.CreateHostObject(_tableTemplate, table) : table.SourceObject;
            });
    }

    private bool TryGetModuleBytes(ScriptValue value, out byte[] bytes) =>
        _engine.TryGetArrayBufferBytes(value, out bytes) || _engine.TryGetBytes(value, out bytes);

    private void EnsureAllowed()
    {
        if (_enabled is not null && !_enabled())
            throw WebIdlException.DomException("NotSupportedError", "WebAssembly is disabled by the host.");
    }

    private static WebIdlException? FindWebIdlException(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is WebIdlException webError) return webError;
        return null;
    }

    private ScriptValue WrapFunction(Function function, object gate)
    {
        var host = _engine.CreateHostObject(_functionTemplate, new NativeWebAssemblyFunction(function, gate));
        _engine.SetGlobalProperty("__folio_native_wasm_function_host", host);
        try { return _engine.Evaluate("(function(host){var f=function(){return host.invoke(Array.from(arguments));}; Object.defineProperty(f,'__folio_native_wasm_function',{value:host}); return f;})(__folio_native_wasm_function_host)", "folio:native-wasm-function"); }
        finally { _engine.SetGlobalProperty("__folio_native_wasm_function_host", ScriptValue.Undefined); }
    }

    internal static NativeWebAssemblyBridge Install(IScriptEngine engine, DomContext context, int maxBytes)
    {
        var bridge = Install(engine, maxBytes, default, () => context.CheckCapability("WebAssembly", context.Capabilities.WebAssembly));
        context.DisposeWebAssembly = bridge.Dispose;
        return bridge;
    }

    internal static NativeWebAssemblyBridge Install(IScriptEngine engine, int maxBytes, TimeSpan executionTimeout = default, Func<bool>? enabled = null)
    {
        var bridge = new NativeWebAssemblyBridge(engine, maxBytes, executionTimeout, enabled);
        engine.SetGlobalProperty("__folio_native_wasm", engine.CreateHostObject(bridge._template, bridge));
        return bridge;
    }

    internal int CachedModuleCount => _runtime.CachedModuleCount;

    public void Dispose() { _runtime.Dispose(); _engine.SetGlobalProperty("__folio_native_wasm", ScriptValue.Undefined); }
}

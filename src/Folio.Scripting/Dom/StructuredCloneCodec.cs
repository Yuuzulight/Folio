using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Folio.Scripting.Dom;

internal sealed class StructuredClonePayload
{
    public StructuredClonePayload(string json, IReadOnlyList<MessagePortEndpoint> ports, IReadOnlyList<byte[]>? transferredBuffers = null, long byteCount = 0)
    {
        Json = json;
        Ports = ports;
        TransferredBuffers = transferredBuffers ?? [];
        ByteCount = byteCount;
    }

    public string Json { get; }
    public IReadOnlyList<MessagePortEndpoint> Ports { get; }
    public IReadOnlyList<byte[]> TransferredBuffers { get; }
    public long ByteCount { get; }
    public long MemoryCost => checked(128L + Json.Length * 2L + Ports.Count * 16L + TransferredBuffers.Sum(buffer => (long)buffer.Length));
    internal void AbortTransfers()
    {
        foreach (var port in Ports) port.AbortTransfer();
    }
}

internal sealed class StructuredClonePreparation(IScriptEngine source, StructuredClonePayload payload,
    IReadOnlyList<ScriptValue> buffers)
{
    private bool _committed;
    internal StructuredClonePayload Payload { get; } = payload;

    internal StructuredClonePayload Commit()
    {
        if (_committed) throw new InvalidOperationException("The clone was already committed.");
        foreach (var buffer in buffers)
            if (!source.TryGetArrayBufferBytes(buffer, out _))
                throw WebIdlException.DomException("DataCloneError", "The ArrayBuffer is detached.");
        foreach (var port in Payload.Ports) port.ValidateTransfer();
        foreach (var buffer in buffers)
            if (!source.TryDetachArrayBuffer(buffer))
                throw WebIdlException.DomException("DataCloneError", "The ArrayBuffer could not be detached.");
        foreach (var port in Payload.Ports) port.PrepareTransfer();
        _committed = true;
        return Payload;
    }
}
internal sealed record StructuredCloneResult(ScriptValue Value, IReadOnlyList<ScriptValue> Ports)
{
    internal void AbortPorts(IScriptEngine engine)
    {
        foreach (var port in Ports) MessagePortEndpoint.TryGetPort(engine, port)?.Close();
    }
}

/// <summary>Engine-owner-thread structured clone transport used by workers and message ports.</summary>
internal static class StructuredCloneCodec
{
    private sealed record Helpers(ScriptValue Encode, ScriptValue Decode, ScriptValue Freeze, ScriptValue NormalizeTransfer);
    private static readonly ConditionalWeakTable<IScriptEngine, Helpers> HelpersByEngine = new();

    private static Helpers GetHelpers(IScriptEngine engine)
    {
        return HelpersByEngine.GetValue(engine, static e =>
        {
            var result = e.EvaluateHostScript(HelperScript, "folio:structured-clone");
            if (!e.TryGetArrayItems(result, out var items) || items.Count < 4 || !items[0].IsFunction || !items[1].IsFunction || !items[2].IsFunction)
                throw new InvalidOperationException("The script engine did not return structured clone helpers.");
            return new Helpers(items[0], items[1], items[2], items[3]);
        });
    }

    internal static void ResetHelpers(IScriptEngine engine) => HelpersByEngine.Remove(engine);

    internal static void EnsureHelpers(IScriptEngine engine) => _ = GetHelpers(engine);
    internal static ScriptValue NormalizeTransfer(IScriptEngine engine, ScriptValue value, bool postMessage)
    {
        if (postMessage && engine is Folio.Scripting.Native.NativeJavaScriptEngine && value.Raw is IReadOnlyDictionary<string, object?> options && options.TryGetValue("transfer", out var transfer))
            return transfer is null ? engine.CreateArray([]) : ScriptValue.FromObject(transfer);
        return engine.Call(GetHelpers(engine).NormalizeTransfer, ScriptValue.Undefined, [value, ScriptValue.FromBoolean(postMessage)]);
    }

    internal static ScriptValue CreatePorts(IScriptEngine engine, IReadOnlyList<ScriptValue> ports)
    {
        var array = engine.CreateArray(ports.ToArray());
        return engine.Call(GetHelpers(engine).Freeze, ScriptValue.Undefined, [array]);
    }

    internal static StructuredClonePayload Encode(IScriptEngine source, ScriptValue value, ScriptValue transfer,
        int maxBytes, Func<IScriptEngine, ScriptValue, MessagePortEndpoint?> findPort) =>
        Prepare(source, value, transfer, maxBytes, findPort).Commit();

    internal static StructuredClonePreparation Prepare(IScriptEngine source, ScriptValue value, ScriptValue transfer,
        int maxBytes, Func<IScriptEngine, ScriptValue, MessagePortEndpoint?> findPort)
    {
        if (source is Folio.Scripting.Native.NativeJavaScriptEngine && value.Kind is ScriptValueKind.Undefined or ScriptValueKind.Null or ScriptValueKind.Boolean or ScriptValueKind.Number or ScriptValueKind.String)
        {
            var primitiveJson = value.Kind switch
            {
                ScriptValueKind.Undefined => "null",
                ScriptValueKind.Null => "null",
                ScriptValueKind.Boolean => value.AsBoolean() ? "true" : "false",
                ScriptValueKind.Number => value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => JsonSerializer.Serialize(value.AsString() ?? string.Empty),
            };
            return new StructuredClonePreparation(source, new StructuredClonePayload(primitiveJson, [], byteCount: Encoding.UTF8.GetByteCount(primitiveJson)), []);
        }
        var transferItems = ValidateTransferList(source, transfer, findPort, out var transferPorts, out var transferBuffers);
        var ports = transferItems.Select(item => findPort(source, item)).Where(port => port is not null).Cast<MessagePortEndpoint>().ToList();
        var transferBufferIndexes = transferBuffers.Select((buffer, index) => (buffer, index)).ToDictionary(item => item.buffer, item => item.index);
        var transferredBytes = new List<byte[]>(transferBuffers.Count);
        var detector = source.CreateHostObject(DetectorTemplate, new Detector(source, findPort, transferPorts, ports, transferBufferIndexes, transferredBytes));
        var encoder = GetHelpers(source).Encode;

        var json = source.Call(encoder, ScriptValue.Undefined, [value, detector]).AsString()
            ?? throw WebIdlException.DomException("DataCloneError", "The value cannot be cloned.");
        var jsonBytes = Encoding.UTF8.GetByteCount(json);
        var transferredByteCount = transferredBytes.Sum(buffer => (long)buffer.Length);
        if (checked(jsonBytes + transferredByteCount) > Math.Max(0, maxBytes))
            throw WebIdlException.DomException("DataCloneError", "The message exceeds the host limit.");
        return new StructuredClonePreparation(source,
            new StructuredClonePayload(json, ports, transferredBytes, checked(jsonBytes + transferredByteCount)), transferBuffers);
    }

    internal static StructuredCloneResult Decode(IScriptEngine destination, StructuredClonePayload payload,
        Func<MessagePortEndpoint, IScriptEngine, ScriptValue> materializePort)
    {
        if (destination is Folio.Scripting.Native.NativeJavaScriptEngine && payload.Ports.Count == 0 && payload.TransferredBuffers.Count == 0)
        {
            using var primitive = JsonDocument.Parse(payload.Json);
            var value = primitive.RootElement.ValueKind switch
            {
                JsonValueKind.Null => ScriptValue.Null,
                JsonValueKind.True => ScriptValue.True,
                JsonValueKind.False => ScriptValue.False,
                JsonValueKind.Number => ScriptValue.FromNumber(primitive.RootElement.GetDouble()),
                JsonValueKind.String => ScriptValue.FromString(primitive.RootElement.GetString() ?? string.Empty),
                _ => (ScriptValue?)null,
            };
            if (value is { } simple) return new StructuredCloneResult(simple, []);
        }
        var decoder = GetHelpers(destination).Decode;
        var materializedPorts = new List<ScriptValue>();
        try
        {
            foreach (var port in payload.Ports) materializedPorts.Add(materializePort(port, destination));
            var resolver = destination.CreateHostObject(ResolverTemplate, new Resolver(destination, materializedPorts, payload.TransferredBuffers));
            var value = destination.Call(decoder, ScriptValue.Undefined, [ScriptValue.FromString(payload.Json), resolver]);
            return new StructuredCloneResult(value, materializedPorts);
        }
        catch
        {
            foreach (var value in materializedPorts)
                MessagePortEndpoint.TryGetPort(destination, value)?.Close();
            payload.AbortTransfers();
            throw;
        }
    }

    private static IReadOnlyList<ScriptValue> ValidateTransferList(IScriptEngine engine, ScriptValue transfer,
        Func<IScriptEngine, ScriptValue, MessagePortEndpoint?> findPort,
        out HashSet<MessagePortEndpoint> ports, out List<ScriptValue> buffers)
    {
        ports = new HashSet<MessagePortEndpoint>(ReferenceEqualityComparer.Instance);
        buffers = [];
        if (transfer.IsNullOrUndefined) return [];
        if (!engine.TryGetArrayItems(transfer, out var items))
            throw WebIdlException.DomException("DataCloneError", "The transfer list must be an array.");
        var seen = new HashSet<ScriptValue>();
        foreach (var item in items)
        {
            if (!seen.Add(item)) throw WebIdlException.DomException("DataCloneError", "The transfer list contains duplicates.");
            if (findPort(engine, item) is { } port)
            {
                if (port.Closed) throw WebIdlException.DomException("DataCloneError", "The message port is closed.");
                if (!ports.Add(port)) throw WebIdlException.DomException("DataCloneError", "The transfer list contains duplicates.");
            }
            else if (engine.TryGetArrayBufferBytes(item, out _)) buffers.Add(item);
            else throw WebIdlException.DomException("DataCloneError", "The transfer list contains a non-transferable value.");
        }
        return items;
    }

    private sealed class Detector(IScriptEngine engine, Func<IScriptEngine, ScriptValue, MessagePortEndpoint?> findPort,
        HashSet<MessagePortEndpoint> transferPorts, List<MessagePortEndpoint> ports,
        IReadOnlyDictionary<ScriptValue, int> transferBufferIndexes, List<byte[]> transferredBytes)
    {
        public ScriptValue Kind(ScriptValue value) => ScriptValue.FromString(engine is IStructuredCloneEngine native
            ? native.ClassifyCloneObject(value) : "unsupported");

        public ScriptValue Buffer(ScriptValue value)
        {
            var isTransferred = transferBufferIndexes.ContainsKey(value);
            if (!(isTransferred ? engine.TryGetArrayBufferBytesOwned(value, out var bytes) : engine.TryGetArrayBufferBytes(value, out bytes)))
                throw WebIdlException.DomException("DataCloneError", "The ArrayBuffer is detached or unavailable.");
            if (transferBufferIndexes.TryGetValue(value, out var index))
            {
                while (transferredBytes.Count <= index) transferredBytes.Add([]);
                transferredBytes[index] = bytes;
                return ScriptValue.FromString("transfer-buffer:" + index);
            }
            return ScriptValue.FromString(Convert.ToBase64String(bytes));
        }

        public ScriptValue Typed(ScriptValue value)
        {
            if (engine is not Folio.Scripting.Native.NativeJavaScriptEngine) return ScriptValue.Undefined;
            if (!engine.TryGetBufferSourceInfo(value, out var info) || info.Kind is ScriptBufferSourceKind.ArrayBuffer or ScriptBufferSourceKind.DataView)
                return ScriptValue.Undefined;
            var bytes = new byte[info.ByteLength];
            if (!engine.TryCopyBufferSource(value, 0, bytes)) return ScriptValue.Undefined;
            var name = info.Kind switch
            {
                ScriptBufferSourceKind.Uint8 => "Uint8Array", ScriptBufferSourceKind.Uint8Clamped => "Uint8ClampedArray",
                ScriptBufferSourceKind.Int8 => "Int8Array", ScriptBufferSourceKind.Uint16 => "Uint16Array",
                ScriptBufferSourceKind.Int16 => "Int16Array", ScriptBufferSourceKind.Uint32 => "Uint32Array",
                ScriptBufferSourceKind.Int32 => "Int32Array", ScriptBufferSourceKind.Float16 => "Float16Array",
                ScriptBufferSourceKind.Float32 => "Float32Array", ScriptBufferSourceKind.Float64 => "Float64Array",
                ScriptBufferSourceKind.BigInt64 => "BigInt64Array", ScriptBufferSourceKind.BigUint64 => "BigUint64Array",
                _ => string.Empty,
            };
            return engine.CreateArray([ScriptValue.FromString(name), engine.CreateArrayBuffer(bytes), ScriptValue.FromNumber(0), ScriptValue.FromNumber(info.Length)]);
        }

        public ScriptValue Error(ScriptValue value) => value.Raw is Folio.Scripting.Native.NativeJavaScriptEngine.NativeError error
            ? engine.CreateArray([ScriptValue.FromString(error["name"]?.ToString() ?? "Error"), ScriptValue.FromString(error["message"]?.ToString() ?? ""), ScriptValue.FromString("")])
            : ScriptValue.Undefined;

        public ScriptValue RegExp(ScriptValue value)
        {
            if (engine is not IStructuredCloneEngine native)
                throw WebIdlException.DomException("DataCloneError", "The engine cannot clone regular expressions.");
            var data = native.GetCloneRegExp(value);
            return engine.CreateArray([ScriptValue.FromString(data.Source), ScriptValue.FromString(data.Flags)]);
        }

        public ScriptValue Find(ScriptValue value)
        {
            if (findPort(engine, value) is { } port)
            {
                if (!transferPorts.Contains(port)) throw WebIdlException.DomException("DataCloneError", "A message port must be transferred.");
                var index = ports.IndexOf(port);
                if (index < 0) { ports.Add(port); index = ports.Count - 1; }
                return ScriptValue.FromString("port:" + index);
            }
            if (engine.TryGetHostTarget(value, out _))
                throw WebIdlException.DomException("DataCloneError", "Host objects cannot be cloned.");
            return ScriptValue.Undefined;
        }
    }

    private sealed class Resolver(IScriptEngine engine, IReadOnlyList<ScriptValue> ports, IReadOnlyList<byte[]> transferredBuffers)
    {
        public ScriptValue Buffer(ScriptValue value)
        {
            var encoded = value.AsString() ?? throw new InvalidDataException("Invalid clone buffer.");
            if (encoded.StartsWith("transfer-buffer:", StringComparison.Ordinal)
                && int.TryParse(encoded[16..], out var index)
                && (uint)index < (uint)transferredBuffers.Count)
                return engine.CreateArrayBufferOwned(transferredBuffers[index]);
            return engine.CreateArrayBuffer(Convert.FromBase64String(encoded));
        }

        public ScriptValue Find(ScriptValue token)
        {
            var key = token.AsString() ?? string.Empty;
            if (!key.StartsWith("port:", StringComparison.Ordinal) || !int.TryParse(key[5..], out var index) || (uint)index >= (uint)ports.Count)
                throw WebIdlException.DomException("DataCloneError", "The transferred port is invalid.");
            return ports[index];
        }
    }

    private static readonly HostObjectTemplate DetectorTemplate = new HostObjectTemplate("StructuredCloneDetector")
        .DefineMethod("find", (target, _, args) => ((Detector)target).Find(args.Length > 0 ? args[0] : ScriptValue.Undefined))
        .DefineMethod("kind", (target, _, args) => ((Detector)target).Kind(args[0]))
        .DefineMethod("typed", (target, _, args) => ((Detector)target).Typed(args[0]))
        .DefineMethod("error", (target, _, args) => ((Detector)target).Error(args[0]))
        .DefineMethod("buffer", (target, _, args) => ((Detector)target).Buffer(args[0]))
        .DefineMethod("regexp", (target, _, args) => ((Detector)target).RegExp(args[0]));
    private static readonly HostObjectTemplate ResolverTemplate = new HostObjectTemplate("StructuredCloneResolver")
        .DefineMethod("find", (target, _, args) => ((Resolver)target).Find(args.Length > 0 ? args[0] : ScriptValue.Undefined))
        .DefineMethod("buffer", (target, _, args) => ((Resolver)target).Buffer(args[0]));

    private const string HelperScript = """
        (function() {
          const apply = Reflect.apply;
          const parse = JSON.parse, stringify = JSON.stringify;
          const create = Object.create, define = Object.defineProperty, keys = Object.keys;
          const setPrototype = Object.setPrototypeOf, getPrototype = Object.getPrototypeOf;
          const hasOwn = Object.hasOwn, same = Object.is, freezeObject = Object.freeze;
          const ObjectPrototype = Object.prototype;
          const arrayFrom = Array.from, iteratorSymbol = Symbol.iterator, TypeErrorCtor = TypeError;
          const ArrayCtor = Array, DateCtor = Date, RegExpCtor = RegExp, MapCtor = Map, SetCtor = Set;
          const StringCtor = String, NumberCtor = Number, BigIntCtor = BigInt, DOMExceptionCtor = DOMException;
          const mapGet = Map.prototype.get, mapSet = Map.prototype.set, mapHas = Map.prototype.has;
          const mapForEach = Map.prototype.forEach, setForEach = Set.prototype.forEach, setAdd = Set.prototype.add;
          const dateTime = Date.prototype.getTime;
          const boxedValueOf = [Number.prototype.valueOf, String.prototype.valueOf, Boolean.prototype.valueOf, BigInt.prototype.valueOf];
          // The primitive a Number, String, Boolean or BigInt object wraps, as a one-element array; nothing for any other object.
          function unbox(value) {
            for (let i = 0; i < boxedValueOf.length; i++) {
              try { const primitive = apply(boxedValueOf[i], value, []); const result = array(); push(result, primitive); return result; } catch (_) { }
            }
            return undefined;
          }
          const typedPrototype = getPrototype(Uint8Array.prototype);
          const descriptor = Object.getOwnPropertyDescriptor;
          const typedBuffer = descriptor(typedPrototype, 'buffer').get;
          const typedOffset = descriptor(typedPrototype, 'byteOffset').get;
          const typedLength = descriptor(typedPrototype, 'length').get;
          const typedTag = descriptor(typedPrototype, Symbol.toStringTag).get;
          const viewBuffer = descriptor(DataView.prototype, 'buffer').get;
          const viewOffset = descriptor(DataView.prototype, 'byteOffset').get;
          const viewLength = descriptor(DataView.prototype, 'byteLength').get;
          const DataViewCtor = DataView;
          const typed = create(null);
          typed.Uint8Array = Uint8Array; typed.Uint8ClampedArray = Uint8ClampedArray;
          typed.Int8Array = Int8Array; typed.Uint16Array = Uint16Array; typed.Int16Array = Int16Array;
          typed.Uint32Array = Uint32Array; typed.Int32Array = Int32Array;
          typed.Float32Array = Float32Array; typed.Float64Array = Float64Array;
          typed.BigInt64Array = BigInt64Array; typed.BigUint64Array = BigUint64Array;
          const errors = create(null);
          errors.Error = Error; errors.EvalError = EvalError; errors.RangeError = RangeError;
          errors.ReferenceError = ReferenceError; errors.SyntaxError = SyntaxError;
          errors.TypeError = TypeError; errors.URIError = URIError;
          function array() { const value = new ArrayCtor(); setPrototype(value, null); return value; }
          function record(key, value) { const result = create(null); result[key] = value; return result; }
          function push(values, value) { values[values.length] = value; }
          function defineValue(target, key, value, enumerable) {
            const descriptor = create(null);
            descriptor.value = value; descriptor.writable = true;
            descriptor.configurable = true; descriptor.enumerable = enumerable;
            define(target, key, descriptor);
          }
          function cloneError() { throw new DOMExceptionCtor('Value cannot be cloned', 'DataCloneError'); }
          const encode = function(root, detector) {
            const seen = new MapCtor(), records = array();
            function visit(value) {
              if (value === null || typeof value !== 'object') {
                if (typeof value === 'function' || typeof value === 'symbol') cloneError();
                if (value === undefined) return record('u', 1);
                if (typeof value === 'number') {
                  if (same(value, -0)) return record('n', '-0');
                  if (value !== value) return record('n', 'NaN');
                  if (value === Infinity) return record('n', 'Infinity');
                  if (value === -Infinity) return record('n', '-Infinity');
                }
                if (typeof value === 'bigint') return record('i', StringCtor(value));
                return record('p', value);
              }
              if (apply(mapHas, seen, [value])) return record('r', apply(mapGet, seen, [value]));
              const index = records.length, item = create(null);
              apply(mapSet, seen, [value, index]); push(records, item);
              const port = detector.find(value);
              if (port !== undefined) { item.t = 'port'; item.k = port; return record('r', index); }
              const kind = detector.kind(value);
              item.t = kind;
              if (kind === 'buffer') item.b = detector.buffer(value);
              else if (kind === 'typedarray') {
                const nativeView = detector.typed(value);
                if (nativeView !== undefined) {
                  item.c = nativeView[0]; item.b = visit(nativeView[1]); item.o = nativeView[2]; item.l = nativeView[3];
                } else {
                  item.c = apply(typedTag, value, []);
                  if (!hasOwn(typed, item.c)) cloneError();
                  item.b = visit(apply(typedBuffer, value, []));
                  item.o = apply(typedOffset, value, []); item.l = apply(typedLength, value, []);
                }
              } else if (kind === 'dataview') {
                item.b = visit(apply(viewBuffer, value, []));
                item.o = apply(viewOffset, value, []); item.l = apply(viewLength, value, []);
              } else if (kind === 'date') item.v = visit(apply(dateTime, value, []));
              else if (kind === 'regexp') { const data = detector.regexp(value); item.s = data[0]; item.f = data[1]; }
              else if (kind === 'map') {
                item.e = array();
                apply(mapForEach, value, [function(entry, key) {
                  const pair = array(); push(pair, visit(key)); push(pair, visit(entry)); push(item.e, pair);
                }]);
              } else if (kind === 'set') {
                item.e = array(); apply(setForEach, value, [function(entry) { push(item.e, visit(entry)); }]);
              } else if (kind === 'error') {
                const name = value.name, message = value.message, stack = value.stack;
                const nativeError = detector.error(value);
                if (nativeError !== undefined) { item.n = nativeError[0]; item.m = nativeError[1]; item.s = nativeError[2]; }
                else { item.n = typeof name === 'string' && hasOwn(errors, name) ? name : 'Error'; item.m = typeof message === 'string' ? message : ''; item.s = typeof stack === 'string' ? stack : ''; }
                if (hasOwn(value, 'cause')) item.cause = visit(value.cause);
              } else if (kind === 'array' || kind === 'object') {
                if (kind === 'array') item.l = value.length;
                item.e = array(); const names = keys(value);
                for (let i = 0; i < names.length; i++) {
                  const pair = array(); push(pair, names[i]); push(pair, visit(value[names[i]])); push(item.e, pair);
                }
              } else {
                const wrapped = unbox(value);
                if (wrapped === undefined) cloneError();
                item.t = 'boxed'; item.v = visit(wrapped[0]);
              }
              return record('r', index);
            }
            const data = record('root', visit(root)); data.records = records;
            return stringify(data);
          };
          const decode = function(text, resolver) {
            const data = parse(text), records = data.records, objects = array();
            objects.length = records.length;
            function read(value) {
              if (hasOwn(value, 'r')) return objects[value.r];
              if (hasOwn(value, 'u')) return undefined;
              if (hasOwn(value, 'n')) return value.n === 'NaN' ? NaN : value.n === '-0' ? -0 : NumberCtor(value.n);
              if (hasOwn(value, 'i')) return BigIntCtor(value.i);
              return value.p;
            }
            for (let i = 0; i < records.length; i++) {
              const item = records[i];
              if (item.t === 'port') objects[i] = resolver.find(item.k);
              else if (item.t === 'buffer') objects[i] = resolver.buffer(item.b);
              else if (item.t === 'date') objects[i] = new DateCtor(NaN);
              else if (item.t === 'boxed') objects[i] = Object(read(item.v));
              else if (item.t === 'regexp') objects[i] = new RegExpCtor(item.s, item.f);
              else if (item.t === 'map') objects[i] = new MapCtor();
              else if (item.t === 'set') objects[i] = new SetCtor();
              else if (item.t === 'error') {
                const ctor = hasOwn(errors, item.n) ? errors[item.n] : errors.Error;
                objects[i] = new ctor(item.m);
                defineValue(objects[i], 'stack', item.s, false);
              } else if (item.t === 'array') objects[i] = new ArrayCtor(item.l);
              else if (item.t === 'object') objects[i] = create(ObjectPrototype);
            }
            for (let i = 0; i < records.length; i++) {
              const item = records[i];
              if (item.t === 'typedarray') {
                if (!hasOwn(typed, item.c)) cloneError();
                objects[i] = new typed[item.c](read(item.b), item.o, item.l);
              } else if (item.t === 'dataview') objects[i] = new DataViewCtor(read(item.b), item.o, item.l);
              else if (item.t === 'date') objects[i] = new DateCtor(read(item.v));
            }
            for (let i = 0; i < records.length; i++) {
              const item = records[i], object = objects[i];
              if (item.t === 'map') {
                for (let j = 0; j < item.e.length; j++) apply(mapSet, object, [read(item.e[j][0]), read(item.e[j][1])]);
              } else if (item.t === 'set') {
                for (let j = 0; j < item.e.length; j++) apply(setAdd, object, [read(item.e[j])]);
              } else if (item.t === 'array' || item.t === 'object') {
                for (let j = 0; j < item.e.length; j++) {
                  const entry = item.e[j];
                  defineValue(object, entry[0], read(entry[1]), true);
                }
              } else if (item.t === 'error' && hasOwn(item, 'cause'))
                defineValue(object, 'cause', read(item.cause), false);
            }
            return read(data.root);
          };
          function sequence(value, method) {
            if (value === undefined) return array();
            if (value === null || (typeof value !== 'object' && typeof value !== 'function'))
              throw new TypeErrorCtor('A transfer list must be an iterable object');
            if (arguments.length < 2) method = value[iteratorSymbol];
            if (typeof method !== 'function') throw new TypeErrorCtor('A transfer list must be iterable');
            const wrapper = create(null);
            defineValue(wrapper, iteratorSymbol, function() { return apply(method, value, []); }, false);
            return apply(arrayFrom, ArrayCtor, [wrapper]);
          }
          function normalizeTransfer(value, postMessage) {
            if (!postMessage) return sequence(value);
            if (value === undefined || value === null) return array();
            if (typeof value !== 'object' && typeof value !== 'function')
              throw new TypeErrorCtor('Message options must be an object');
            const method = value[iteratorSymbol];
            return method === undefined || method === null ? sequence(value.transfer) : sequence(value, method);
          }
          return [encode, decode, function(value) { return freezeObject(value); }, normalizeTransfer];
        })();
        """;
}

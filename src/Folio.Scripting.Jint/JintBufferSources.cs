using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;

namespace Folio.Scripting.Jint;

public sealed partial class JintScriptEngine
{
    private sealed record BufferSourceIntrinsics(
        JsValue Buffer, JsValue ByteLength, JsValue ByteOffset, JsValue Length,
        JsValue Name, JsValue Values, JsValue IteratorSymbol, JsValue DataViewBuffer, JsValue DataViewOffset,
        IReadOnlyDictionary<ScriptBufferSourceKind, JsValue> Constructors);

    private BufferSourceIntrinsics? _bufferSourceIntrinsics;

    private void CaptureBufferSourceIntrinsics()
    {
        var typed = _engine!.Intrinsics.Uint8Array.Get("prototype").AsObject().Prototype!;
        var dataView = _engine.GetValue("DataView").Get("prototype").AsObject();
        var constructors = new Dictionary<ScriptBufferSourceKind, JsValue>();
        foreach (var kind in Enum.GetValues<ScriptBufferSourceKind>())
        {
            if (kind is ScriptBufferSourceKind.ArrayBuffer or ScriptBufferSourceKind.DataView) continue;
            var constructor = _engine.GetValue(kind + "Array");
            if (constructor.IsObject()) constructors.Add(kind, constructor);
        }
        _bufferSourceIntrinsics = new(
            typed.GetOwnProperty("buffer").Get!, typed.GetOwnProperty("byteLength").Get!,
            typed.GetOwnProperty("byteOffset").Get!, typed.GetOwnProperty("length").Get!,
            typed.GetOwnProperty(_engine.GetValue("Symbol").Get("toStringTag")).Get!,
            typed.Get("values"), _engine.GetValue("Symbol").Get("iterator"), dataView.GetOwnProperty("buffer").Get!,
            dataView.GetOwnProperty("byteOffset").Get!, constructors);
    }

    public bool TryGetBufferSourceInfo(ScriptValue value, out ScriptBufferSourceInfo info) =>
        TryDescribeBufferSource(value, out info, out _);

    private bool TryDescribeBufferSource(ScriptValue value, out ScriptBufferSourceInfo info, out JsArrayBuffer? buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        info = default;
        buffer = null;
        if (value.Raw is not ObjectInstance instance || !ReferenceEquals(instance.Engine, _engine)) return false;
        var intrinsics = _bufferSourceIntrinsics!;
        try
        {
            ScriptBufferSourceKind kind;
            int offset, bytes, length;
            if (instance is JsTypedArray)
            {
                // Validate the intrinsic view, including resizable-buffer bounds.
                // Artifact properties named buffer/length/constructor are never read.
                _engine!.Call(intrinsics.Values, instance, []);
                buffer = _engine.Call(intrinsics.Buffer, instance, []) as JsArrayBuffer;
                var name = _engine.Call(intrinsics.Name, instance, []).AsString();
                if (!name.EndsWith("Array", StringComparison.Ordinal)
                    || !Enum.TryParse(name[..^5], out kind)) return false;
                offset = checked((int)_engine.Call(intrinsics.ByteOffset, instance, []).AsNumber());
                bytes = checked((int)_engine.Call(intrinsics.ByteLength, instance, []).AsNumber());
                length = checked((int)_engine.Call(intrinsics.Length, instance, []).AsNumber());
            }
            else if (instance is JsArrayBuffer arrayBuffer && instance.GetType() == typeof(JsArrayBuffer))
            {
                kind = ScriptBufferSourceKind.ArrayBuffer;
                buffer = arrayBuffer;
                offset = 0;
                bytes = length = checked((int)_engine!.Intrinsics.Uint8Array.Construct(buffer, null, null).Length);
            }
            else
            {
                // Jint's DataView class is internal. Intrinsic accessors perform
                // the receiver-brand check without invoking artifact properties.
                kind = ScriptBufferSourceKind.DataView;
                bytes = checked((int)_engine!.Call(_dataViewByteLengthGetter, instance, []).AsNumber());
                offset = checked((int)_engine.Call(intrinsics.DataViewOffset, instance, []).AsNumber());
                buffer = _engine.Call(intrinsics.DataViewBuffer, instance, []) as JsArrayBuffer;
                length = bytes;
            }
            if (buffer is null || buffer.GetType() != typeof(JsArrayBuffer)
                || !ReferenceEquals(buffer.Engine, _engine)
                || _engine!.Call(_arrayBufferDetachedGetter, buffer, []).AsBoolean()) return false;
            var elementSize = ElementSize(kind);
            if (offset < 0 || bytes < 0 || length < 0 || (long)length * elementSize != bytes) return false;
            info = new(kind, bytes, offset, elementSize, length);
            return true;
        }
        catch (JavaScriptException) { buffer = null; return false; }
        catch (OverflowException) { buffer = null; return false; }
        catch (Exception error) { throw TranslateException(error); }
    }

    public bool TryCopyBufferSource(ScriptValue value, int byteOffset, Span<byte> destination)
    {
        if (!TryDescribeBufferSource(value, out var info, out var buffer)
            || byteOffset < 0 || byteOffset > info.ByteLength || destination.Length > info.ByteLength - byteOffset) return false;
        try
        {
            var view = (JsTypedArray)_engine!.Construct(_bufferSourceIntrinsics!.Constructors[ScriptBufferSourceKind.Uint8],
                [buffer!, checked(info.ByteOffset + byteOffset), destination.Length]);
            for (var index = 0; index < destination.Length; index++) destination[index] = (byte)view[index].AsNumber();
            return true;
        }
        catch (JavaScriptException) { return false; }
        catch (OverflowException) { return false; }
        catch (Exception error) { throw TranslateException(error); }
    }

    public bool TryWriteBufferSource(ScriptValue value, int byteOffset, ReadOnlySpan<byte> source)
    {
        if (!TryDescribeBufferSource(value, out var info, out var buffer)
            || byteOffset < 0 || byteOffset > info.ByteLength || source.Length > info.ByteLength - byteOffset) return false;
        try
        {
            var view = (JsTypedArray)_engine!.Construct(_bufferSourceIntrinsics!.Constructors[ScriptBufferSourceKind.Uint8],
                [buffer!, checked(info.ByteOffset + byteOffset), source.Length]);
            for (var index = 0; index < source.Length; index++) view[index] = (int)source[index];
            return true;
        }
        catch (JavaScriptException) { return false; }
        catch (OverflowException) { return false; }
        catch (Exception error) { throw TranslateException(error); }
    }

    public ScriptValue CreateTypedArray(ScriptBufferSourceKind kind, ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_bufferSourceIntrinsics!.Constructors.TryGetValue(kind, out var constructor))
            throw new NotSupportedException($"The script engine does not provide {kind}Array.");
        if (bytes.Length % ElementSize(kind) != 0)
            throw new ArgumentException("The byte length is not a whole number of typed-array elements.", nameof(bytes));
        var buffer = _engine!.Intrinsics.ArrayBuffer.Construct(bytes.ToArray());
        return ToScriptValue(_engine.Construct(constructor, [buffer]));
    }

    private static int ElementSize(ScriptBufferSourceKind kind) => kind switch
    {
        ScriptBufferSourceKind.Int16 or ScriptBufferSourceKind.Uint16 or ScriptBufferSourceKind.Float16 => 2,
        ScriptBufferSourceKind.Int32 or ScriptBufferSourceKind.Uint32 or ScriptBufferSourceKind.Float32 => 4,
        ScriptBufferSourceKind.Float64 or ScriptBufferSourceKind.BigInt64 or ScriptBufferSourceKind.BigUint64 => 8,
        _ => 1,
    };
}

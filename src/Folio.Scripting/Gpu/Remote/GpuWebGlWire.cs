namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// Reply to one batch: magic, batch sequence, session status, the deferred failures of the batch
/// in call order, per-context statistics, then the result of the final (synchronous) command.
/// A synchronous result starts with its own success flag and failure, then its outputs.
/// </summary>
internal static class GpuWebGlWire
{
    internal const int ReplyMagic = 0x31525047; // GPR1
    internal const int MaxReplyBytes = 4 * 1024 * 1024, MaxDeferredFailures = 256;
    internal const int StatusOk = 0, StatusLost = 1;

    internal static void Failure(GpuPayloadWriter writer, GpuFailure? failure)
    {
        writer.I32((int)(failure?.Kind ?? GpuFailureKind.NativeError));
        writer.Bool(failure?.ApiError is not null).U32(failure?.ApiError ?? 0);
        writer.String(failure?.Message ?? "The GPU worker rejected the call.");
    }

    internal static GpuFailure Failure(ref GpuPayloadReader reader)
    {
        var kind = reader.I32();
        if (!Enum.IsDefined((GpuFailureKind)kind)) throw new InvalidDataException("Invalid failure kind.");
        var hasApi = reader.Bool();
        var api = reader.U32();
        return new((GpuFailureKind)kind, reader.String(4096), hasApi ? api : null);
    }

    // Native contexts may fill more than the field their kind names (a binding is UInt32 and Int32,
    // a boolean also sets Int32), and bindings may read either, so every field travels.
    internal static void Parameter(GpuPayloadWriter writer, in WebGlParameterValue value)
    {
        writer.I32((int)value.Kind).I32(value.Int32).U32(value.UInt32).I64(value.Int64).F32(value.Float32).Bool(value.Boolean);
        writer.Bool(value.String is not null).String(value.String);
        writer.Bool(value.Int32Array is not null); Ints(writer, value.Int32Array);
        writer.Bool(value.UInt32Array is not null); UInts(writer, value.UInt32Array);
        writer.Bool(value.Float32Array is not null); Floats(writer, value.Float32Array);
    }

    internal static WebGlParameterValue Parameter(ref GpuPayloadReader reader)
    {
        var kind = (WebGlParameterKind)reader.I32();
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Invalid parameter kind.");
        var int32 = reader.I32(); var uint32 = reader.U32(); var int64 = reader.I64(); var float32 = reader.F32(); var boolean = reader.Bool();
        var hasString = reader.Bool(); var text = reader.String();
        var hasInts = reader.Bool(); var ints = Ints(ref reader);
        var hasUInts = reader.Bool(); var uints = UInts(ref reader);
        var hasFloats = reader.Bool(); var floats = Floats(ref reader);
        return new(kind, int32, uint32, int64, float32, boolean, hasString ? text : null,
            hasInts ? ints : null, hasUInts ? uints : null, hasFloats ? floats : null);
    }

    internal static void Uniform(GpuPayloadWriter writer, in WebGlUniformValue value)
    {
        writer.I32((int)value.Kind);
        writer.Bool(value.Int32 is not null); Ints(writer, value.Int32);
        writer.Bool(value.UInt32 is not null); UInts(writer, value.UInt32);
        writer.Bool(value.Float32 is not null); Floats(writer, value.Float32);
    }

    internal static WebGlUniformValue Uniform(ref GpuPayloadReader reader)
    {
        var kind = (WebGlParameterKind)reader.I32();
        if (!Enum.IsDefined(kind)) throw new InvalidDataException("Invalid uniform kind.");
        var hasInt = reader.Bool(); var ints = Ints(ref reader);
        var hasUInt = reader.Bool(); var uints = UInts(ref reader);
        var hasFloat = reader.Bool(); var floats = Floats(ref reader);
        return new(kind, hasInt ? ints : null, hasUInt ? uints : null, hasFloat ? floats : null);
    }

    internal static void ActiveInfo(GpuPayloadWriter writer, WebGlActiveInfo? info) => writer.String(info?.Name).I32(info?.Size ?? 0).U32(info?.Type ?? 0);
    internal static WebGlActiveInfo ActiveInfo(ref GpuPayloadReader reader) => new(reader.String(), reader.I32(), reader.U32());

    internal static void Ints(GpuPayloadWriter writer, IReadOnlyList<int>? values) { writer.I32(values?.Count ?? 0); if (values is not null) foreach (var value in values) writer.I32(value); }
    internal static void UInts(GpuPayloadWriter writer, IReadOnlyList<uint>? values) { writer.I32(values?.Count ?? 0); if (values is not null) foreach (var value in values) writer.U32(value); }
    internal static void Floats(GpuPayloadWriter writer, IReadOnlyList<float>? values) { writer.I32(values?.Count ?? 0); if (values is not null) foreach (var value in values) writer.F32(value); }
    internal static int[] Ints(ref GpuPayloadReader reader) { var values = new int[Count(ref reader)]; for (var i = 0; i < values.Length; i++) values[i] = reader.I32(); return values; }
    internal static uint[] UInts(ref GpuPayloadReader reader) { var values = new uint[Count(ref reader)]; for (var i = 0; i < values.Length; i++) values[i] = reader.U32(); return values; }
    internal static float[] Floats(ref GpuPayloadReader reader) { var values = new float[Count(ref reader)]; for (var i = 0; i < values.Length; i++) values[i] = reader.F32(); return values; }

    private static int Count(ref GpuPayloadReader reader)
    {
        var count = reader.I32();
        if (count < 0 || count > 1 << 16) throw new InvalidDataException("Invalid array length.");
        return count;
    }
}

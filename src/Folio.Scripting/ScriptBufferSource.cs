namespace Folio.Scripting;

/// <summary>The intrinsic storage brand of a script buffer or view.</summary>
public enum ScriptBufferSourceKind
{
    ArrayBuffer, DataView, Int8, Uint8, Uint8Clamped, Int16, Uint16,
    Int32, Uint32, Float16, Float32, Float64, BigInt64, BigUint64,
}

/// <summary>View metadata, read without invoking artifact-defined properties.</summary>
public readonly record struct ScriptBufferSourceInfo(
    ScriptBufferSourceKind Kind, int ByteLength, int ByteOffset, int ElementSize, int Length);

/// <summary>A branded buffer argument. Storage is copied only after the caller checks its allocation budget.</summary>
public readonly record struct ScriptBufferSource(ScriptValue Value, ScriptBufferSourceInfo Info);

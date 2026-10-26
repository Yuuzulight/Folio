using System.Diagnostics;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Benchmarks;

internal static class TransferBenchmark
{
    internal const int Size = 16 * 1024 * 1024;

    internal static (TimeSpan Elapsed, long Allocated, bool Adopted) Measure16MiB()
    {
        using var sender = new JintScriptEngine();
        using var receiver = new JintScriptEngine();
        sender.Evaluate("globalThis.DOMException = function DOMException(message, name) { this.message = message; this.name = name; };");
        receiver.Evaluate("globalThis.DOMException = function DOMException(message, name) { this.message = message; this.name = name; };");

        var bytes = new byte[Size];
        bytes[0] = 0x5A;
        bytes[^1] = 0xA5;
        var source = sender.CreateArrayBufferOwned(bytes);
        var transfer = sender.CreateArray([source]);
        AssertOwned(sender, source, out var senderBacking);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();
        var payload = StructuredCloneCodec.Encode(sender, source, transfer, Size + 1024, static (_, _) => null);
        var result = StructuredCloneCodec.Decode(receiver, payload, static (_, _) => throw new InvalidOperationException());
        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        AssertOwned(receiver, result.Value, out var receiverBacking);

        return (elapsed, allocated, ReferenceEquals(senderBacking, receiverBacking)
            && receiverBacking[0] == 0x5A && receiverBacking[^1] == 0xA5);
    }

    private static void AssertOwned(IScriptEngine engine, ScriptValue value, out byte[] bytes)
    {
        if (!engine.TryGetArrayBufferBytesOwned(value, out bytes!))
            throw new InvalidOperationException("The engine did not expose an owned ArrayBuffer backing store.");
    }
}

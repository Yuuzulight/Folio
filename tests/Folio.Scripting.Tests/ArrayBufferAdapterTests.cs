using Folio.Scripting;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class ArrayBufferAdapterTests
{
    [Fact]
    public void AnAttachedEmptyBufferIsDifferentFromADetachedBuffer()
    {
        using var engine = new JintScriptEngine();
        var buffer = engine.Evaluate("new ArrayBuffer(0)");
        Assert.True(engine.TryGetArrayBufferBytes(buffer, out var bytes));
        Assert.Empty(bytes);
        Assert.True(engine.TryDetachArrayBuffer(buffer));
        Assert.False(engine.TryGetArrayBufferBytes(buffer, out _));
        Assert.False(engine.TryDetachArrayBuffer(buffer));
    }

    [Fact]
    public void BufferOperationsUseCapturedIntrinsicsAndIgnoreSpoofedBrands()
    {
        using var engine = new JintScriptEngine();
        var buffer = engine.Evaluate("""
            const buffer = new ArrayBuffer(4);
            new Uint8Array(buffer).set([1, 2, 3, 4]);
            buffer.transfer = () => buffer;
            ArrayBuffer.prototype.transfer = () => buffer;
            Object.defineProperty(ArrayBuffer.prototype, 'detached', { get: () => false });
            Object.prototype.toString = () => '[object ArrayBuffer]';
            globalThis.Uint8Array = function() { throw new Error('replaced constructor'); };
            buffer;
            """);
        Assert.True(engine.TryGetArrayBufferBytes(buffer, out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.False(engine.TryGetArrayBufferBytes(engine.Evaluate("({ length: 4 })"), out _));
        Assert.True(engine.TryDetachArrayBuffer(buffer));
        Assert.Equal(0, engine.Evaluate("buffer.byteLength").AsNumber());
        Assert.False(engine.TryGetArrayBufferBytes(buffer, out _));
    }

    [Fact]
    public void BufferHandlesFromAnotherEngineAreRefused()
    {
        using var first = new JintScriptEngine();
        using var second = new JintScriptEngine();
        var buffer = first.Evaluate("new ArrayBuffer(4)");
        Assert.False(second.TryGetArrayBufferBytes(buffer, out _));
        Assert.False(second.TryDetachArrayBuffer(buffer));
        Assert.Equal(4, first.GetProperty(buffer, "byteLength").AsNumber());
    }
}

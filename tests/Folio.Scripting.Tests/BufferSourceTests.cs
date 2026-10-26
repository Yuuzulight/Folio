using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class BufferSourceTests
{
    [Theory]
    [InlineData("Int8Array", ScriptBufferSourceKind.Int8, 1)]
    [InlineData("Uint8Array", ScriptBufferSourceKind.Uint8, 1)]
    [InlineData("Uint8ClampedArray", ScriptBufferSourceKind.Uint8Clamped, 1)]
    [InlineData("Int16Array", ScriptBufferSourceKind.Int16, 2)]
    [InlineData("Uint16Array", ScriptBufferSourceKind.Uint16, 2)]
    [InlineData("Int32Array", ScriptBufferSourceKind.Int32, 4)]
    [InlineData("Uint32Array", ScriptBufferSourceKind.Uint32, 4)]
    [InlineData("Float32Array", ScriptBufferSourceKind.Float32, 4)]
    [InlineData("Float64Array", ScriptBufferSourceKind.Float64, 8)]
    [InlineData("BigInt64Array", ScriptBufferSourceKind.BigInt64, 8)]
    [InlineData("BigUint64Array", ScriptBufferSourceKind.BigUint64, 8)]
    public void ViewsPreserveExactStorageAndOffsets(string constructor, ScriptBufferSourceKind kind, int elementSize)
    {
        using var engine = new JintScriptEngine();
        engine.Evaluate("var buffer = new ArrayBuffer(40); var all = new Uint8Array(buffer); for(var i=0;i<40;i++) all[i]=(i*37+11)%256;");
        var view = engine.Evaluate($"new {constructor}(buffer,8,2)");
        Assert.True(engine.TryGetBufferSourceInfo(view, out var info));
        Assert.Equal(new(kind, elementSize * 2, 8, elementSize, 2), info);
        var bytes = new byte[info.ByteLength];
        Assert.True(engine.TryCopyBufferSource(view, 0, bytes));
        Assert.Equal(Enumerable.Range(8, bytes.Length).Select(index => (byte)((index * 37 + 11) % 256)), bytes);
        var clone = engine.CreateTypedArray(kind, bytes);
        Assert.True(engine.TryGetBufferSourceInfo(clone, out var cloneInfo));
        Assert.Equal(new(kind, bytes.Length, 0, elementSize, 2), cloneInfo);
        var recovered = new byte[bytes.Length];
        Assert.True(engine.TryCopyBufferSource(clone, 0, recovered));
        Assert.Equal(bytes, recovered);
    }

    [Fact]
    public void DataViewWritesAreRelativeAndCannotTouchAdjacentStorage()
    {
        using var engine = new JintScriptEngine();
        var view = engine.Evaluate("var buffer=new ArrayBuffer(12); var all=new Uint8Array(buffer); all.fill(99); new DataView(buffer,3,5)");
        Assert.True(engine.TryGetBufferSourceInfo(view, out var info));
        Assert.Equal(new(ScriptBufferSourceKind.DataView, 5, 3, 1, 5), info);
        Assert.True(engine.TryWriteBufferSource(view, 1, new byte[] { 1, 2, 3 }));
        Assert.False(engine.TryWriteBufferSource(view, 4, new byte[] { 8, 9 }));
        Assert.False(engine.TryWriteBufferSource(view, -1, new byte[] { 8 }));
        Assert.True(engine.TryGetArrayBufferBytes(engine.Evaluate("buffer"), out var all));
        Assert.Equal(new byte[] { 99, 99, 99, 99, 1, 2, 3, 99, 99, 99, 99, 99 }, all);
        var destination = new byte[] { 17, 17 };
        Assert.False(engine.TryCopyBufferSource(view, 4, destination));
        Assert.Equal(new byte[] { 17, 17 }, destination);
    }

    [Fact]
    public void ShadowedPropertiesAndMutatedIntrinsicsCannotForgeBoundsOrBrands()
    {
        using var engine = new JintScriptEngine();
        var view = engine.Evaluate("""
            var calls=0, storage=new ArrayBuffer(16), view=new Float32Array(storage,4,2);
            var proto=Object.getPrototypeOf(Uint8Array.prototype);
            for (var name of ['buffer','length','byteLength','byteOffset']) {
                Object.defineProperty(view,name,{get(){calls++;throw Error('own property')}});
                Object.defineProperty(proto,name,{get(){calls++;throw Error('mutated prototype')}});
            }
            proto.values=function(){calls++;throw Error('mutated iterator')};
            Object.defineProperty(view,Symbol.toStringTag,{get(){calls++;return 'Uint8Array'}});
            globalThis.Uint8Array=function(){calls++;throw Error('mutated constructor')};
            view;
            """);
        Assert.True(engine.TryGetBufferSourceInfo(view, out var info));
        Assert.Equal(new(ScriptBufferSourceKind.Float32, 8, 4, 4, 2), info);
        Assert.True(engine.TryWriteBufferSource(view, 0, new byte[] { 1, 2, 3, 4 }));
        var bytes = new byte[4];
        Assert.True(engine.TryCopyBufferSource(view, 0, bytes));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
        Assert.Equal(0, engine.Evaluate("calls").AsNumber());
        Assert.False(engine.TryGetBufferSourceInfo(engine.Evaluate("new Proxy(view,{get(){calls++;throw Error('proxy')}})"), out _));
        Assert.Equal(0, engine.Evaluate("calls").AsNumber());
    }

    [Theory]
    [InlineData("buffer")]
    [InlineData("new Uint16Array(buffer,2,2)")]
    [InlineData("new DataView(buffer,2,4)")]
    public void DetachedSourcesAreRejectedBeforeReadOrWrite(string expression)
    {
        using var engine = new JintScriptEngine();
        engine.Evaluate("var buffer=new ArrayBuffer(12)");
        var source = engine.Evaluate(expression);
        Assert.True(engine.TryGetBufferSourceInfo(source, out _));
        Assert.True(engine.TryDetachArrayBuffer(engine.Evaluate("buffer")));
        Assert.False(engine.TryGetBufferSourceInfo(source, out _));
        Assert.False(engine.TryCopyBufferSource(source, 0, new byte[1]));
        Assert.False(engine.TryWriteBufferSource(source, 0, new byte[1]));
    }

    [Fact]
    public void ResizedOutOfBoundsViewsAreRejectedAndRecoverAfterRegrowth()
    {
        using var engine = new JintScriptEngine();
        var source = engine.Evaluate("var buffer=new ArrayBuffer(16,{maxByteLength:32}); var view=new Uint16Array(buffer,8,2); view");
        Assert.True(engine.TryGetBufferSourceInfo(source, out _));
        engine.Evaluate("buffer.resize(4)");
        Assert.False(engine.TryGetBufferSourceInfo(source, out _));
        engine.Evaluate("buffer.resize(16)");
        Assert.True(engine.TryGetBufferSourceInfo(source, out var info));
        Assert.Equal(new(ScriptBufferSourceKind.Uint16, 4, 8, 2, 2), info);
    }

    [Fact]
    public void ForeignAndRetiredRealmValuesAreRejected()
    {
        using var engine = new JintScriptEngine();
        using var other = new JintScriptEngine();
        var source = other.Evaluate("new Uint8Array(4)");
        Assert.False(engine.TryGetBufferSourceInfo(source, out _));
        Assert.False(engine.TryCopyBufferSource(source, 0, new byte[4]));
        var retired = engine.Evaluate("new Uint8Array(4)");
        engine.Configure(ScriptLimits.Default);
        Assert.False(engine.TryGetBufferSourceInfo(retired, out _));
    }
}

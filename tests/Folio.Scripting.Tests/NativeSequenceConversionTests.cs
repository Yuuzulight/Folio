using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeSequenceConversionTests
{
    [Fact]
    public void BufferViewsShareStorageWhileTypedCopiesConvertElements()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("7:2:4", engine.Evaluate("var storage=new ArrayBuffer(8);var all=new Uint8Array(storage);var view=new Uint8Array(storage,2,4);view[0]=7;all[2]+':'+view.byteOffset+':'+view.byteLength;").AsString());
        Assert.Equal("3:4", engine.Evaluate("var source=new Int32Array([3,4]);var copy=new Float32Array(source);source[0]=99;copy[0]+':'+copy[1];").AsString());
    }

    [Theory]
    [InlineData("new ArrayBuffer(-1)")]
    [InlineData("new Uint8Array(Infinity)")]
    [InlineData("new Uint32Array(new ArrayBuffer(8),1)")]
    [InlineData("new Uint16Array(new ArrayBuffer(3))")]
    [InlineData("new Uint32Array(new ArrayBuffer(8),4,2)")]
    public void InvalidBufferLengthsAndAlignmentAreRejected(string source)
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Throws<WebIdlException>(() => engine.Evaluate(source));
    }

    [Theory]
    [InlineData("Uint8Array")]
    [InlineData("Int32Array")]
    [InlineData("Float32Array")]
    [InlineData("Float64Array")]
    public void TypedSequencesRespectSubarrayOffsets(string type)
    {
        using var engine = new NativeJavaScriptEngine();
        var items = new List<double>();
        Assert.True(engine.TryReadSequence(engine.Evaluate($"new {type}([99,2,3,88]).subarray(1,3)"), item => items.Add(item.AsNumber())));
        Assert.Equal(new[] { 2d, 3d }, items);
        Assert.False(engine.TryReadSequence(engine.Evaluate("new ArrayBuffer(8)"), _ => throw new Exception()));
        Assert.False(engine.TryReadSequence(engine.Evaluate("new DataView(new ArrayBuffer(8))"), _ => throw new Exception()));
    }

    [Fact]
    public void DetachedTypedSequenceIsRejected()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("var bytes=new ArrayBuffer(4);new Uint8Array(bytes)");
        Assert.True(engine.TryDetachArrayBuffer(engine.GetGlobalProperty("bytes")));
        Assert.Throws<WebIdlException>(() => engine.TryReadSequence(value, _ => throw new Exception()));
    }

    [Fact]
    public void IteratorReadsNextOnceAndUsesBigIntTruthiness()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("var reads=0,step=0;({[Symbol.iterator](){return {get next(){reads++;return function(){return step++===0?{done:0n,value:7}:{done:1n};};}};}})");
        var items = new List<double>();
        Assert.True(engine.TryReadSequence(value, item => items.Add(item.AsNumber())));
        Assert.Equal(new[] { 7d }, items);
        Assert.Equal(1, engine.Evaluate("reads").AsNumber());
        Assert.False(engine.Evaluate("Boolean(0n)").AsBoolean());
        Assert.False(engine.CoerceBoolean(engine.Evaluate("0n")));
    }

    [Fact]
    public void ConversionFailureClosesIteratorAndPreservesOriginalError()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("var closed=0;({[Symbol.iterator](){return {next(){return {done:false,value:3};},return(){closed++;throw Error('cleanup failed');}};}})");
        var original = new InvalidOperationException("conversion failed");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => engine.TryReadSequence(value, _ => throw original)));
        Assert.Equal(1, engine.Evaluate("closed").AsNumber());
    }

    [Fact]
    public void IteratorStepFailureDoesNotCallReturn()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("var closed=0;({[Symbol.iterator](){return {next(){throw Error('step failed');},return(){closed++;return {};}};}})");
        Assert.ThrowsAny<Exception>(() => engine.TryReadSequence(value, _ => { }));
        Assert.Equal(0, engine.Evaluate("closed").AsNumber());
    }

    [Fact]
    public void SequenceAllocationLimitClosesAnUnboundedIterator()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("var closed=0;({[Symbol.iterator](){return {next(){return {done:false,value:3};},return(){closed++;return {};}};}})");
        // Leave room for the iterator method's own bounded execution. The
        // sequence's cumulative element admission must be what fails here.
        engine.Configure(ScriptLimits.Default with { MemoryLimitBytes = 32 * 1024 });
        var admitted = 0;
        var failure = Assert.Throws<ScriptLimitExceededException>(() => engine.TryReadSequence(value, _ => admitted++));
        Assert.Equal("Memory", failure.LimitName);
        Assert.Equal(512, admitted);
        engine.Configure(ScriptLimits.Default);
        Assert.Equal(1, engine.Evaluate("closed").AsNumber());
    }
}

using Folio.Scripting.Dom;
using Folio.Scripting.Gpu;

namespace Folio.Scripting.Tests;

public sealed class WebGlObjectRegistryTests
{
    [Fact]
    public void DeletingAttachedShaderDefersNativeDeleteUntilDetach()
    {
        var native = new NativeDouble();
        using var registry = new WebGlObjectRegistry(native);
        var shader = Assert.IsType<WebGlShaderObject>(registry.CreateShader(0x8B31, out _));
        var program = Assert.IsType<WebGlProgramObject>(registry.CreateProgram(out _));

        program.Attach(shader);
        shader.Delete();
        Assert.Equal(0, native.DeleteShaderCalls);

        program.Detach(shader);
        Assert.Equal(1, native.DeleteShaderCalls);
    }

    [Fact]
    public void UniformLocationIsInvalidAfterRelinkAndSameNumericLocationDoesNotCollide()
    {
        using var registry = new WebGlObjectRegistry(new NativeDouble());
        var program = Assert.IsType<WebGlProgramObject>(registry.CreateProgram(out _));
        var before = Assert.IsType<WebGlUniformLocationObject>(registry.CreateUniformLocation(program, 3));

        Assert.True(program.Link(out _));
        Assert.Throws<WebGlValidationException>(before.EnsureCurrent);

        var after = Assert.IsType<WebGlUniformLocationObject>(registry.CreateUniformLocation(program, 3));
        Assert.NotSame(before, after);
        after.EnsureCurrent();
    }

    [Fact]
    public void ForeignObjectIsInternalValidationFailureAndDuplicateAttachIsRejected()
    {
        using var first = new WebGlObjectRegistry(new NativeDouble());
        using var second = new WebGlObjectRegistry(new NativeDouble());
        var shader = Assert.IsType<WebGlShaderObject>(first.CreateShader(0x8B31, out _));
        var program = Assert.IsType<WebGlProgramObject>(second.CreateProgram(out _));

        var foreign = Assert.Throws<WebGlValidationException>(() => program.Attach(shader));
        Assert.Equal(WebGlValidationFailure.InvalidOperation, foreign.Failure);

        var localShader = Assert.IsType<WebGlShaderObject>(second.CreateShader(0x8B31, out _));
        program.Attach(localShader);
        var duplicate = Assert.Throws<WebGlValidationException>(() => program.Attach(localShader));
        Assert.Equal(WebGlValidationFailure.InvalidOperation, duplicate.Failure);
    }

    [Fact]
    public void AdapterConvertsParameterReportsCompileLogAndQueuesValidationError()
    {
        var native = new NativeDouble { CompileResult = false, CompileLog = "syntax error", ShaderParameter = new WebGlParameterValue(WebGlParameterKind.Boolean, Boolean: true) };
        using var registry = new WebGlObjectRegistry(native);
        var diagnostics = new List<string>();
        var adapter = new Adapter(registry, diagnostics.Add);
        var shader = Assert.IsType<WebGlShaderObject>(adapter.CreateShader(0x8B31));

        Assert.True(adapter.GetShaderParameter(shader, 0x8B81).AsBoolean());
        adapter.CompileShader(shader);
        Assert.Contains("syntax error", Assert.Single(diagnostics));

        using var other = new WebGlObjectRegistry(new NativeDouble());
        var foreign = Assert.IsType<WebGlShaderObject>(other.CreateShader(0x8B31, out _));
        Assert.True(adapter.GetShaderParameter(foreign, 0x8B81).IsUndefined);
        Assert.Equal(0x0502u, adapter.GetError());
    }

    [Fact]
    public void AdapterForwardsBoundedBufferAllocationAndQueuesNativeFailure()
    {
        var native = new NativeDouble { AllocationResult = true };
        using var registry = new WebGlObjectRegistry(native);
        var adapter = new Adapter(registry, _ => { });
        adapter.BufferData(0x8892, 32, 0x88E4);
        Assert.Equal(32, native.LastAllocationSize);

        native.AllocationResult = false;
        adapter.BufferData(0x8892, 64, 0x88E4);
        Assert.Equal(0x0502u, adapter.GetError());
    }

    [Fact]
    public void AdapterTreatsNullUniformAsNoOpAndRetainedForeignOrDeletedHandlesAsGetError()
    {
        using var first = new WebGlObjectRegistry(new NativeDouble());
        using var second = new WebGlObjectRegistry(new NativeDouble());
        var adapter = new Adapter(first, _ => { });
        var foreign = Assert.IsType<WebGlBufferObject>(second.CreateBuffer(out _));
        var local = Assert.IsType<WebGlBufferObject>(first.CreateBuffer(out _));

        adapter.Uniform1f(null, 1);
        Assert.Equal(0u, adapter.GetError());

        adapter.BindBuffer(0x8892, foreign);
        Assert.Equal(0x0502u, adapter.GetError());
        local.Delete();
        adapter.BindBuffer(0x8892, local);
        Assert.Equal(0x0502u, adapter.GetError());
    }

    [Fact]
    public void ExtensionObjectIdentityIsStableAcrossRepeatedLookup()
    {
        var native = new NativeDouble();
        using var registry = new WebGlObjectRegistry(native);
        var adapter = new Adapter(registry, _ => { });
        var first = adapter.GetExtension("WEBGL_lose_context");
        var second = adapter.GetExtension("WEBGL_lose_context");
        Assert.True(first.IsObject);
        Assert.Same(first.Raw, second.Raw);
    }

    [Fact]
    public void AdapterPreservesNativeErrorCodeAndMapsBudgetFailureToOutOfMemory()
    {
        var native = new NativeDouble { ShaderParameterFailure = new GpuFailure(GpuFailureKind.NativeError, "bad enum", 0x0500) };
        using var registry = new WebGlObjectRegistry(native);
        var adapter = new Adapter(registry, _ => { });
        var shader = Assert.IsType<WebGlShaderObject>(adapter.CreateShader(0x8B31));
        Assert.True(adapter.GetShaderParameter(shader, 0xDEAD).IsUndefined);
        Assert.Equal(0x0500u, adapter.GetError());

        native.ShaderParameterFailure = new GpuFailure(GpuFailureKind.BudgetExceeded, "out of memory");
        Assert.True(adapter.GetShaderParameter(shader, 0xDEAD).IsUndefined);
        Assert.Equal(0x0505u, adapter.GetError());
    }

    private sealed class Adapter(WebGlObjectRegistry registry, Action<string> diagnostics) : WebGlBindingAdapter(registry, diagnosticSink: diagnostics);

    private sealed class NativeDouble : NativeWebGlTestDouble
    {
        private uint _next = 10;
        public int DeleteShaderCalls { get; private set; }
        public bool CompileResult { get; init; } = true;
        public string CompileLog { get; init; } = string.Empty;
        public WebGlParameterValue ShaderParameter { get; init; }
        public GpuFailure? ShaderParameterFailure { get; set; }
        public bool AllocationResult { get; set; } = true;
        public long LastAllocationSize { get; private set; }
        public override bool IsLost => false;
        public override WebGlContextAttributes Attributes => new(WebGlContextVersion.WebGl2, true, true, false, true, true, false);
        public override int DrawingBufferWidth => 1;
        public override int DrawingBufferHeight => 1;
        public override bool TryGetExtensions(out IReadOnlyList<string> extensions, out GpuFailure? failure)
        { extensions = ["WEBGL_lose_context"]; failure = null; return true; }
        public override bool TryCreateShader(uint type, out WebGlShaderHandle shader, out GpuFailure? failure)
        { shader = new(++_next); failure = null; return true; }
        public override bool TryCreateBuffer(out WebGlBufferHandle buffer, out GpuFailure? failure)
        { buffer = new(++_next); failure = null; return true; }
        public override bool TryDeleteBuffer(WebGlBufferHandle buffer, out GpuFailure? failure)
        { failure = null; return true; }
        public override bool TryCreateProgram(out WebGlProgramHandle program, out GpuFailure? failure)
        { program = new(++_next); failure = null; return true; }
        public override bool TryAttachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure)
        { failure = null; return true; }
        public override bool TryDetachShader(WebGlProgramHandle program, WebGlShaderHandle shader, out GpuFailure? failure)
        { failure = null; return true; }
        public override bool TryDeleteShader(WebGlShaderHandle shader, out GpuFailure? failure)
        { DeleteShaderCalls++; failure = null; return true; }
        public override bool TryGetShaderParameter(WebGlShaderHandle shader, uint parameter, out WebGlParameterValue value, out GpuFailure? failure)
        { value = ShaderParameter; failure = ShaderParameterFailure; return failure is null; }
        public override bool TryCompileShader(WebGlShaderHandle shader, out bool compiled, out string infoLog, out GpuFailure? failure)
        { compiled = CompileResult; infoLog = CompileLog; failure = null; return true; }
        public override bool TryAllocateBufferStorage(uint target, long size, uint usage, out GpuFailure? failure)
        { LastAllocationSize = size; failure = AllocationResult ? null : new GpuFailure(GpuFailureKind.NativeError, "allocation failed"); return AllocationResult; }
        public override bool TryLinkProgram(WebGlProgramHandle program, out bool linked, out string infoLog, out GpuFailure? failure)
        { linked = true; infoLog = string.Empty; failure = null; return true; }
        public override bool TryDeleteProgram(WebGlProgramHandle program, out GpuFailure? failure)
        { failure = null; return true; }
        public override void Dispose() { }
    }
}

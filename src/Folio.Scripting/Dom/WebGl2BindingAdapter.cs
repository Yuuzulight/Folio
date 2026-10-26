using Folio.Scripting.Bindings;
using Folio.Scripting.Gpu;
using Folio.Scripting;
using System.Text;

namespace Folio.Scripting.Dom;

internal sealed class WebGl2BindingAdapter : WebGlBindingAdapter, IWebGL2RenderingContextTarget
{
    internal WebGl2BindingAdapter(WebGlObjectRegistry registry, IScriptEngine? engine = null, Action<string>? diagnosticSink = null, ScriptValue canvas = default, Action? present = null, Action? loseContext = null, Func<bool>? restoreContext = null)
        : base(registry, engine, diagnosticSink, canvas, present: present, loseContext: loseContext, restoreContext: restoreContext) { }

    public IReadOnlyList<uint> GetUniformIndices(IWebGLProgramTarget program, IReadOnlyList<string> names)
    {
        try
        {
            var value = RequireProgram(program);
            var indices = new uint[names.Count];
            if (!Native.TryGetUniformIndices(value.Handle, names, indices, out var failure)) { RecordNativeFailure(failure); return []; }
            return indices;
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); return []; }
    }
    public IReadOnlyList<uint> GetActiveUniforms(IWebGLProgramTarget program, IReadOnlyList<uint> indices, uint parameter)
    {
        try
        {
            var value = RequireProgram(program);
            var result = new int[indices.Count];
            if (!Native.TryGetActiveUniforms(value.Handle, indices.ToArray(), parameter, result, out var failure)) { RecordNativeFailure(failure); return []; }
            return result.Select(static item => unchecked((uint)item)).ToArray();
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); return []; }
    }
    public uint GetUniformBlockIndex(IWebGLProgramTarget program, string name)
    {
        try
        {
            var value = RequireProgram(program);
            if (!Native.TryGetUniformBlockIndex(value.Handle, Encoding.UTF8.GetBytes(name), out var index, out var failure)) { RecordNativeFailure(failure); return uint.MaxValue; }
            return index;
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); return uint.MaxValue; }
    }
    public void UniformBlockBinding(IWebGLProgramTarget program, uint index, uint binding)
    {
        try
        {
            var value = RequireProgram(program);
            if (!Native.TryUniformBlockBinding(value.Handle, index, binding, out var failure)) RecordNativeFailure(failure);
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); }
    }
    public void BindBufferBase(uint target, uint index, IWebGLBufferTarget? buffer)
    {
        try
        {
            WebGlBufferHandle? handle = null;
            if (buffer is not null) handle = RequireBuffer(buffer).Handle;
            if (!Native.TryBindBufferBase(target, index, handle, out var failure)) RecordNativeFailure(failure);
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); }
    }
    public void BindBufferRange(uint target, uint index, IWebGLBufferTarget? buffer, int offset, int size)
    {
        try
        {
            WebGlBufferHandle? handle = null;
            if (buffer is not null) handle = RequireBuffer(buffer).Handle;
            if (!Native.TryBindBufferRange(target, index, handle, offset, size, out var failure)) RecordNativeFailure(failure);
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); }
    }

    public IWebGLTransformFeedbackTarget? CreateTransformFeedback()
    {
        try
        {
            var value = Registry.CreateTransformFeedback(out var failure);
            if (value is null) { RecordNativeFailure(failure); return null; }
            return value;
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); return null; }
    }
    public void DeleteTransformFeedback(IWebGLTransformFeedbackTarget? feedback)
    {
        if (feedback is null) return;
        try { RequireTransformFeedback(feedback).Delete(); }
        catch (WebGlValidationException error) { RecordValidationFailure(error); }
    }
    public bool IsTransformFeedback(IWebGLTransformFeedbackTarget? feedback) => feedback is WebGlTransformFeedbackObject value && value.Owner == Registry && !value.Deleted;
    public void BindTransformFeedback(uint target, IWebGLTransformFeedbackTarget? feedback)
    {
        try
        {
            WebGlTransformFeedbackHandle? handle = feedback is null ? null : RequireTransformFeedback(feedback).Handle;
            if (!Native.TryBindTransformFeedback(target, handle, out var failure)) RecordNativeFailure(failure);
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); }
    }
    public void BeginTransformFeedback(uint primitiveMode) { if (!Native.TryBeginTransformFeedback(primitiveMode, out var failure)) RecordNativeFailure(failure); }
    public void EndTransformFeedback() { if (!Native.TryEndTransformFeedback(out var failure)) RecordNativeFailure(failure); }
    public void PauseTransformFeedback() { if (!Native.TryPauseTransformFeedback(out var failure)) RecordNativeFailure(failure); }
    public void ResumeTransformFeedback() { if (!Native.TryResumeTransformFeedback(out var failure)) RecordNativeFailure(failure); }
    public void TransformFeedbackVaryings(IWebGLProgramTarget program, IReadOnlyList<string> varyings, uint bufferMode)
    {
        try
        {
            var value = RequireProgram(program);
            if (!Native.TryTransformFeedbackVaryings(value.Handle, varyings, bufferMode, out var failure)) RecordNativeFailure(failure);
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); }
    }
    public IWebGLActiveInfoTarget? GetTransformFeedbackVarying(IWebGLProgramTarget program, uint index)
    {
        try
        {
            var value = RequireProgram(program);
            if (!Native.TryGetTransformFeedbackVarying(value.Handle, index, out var info, out var failure)) { RecordNativeFailure(failure); return null; }
            return new WebGlActiveInfoObject(info);
        }
        catch (WebGlValidationException error) { RecordValidationFailure(error); return null; }
    }
    public void ClearBufferfv(uint buffer, int drawbuffer, IReadOnlyList<float> values)
    {
        if (!Native.TryClearBufferfv(buffer, drawbuffer, values.ToArray(), out var failure)) RecordNativeFailure(failure);
    }
    public void ClearBufferiv(uint buffer, int drawbuffer, IReadOnlyList<int> values)
    {
        if (!Native.TryClearBufferiv(buffer, drawbuffer, values.ToArray(), out var failure)) RecordNativeFailure(failure);
    }
    public void ClearBufferuiv(uint buffer, int drawbuffer, IReadOnlyList<uint> values)
    {
        if (!Native.TryClearBufferuiv(buffer, drawbuffer, values.ToArray(), out var failure)) RecordNativeFailure(failure);
    }
    public void ClearBufferfi(uint buffer, int drawbuffer, float depth, int stencil)
    {
        if (!Native.TryClearBufferfi(buffer, drawbuffer, depth, stencil, out var failure)) RecordNativeFailure(failure);
    }
    private WebGlProgramObject RequireProgram(IWebGLProgramTarget target)
    {
        if (target is not WebGlProgramObject value) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL program target is invalid.");
        Registry.Validate(value);
        return value;
    }
    private WebGlBufferObject RequireBuffer(IWebGLBufferTarget target)
    {
        if (target is not WebGlBufferObject value) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL buffer target is invalid.");
        Registry.Validate(value);
        return value;
    }
    private WebGlTransformFeedbackObject RequireTransformFeedback(IWebGLTransformFeedbackTarget target)
    {
        if (target is not WebGlTransformFeedbackObject value) throw new WebGlValidationException(WebGlValidationFailure.InvalidOperation, "The WebGL transform-feedback target is invalid.");
        Registry.Validate(value);
        return value;
    }

    private void RecordValidationFailure(WebGlValidationException error) =>
        RecordNativeFailure(new GpuFailure(error.Failure == WebGlValidationFailure.ContextLost ? GpuFailureKind.Unavailable : GpuFailureKind.NativeError, error.Message));
}

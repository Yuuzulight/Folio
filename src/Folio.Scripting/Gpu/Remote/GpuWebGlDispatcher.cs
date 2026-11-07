using System.Runtime.InteropServices;
using System.Text;
using Op = Folio.Scripting.Gpu.Remote.GpuWebGlOp;

namespace Folio.Scripting.Gpu.Remote;

/// <summary>
/// GPU-worker end of a session. Executes validated batches against a real native WebGL factory.
/// Client handles are mapped per context and per object kind; an unknown or mismatched handle is
/// an INVALID_OPERATION failure for that call (a failed creation leaves its handle unmapped), while
/// a malformed batch or payload loses the whole session.
/// </summary>
internal sealed class GpuWebGlDispatcher : IDisposable
{
    private const int MaxContexts = 64, MaxHandlesPerContext = 1 << 20, MaxShaderSource = 1_000_000;
    private const uint InvalidOperation = 0x0502;
    private readonly INativeWebGlFactory _factory;
    private readonly GpuArena _upload, _readback;
    private readonly Dictionary<uint, Context> _contexts = [];
    private readonly GpuPayloadWriter _reply = new(), _result = new();
    private readonly List<(uint Context, GpuFailure Failure)> _failures = [];
    private readonly HashSet<uint> _touched = [];
    private bool _lost;

    internal GpuWebGlDispatcher(INativeWebGlFactory factory, GpuArena upload, GpuArena readback)
    {
        _factory = factory; _upload = upload; _readback = readback;
    }

    internal bool IsLost => _lost;

    internal byte[] Execute(ReadOnlySpan<byte> batch)
    {
        var sequence = batch.Length >= 8 ? BitConverter.ToInt32(batch[4..]) : 0;
        if (_lost || !GpuCommandBatch.TryValidate(batch, _upload.Length, out var header)) return Lost(sequence);
        _failures.Clear(); _touched.Clear(); _result.Reset();
        try
        {
            var reader = new GpuCommandBatch.Reader(batch);
            for (var index = 0; reader.MoveNext(); index++)
            {
                var last = index == header.CommandCount - 1;
                var payload = new GpuPayloadReader(reader.Current.Payload);
                var op = (Op)reader.Current.Opcode;
                var contextId = payload.U32();
                var sync = header.Sync && last;
                _result.Reset();
                if (op == Op.CreateContext) CreateContext(contextId, ref payload);
                else if (!_contexts.TryGetValue(contextId, out var context)) { SkipArguments(ref payload); Failed(Unknown("context")); }
                else { _touched.Add(contextId); Run(context, op, ref payload, header); }
                if (!payload.AtEnd) throw new InvalidDataException("Trailing GPU command data.");
                if (!sync && _result.Length > 0 && !IsDeferredOk(_result.Written)) MoveResultFailureToDeferred(contextId);
                if (!sync) _result.Reset();
            }
            return Reply(header.Sequence);
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException) { return Lost(header.Sequence); }
    }

    // ---- per-command execution --------------------------------------------------------------
    private void Run(Context c, Op op, ref GpuPayloadReader p, in GpuCommandBatch.Header h)
    {
        var n = c.Native;
        GpuFailure? f = null;
        switch (op)
        {
            case Op.DestroyContext: n.Dispose(); _contexts.Remove(c.Id); Ok(); return;
            case Op.CreateShader: { var id = p.U32(); var type = p.U32(); Created(c, id, Kind.Shader, n.TryCreateShader(type, out var x, out f), x.Value, f); return; }
            case Op.SetShaderSource: { var s = Shader(c, ref p); var src = p.Blob(h, _upload, MaxShaderSource); Done(s is { } v && n.TrySetShaderSource(v, src, out f), s, f); return; }
            case Op.CompileShader: { var s = Shader(c, ref p); if (s is not { } v) { Missing(); return; } if (!n.TryCompileShader(v, out var ok, out var log, out f)) { Failed(f); return; } Ok().Bool(ok).String(log); return; }
            case Op.GetShaderParameter: { var s = Shader(c, ref p); var q = p.U32(); if (s is not { } v) { Missing(); return; } if (!n.TryGetShaderParameter(v, q, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Parameter(Ok(), val); return; }
            case Op.GetShaderSource: { var s = Shader(c, ref p); if (s is not { } v) { Missing(); return; } if (!n.TryGetShaderSource(v, out var val, out f)) { Failed(f); return; } Ok().String(val); return; }
            case Op.GetShaderInfoLog: { var s = Shader(c, ref p); if (s is not { } v) { Missing(); return; } if (!n.TryGetShaderInfoLog(v, out var val, out f)) { Failed(f); return; } Ok().String(val); return; }
            case Op.GetShaderPrecisionFormat: { var a = p.U32(); var b = p.U32(); if (!n.TryGetShaderPrecisionFormat(a, b, out var min, out var max, out var precision, out f)) { Failed(f); return; } Ok().I32(min).I32(max).I32(precision); return; }
            case Op.DeleteShader: { var id = p.U32(); var s = Shader(c, id); Done(s is { } v && n.TryDeleteShader(v, out f), s, f); if (s is not null) c.Unmap(id); return; }
            case Op.CreateProgram: { var id = p.U32(); Created(c, id, Kind.Program, n.TryCreateProgram(out var x, out f), x.Value, f); return; }
            case Op.DeleteProgram: { var id = p.U32(); var g = Program(c, id); Done(g is { } v && n.TryDeleteProgram(v, out f), g, f); if (g is not null) c.Unmap(id); return; }
            case Op.AttachShader: { var g = Program(c, ref p); var s = Shader(c, ref p); Done(g is { } a && s is { } b && n.TryAttachShader(a, b, out f), g is null || s is null ? null : 1, f); return; }
            case Op.DetachShader: { var g = Program(c, ref p); var s = Shader(c, ref p); Done(g is { } a && s is { } b && n.TryDetachShader(a, b, out f), g is null || s is null ? null : 1, f); return; }
            case Op.LinkProgram: { var g = Program(c, ref p); if (g is not { } v) { Missing(); return; } if (!n.TryLinkProgram(v, out var ok, out var log, out f)) { Failed(f); return; } Ok().Bool(ok).String(log); return; }
            case Op.ValidateProgram: { var g = Program(c, ref p); if (g is not { } v) { Missing(); return; } if (!n.TryValidateProgram(v, out var ok, out var log, out f)) { Failed(f); return; } Ok().Bool(ok).String(log); return; }
            case Op.GetProgramParameter: { var g = Program(c, ref p); var q = p.U32(); if (g is not { } v) { Missing(); return; } if (!n.TryGetProgramParameter(v, q, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Parameter(Ok(), val); return; }
            case Op.GetProgramInfoLog: { var g = Program(c, ref p); if (g is not { } v) { Missing(); return; } if (!n.TryGetProgramInfoLog(v, out var val, out f)) { Failed(f); return; } Ok().String(val); return; }
            case Op.GetAttachedShaders:
            {
                var g = Program(c, ref p); var capacity = Capacity(p.I32(), 64);
                if (g is not { } v) { Missing(); return; }
                var shaders = new WebGlShaderHandle[capacity];
                if (!n.TryGetAttachedShaders(v, shaders, out var count, out f)) { Failed(f); return; }
                var ids = new List<uint>();
                for (var i = 0; i < Math.Min(count, capacity); i++) if (c.Reverse(Kind.Shader, shaders[i].Value) is { } id) ids.Add(id);
                GpuWebGlWire.UInts(Ok().I32(count), ids); return;
            }
            case Op.GetActiveAttrib: { var g = Program(c, ref p); var i = p.U32(); if (g is not { } v) { Missing(); return; } if (!n.TryGetActiveAttrib(v, i, out var info, out f)) { Failed(f); return; } GpuWebGlWire.ActiveInfo(Ok(), info); return; }
            case Op.GetActiveUniform: { var g = Program(c, ref p); var i = p.U32(); if (g is not { } v) { Missing(); return; } if (!n.TryGetActiveUniform(v, i, out var info, out f)) { Failed(f); return; } GpuWebGlWire.ActiveInfo(Ok(), info); return; }
            case Op.BindAttribLocation: { var g = Program(c, ref p); var i = p.U32(); var name = p.Blob(h, _upload, 4096); Done(g is { } v && n.TryBindAttribLocation(v, i, name, out f), g, f); return; }
            case Op.UseProgram: { var g = OptionalProgram(c, ref p, out var bad); Done(!bad && n.TryUseProgram(g, out f), bad ? null : 1, f); return; }
            case Op.GetAttribLocation: { var g = Program(c, ref p); var name = Encoding.UTF8.GetString(p.Blob(h, _upload, 4096)); if (g is not { } v) { Missing(); return; } if (!n.TryGetAttribLocation(v, name, out var l, out f)) { Failed(f); return; } Ok().I32(l.Value); return; }
            case Op.GetUniformLocation: { var g = Program(c, ref p); var name = Encoding.UTF8.GetString(p.Blob(h, _upload, 4096)); if (g is not { } v) { Missing(); return; } if (!n.TryGetUniformLocation(v, name, out var l, out f)) { Failed(f); return; } Ok().I32(l.Value); return; }
            case Op.GetUniformIndices:
            {
                var g = Program(c, ref p); var length = Capacity(p.I32(), 4096);
                var encoded = p.Blob(h, _upload, 1 << 20); var names = new GpuPayloadReader(encoded).Strings();
                if (g is not { } v) { Missing(); return; }
                var indices = new uint[length];
                if (!n.TryGetUniformIndices(v, names, indices, out f)) { Failed(f); return; }
                GpuWebGlWire.UInts(Ok(), indices); return;
            }
            case Op.GetActiveUniforms:
            {
                var g = Program(c, ref p); var q = p.U32(); var length = Capacity(p.I32(), 4096);
                var indices = MemoryMarshal.Cast<byte, uint>(p.Blob(h, _upload, 4096 * 4)).ToArray();
                if (g is not { } v) { Missing(); return; }
                var values = new int[length];
                if (!n.TryGetActiveUniforms(v, indices, q, values, out f)) { Failed(f); return; }
                GpuWebGlWire.Ints(Ok(), values); return;
            }
            case Op.GetUniformBlockIndex: { var g = Program(c, ref p); var name = p.Blob(h, _upload, 4096); if (g is not { } v) { Missing(); return; } if (!n.TryGetUniformBlockIndex(v, name, out var i, out f)) { Failed(f); return; } Ok().U32(i); return; }
            case Op.UniformBlockBinding: { var g = Program(c, ref p); var i = p.U32(); var b = p.U32(); Done(g is { } v && n.TryUniformBlockBinding(v, i, b, out f), g, f); return; }

            case Op.CreateBuffer: { var id = p.U32(); Created(c, id, Kind.Buffer, n.TryCreateBuffer(out var x, out f), x.Value, f); return; }
            case Op.DeleteBuffer: { var id = p.U32(); var b = Handle(c, id, Kind.Buffer); Done(b is { } v && n.TryDeleteBuffer(new(v), out f), b, f); if (b is not null) c.Unmap(id); return; }
            case Op.BindBuffer: { var t = p.U32(); var b = Optional(c, ref p, Kind.Buffer, out var bad); Done(!bad && n.TryBindBuffer(t, b is { } v ? new WebGlBufferHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.AllocateBufferStorage: { var t = p.U32(); var size = p.I64(); var u = p.U32(); Done(n.TryAllocateBufferStorage(t, size, u, out f), 1, f); return; }
            case Op.UploadBuffer: { var t = p.U32(); var u = p.U32(); var data = p.Blob(h, _upload, int.MaxValue); Done(n.TryUploadBuffer(t, data, u, out f), 1, f); return; }
            case Op.UpdateBuffer: { var t = p.U32(); var o = p.I32(); var data = p.Blob(h, _upload, int.MaxValue); Done(n.TryUpdateBuffer(t, o, data, out f), 1, f); return; }
            case Op.CopyBuffer: { var a = p.U32(); var b = p.U32(); var ro = p.I64(); var wo = p.I64(); var size = p.I64(); Done(n.TryCopyBuffer(a, b, ro, wo, size, out f), 1, f); return; }
            case Op.BindBufferBase: { var t = p.U32(); var i = p.U32(); var b = Optional(c, ref p, Kind.Buffer, out var bad); Done(!bad && n.TryBindBufferBase(t, i, b is { } v ? new WebGlBufferHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.BindBufferRange: { var t = p.U32(); var i = p.U32(); var b = Optional(c, ref p, Kind.Buffer, out var bad); var o = p.I64(); var size = p.I64(); Done(!bad && n.TryBindBufferRange(t, i, b is { } v ? new WebGlBufferHandle(v) : null, o, size, out f), bad ? null : 1, f); return; }
            case Op.GetBufferParameter: { var t = p.U32(); var q = p.U32(); if (!n.TryGetBufferParameter(t, q, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Parameter(Ok(), val); return; }
            case Op.GetBufferSubData: { var t = p.U32(); var o = p.I64(); var length = ReadbackLength(p.I32()); var bytes = new byte[length]; if (!n.TryReadBuffer(t, o, bytes, out f)) { Failed(f); return; } Readback(bytes); return; }

            case Op.CreateTexture: { var id = p.U32(); Created(c, id, Kind.Texture, n.TryCreateTexture(out var x, out f), x.Value, f); return; }
            case Op.DeleteTexture: { var id = p.U32(); var t = Handle(c, id, Kind.Texture); Done(t is { } v && n.TryDeleteTexture(new(v), out f), t, f); if (t is not null) c.Unmap(id); return; }
            case Op.BindTexture: { var target = p.U32(); var t = Optional(c, ref p, Kind.Texture, out var bad); Done(!bad && n.TryBindTexture(target, t is { } v ? new WebGlTextureHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.TextureParameter: { var a = p.U32(); var b = p.U32(); var value = p.I32(); Done(n.TryTextureParameter(a, b, value, out f), 1, f); return; }
            case Op.GetTextureParameter: { var a = p.U32(); var b = p.U32(); if (!n.TryGetTextureParameter(a, b, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Parameter(Ok(), val); return; }
            case Op.GetTextureLevelParameter: { var a = p.U32(); var level = p.I32(); var b = p.U32(); if (!n.TryGetTextureLevelParameter(a, level, b, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Parameter(Ok(), val); return; }
            case Op.TextureImage2D: { var t = p.U32(); var l = p.I32(); var i = p.I32(); var w = p.I32(); var ht = p.I32(); var fmt = p.U32(); var ty = p.U32(); var px = p.Blob(h, _upload, int.MaxValue); Done(n.TryTextureImage2D(t, l, i, w, ht, fmt, ty, px, out f), 1, f); return; }
            case Op.TextureStorage2D: { var t = p.U32(); var l = p.I32(); var i = p.U32(); var w = p.I32(); var ht = p.I32(); Done(n.TryTextureStorage2D(t, l, i, w, ht, out f), 1, f); return; }
            case Op.TextureSubImage2D: { var t = p.U32(); var l = p.I32(); var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); var fmt = p.U32(); var ty = p.U32(); var px = p.Blob(h, _upload, int.MaxValue); Done(n.TryTextureSubImage2D(t, l, x, y, w, ht, fmt, ty, px, out f), 1, f); return; }
            case Op.CopyTextureImage2D: { var t = p.U32(); var l = p.I32(); var i = p.I32(); var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); var b = p.I32(); Done(n.TryCopyTextureImage2D(t, l, i, x, y, w, ht, b, out f), 1, f); return; }
            case Op.CopyTextureSubImage2D: { var t = p.U32(); var l = p.I32(); var xo = p.I32(); var yo = p.I32(); var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); Done(n.TryCopyTextureSubImage2D(t, l, xo, yo, x, y, w, ht, out f), 1, f); return; }
            case Op.TextureImage3D: { var t = p.U32(); var l = p.I32(); var i = p.I32(); var w = p.I32(); var ht = p.I32(); var d = p.I32(); var fmt = p.U32(); var ty = p.U32(); var px = p.Blob(h, _upload, int.MaxValue); Done(n.TryTextureImage3D(t, l, i, w, ht, d, fmt, ty, px, out f), 1, f); return; }
            case Op.TextureStorage3D: { var t = p.U32(); var l = p.I32(); var i = p.U32(); var w = p.I32(); var ht = p.I32(); var d = p.I32(); Done(n.TryTextureStorage3D(t, l, i, w, ht, d, out f), 1, f); return; }
            case Op.TextureSubImage3D: { var t = p.U32(); var l = p.I32(); var x = p.I32(); var y = p.I32(); var z = p.I32(); var w = p.I32(); var ht = p.I32(); var d = p.I32(); var fmt = p.U32(); var ty = p.U32(); var px = p.Blob(h, _upload, int.MaxValue); Done(n.TryTextureSubImage3D(t, l, x, y, z, w, ht, d, fmt, ty, px, out f), 1, f); return; }
            case Op.GenerateMipmap: { var t = p.U32(); Done(n.TryGenerateMipmap(t, out f), 1, f); return; }

            case Op.CreateFramebuffer: { var id = p.U32(); Created(c, id, Kind.Framebuffer, n.TryCreateFramebuffer(out var x, out f), x.Value, f); return; }
            case Op.DeleteFramebuffer: { var id = p.U32(); var b = Handle(c, id, Kind.Framebuffer); Done(b is { } v && n.TryDeleteFramebuffer(new(v), out f), b, f); if (b is not null) c.Unmap(id); return; }
            case Op.BindFramebuffer: { var t = p.U32(); var b = Optional(c, ref p, Kind.Framebuffer, out var bad); Done(!bad && n.TryBindFramebuffer(t, b is { } v ? new WebGlFramebufferHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.FramebufferTexture2D: { var t = p.U32(); var a = p.U32(); var tt = p.U32(); var tex = Optional(c, ref p, Kind.Texture, out var bad); var l = p.I32(); Done(!bad && n.TryFramebufferTexture2D(t, a, tt, tex is { } v ? new WebGlTextureHandle(v) : null, l, out f), bad ? null : 1, f); return; }
            case Op.FramebufferTextureLayer: { var t = p.U32(); var a = p.U32(); var tex = Optional(c, ref p, Kind.Texture, out var bad); var l = p.I32(); var layer = p.I32(); Done(!bad && n.TryFramebufferTextureLayer(t, a, tex is { } v ? new WebGlTextureHandle(v) : null, l, layer, out f), bad ? null : 1, f); return; }
            case Op.CheckFramebufferStatus: { var t = p.U32(); if (!n.TryCheckFramebufferStatus(t, out var status, out f)) { Failed(f); return; } Ok().U32(status); return; }
            case Op.BlitFramebuffer: { var a = p.I32(); var b = p.I32(); var cc = p.I32(); var d = p.I32(); var e = p.I32(); var g = p.I32(); var i = p.I32(); var j = p.I32(); var m = p.U32(); var fl = p.U32(); Done(n.TryBlitFramebuffer(a, b, cc, d, e, g, i, j, m, fl, out f), 1, f); return; }
            case Op.CreateRenderbuffer: { var id = p.U32(); Created(c, id, Kind.Renderbuffer, n.TryCreateRenderbuffer(out var x, out f), x.Value, f); return; }
            case Op.DeleteRenderbuffer: { var id = p.U32(); var b = Handle(c, id, Kind.Renderbuffer); Done(b is { } v && n.TryDeleteRenderbuffer(new(v), out f), b, f); if (b is not null) c.Unmap(id); return; }
            case Op.BindRenderbuffer: { var t = p.U32(); var b = Optional(c, ref p, Kind.Renderbuffer, out var bad); Done(!bad && n.TryBindRenderbuffer(t, b is { } v ? new WebGlRenderbufferHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.RenderbufferStorage: { var t = p.U32(); var i = p.U32(); var w = p.I32(); var ht = p.I32(); Done(n.TryRenderbufferStorage(t, i, w, ht, out f), 1, f); return; }
            case Op.RenderbufferStorageMultisample: { var t = p.U32(); var s = p.I32(); var i = p.U32(); var w = p.I32(); var ht = p.I32(); Done(n.TryRenderbufferStorageMultisample(t, s, i, w, ht, out f), 1, f); return; }
            case Op.FramebufferRenderbuffer: { var t = p.U32(); var a = p.U32(); var rt = p.U32(); var rb = Optional(c, ref p, Kind.Renderbuffer, out var bad); Done(!bad && n.TryFramebufferRenderbuffer(t, a, rt, rb is { } v ? new WebGlRenderbufferHandle(v) : null, out f), bad ? null : 1, f); return; }

            case Op.CreateVertexArray: { var id = p.U32(); Created(c, id, Kind.VertexArray, n.TryCreateVertexArray(out var x, out f), x.Value, f); return; }
            case Op.DeleteVertexArray: { var id = p.U32(); var b = Handle(c, id, Kind.VertexArray); Done(b is { } v && n.TryDeleteVertexArray(new(v), out f), b, f); if (b is not null) c.Unmap(id); return; }
            case Op.BindVertexArray: { var b = Optional(c, ref p, Kind.VertexArray, out var bad); Done(!bad && n.TryBindVertexArray(b is { } v ? new WebGlVertexArrayHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.CreateTransformFeedback: { var id = p.U32(); Created(c, id, Kind.TransformFeedback, n.TryCreateTransformFeedback(out var x, out f), x.Value, f); return; }
            case Op.DeleteTransformFeedback: { var id = p.U32(); var b = Handle(c, id, Kind.TransformFeedback); Done(b is { } v && n.TryDeleteTransformFeedback(new(v), out f), b, f); if (b is not null) c.Unmap(id); return; }
            case Op.BindTransformFeedback: { var t = p.U32(); var b = Optional(c, ref p, Kind.TransformFeedback, out var bad); Done(!bad && n.TryBindTransformFeedback(t, b is { } v ? new WebGlTransformFeedbackHandle(v) : null, out f), bad ? null : 1, f); return; }
            case Op.BeginTransformFeedback: { var m = p.U32(); Done(n.TryBeginTransformFeedback(m, out f), 1, f); return; }
            case Op.EndTransformFeedback: Done(n.TryEndTransformFeedback(out f), 1, f); return;
            case Op.PauseTransformFeedback: Done(n.TryPauseTransformFeedback(out f), 1, f); return;
            case Op.ResumeTransformFeedback: Done(n.TryResumeTransformFeedback(out f), 1, f); return;
            case Op.TransformFeedbackVaryings: { var g = Program(c, ref p); var mode = p.U32(); var names = new GpuPayloadReader(p.Blob(h, _upload, 1 << 20)).Strings(); Done(g is { } v && n.TryTransformFeedbackVaryings(v, names, mode, out f), g, f); return; }
            case Op.GetTransformFeedbackVarying: { var g = Program(c, ref p); var i = p.U32(); if (g is not { } v) { Missing(); return; } if (!n.TryGetTransformFeedbackVarying(v, i, out var info, out f)) { Failed(f); return; } GpuWebGlWire.ActiveInfo(Ok(), info); return; }
            case Op.EnableVertexAttrib: { var l = new WebGlAttribLocation(p.I32()); Done(n.TryEnableVertexAttrib(l, out f), 1, f); return; }
            case Op.DisableVertexAttrib: { var l = new WebGlAttribLocation(p.I32()); Done(n.TryDisableVertexAttrib(l, out f), 1, f); return; }
            case Op.VertexAttribPointer: { var l = new WebGlAttribLocation(p.I32()); var s = p.I32(); var t = p.U32(); var norm = p.Bool(); var st = p.I32(); var o = p.I32(); Done(n.TryVertexAttribPointer(l, s, t, norm, st, o, out f), 1, f); return; }
            case Op.VertexAttribIPointer: { var l = new WebGlAttribLocation(p.I32()); var s = p.I32(); var t = p.U32(); var st = p.I32(); var o = p.I32(); Done(n.TryVertexAttribIPointer(l, s, t, st, o, out f), 1, f); return; }
            case Op.VertexAttribDivisor: { var l = new WebGlAttribLocation(p.I32()); var d = p.U32(); Done(n.TryVertexAttribDivisor(l, d, out f), 1, f); return; }
            case Op.SetVertexAttribConstant: { var l = new WebGlAttribLocation(p.I32()); var v = MemoryMarshal.Cast<byte, float>(p.Blob(h, _upload, 64)); Done(n.TrySetVertexAttribConstant(l, v, out f), 1, f); return; }
            case Op.SetVertexAttribIConstant: { var l = new WebGlAttribLocation(p.I32()); var v = MemoryMarshal.Cast<byte, int>(p.Blob(h, _upload, 64)); Done(n.TrySetVertexAttribIConstant(l, v, out f), 1, f); return; }
            case Op.SetVertexAttribUIConstant: { var l = new WebGlAttribLocation(p.I32()); var v = MemoryMarshal.Cast<byte, uint>(p.Blob(h, _upload, 64)); Done(n.TrySetVertexAttribUIConstant(l, v, out f), 1, f); return; }
            case Op.GetVertexAttrib:
            {
                var l = new WebGlAttribLocation(p.I32()); var q = p.U32();
                if (!n.TryGetVertexAttrib(l, q, out var val, out f)) { Failed(f); return; }
                GpuWebGlWire.Parameter(Ok(), q == 0x889F ? Translate(c, Kind.Buffer, val) : val); return;
            }
            case Op.GetVertexAttribOffset: { var l = new WebGlAttribLocation(p.I32()); var q = p.U32(); if (!n.TryGetVertexAttribOffset(l, q, out var o, out f)) { Failed(f); return; } Ok().I64(o); return; }

            case Op.UniformFloat: { var l = new WebGlUniformLocation(p.I32()); var k = p.I32(); var v = MemoryMarshal.Cast<byte, float>(p.Blob(h, _upload, int.MaxValue)); Done(n.TryUniformFloat(l, k, v, out f), 1, f); return; }
            case Op.UniformInt: { var l = new WebGlUniformLocation(p.I32()); var k = p.I32(); var v = MemoryMarshal.Cast<byte, int>(p.Blob(h, _upload, int.MaxValue)); Done(n.TryUniformInt(l, k, v, out f), 1, f); return; }
            case Op.UniformUInt: { var l = new WebGlUniformLocation(p.I32()); var k = p.I32(); var v = MemoryMarshal.Cast<byte, uint>(p.Blob(h, _upload, int.MaxValue)); Done(n.TryUniformUInt(l, k, v, out f), 1, f); return; }
            case Op.UniformMatrix: { var l = new WebGlUniformLocation(p.I32()); var cols = p.I32(); var rows = p.I32(); var tr = p.Bool(); var v = MemoryMarshal.Cast<byte, float>(p.Blob(h, _upload, int.MaxValue)); Done(n.TryUniformMatrix(l, cols, rows, v, tr, out f), 1, f); return; }
            case Op.GetUniform: { var g = Program(c, ref p); var l = new WebGlUniformLocation(p.I32()); if (g is not { } v) { Missing(); return; } if (!n.TryGetUniform(v, l, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Uniform(Ok(), val); return; }

            case Op.SetViewport: { var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); Done(n.TrySetViewport(x, y, w, ht, out f), 1, f); return; }
            case Op.SetScissor: { var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); var e = p.Bool(); Done(n.TrySetScissor(x, y, w, ht, e, out f), 1, f); return; }
            case Op.SetCapability: { var cap = p.U32(); var e = p.Bool(); Done(n.TrySetCapability(cap, e, out f), 1, f); return; }
            case Op.DrawBuffers: { var b = MemoryMarshal.Cast<byte, uint>(p.Blob(h, _upload, 64)); Done(n.TryDrawBuffers(b, out f), 1, f); return; }
            case Op.ClearBufferfv: { var b = p.U32(); var d = p.I32(); var v = MemoryMarshal.Cast<byte, float>(p.Blob(h, _upload, 64)); Done(n.TryClearBufferfv(b, d, v, out f), 1, f); return; }
            case Op.ClearBufferiv: { var b = p.U32(); var d = p.I32(); var v = MemoryMarshal.Cast<byte, int>(p.Blob(h, _upload, 64)); Done(n.TryClearBufferiv(b, d, v, out f), 1, f); return; }
            case Op.ClearBufferuiv: { var b = p.U32(); var d = p.I32(); var v = MemoryMarshal.Cast<byte, uint>(p.Blob(h, _upload, 64)); Done(n.TryClearBufferuiv(b, d, v, out f), 1, f); return; }
            case Op.ClearBufferfi: { var b = p.U32(); var d = p.I32(); var depth = p.F32(); var s = p.I32(); Done(n.TryClearBufferfi(b, d, depth, s, out f), 1, f); return; }
            case Op.ReadBuffer: { var b = p.U32(); Done(n.TryReadBuffer(b, out f), 1, f); return; }
            case Op.SetClearColor: { var r = p.F32(); var g = p.F32(); var b = p.F32(); var a = p.F32(); Done(n.TrySetClearColor(r, g, b, a, out f), 1, f); return; }
            case Op.ClearMask: { var m = p.U32(); Done(n.TryClearMask(m, out f), 1, f); return; }
            case Op.PixelStore: { var q = p.U32(); var v = p.I32(); Done(n.TryPixelStore(q, v, out f), 1, f); return; }
            case Op.SetActiveTexture: { var t = p.U32(); Done(n.TrySetActiveTexture(t, out f), 1, f); return; }
            case Op.SetDepthFunction: { var fn = p.U32(); Done(n.TrySetDepthFunction(fn, out f), 1, f); return; }
            case Op.SetDepthMask: { var e = p.Bool(); Done(n.TrySetDepthMask(e, out f), 1, f); return; }
            case Op.SetClearDepth: { var v = p.F32(); Done(n.TrySetClearDepth(v, out f), 1, f); return; }
            case Op.SetStencilFunction: { var face = p.U32(); var fn = p.U32(); var r = p.I32(); var m = p.U32(); Done(n.TrySetStencilFunction(face, fn, r, m, out f), 1, f); return; }
            case Op.SetStencilMask: { var face = p.U32(); var m = p.U32(); Done(n.TrySetStencilMask(face, m, out f), 1, f); return; }
            case Op.SetStencilOperation: { var face = p.U32(); var a = p.U32(); var b = p.U32(); var d = p.U32(); Done(n.TrySetStencilOperation(face, a, b, d, out f), 1, f); return; }
            case Op.SetCullFace: { var face = p.U32(); Done(n.TrySetCullFace(face, out f), 1, f); return; }
            case Op.SetFrontFace: { var m = p.U32(); Done(n.TrySetFrontFace(m, out f), 1, f); return; }
            case Op.SetBlendColor: { var r = p.F32(); var g = p.F32(); var b = p.F32(); var a = p.F32(); Done(n.TrySetBlendColor(r, g, b, a, out f), 1, f); return; }
            case Op.SetBlendFunction: { var a = p.U32(); var b = p.U32(); var cc = p.U32(); var d = p.U32(); Done(n.TrySetBlendFunction(a, b, cc, d, out f), 1, f); return; }
            case Op.SetBlendEquation: { var a = p.U32(); var b = p.U32(); Done(n.TrySetBlendEquation(a, b, out f), 1, f); return; }
            case Op.SetColorMask: { var r = p.Bool(); var g = p.Bool(); var b = p.Bool(); var a = p.Bool(); Done(n.TrySetColorMask(r, g, b, a, out f), 1, f); return; }
            case Op.SetDepthState: { var fn = p.U32(); var m = p.Bool(); var v = p.F32(); Done(n.TrySetDepthState(fn, m, v, out f), 1, f); return; }
            case Op.SetStencilState: { var face = p.U32(); var fn = p.U32(); var r = p.I32(); var cm = p.U32(); var wm = p.U32(); var a = p.U32(); var b = p.U32(); var d = p.U32(); Done(n.TrySetStencilState(face, fn, r, cm, wm, a, b, d, out f), 1, f); return; }
            case Op.SetStencilClear: { var v = p.I32(); Done(n.TrySetStencilClear(v, out f), 1, f); return; }
            case Op.SetCullState: { var a = p.U32(); var b = p.U32(); Done(n.TrySetCullState(a, b, out f), 1, f); return; }
            case Op.SetDepthRange: { var a = p.F32(); var b = p.F32(); Done(n.TrySetDepthRange(a, b, out f), 1, f); return; }
            case Op.SetLineWidth: { var w = p.F32(); Done(n.TrySetLineWidth(w, out f), 1, f); return; }
            case Op.SetPolygonOffset: { var a = p.F32(); var b = p.F32(); Done(n.TrySetPolygonOffset(a, b, out f), 1, f); return; }
            case Op.SetSampleCoverage: { var v = p.F32(); var i = p.Bool(); Done(n.TrySetSampleCoverage(v, i, out f), 1, f); return; }
            case Op.SetHint: { var t = p.U32(); var m = p.U32(); Done(n.TrySetHint(t, m, out f), 1, f); return; }
            case Op.Flush: Done(n.TryFlush(out f), 1, f); return;
            case Op.Finish: if (!n.TryFinish(out f)) { Failed(f); return; } Ok(); return;
            case Op.Clear: { var r = p.F32(); var g = p.F32(); var b = p.F32(); var a = p.F32(); var m = p.U32(); Done(n.TryClear(r, g, b, a, m, out f), 1, f); return; }
            case Op.DrawArrays: { var m = p.U32(); var first = p.I32(); var count = p.I32(); Done(n.TryDrawArrays(m, first, count, out f), 1, f); return; }
            case Op.DrawElements: { var m = p.U32(); var count = p.I32(); var t = p.U32(); var o = p.I32(); Done(n.TryDrawElements(m, count, t, o, out f), 1, f); return; }
            case Op.DrawArraysInstanced: { var m = p.U32(); var first = p.I32(); var count = p.I32(); var i = p.I32(); Done(n.TryDrawArraysInstanced(m, first, count, i, out f), 1, f); return; }
            case Op.DrawElementsInstanced: { var m = p.U32(); var count = p.I32(); var t = p.U32(); var o = p.I32(); var i = p.I32(); Done(n.TryDrawElementsInstanced(m, count, t, o, i, out f), 1, f); return; }
            case Op.ReadPixels: { var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); var length = ReadbackLength(p.I32()); var fmt = p.U32(); var ty = p.U32(); var bytes = new byte[length]; if (!n.TryReadPixels(x, y, w, ht, bytes, fmt, ty, out f)) { Failed(f); return; } Readback(bytes); return; }
            case Op.ReadPixelsPresentation: { var x = p.I32(); var y = p.I32(); var w = p.I32(); var ht = p.I32(); var length = ReadbackLength(p.I32()); var bytes = new byte[length]; if (!n.TryReadPixelsPresentation(x, y, w, ht, bytes, out f)) { Failed(f); return; } Readback(bytes); return; }
            case Op.GetParameter: { var q = p.U32(); if (!n.TryGetParameter(q, out var val, out f)) { Failed(f); return; } GpuWebGlWire.Parameter(Ok(), BindingKind(q) is { } kind ? Translate(c, kind, val) : val); return; }
            case Op.GetError: { if (!n.TryGetError(out var e, out f)) { Failed(f); return; } Ok().U32(e); return; }
            case Op.GetExtensions: { if (!n.TryGetExtensions(out var list, out f)) { Failed(f); return; } Ok().Strings(list); return; }
            case Op.Resize: { var w = p.I32(); var ht = p.I32(); if (!n.TryResize(w, ht, out f)) { Failed(f); return; } Ok(); return; }
            default: throw new InvalidDataException("Unknown GPU command.");
        }
    }

    // A command for a context that no longer exists is answered with a failure; its arguments are
    // well-formed by construction of the batch but are not decoded further.
    private static void SkipArguments(ref GpuPayloadReader p) => p.SkipRest();

    private void CreateContext(uint id, ref GpuPayloadReader p)
    {
        var version = Version(p.I32()); var width = p.I32(); var height = p.I32(); var maxBytes = p.I64(); var maxSource = p.I32();
        var requested = new WebGlContextAttributes(Version(p.I32()), p.Bool(), p.Bool(), p.Bool(), p.Bool(), p.Bool(), p.Bool());
        if (id == 0 || _contexts.ContainsKey(id) || _contexts.Count >= MaxContexts) { Failed(new(GpuFailureKind.BudgetExceeded, "Too many GPU worker contexts.")); return; }
        if (!_factory.TryCreate(new(version, width, height, requested, maxBytes, maxSource), out var native, out var failure) || native is null) { Failed(failure); return; }
        _contexts[id] = new(id, native);
        _touched.Add(id);
        var a = native.Attributes;
        Ok().I32((int)a.Version).Bool(a.Alpha).Bool(a.Depth).Bool(a.Stencil).Bool(a.Antialias).Bool(a.PremultipliedAlpha).Bool(a.PreserveDrawingBuffer)
            .I32(native.DrawingBufferWidth).I32(native.DrawingBufferHeight);
    }

    // ---- results and failures ---------------------------------------------------------------
    // Every command writes [bool ok][failure | outputs] into _result. Deferred commands that fail
    // have their failure moved to the batch's deferred list; the sync command's result is returned.
    private GpuPayloadWriter Ok() => _result.Reset().Bool(true);
    private void Failed(GpuFailure? failure) { _result.Reset().Bool(false); GpuWebGlWire.Failure(_result, failure); }
    private void Missing() => Failed(Unknown("object"));
    private void Done(bool succeeded, object? resolved, GpuFailure? failure)
    {
        if (succeeded) Ok();
        else Failed(resolved is null ? Unknown("object") : failure);
    }
    private static bool IsDeferredOk(ReadOnlySpan<byte> result) => result.Length >= 4 && result[0] == 1;
    private void MoveResultFailureToDeferred(uint contextId)
    {
        var reader = new GpuPayloadReader(_result.Written);
        reader.Bool();
        Fail(contextId, GpuWebGlWire.Failure(ref reader));
    }
    private void Fail(uint contextId, GpuFailure failure) { if (_failures.Count < GpuWebGlWire.MaxDeferredFailures) _failures.Add((contextId, failure)); }
    private static GpuFailure Unknown(string what) => new(GpuFailureKind.NativeError, $"The GPU worker has no such {what}.", InvalidOperation);

    private void Readback(byte[] bytes) { _readback.Write(0, bytes); Ok().I32(bytes.Length); }
    private int ReadbackLength(int length) => length < 0 || length > _readback.Length ? throw new InvalidDataException("Invalid readback length.") : length;
    private static int Capacity(int value, int max) => value < 0 || value > max ? throw new InvalidDataException("Invalid output capacity.") : value;
    private static WebGlContextVersion Version(int value) => Enum.IsDefined((WebGlContextVersion)value) ? (WebGlContextVersion)value : throw new InvalidDataException("Invalid WebGL version.");

    private byte[] Reply(int sequence)
    {
        var result = _result.Written.ToArray();
        _reply.Reset().I32(GpuWebGlWire.ReplyMagic).I32(sequence).I32(GpuWebGlWire.StatusOk).I64(_factory.ReservedBytes).I64(_factory.PeakReservedBytes);
        _reply.I32(_failures.Count);
        foreach (var (context, failure) in _failures) { _reply.U32(context); GpuWebGlWire.Failure(_reply, failure); }
        var stats = _touched.Where(_contexts.ContainsKey).ToArray();
        _reply.I32(stats.Length);
        foreach (var id in stats)
        {
            var n = _contexts[id].Native;
            _reply.U32(id).I64(n.ReservedBytes).I64(n.PeakReservedBytes).Bool(n.IsLost).I32(n.DrawingBufferWidth).I32(n.DrawingBufferHeight);
        }
        _reply.Bytes(result);
        return _reply.Written.ToArray();
    }

    private byte[] Lost(int sequence)
    {
        if (!_lost) { _lost = true; DisposeContexts(); }
        return _reply.Reset().I32(GpuWebGlWire.ReplyMagic).I32(sequence).I32(GpuWebGlWire.StatusLost).Written.ToArray();
    }

    // ---- handle mapping ---------------------------------------------------------------------
    private enum Kind { Shader = 1, Program, Buffer, Texture, Framebuffer, Renderbuffer, VertexArray, TransformFeedback }

    private void Created(Context c, uint id, Kind kind, bool succeeded, uint native, GpuFailure? failure)
    {
        if (!succeeded) { Failed(failure); return; }
        if (id == 0 || !c.Map(id, kind, native)) throw new InvalidDataException("Invalid or reused GPU handle.");
        Ok();
    }

    private static uint? Handle(Context c, uint id, Kind kind) => c.Resolve(id, kind);
    private static WebGlShaderHandle? Shader(Context c, uint id) => c.Resolve(id, Kind.Shader) is { } v ? new(v) : null;
    private static WebGlShaderHandle? Shader(Context c, ref GpuPayloadReader p) => Shader(c, p.U32());
    private static WebGlProgramHandle? Program(Context c, uint id) => c.Resolve(id, Kind.Program) is { } v ? new(v) : null;
    private static WebGlProgramHandle? Program(Context c, ref GpuPayloadReader p) => Program(c, p.U32());

    /// <summary>Zero means "none"; a non-zero id must resolve, otherwise the call fails.</summary>
    private static uint? Optional(Context c, ref GpuPayloadReader p, Kind kind, out bool unresolved)
    {
        var id = p.U32();
        if (id == 0) { unresolved = false; return null; }
        var native = c.Resolve(id, kind);
        unresolved = native is null;
        return native;
    }
    private static WebGlProgramHandle? OptionalProgram(Context c, ref GpuPayloadReader p, out bool unresolved) =>
        Optional(c, ref p, Kind.Program, out unresolved) is { } v ? new WebGlProgramHandle(v) : null;

    /// <summary>Binding queries report native object names; translate them back to client handles.</summary>
    private static Kind? BindingKind(uint parameter) => parameter switch
    {
        0x8894 or 0x8895 or 0x8F36 or 0x8F37 or 0x88ED or 0x88EF or 0x8A28 or 0x8C8F => Kind.Buffer,
        0x85B5 => Kind.VertexArray,
        0x8069 or 0x8514 or 0x806A or 0x8C1D => Kind.Texture,
        0x8CA6 or 0x8CAA => Kind.Framebuffer,
        0x8CA7 => Kind.Renderbuffer,
        0x8E25 => Kind.TransformFeedback,
        0x8B8D => Kind.Program,
        _ => null,
    };

    private static WebGlParameterValue Translate(Context c, Kind kind, WebGlParameterValue value)
    {
        if (value.Kind is not (WebGlParameterKind.Int32 or WebGlParameterKind.UInt32)) return value;
        var native = value.Kind == WebGlParameterKind.UInt32 ? value.UInt32 : unchecked((uint)value.Int32);
        var client = native == 0 ? 0 : c.Reverse(kind, native) ?? 0;
        // Both copies of the name are rewritten; a native name never leaves the worker.
        return value with { UInt32 = client, Int32 = unchecked((int)client) };
    }

    private sealed class Context(uint id, INativeWebGlContext native)
    {
        private readonly Dictionary<uint, (Kind Kind, uint Native)> _handles = [];
        private readonly Dictionary<(Kind, uint), uint> _reverse = [];
        internal uint Id { get; } = id;
        internal INativeWebGlContext Native { get; } = native;

        internal bool Map(uint id, Kind kind, uint native)
        {
            if (_handles.Count >= MaxHandlesPerContext || !_handles.TryAdd(id, (kind, native))) return false;
            _reverse[(kind, native)] = id;
            return true;
        }
        internal uint? Resolve(uint id, Kind kind) => _handles.TryGetValue(id, out var entry) && entry.Kind == kind ? entry.Native : null;
        internal uint? Reverse(Kind kind, uint native) => _reverse.TryGetValue((kind, native), out var id) ? id : null;
        internal void Unmap(uint id)
        {
            if (_handles.Remove(id, out var entry) && _reverse.TryGetValue((entry.Kind, entry.Native), out var current) && current == id)
                _reverse.Remove((entry.Kind, entry.Native));
        }
    }

    private void DisposeContexts()
    {
        foreach (var context in _contexts.Values) { try { context.Native.Dispose(); } catch (Exception error) when (error is not OutOfMemoryException) { } }
        _contexts.Clear();
    }

    public void Dispose() { _lost = true; DisposeContexts(); }
}

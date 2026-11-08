namespace Folio.Scripting.Gpu.Remote;

/// <summary>Carries one sealed batch to the GPU worker and returns its reply.</summary>
internal interface IGpuWebGlTransport
{
    byte[] Exchange(byte[] batch);
}

/// <summary>Deferred native failures a remote context reports at its next synchronization point.</summary>
internal interface IDeferredGpuFailures
{
    IReadOnlyList<GpuFailure> TakeDeferredFailures();
}

/// <summary>
/// Document-side end of one GPU worker session: the WebGL factory for every canvas of the document.
/// Deferred calls are queued in the current batch; a synchronous call (or a full batch) flushes it
/// and waits for one reply. Any transport fault or malformed reply loses the whole session.
/// </summary>
internal sealed class GpuWebGlSession : INativeWebGlFactory, IDisposable
{
    private readonly IGpuWebGlTransport _transport;
    private readonly GpuUploadRing _ring;
    private readonly GpuArena _readback;
    private readonly GpuCommandBatchWriter _batch = new();
    private readonly GpuPayloadWriter _payload = new();
    private readonly Dictionary<uint, RemoteWebGlContext> _contexts = [];
    private readonly object _gate = new();
    private GpuWebGlOp _pendingOp;
    private int _sequence;
    private uint _nextId;
    private bool _lost, _disposed;

    internal GpuWebGlSession(IGpuWebGlTransport transport, GpuArena upload, GpuArena readback)
    {
        _transport = transport;
        _ring = new GpuUploadRing(upload);
        _readback = readback;
    }

    public long ReservedBytes { get; private set; }
    public long PeakReservedBytes { get; private set; }
    /// <summary>The worker device's renderer, as its first reply reported it; null before any reply.</summary>
    internal GpuRendererKind? RendererKind { get; private set; }
    internal bool IsLost => _lost;
    internal object Gate => _gate;
    internal GpuArena Readback => _readback;

    public bool TryCreate(WebGlContextOptions options, out INativeWebGlContext? context, out GpuFailure? failure)
    {
        context = null;
        lock (_gate)
        {
            var id = NextId();
            Begin(id, GpuWebGlOp.CreateContext).I32((int)options.Version).I32(options.Width).I32(options.Height).I64(options.MaxBytes).I32(options.MaxShaderSourceBytes);
            var requested = options.RequestedAttributes;
            _payload.I32((int)requested.Version).Bool(requested.Alpha).Bool(requested.Depth).Bool(requested.Stencil)
                .Bool(requested.Antialias).Bool(requested.PremultipliedAlpha).Bool(requested.PreserveDrawingBuffer);
            if (Sync(out failure) is not { } outputs) return false;
            var reader = new GpuPayloadReader(outputs);
            var attributes = new WebGlContextAttributes((WebGlContextVersion)reader.I32(), reader.Bool(), reader.Bool(), reader.Bool(), reader.Bool(), reader.Bool(), reader.Bool());
            var remote = new RemoteWebGlContext(this, id, attributes, reader.I32(), reader.I32());
            _contexts[id] = remote;
            context = remote;
            return true;
        }
    }

    internal uint NextId() => _nextId = checked(_nextId + 1);

    /// <summary>Starts a command for <paramref name="contextId"/>; flushes first if the batch could not take it.</summary>
    internal GpuPayloadWriter Begin(uint contextId, GpuWebGlOp op)
    {
        if (!_batch.HasRoom(GpuCommandBatch.MaxInlineBytes)) Flush(out _);
        _pendingOp = op;
        return _payload.Reset().U32(contextId);
    }

    /// <summary>Adds data to the current command, through the upload ring when large.</summary>
    internal bool TryBlob(ReadOnlySpan<byte> data, out GpuFailure? failure)
    {
        failure = null;
        if (data.Length > _ring.Capacity)
        {
            failure = new(GpuFailureKind.NativeError, "The upload exceeds the GPU worker arena.", 0x0505);
            return false;
        }
        if (_payload.TryBlob(data, _ring)) return true;
        // The ring is full of data the worker has not acknowledged yet: flush, then retry once.
        if (!Flush(out failure)) return false;
        if (_payload.TryBlob(data, _ring)) return true;
        failure = new(GpuFailureKind.NativeError, "The GPU worker arena is exhausted.", 0x0505);
        return false;
    }

    /// <summary>Queues the current command; success means it was accepted for execution, not that it ran.</summary>
    internal bool Defer(out GpuFailure? failure)
    {
        if (_lost) { failure = LostFailure; return false; }
        if (!_batch.TryAppend((ushort)_pendingOp, _payload.Written)) throw new InvalidOperationException("Batch room was reserved.");
        failure = null;
        return true;
    }

    /// <summary>Sends the batch ending with the current command and returns that command's outputs.</summary>
    internal byte[]? Sync(out GpuFailure? failure)
    {
        if (_lost) { failure = LostFailure; return null; }
        if (!_batch.TryAppend((ushort)_pendingOp, _payload.Written)) throw new InvalidOperationException("Batch room was reserved.");
        if (Exchange(sync: true, out failure) is not { } reply) return null;
        var reader = new GpuPayloadReader(reply);
        try
        {
            if (!reader.Bool()) { failure = GpuWebGlWire.Failure(ref reader); return null; }
            failure = null;
            return reader.Rest().ToArray();
        }
        catch (InvalidDataException) { Lose(); failure = LostFailure; return null; }
    }

    /// <summary>Sends queued commands without a synchronous result (frame boundary, full batch or ring).</summary>
    internal bool Flush(out GpuFailure? failure)
    {
        failure = null;
        if (_lost) { failure = LostFailure; return false; }
        if (_batch.IsEmpty) return true;
        return Exchange(sync: false, out failure) is not null;
    }

    private byte[]? Exchange(bool sync, out GpuFailure? failure)
    {
        failure = null;
        var sequence = checked(++_sequence);
        var (start, end) = _ring.PublishBatch(sequence);
        var batch = _batch.Seal(sequence, start, end, sync);
        try
        {
            var reply = _transport.Exchange(batch);
            var result = ReadReply(reply, sequence);
            _ring.Acknowledge(sequence);
            if (result is null) { failure = LostFailure; }
            return result;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ObjectDisposedException or TimeoutException or OperationCanceledException)
        {
            Lose();
            failure = LostFailure;
            return null;
        }
    }

    private byte[]? ReadReply(byte[] reply, int sequence)
    {
        if (reply.Length > GpuWebGlWire.MaxReplyBytes) throw new InvalidDataException("GPU reply is too large.");
        var reader = new GpuPayloadReader(reply);
        if (reader.I32() != GpuWebGlWire.ReplyMagic || reader.I32() != sequence) throw new InvalidDataException("Invalid GPU reply header.");
        var status = reader.I32();
        if (status == GpuWebGlWire.StatusLost) { Lose(); return null; }
        if (status != GpuWebGlWire.StatusOk) throw new InvalidDataException("Invalid GPU reply status.");
        ReservedBytes = reader.I64();
        PeakReservedBytes = reader.I64();
        var renderer = (GpuRendererKind)reader.I32();
        if (!Enum.IsDefined(renderer)) throw new InvalidDataException("Invalid GPU renderer kind.");
        RendererKind = renderer;
        var failures = reader.I32();
        if (failures < 0 || failures > GpuWebGlWire.MaxDeferredFailures) throw new InvalidDataException("Invalid failure count.");
        for (var i = 0; i < failures; i++)
        {
            var contextId = reader.U32();
            var failure = GpuWebGlWire.Failure(ref reader);
            if (_contexts.TryGetValue(contextId, out var context)) context.AddDeferred(failure);
        }
        var stats = reader.I32();
        if (stats < 0 || stats > 1024) throw new InvalidDataException("Invalid statistics count.");
        for (var i = 0; i < stats; i++)
        {
            var contextId = reader.U32();
            var reserved = reader.I64(); var peak = reader.I64(); var lost = reader.Bool();
            var width = reader.I32(); var height = reader.I32();
            if (_contexts.TryGetValue(contextId, out var context)) context.Update(reserved, peak, lost, width, height);
        }
        return reader.Bytes(GpuWebGlWire.MaxReplyBytes).ToArray();
    }

    /// <summary>Executes queued work so metrics and failures reflect every call made so far.</summary>
    internal void Synchronize() { lock (_gate) Flush(out _); }

    internal void Forget(uint contextId) => _contexts.Remove(contextId);

    /// <summary>Raised once, on whichever thread first observes the loss.</summary>
    internal event Action? Lost;

    /// <summary>Marks the session lost from outside a call (the worker process exited).</summary>
    internal void Abandon() { lock (_gate) Lose(); }

    private void Lose()
    {
        if (_lost) return;
        _lost = true;
        foreach (var context in _contexts.Values) context.MarkLost();
        Lost?.Invoke();
    }

    internal static GpuFailure LostFailure { get; } = new(GpuFailureKind.Unavailable, "The GPU worker session is lost.");

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_lost) Flush(out _);
            _lost = true;
        }
    }
}

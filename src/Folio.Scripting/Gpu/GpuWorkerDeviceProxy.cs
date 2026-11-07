namespace Folio.Scripting.Gpu;

/// <summary>Surface-only proxy. Deliberately does not implement the concrete native WebGL factory.</summary>
internal sealed class GpuWorkerDeviceProxy : IGpuDevice
{
    private readonly GpuWorkerBroker _broker;
    private readonly object _gate = new();
    private readonly HashSet<Surface> _surfaces = [];
    private readonly long _budget;
    private long _reserved, _peak;
    private bool _disposed;
    public GpuRendererKind RendererKind { get; }
    public string RendererIdentity => $"{RendererKind}:FolioGpuWorker-surface-v1";
    public long ReservedBytes { get { lock (_gate) return _reserved; } }
    public long PeakReservedBytes { get { lock (_gate) return _peak; } }
    public bool IsDisposed { get { lock (_gate) return _disposed || _broker.IsLost; } }

    internal GpuWorkerDeviceProxy(GpuWorkerBroker broker, long budget)
    {
        if (budget <= 0 || budget > GpuWorkerProtocol.MaxDeviceBytes) throw new ArgumentOutOfRangeException(nameof(budget));
        _broker = broker; _budget = budget;
        var request = GpuWorkerProtocol.Request(GpuWorkerCommand.Open);
        GpuWorkerProtocol.Put(request, 4, checked((int)budget));
        var reply = broker.Exchange(request);
        RendererKind = (GpuRendererKind)GpuWorkerProtocol.Get(reply, 5);
        if (RendererKind is not (GpuRendererKind.Hardware or GpuRendererKind.Warp))
        { broker.Dispose(); throw new InvalidDataException("Invalid GPU renderer."); }
    }

    public GpuSurfaceResult TryCreateSurface(int width, int height)
    {
        lock (_gate)
        {
            if (IsDisposed) return new(null, Lost());
            int bytes;
            try { bytes = GpuWorkerProtocol.SurfaceBytes(width, height); }
            catch (InvalidDataException error) { return new(null, new(GpuFailureKind.BudgetExceeded, error.Message)); }
            if (_surfaces.Count >= GpuWorkerProtocol.MaxSurfaces || bytes > _budget - _reserved)
                return new(null, new(GpuFailureKind.BudgetExceeded, "GPU proxy surface budget exceeded."));
            var request = GpuWorkerProtocol.Request(GpuWorkerCommand.CreateSurface);
            GpuWorkerProtocol.Put(request, 4, width); GpuWorkerProtocol.Put(request, 5, height);
            try
            {
                var reply = _broker.Exchange(request);
                var id = GpuWorkerProtocol.Get(reply, 5);
                if (id <= 0 || _surfaces.Any(s => s.Id == id))
                { _broker.Dispose(); return new(null, Lost()); }
                var surface = new Surface(this, id, width, height, bytes);
                _surfaces.Add(surface); _reserved += bytes; _peak = Math.Max(_peak, _reserved);
                return new(surface, null);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
            { return new(null, Lost()); }
        }
    }

    private static GpuFailure Lost() => new(GpuFailureKind.Unavailable, "GPU worker context lost.");
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _surfaces.Clear(); _reserved = 0; _broker.Dispose();
        }
    }

    private sealed class Surface(GpuWorkerDeviceProxy owner, int id, int width, int height, int bytes) : IGpuSurface, IGpuResourceSet
    {
        private bool _disposed;
        internal int Id => id;
        public int Width => width;
        public int Height => height;
        public long ReservedBytes => bytes;
        public IGpuResourceSet Resources => this;

        public bool ClearColor(float red, float green, float blue, float alpha, out GpuFailure? failure)
            => Clear(GpuWorkerCommand.Clear, red, green, blue, alpha, 0, 0, width, height, out failure);
        public bool ClearColorRect(float red, float green, float blue, float alpha, int x, int y, int w, int h, out GpuFailure? failure)
            => Clear(GpuWorkerCommand.ClearRect, red, green, blue, alpha, x, y, w, h, out failure);
        private bool Clear(GpuWorkerCommand command, float red, float green, float blue, float alpha, int x, int y, int w, int h, out GpuFailure? failure)
        {
            if (!float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue) || !float.IsFinite(alpha) ||
                x < 0 || y < 0 || w < 0 || h < 0 || x > width - w || y > height - h)
            { failure = new(GpuFailureKind.NativeError, "Invalid GPU clear range or color."); return false; }
            var request = GpuWorkerProtocol.Request(command, id);
            GpuWorkerProtocol.Put(request, 4, BitConverter.SingleToInt32Bits(red)); GpuWorkerProtocol.Put(request, 5, BitConverter.SingleToInt32Bits(green));
            GpuWorkerProtocol.Put(request, 6, BitConverter.SingleToInt32Bits(blue)); GpuWorkerProtocol.Put(request, 7, BitConverter.SingleToInt32Bits(alpha));
            GpuWorkerProtocol.Put(request, 8, x); GpuWorkerProtocol.Put(request, 9, y); GpuWorkerProtocol.Put(request, 10, w); GpuWorkerProtocol.Put(request, 11, h);
            lock (owner._gate) return Send(request, out _, out failure);
        }
        private bool Send(byte[] request, out byte[] reply, out GpuFailure? failure)
        {
            reply = []; failure = null;
            if (_disposed || owner.IsDisposed) { failure = Lost(); return false; }
            try { reply = owner._broker.Exchange(request); return true; }
            catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
            { failure = Lost(); return false; }
        }
        public bool TryReadPixels(Span<byte> destination, out GpuFailure? failure)
        {
            failure = null;
            if (destination.Length < bytes) { failure = new(GpuFailureKind.NativeError, "Readback destination is too small."); return false; }
            lock (owner._gate)
            {
                // Commit to the caller only after all chunks arrive, avoiding partial frames on loss.
                var pixels = new byte[bytes];
                for (int offset = 0; offset < bytes;)
                {
                    int count = Math.Min(bytes - offset, GpuWorkerProtocol.MaxPayloadBytes);
                    var request = GpuWorkerProtocol.Request(GpuWorkerCommand.ReadPixels, id);
                    GpuWorkerProtocol.Put(request, 4, offset); GpuWorkerProtocol.Put(request, 5, count);
                    if (!Send(request, out var reply, out failure)) return false;
                    if (reply.Length != GpuWorkerProtocol.HeaderBytes + count)
                    { owner._broker.Dispose(); failure = Lost(); return false; }
                    reply.AsSpan(GpuWorkerProtocol.HeaderBytes).CopyTo(pixels.AsSpan(offset)); offset += count;
                }
                pixels.CopyTo(destination); return true;
            }
        }
        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                if (!owner.IsDisposed) Send(GpuWorkerProtocol.Request(GpuWorkerCommand.Release, id), out _, out _);
                _disposed = true;
                if (owner._surfaces.Remove(this)) owner._reserved -= bytes;
            }
        }
        private static bool Unsupported(out IGpuResource? resource, out GpuFailure? failure)
        { resource = null; failure = new(GpuFailureKind.Unavailable, "GPU worker resource allocation is not implemented."); return false; }
        public bool TryAllocateTexture(int w, int h, out IGpuResource? resource, out GpuFailure? failure) => Unsupported(out resource, out failure);
        public bool TryAllocateBuffer(int size, out IGpuResource? resource, out GpuFailure? failure) => Unsupported(out resource, out failure);
        public bool TryAllocateRenderbuffer(int w, int h, int bpp, out IGpuResource? resource, out GpuFailure? failure) => Unsupported(out resource, out failure);
    }
}

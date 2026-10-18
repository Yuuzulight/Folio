namespace Folio.Scripting.Dom;

/// <summary>Document-wide reservations made before a media decoder is opened.</summary>
internal sealed class MediaResourceBudget(int maxBytes, int maxDecoders)
{
    private readonly object _gate = new();
    private readonly int _maxBytes = Math.Max(0, maxBytes);
    private readonly int _maxDecoders = Math.Max(0, maxDecoders);
    private long _bytes;
    private int _decoders;

    public Lease? Acquire()
    {
        lock (_gate)
        {
            if (_decoders >= _maxDecoders) return null;
            _decoders++;
            return new Lease(this);
        }
    }

    internal sealed class Lease(MediaResourceBudget owner) : IDisposable
    {
        private long _reserved;
        private bool _disposed;

        public long AvailableBytes
        {
            get { lock (owner._gate) return _disposed ? 0 : owner._maxBytes - owner._bytes + _reserved; }
        }

        public bool Resize(long bytes)
        {
            lock (owner._gate)
            {
                if (_disposed || bytes < 0 || bytes > owner._maxBytes - owner._bytes + _reserved) return false;
                owner._bytes += bytes - _reserved;
                _reserved = bytes;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                if (_disposed) return;
                owner._bytes -= _reserved;
                owner._decoders--;
                _disposed = true;
            }
        }
    }
}

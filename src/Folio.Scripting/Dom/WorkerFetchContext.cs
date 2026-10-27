using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>Worker-owned fetch state. Host loads never acquire a document or ambient network access.</summary>
internal sealed class WorkerFetchContext(Uri? baseUri, IResourceLoader? loader,
    CancellationToken cancellationToken, ScriptMessageBudget budget, Action<string>? reportFailure = null) : IFetchContext, IDisposable
{
    public Uri? BaseUri { get; } = baseUri;
    private readonly Dictionary<int, (byte[] Data, ScriptMessageBudget.Reservation Reservation)> _bodies = [];
    private int _nextBody;
    private long _fetchedBytes;
    private bool _disposed;

    public (ResourceResponse? Response, string? Error) Fetch(string url, string? integrity = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ResourceResponse? response = null;
        string? error = null;
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            response = ResourceLoader.DataUrlsOnly.Load(new ResourceRequest(url, ResourceKind.Fetch, integrity));
        else if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            error = "Only data:, http: and https: URLs can be fetched.";
        else if (loader is null)
            error = "The host has not provided a resource loader.";
        else
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResourceLoader.HostTimeout);
            try
            {
                // A host may perform synchronous work before returning its Task.
                // Keep both that work and an uncooperative async Task off the VM owner.
                response = Task.Run(() => loader.LoadAsync(new ResourceRequest(url, ResourceKind.Fetch, integrity), timeout.Token), timeout.Token)
                    .WaitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { error = "The host resource load timed out."; }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { error = "The host resource load failed."; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (response?.Succeeded == true)
        {
            var length = response.Data!.Length;
            if (length > ResourceLoader.MaxResourceBytes) error = "The response exceeds the resource size limit.";
            else if (_fetchedBytes + length > DomContext.MaxFetchedBytes) error = "This worker has already fetched the most bytes it may.";
            else { _fetchedBytes += length; return (response, null); }
        }
        error ??= response?.Error ?? "The host refused the resource.";
        reportFailure?.Invoke(error);
        return (null, error);
    }

    public int StoreFetchBody(byte[] data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = budget.Reserve(data.LongLength);
        try
        {
            var handle = checked(++_nextBody);
            _bodies.Add(handle, (data, reservation));
            return handle;
        }
        catch { reservation.Dispose(); throw; }
    }

    public byte[]? FetchBody(int handle) => _bodies.TryGetValue(handle, out var body) ? body.Data : null;
    public void FreeFetchBody(int handle)
    {
        if (_bodies.Remove(handle, out var body)) body.Reservation.Dispose();
    }
    public void Dispose()
    {
        _disposed = true;
        foreach (var body in _bodies.Values) body.Reservation.Dispose();
        _bodies.Clear();
    }
}

using System.Collections.Concurrent;
using System.Text.Json;

namespace Folio.Scripting.Ipc;

public sealed partial class ContentHostSession
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> _mediaCapabilitiesRequests = new();
    private int _mediaCapabilitiesBusy;

    /// <summary>Asks the owning host decoder whether a MIME type is a supported hint; no source is decoded.</summary>
    public bool RequestMediaCanDecode(string? contentType, bool video, CancellationToken cancellationToken = default)
    {
        if (_connectionClosed || contentType is not { Length: > 0 and <= 256 }) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var acquired = false;
        var sequence = Interlocked.Increment(ref _sequence);
        try
        {
            _requestSlots.Wait(timeout.Token);
            acquired = true;
            if (_connectionClosed) return false;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mediaCapabilitiesRequests[sequence] = completion;
            WriteAsync(ContentMessageKind.MediaCapabilitiesRequest, sequence,
                new Dictionary<string, object?> { ["contentType"] = contentType, ["video"] = video }, timeout.Token).AsTask().GetAwaiter().GetResult();
            return completion.Task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { return false; }
        finally { _mediaCapabilitiesRequests.TryRemove(sequence, out _); if (acquired) _requestSlots.Release(); }
    }

    private async Task<bool> QueryMediaCapabilitiesAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (_mediaDecoder is null || message.Payload.ValueKind != JsonValueKind.Object
            || !message.Payload.TryGetProperty("contentType", out var mime) || mime.ValueKind != JsonValueKind.String
            || mime.GetString() is not { Length: > 0 and <= 256 } contentType
            || !message.Payload.TryGetProperty("video", out var kind) || kind.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        var video = kind.GetBoolean();
        var grant = HostCapabilities?.Invoke();
        if (grant is not null && !grant.Media && (video || !grant.Sound)) return false;
        if (cancellationToken.IsCancellationRequested || Interlocked.CompareExchange(ref _mediaCapabilitiesBusy, 1, 0) != 0) return false;
        try
        {
            // A noncooperating host callback consumes at most one worker, rather than one per repeated page query.
            return await Task.Run(() =>
            {
                try { return !_connectionClosed && !cancellationToken.IsCancellationRequested && _mediaDecoder.CanDecode(contentType, video); }
                finally { Volatile.Write(ref _mediaCapabilitiesBusy, 0); }
            })
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private void CompleteMediaCapabilities(ContentMessage message)
    {
        if (message.Payload.ValueKind != JsonValueKind.Object || !message.Payload.TryGetProperty("requestId", out var id)
            || !id.TryGetInt64(out var sequence) || !message.Payload.TryGetProperty("supported", out var supported)
            || supported.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("Invalid media capability response.");
        if (_mediaCapabilitiesRequests.TryGetValue(sequence, out var request)) request.TrySetResult(supported.GetBoolean());
    }

    private void CloseMediaRequests()
    {
        foreach (var request in _mediaCapabilitiesRequests.Values) request.TrySetResult(false);
        foreach (var request in _mediaDecodeRequests.Values) request.TrySetResult(null);
        foreach (var request in _mediaCaptureRequests.Values) request.TrySetException(new IOException("The content connection closed."));
    }
}

using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Folio.Resources;

namespace Folio.Scripting.Ipc;

/// <summary>Protocol session used by a content-host process or an in-process test transport.</summary>
public sealed class ContentHostSession : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ContentProcessSupervisor _supervisor;
    private readonly ContentResourcePolicy? _resourcePolicy;
    private readonly IResourceLoader? _resourceLoader;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ResourceResponse>> _resourceRequests = new();
    private readonly SemaphoreSlim _requestSlots = new(16, 16);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _sequence;
    private volatile bool _connectionClosed;
    /// <summary>Binary limit, leaving room for base64 and metadata in a protocol frame.</summary>
    public const int MaxResourceResponseBytes = 8 * 1024 * 1024;

    /// <summary>Creates a session without a resource loader.</summary>
    public ContentHostSession(Stream input, Stream output, ContentProcessSupervisor? supervisor, ContentResourcePolicy? resourcePolicy)
        : this(input, output, supervisor, resourcePolicy, null) { }

    public ContentHostSession(Stream input, Stream output, ContentProcessSupervisor? supervisor = null, ContentResourcePolicy? resourcePolicy = null, IResourceLoader? resourceLoader = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _supervisor = supervisor ?? new ContentProcessSupervisor();
        _resourcePolicy = resourcePolicy;
        _resourceLoader = resourceLoader;
    }

    public event Func<ContentMessage, CancellationToken, ValueTask>? MessageReceived;

    public async ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var messages = Channel.CreateBounded<(ContentMessage Message, int Bytes)>(64);
        long queuedBytes = 0;
        // Keep reading correlated replies while a serialized document callback
        // synchronously waits for a host resource. No engine runs on this reader.
        var dispatch = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in messages.Reader.ReadAllAsync(running.Token).ConfigureAwait(false))
                {
                    Interlocked.Add(ref queuedBytes, -entry.Bytes);
                    await DispatchAsync(entry.Message, running.Token).ConfigureAwait(false);
                }
            }
            catch { running.Cancel(); throw; }
        }, CancellationToken.None);
        Exception? readerError = null;
        try
        {
            while (!running.IsCancellationRequested && !_supervisor.IsStopped)
            {
                var message = await ContentTransport.ReadAsync(_input, running.Token).ConfigureAwait(false);
                if (message is null || message.Kind == ContentMessageKind.Shutdown) break;
                _supervisor.Heartbeat();
                if (message.Kind == ContentMessageKind.ResourceResponse) CompleteResource(message);
                else
                {
                    var bytes = Encoding.UTF8.GetByteCount(message.Payload.GetRawText()) + 256;
                    if (Interlocked.Add(ref queuedBytes, bytes) > 32 * 1024 * 1024 || !messages.Writer.TryWrite((message, bytes)))
                    {
                        Interlocked.Add(ref queuedBytes, -bytes);
                        throw new InvalidDataException("The content command queue is full.");
                    }
                }
            }
        }
        catch (Exception error) { readerError = error; running.Cancel(); }
        finally
        {
            _connectionClosed = true;
            foreach (var request in _resourceRequests.Values)
                request.TrySetResult(ResourceResponse.Refused("The content connection closed."));
            messages.Writer.TryComplete();
        }
        try { await dispatch.ConfigureAwait(false); }
        catch when (readerError is not null) { } // Preserve the transport/protocol failure.
        if (readerError is not null) ExceptionDispatchInfo.Capture(readerError).Throw();
    }

    private async ValueTask DispatchAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (message.Kind == ContentMessageKind.Heartbeat)
        {
            // Acknowledgements are terminal, not another heartbeat request.
            // Queue requests with document work so a hung script cannot fake health.
            if (!message.Payload.TryGetProperty("acknowledged", out _))
                await SendAsync(ContentMessageKind.Heartbeat, new { acknowledged = message.Sequence }, cancellationToken).ConfigureAwait(false);
        }
        else if (message.Kind == ContentMessageKind.ResourceRequest)
        {
            var response = await LoadResourceAsync(message, cancellationToken).ConfigureAwait(false);
            await SendAsync(ContentMessageKind.ResourceResponse, new { requestId = message.Sequence, status = response.Succeeded ? 200 : 0, data = response.Data, contentType = response.ContentType, error = response.Error }, cancellationToken).ConfigureAwait(false);
        }
        else if (MessageReceived is { } handler)
            await handler(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ResourceResponse> LoadResourceAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        var payload = message.Payload;
        if (payload.ValueKind != JsonValueKind.Object || message.Sequence <= 0 || !payload.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: > 0 and <= 32768 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !payload.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.Number || !kindValue.TryGetInt32(out var kind) || !Enum.IsDefined((ResourceKind)kind))
            return ResourceResponse.Refused("Invalid content resource request.");
        string? integrity = null;
        if (payload.TryGetProperty("integrity", out var integrityValue) && integrityValue.ValueKind != JsonValueKind.Null)
        {
            if (integrityValue.ValueKind != JsonValueKind.String || integrityValue.GetString() is not { Length: <= 4096 } text)
                return ResourceResponse.Refused("Invalid content resource integrity.");
            integrity = text;
        }
        if (_resourceLoader is null || (_resourcePolicy is not null && !_resourcePolicy.IsAllowed(uri)))
            return ResourceResponse.Refused("Blocked by content resource policy.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var result = await _resourceLoader.LoadAsync(new ResourceRequest(url, (ResourceKind)kind, integrity), timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
            return BoundedResponse(result ?? ResourceResponse.Refused("The host loader did not handle this resource."));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return ResourceResponse.Refused("The host resource load timed out."); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return ResourceResponse.Refused("The host resource loader failed."); }
    }

    private static ResourceResponse BoundedResponse(ResourceResponse response) =>
        response.Data is { Length: > MaxResourceResponseBytes } || response.ContentType is { Length: > 4096 } || response.Error is { Length: > 4096 }
            ? ResourceResponse.Refused("The host resource response exceeds the content protocol limit.") : response;

    private void CompleteResource(ContentMessage message)
    {
        var payload = message.Payload;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var sequence) || sequence <= 0)
            throw new InvalidDataException("Invalid resource response correlation.");
        if (!_resourceRequests.TryGetValue(sequence, out var request)) return; // A late, timed-out reply.
        try
        {
            if (!payload.TryGetProperty("status", out var statusValue) || statusValue.ValueKind != JsonValueKind.Number || !statusValue.TryGetInt32(out var status) || status is not (0 or 200))
                throw new InvalidDataException("Invalid resource response status.");
            var data = status == 200 && payload.TryGetProperty("data", out var bytes) && bytes.ValueKind != JsonValueKind.Null ? bytes.GetBytesFromBase64() : null;
            var type = payload.TryGetProperty("contentType", out var contentType) ? contentType.GetString() : null;
            var error = payload.TryGetProperty("error", out var failure) ? failure.GetString() : null;
            request.TrySetResult(BoundedResponse(new ResourceResponse(data, type, status == 0 ? error ?? "The host refused the resource." : error)));
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Invalid resource response body.", error);
        }
    }

    /// <summary>Asks the other endpoint's host-supplied loader for a bounded resource.</summary>
    public async Task<ResourceResponse> RequestResourceAsync(ResourceRequest request, CancellationToken cancellationToken = default)
    {
        if (_connectionClosed) return ResourceResponse.Refused("The content connection closed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _requestSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<ResourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resourceRequests[sequence] = completion;
        try
        {
            if (_connectionClosed) return ResourceResponse.Refused("The content connection closed.");
            await WriteAsync(ContentMessageKind.ResourceRequest, sequence, new { url = request.Url, kind = (int)request.Kind, integrity = request.Integrity }, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _resourceRequests.TryRemove(sequence, out _); _requestSlots.Release(); }
    }

    public ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default) =>
        WriteAsync(kind, Interlocked.Increment(ref _sequence), payload, cancellationToken);

    private async ValueTask WriteAsync(ContentMessageKind kind, long sequence, object payload, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ContentTransport.WriteAsync(_output, new ContentMessage(kind, sequence, document.RootElement.Clone()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _connectionClosed = true;
        foreach (var request in _resourceRequests.Values)
            request.TrySetResult(ResourceResponse.Refused("The content connection closed."));
        _supervisor.Stop("content host session disposed");
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        _supervisor.Dispose();
        _writeGate.Dispose();
    }
}

using System.Text.Json;

namespace Folio.Scripting.Ipc;

/// <summary>Protocol session used by a content-host process or an in-process test transport.</summary>
public sealed class ContentHostSession : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ContentProcessSupervisor _supervisor;
    private readonly ContentResourcePolicy _resourcePolicy;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _sequence;

    public ContentHostSession(Stream input, Stream output, ContentProcessSupervisor? supervisor = null, ContentResourcePolicy? resourcePolicy = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _supervisor = supervisor ?? new ContentProcessSupervisor();
        _resourcePolicy = resourcePolicy ?? ContentResourcePolicy.DenyAll;
    }

    public event Func<ContentMessage, CancellationToken, ValueTask>? MessageReceived;

    public async ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && !_supervisor.IsStopped)
        {
            var message = await ContentTransport.ReadAsync(_input, cancellationToken).ConfigureAwait(false);
            if (message is null) break;
            _supervisor.Heartbeat();
            if (message.Kind == ContentMessageKind.Shutdown) break;
            if (message.Kind == ContentMessageKind.Heartbeat)
            {
                await SendAsync(ContentMessageKind.Heartbeat, new { acknowledged = message.Sequence }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (message.Kind == ContentMessageKind.ResourceRequest)
            {
                var url = message.Payload.TryGetProperty("url", out var value) ? value.GetString() : null;
                var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && _resourcePolicy.IsAllowed(uri);
                if (!allowed)
                    await SendAsync(ContentMessageKind.ResourceResponse, new { requestId = message.Sequence, status = 0, error = "blocked by content resource policy" }, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (MessageReceived is { } handler)
                await handler(message, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask SendAsync(ContentMessageKind kind, object payload, CancellationToken cancellationToken = default)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ContentTransport.WriteAsync(_output, new ContentMessage(kind, Interlocked.Increment(ref _sequence), document.RootElement.Clone()), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _supervisor.Stop("content host session disposed");
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        _supervisor.Dispose();
        _writeGate.Dispose();
    }
}

using System.Text.Json;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;
using Folio.Resources;
using Folio.Hosting;
using Folio.Imaging;

namespace Folio.Scripting.Ipc;

/// <summary>Protocol session used by a content-host process or an in-process test transport.</summary>
public sealed partial class ContentHostSession : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ContentProcessSupervisor _supervisor;
    private readonly ContentResourcePolicy? _resourcePolicy;
    private readonly IResourceLoader? _resourceLoader;
    private readonly IMediaDecoder? _mediaDecoder;
    private readonly IMediaAudioOutput? _mediaAudioOutput;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _mediaCaptureRequests = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<MediaDecodeResult?>> _mediaDecodeRequests = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<ResourceResponse>> _resourceRequests = new();
    private readonly SemaphoreSlim _requestSlots = new(16, 16);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _sequence;
    private volatile bool _connectionClosed;
    /// <summary>Binary limit, leaving room for base64 and metadata in a protocol frame.</summary>
    public const int MaxResourceResponseBytes = 8 * 1024 * 1024;

    /// <summary>Creates a session without a resource loader.</summary>
    public ContentHostSession(Stream input, Stream output, ContentProcessSupervisor? supervisor, ContentResourcePolicy? resourcePolicy)
        : this(input, output, supervisor, resourcePolicy, null, null, null) { }

    public ContentHostSession(Stream input, Stream output, ContentProcessSupervisor? supervisor = null, ContentResourcePolicy? resourcePolicy = null, IResourceLoader? resourceLoader = null, IMediaDecoder? mediaDecoder = null, IMediaAudioOutput? mediaAudioOutput = null)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _supervisor = supervisor ?? new ContentProcessSupervisor();
        _resourcePolicy = resourcePolicy;
        _resourceLoader = resourceLoader;
        _mediaDecoder = mediaDecoder;
        _mediaAudioOutput = mediaAudioOutput;
    }

    public event Func<ContentMessage, CancellationToken, ValueTask>? MessageReceived;

    public async ValueTask RunAsync(CancellationToken cancellationToken = default)
    {
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _supervisor.CancellationToken);
        var messages = Channel.CreateBounded<(ContentMessage Message, int Bytes)>(64);
        var storage = Channel.CreateBounded<(ContentMessage Message, int Bytes)>(16);
        long queuedBytes = 0;
        Exception? dispatchError = null;
        // Storage remains available even when a document or a host callback is
        // waiting. The reader itself never calls user-provided storage code.
        var storageDispatch = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in storage.Reader.ReadAllAsync(running.Token).ConfigureAwait(false))
                {
                    Interlocked.Add(ref queuedBytes, -entry.Bytes);
                    await DispatchStorageAsync(entry.Message, running.Token).ConfigureAwait(false);
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException) Interlocked.Exchange(ref dispatchError, error);
                else Interlocked.CompareExchange(ref dispatchError, error, null);
                running.Cancel();
                throw;
            }
        }, CancellationToken.None);
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
            catch (Exception error)
            {
                if (error is not OperationCanceledException) Interlocked.Exchange(ref dispatchError, error);
                else Interlocked.CompareExchange(ref dispatchError, error, null);
                running.Cancel();
                throw;
            }
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
                else if (message.Kind == ContentMessageKind.MediaDecodeResponse) CompleteMediaDecode(message);
                else if (message.Kind == ContentMessageKind.MediaCaptureResponse) CompleteMediaCapture(message);
                else if (message.Kind == ContentMessageKind.StorageResponse) CompleteStorage(message);
                else
                {
                    var bytes = Encoding.UTF8.GetByteCount(message.Payload.GetRawText()) + 256;
                    var writer = message.Kind == ContentMessageKind.StorageRequest ? storage.Writer : messages.Writer;
                    if (Interlocked.Add(ref queuedBytes, bytes) > 32 * 1024 * 1024 || !writer.TryWrite((message, bytes)))
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
            storage.Writer.TryComplete();
            CloseStorageRequests();
        }
        try { await Task.WhenAll(dispatch, storageDispatch).ConfigureAwait(false); }
        catch when (readerError is not null) { } // Preserve the transport/protocol failure.
        finally { DisposeStorage(); }
        if (readerError is OperationCanceledException && dispatchError is not null) ExceptionDispatchInfo.Capture(dispatchError).Throw();
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
        else if (message.Kind == ContentMessageKind.MediaDecodeRequest)
        {
            var response = await DecodeMediaAsync(message, cancellationToken).ConfigureAwait(false);
            await SendAsync(ContentMessageKind.MediaDecodeResponse, new { requestId = message.Sequence, result = response }, cancellationToken).ConfigureAwait(false);
        }
        else if (message.Kind == ContentMessageKind.MediaAudioSubmit)
        {
            if (_mediaAudioOutput is not null && TryReadAudio(message.Payload, out var frame)) _mediaAudioOutput.Submit(frame);
        }
        else if (message.Kind == ContentMessageKind.MediaAudioLifecycle)
        {
            if (_mediaAudioOutput is IMediaAudioOutputLifecycle lifecycle && message.Payload.TryGetProperty("action", out var action))
            {
                switch (action.GetString())
                {
                    case "start": lifecycle.Start(); break;
                    case "pause": lifecycle.Pause(); break;
                    case "flush": lifecycle.Flush(); break;
                    case "stop": lifecycle.Stop(); break;
                }
            }
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

    private static bool TryReadAudio(JsonElement payload, out MediaAudioFrame frame)
    {
        frame = default;
        if (!payload.TryGetProperty("sampleRate", out var rate) || !rate.TryGetInt32(out var sampleRate)
            || !payload.TryGetProperty("channels", out var channels) || !channels.TryGetInt32(out var channelCount)
            || !payload.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array
            || sampleRate is <= 0 or > 384000 || channelCount is <= 0 or > 32
            || samples.GetArrayLength() == 0 || samples.GetArrayLength() > 2_000_000
            || samples.GetArrayLength() % channelCount != 0) return false;
        try
        {
            var values = samples.EnumerateArray().Select(item => item.GetSingle()).ToArray();
            if (values.Any(value => !float.IsFinite(value))) return false;
            frame = new MediaAudioFrame(sampleRate, channelCount, values);
            return true;
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException) { return false; }
    }

    private async Task<object?> DecodeMediaAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (_mediaDecoder is null || message.Payload.ValueKind != JsonValueKind.Object
            || !message.Payload.TryGetProperty("data", out var dataValue) || dataValue.ValueKind != JsonValueKind.String
            || !message.Payload.TryGetProperty("video", out var videoValue) || videoValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !message.Payload.TryGetProperty("maxOutputBytes", out var limitValue) || !limitValue.TryGetInt32(out var maxOutputBytes)
            || maxOutputBytes < 0)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(dataValue.GetString() ?? string.Empty);
            if (bytes.Length > MaxResourceResponseBytes) return null;
            var result = await Task.Run(() => _mediaDecoder.Decode(bytes, message.Payload.TryGetProperty("contentType", out var type) ? type.GetString() : null, videoValue.GetBoolean(), Math.Min(maxOutputBytes, MaxResourceResponseBytes)), cancellationToken).ConfigureAwait(false);
            if (result is null) return null;
            var frameBytes = result.VideoFrame?.Pixels.Length ?? 0;
            var sequenceBytes = result.VideoFrames?.Sum(item => (long)item.Frame.Pixels.Length) ?? 0;
            var audioBytes = (result.Audio?.Samples.Length ?? 0) * sizeof(float);
            // A sequence includes its first frame in the retained output. Charge the
            // sequence once, then add audio; checking each component independently
            // would allow a response whose aggregate exceeds the document budget.
            var videoBytes = sequenceBytes > 0 ? sequenceBytes : frameBytes;
            var totalOutputBytes = checked(videoBytes + audioBytes);
            if (totalOutputBytes > maxOutputBytes || totalOutputBytes > MaxResourceResponseBytes)
                return null;
            var frame = result.VideoFrame is null ? null : new { width = result.VideoFrame.Width, height = result.VideoFrame.Height, pixels = Convert.ToBase64String(result.VideoFrame.Pixels.ToArray()) };
            var frames = result.VideoFrames?.Select(item => new { time = item.Time, width = item.Frame.Width, height = item.Frame.Height, pixels = Convert.ToBase64String(item.Frame.Pixels.ToArray()) }).ToArray();
            var audio = result.Audio is { } sound ? new { sampleRate = sound.SampleRate, channels = sound.Channels, samples = sound.Samples.ToArray() } : null;
            return new { duration = result.Duration, frame, frames, audio };
        }
        catch { return null; }
    }

    public MediaDecodeResult? RequestMediaDecode(ReadOnlyMemory<byte> data, string? contentType, bool video, int maxOutputBytes, CancellationToken cancellationToken = default)
    {
        if (_connectionClosed) throw new InvalidOperationException("The content connection is closed before media decode.");
        if (data.Length > MaxResourceResponseBytes) throw new InvalidDataException("Media decode input exceeds the content limit.");
        if (maxOutputBytes < 0) throw new InvalidDataException("Media decode output limit is invalid.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        _requestSlots.Wait(timeout.Token);
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<MediaDecodeResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaDecodeRequests[sequence] = completion;
        try
        {
            WriteAsync(ContentMessageKind.MediaDecodeRequest, sequence, new { data = Convert.ToBase64String(data.Span), contentType, video, maxOutputBytes }, timeout.Token).AsTask().GetAwaiter().GetResult();
            return completion.Task.WaitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        finally { _mediaDecodeRequests.TryRemove(sequence, out _); _requestSlots.Release(); }
    }

    private void CompleteMediaDecode(ContentMessage message)
    {
        if (!message.Payload.TryGetProperty("requestId", out var id) || !id.TryGetInt64(out var sequence)) throw new InvalidDataException("Invalid media decode correlation.");
        if (!_mediaDecodeRequests.TryGetValue(sequence, out var request)) return;
        if (!message.Payload.TryGetProperty("result", out var result) || result.ValueKind == JsonValueKind.Null) { request.TrySetResult(null); return; }
        try { request.TrySetResult(MediaDecodeWire.Read(result)); }
        catch (Exception error) when (error is FormatException or InvalidOperationException or JsonException) { throw new InvalidDataException("Invalid media decode response.", error); }
    }

    private void CompleteMediaCapture(ContentMessage message)
    {
        if (!message.Payload.TryGetProperty("requestId", out var id) || !id.TryGetInt64(out var sequence))
            throw new InvalidDataException("Invalid media capture correlation.");
        if (_mediaCaptureRequests.TryGetValue(sequence, out var request)
            && message.Payload.TryGetProperty("result", out var result)) request.TrySetResult(result.Clone());
    }

    internal static class MediaDecodeWire
    {
        public static MediaDecodeResult Read(JsonElement value)
        {
            var duration = value.TryGetProperty("duration", out var durationValue) && durationValue.TryGetDouble(out var durationNumber) ? durationNumber : 0;
            if (!double.IsFinite(duration) || duration < 0) throw new JsonException("Invalid media duration.");
            IImageHandle? frame = null;
            if (value.TryGetProperty("frame", out var frameValue) && frameValue.ValueKind != JsonValueKind.Null)
            {
                if (!ReadImage(frameValue, out frame)) throw new JsonException("Invalid media frame image.");
            }
            List<MediaVideoFrame>? frames = null;
            if (value.TryGetProperty("frames", out var frameList))
            {
                // The host wire shape always includes the optional `frames`
                // member, but serializes it as null when the decoder returns a
                // single frame (or audio only).  Null is therefore a valid
                // absence, while any non-null value must be an array.
                if (frameList.ValueKind == JsonValueKind.Null) { frames = null; }
                else if (frameList.ValueKind != JsonValueKind.Array) throw new JsonException("Invalid media frame sequence.");
                else
                {
                frames = [];
                var previous = -1d;
                foreach (var item in frameList.EnumerateArray())
                {
                    if (!item.TryGetProperty("time", out var time) || !time.TryGetDouble(out var timestamp)
                        || !double.IsFinite(timestamp) || timestamp < 0 || timestamp < previous || !ReadImage(item, out var image))
                        throw new JsonException("Invalid media video frame.");
                    frames.Add(new MediaVideoFrame(timestamp, image));
                    previous = timestamp;
                }
                }
            }
            MediaAudioFrame? audio = null;
            if (value.TryGetProperty("audio", out var audioValue))
            {
                if (audioValue.ValueKind != JsonValueKind.Object
                    || !audioValue.TryGetProperty("sampleRate", out var rate) || !rate.TryGetInt32(out var sampleRate)
                    || !audioValue.TryGetProperty("channels", out var channels) || !channels.TryGetInt32(out var channelCount)
                    || !audioValue.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Invalid media audio payload.");
                if (sampleRate is <= 0 or > 384000 || channelCount is <= 0 or > 32
                    || samples.GetArrayLength() == 0 || samples.GetArrayLength() % channelCount != 0)
                    throw new JsonException("Invalid media audio format.");
                float[] values;
                try { values = samples.EnumerateArray().Select(item => item.GetSingle()).ToArray(); }
                catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException)
                { throw new JsonException("Invalid media audio samples.", error); }
                if (values.Any(value => !float.IsFinite(value))) throw new JsonException("Invalid media audio sample.");
                audio = new MediaAudioFrame(sampleRate, channelCount, values);
            }
            return new MediaDecodeResult(duration, frame, audio) { VideoFrames = frames };
        }
        private static IImageHandle? ReadImage(JsonElement parent, string name) => parent.TryGetProperty(name, out var value) ? ReadImage(value, out var image) ? image : null : null;
        private static bool ReadImage(JsonElement value, out IImageHandle image)
        {
            image = null!;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("width", out var width) || !width.TryGetInt32(out var w)
                || !value.TryGetProperty("height", out var height) || !height.TryGetInt32(out var h)
                || !value.TryGetProperty("pixels", out var pixels) || pixels.ValueKind != JsonValueKind.String) return false;
            var bytes = Convert.FromBase64String(pixels.GetString() ?? string.Empty);
            if (w <= 0 || h <= 0 || bytes.Length != checked(w * h * 4)) return false;
            image = new WireImage(w, h, bytes);
            return true;
        }
        private sealed class WireImage(int width, int height, byte[] pixels) : IImageHandle
        {
            public int Width => width;
            public int Height => height;
            public ReadOnlyMemory<byte> Pixels => pixels;
        }
    }

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

    /// <summary>Performs a bounded request against the other endpoint's host capture adapter.</summary>
    public async Task<JsonElement> RequestMediaCaptureAsync(object payload, CancellationToken cancellationToken = default)
    {
        if (_connectionClosed) throw new IOException("The content connection closed.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _requestSlots.WaitAsync(timeout.Token).ConfigureAwait(false);
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediaCaptureRequests[sequence] = completion;
        try
        {
            await WriteAsync(ContentMessageKind.MediaCaptureRequest, sequence, payload, timeout.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { _mediaCaptureRequests.TryRemove(sequence, out _); _requestSlots.Release(); }
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
        CloseStorageRequests();
        foreach (var request in _resourceRequests.Values)
            request.TrySetResult(ResourceResponse.Refused("The content connection closed."));
        _supervisor.Stop("content host session disposed");
        await _input.DisposeAsync().ConfigureAwait(false);
        await _output.DisposeAsync().ConfigureAwait(false);
        _supervisor.Dispose();
        _writeGate.Dispose();
    }
}

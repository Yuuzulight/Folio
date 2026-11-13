using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Folio.Scripting.Ipc;

namespace Folio.ContentHost;

/// <summary>Private stdio broker for a single sandboxed, network-disabled Folio document.</summary>
internal sealed class HeadlessContentBroker : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex FixtureLabel = new("^fixture:[A-Za-z0-9._/-]{1,128}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly string SnapshotExpression = """
        (function() {
            var controls = document.querySelectorAll('a[href],button,input,textarea,select,[role="button"],[role="link"],[contenteditable="true"]');
            var elements = [];
            var visible = [];
            function isVisible(el) {
                for (var node = el; node; node = node.parentElement) {
                    if (node.hasAttribute('hidden') || node.hasAttribute('inert') || node.getAttribute('aria-hidden') === 'true') return false;
                    var style = getComputedStyle(node);
                    if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
                }
                return true;
            }
            for (var visibleIndex = 0; visibleIndex < controls.length; visibleIndex++)
                if (isVisible(controls[visibleIndex])) visible.push(controls[visibleIndex]);
            var count = Math.min(visible.length, 500);
            var next = Number(window.__folioRefCounter || 0);
            for (var i = 0; i < count; i++) {
                var el = visible[i];
                var reference = el.getAttribute('data-folio-ref');
                var prefix = window.__folioRefPrefix || 'folio-e';
                if (!reference || reference.indexOf(prefix) !== 0) {
                    reference = prefix + (++next);
                    el.setAttribute('data-folio-ref', reference);
                }
                var tag = (el.tagName || '').toLowerCase();
                var labelled = '';
                var labelledBy = (el.getAttribute('aria-labelledby') || '').split(/\s+/);
                for (var labelIndex = 0; labelIndex < labelledBy.length; labelIndex++) {
                    var labelElement = document.getElementById(labelledBy[labelIndex]);
                    if (labelElement) labelled += (labelElement.textContent || '') + ' ';
                }
                var labels = '';
                if (el.labels) for (var associatedIndex = 0; associatedIndex < el.labels.length; associatedIndex++)
                    labels += (el.labels[associatedIndex].textContent || '') + ' ';
                var name = el.getAttribute('aria-label') || labelled.trim() || labels.trim() || el.getAttribute('placeholder') ||
                    (tag === 'input' ? el.getAttribute('title') || '' : el.textContent || '');
                var role = el.getAttribute('role') || ({a:'link',button:'button',textarea:'textbox',select:'combobox',input:'input'}[tag] || 'textbox');
                elements.push({
                    reference: reference,
                    tag: tag,
                    role: role,
                    name: name.replace(/\s+/g, ' ').trim().slice(0, 300),
                    href: tag === 'a' ? (el.href || el.getAttribute('href')) : null,
                    type: tag === 'input' ? (el.type || el.getAttribute('type')) : null,
                    disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true'
                });
            }
            window.__folioRefCounter = next;
            var bodyText = document.body ? (document.body.innerText || document.body.textContent || '') : '';
            var maxText = 12000;
            return JSON.stringify({
                url: document.location && document.location.href ? document.location.href : 'about:blank',
                title: document.title || '',
                elements: elements,
                truncated: visible.length > 500,
                text: bodyText.slice(0, maxText),
                textTruncated: bodyText.length > maxText
            });
        })()
        """;

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly int _parentPid;
    private readonly string _referencePrefix = "folio-" + Guid.NewGuid().ToString("N")[..12] + "-e";
    private ContentProcessClient? _client;
    private TaskCompletionSource<ContentMessage>? _pendingReply;
    private string _urlLabel = "about:blank";
    private long _lastWorkerSequence;
    private int _referenceCounter;
    private bool _closed;
    private bool _disposed;

    public HeadlessContentBroker(int parentPid)
    {
        if (parentPid <= 0 || parentPid == Environment.ProcessId)
            throw new ArgumentOutOfRangeException(nameof(parentPid), "The Arcana parent process ID must be a different positive process ID.");
        _parentPid = parentPid;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var parent = Process.GetProcessById(_parentPid);
        var parentExit = WatchParentAsync(parent, lifetime);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(lifetime.Token).ConfigureAwait(false);
                if (line is null) break;
                if (Encoding.UTF8.GetByteCount(line) > ContentProtocol.MaxPayloadBytes)
                {
                    await WriteAsync(ErrorResponse(default, false, "invalid_request", "Request exceeds the content protocol size limit."), lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                JsonElement id = default;
                var hasId = false;
                try
                {
                    using var requestDocument = JsonDocument.Parse(line);
                    var root = requestDocument.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw new BrokerRequestException("invalid_request", "Request must be a JSON object.");
                    if (!root.TryGetProperty("id", out var idValue)) throw new BrokerRequestException("invalid_request", "id is required.");
                    id = idValue.Clone();
                    hasId = true;
                    var operation = RequiredString(root, "op");
                    var response = await DispatchAsync(id, hasId, operation, root, lifetime.Token).ConfigureAwait(false);
                    await WriteAsync(response, lifetime.Token).ConfigureAwait(false);
                    if (operation.Equals("close", StringComparison.OrdinalIgnoreCase)) return;
                }
                catch (BrokerRequestException error)
                {
                    await WriteAsync(ErrorResponse(id, hasId, error.Code, error.Message), lifetime.Token).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await WriteAsync(ErrorResponse(id, hasId, "invalid_request", "Request JSON is malformed."), lifetime.Token).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await WriteAsync(ErrorResponse(id, hasId, "timeout", "The Folio content process did not respond in time."), lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    await WriteAsync(ErrorResponse(id, hasId, "engine_error", error.Message), lifetime.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    await WriteAsync(ErrorResponse(id, hasId, "engine_error", error.Message), lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            await parentExit.ConfigureAwait(false);
        }
    }

    private static async Task WatchParentAsync(Process parent, CancellationTokenSource lifetime)
    {
        try { await parent.WaitForExitAsync(lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
        lifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _closed = true;
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _requestGate.Dispose();
    }

    private async Task<object> DispatchAsync(JsonElement id, bool hasId, string operation, JsonElement request, CancellationToken cancellationToken)
    {
        if (_closed) throw new BrokerRequestException("invalid_state", "The broker has been closed.");
        if (operation.Equals("hello", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureClientAsync().ConfigureAwait(false);
            var hello = await ExchangeAsync(ContentMessageKind.Hello, new { }, ContentMessageKind.Hello, cancellationToken).ConfigureAwait(false);
            var protocol = hello.Payload.TryGetProperty("protocol", out var protocolValue) && protocolValue.TryGetInt32(out var version) ? version : ContentMessage.CurrentVersion;
            return new { id = IdOrNull(id, hasId), ok = true, apiVersion = "v1", engine = new { product = "Folio.ContentHost/native", protocol }, sandboxed = true, networkEnabled = false };
        }
        if (operation.Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            var html = RequiredString(request, "html");
            var urlLabel = request.TryGetProperty("urlLabel", out var labelValue) && labelValue.ValueKind == JsonValueKind.String
                ? labelValue.GetString()! : "about:blank";
            if (!IsAllowedUrlLabel(urlLabel)) throw new BrokerRequestException("invalid_request", "urlLabel must be about:blank or a fixture: label.");
            await EnsureClientAsync().ConfigureAwait(false);
            _urlLabel = urlLabel;
            _lastWorkerSequence = 0;
            var initializeReferences = $"window.__folioRefPrefix={JsonSerializer.Serialize(_referencePrefix)};window.__folioRefCounter={_referenceCounter.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var frame = await ExchangeAsync(ContentMessageKind.Load, new { html, script = initializeReferences, url = _urlLabel, capabilities = FeatureCapabilities.Default }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            return await FrameResponseAsync(id, hasId, frame, cancellationToken).ConfigureAwait(false);
        }
        if (operation.Equals("evaluate", StringComparison.OrdinalIgnoreCase))
        {
            EnsureLoaded();
            var expression = RequiredString(request, "expression");
            var frame = await ExchangeAsync(ContentMessageKind.Load, new { script = expression, url = _urlLabel }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            return await FrameResponseAsync(id, hasId, frame, cancellationToken).ConfigureAwait(false);
        }
        if (operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            EnsureLoaded();
            var frame = await ExchangeAsync(ContentMessageKind.Load, new { script = SnapshotExpression, url = _urlLabel }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            var value = RequiredPayloadString(frame.Payload, "value");
            using var snapshotDocument = JsonDocument.Parse(value);
            var snapshot = snapshotDocument.RootElement.Clone();
            if (snapshot.ValueKind != JsonValueKind.Object ||
                !snapshot.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array ||
                !snapshot.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new BrokerRequestException("engine_error", "Folio returned an invalid page snapshot.");
            var normalized = JsonSerializer.Deserialize<SnapshotResponse>(snapshot.GetRawText(), JsonOptions)
                ?? throw new BrokerRequestException("engine_error", "Folio returned an invalid page snapshot.");
            foreach (var element in normalized.Elements)
                if (element.Reference.StartsWith(_referencePrefix, StringComparison.Ordinal) &&
                    int.TryParse(element.Reference.AsSpan(_referencePrefix.Length), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var referenceNumber))
                    _referenceCounter = Math.Max(_referenceCounter, referenceNumber);
            var sequence = GetWorkerSequence(frame.Payload);
            if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale content-process response.");
            _lastWorkerSequence = sequence;
            normalized = normalized with { Url = _urlLabel };
            return new { id = IdOrNull(id, hasId), ok = true, snapshot = normalized, sequence };
        }
        if (operation.Equals("screenshot", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("click", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("fill", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("select", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("press", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
            operation.Equals("upload", StringComparison.OrdinalIgnoreCase))
            throw new BrokerRequestException("unsupported", $"The {operation} operation is not supported by this Folio broker.");
        if (operation.Equals("close", StringComparison.OrdinalIgnoreCase))
        {
            _closed = true;
            return new { id = IdOrNull(id, hasId), ok = true };
        }
        throw new BrokerRequestException("unsupported", $"The {operation} operation is not supported by this Folio broker.");
    }

    private Task EnsureClientAsync()
    {
        if (_client is not null) return Task.CompletedTask;
        if (!OperatingSystem.IsWindows()) throw new BrokerRequestException("sandbox_unavailable", "Folio's AppContainer content sandbox is only available on Windows.");
        if (string.Equals(Environment.GetEnvironmentVariable("FOLIO_UNSANDBOXED"), "1", StringComparison.Ordinal))
            throw new BrokerRequestException("sandbox_bypassed", "FOLIO_UNSANDBOXED disables the required content-process sandbox.");

        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath)) throw new BrokerRequestException("engine_error", "Unable to locate the Folio content-host build.");
        var hostPath = Path.ChangeExtension(assemblyPath, ".exe");
        if (!File.Exists(hostPath)) hostPath = assemblyPath;
        _client = ContentProcessClient.Start(hostPath, limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(20)));
        _client.MessageReceived += OnContentMessageAsync;
        _client.ScriptStopped += OnScriptStopped;
        return Task.CompletedTask;
    }

    private void EnsureLoaded()
    {
        if (_client is null) throw new BrokerRequestException("invalid_state", "Load a document before requesting page data.");
        if (_lastWorkerSequence == 0) throw new BrokerRequestException("invalid_state", "Load a document before requesting page data.");
    }

    private async Task<ContentMessage> ExchangeAsync(ContentMessageKind requestKind, object payload, ContentMessageKind expectedKind, CancellationToken cancellationToken)
    {
        var client = _client ?? throw new BrokerRequestException("invalid_state", "The content process is not running.");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReply = completion;
            await client.SendAsync(requestKind, payload, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            ContentMessage reply;
            try { reply = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new TimeoutException(); }
            finally { _pendingReply = null; }

            if (reply.Kind == ContentMessageKind.Diagnostic)
            {
                var code = RequiredPayloadString(reply.Payload, "code");
                var message = RequiredPayloadString(reply.Payload, "message");
                throw new BrokerRequestException(code, message);
            }
            if (reply.Kind != expectedKind) throw new BrokerRequestException("engine_error", "The content process returned an unexpected response.");
            return reply;
        }
        finally { _requestGate.Release(); }
    }

    private ValueTask OnContentMessageAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic or ContentMessageKind.Hello)
            _pendingReply?.TrySetResult(message);
        return ValueTask.CompletedTask;
    }

    private void OnScriptStopped(object? sender, ContentProcessStoppedEventArgs eventArgs)
        => _pendingReply?.TrySetException(new BrokerRequestException("content_process_stopped", eventArgs.Reason));

    private Task<object> FrameResponseAsync(JsonElement id, bool hasId, ContentMessage frame, CancellationToken cancellationToken)
    {
        var sequence = GetWorkerSequence(frame.Payload);
        if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale content-process response.");
        _lastWorkerSequence = sequence;
        var value = frame.Payload.TryGetProperty("value", out var valueElement) && valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() : null;
        var displayItems = frame.Payload.TryGetProperty("displayItems", out var displayValue) && displayValue.TryGetInt32(out var displayCount) ? displayCount : 0;
        var canvasCommands = frame.Payload.TryGetProperty("canvasCommands", out var canvasValue) && canvasValue.TryGetInt32(out var commandCount) ? commandCount : 0;
        var diagnostics = frame.Payload.TryGetProperty("diagnostics", out var diagnosticsValue) && diagnosticsValue.ValueKind == JsonValueKind.Array
            ? diagnosticsValue.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
        var canvasFrame = ReadCanvasFrame(frame.Payload);
        return Task.FromResult<object>(new
        {
            id = IdOrNull(id, hasId), ok = true, url = _urlLabel, value,
            displayItems, canvasCommands, diagnostics, sequence,
            rendered = displayItems > 0,
            canvasFrame
        });
    }

    private object? ReadCanvasFrame(JsonElement payload)
    {
        if (_client is null || !payload.TryGetProperty("canvasFrame", out var descriptor) || descriptor.ValueKind != JsonValueKind.Object)
            return null;
        var path = descriptor.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
        var width = descriptor.TryGetProperty("width", out var widthElement) && widthElement.TryGetInt32(out var parsedWidth) ? parsedWidth : 0;
        var height = descriptor.TryGetProperty("height", out var heightElement) && heightElement.TryGetInt32(out var parsedHeight) ? parsedHeight : 0;
        var stride = descriptor.TryGetProperty("stride", out var strideElement) && strideElement.TryGetInt32(out var parsedStride) ? parsedStride : 0;
        var describedSequence = descriptor.TryGetProperty("sequence", out var sequenceElement) && sequenceElement.TryGetInt64(out var parsedSequence) ? parsedSequence : 0;
        if (!string.Equals(path, _client.FramePath, StringComparison.Ordinal) || !CanvasFrameStore.IsValidDescriptor(width, height, stride))
            throw new BrokerRequestException("engine_error", "Folio returned an invalid rendered-frame descriptor.");
        if (describedSequence <= 0) return new { sequence = describedSequence, width, height, stride, pixelProbe = (object?)null };

        using var reader = new CanvasFrameStore(path!, width, height, stride);
        var pixels = reader.ReadLatest(out var actualSequence);
        if (actualSequence != describedSequence) throw new BrokerRequestException("engine_error", "Folio's rendered-frame sequence did not match its descriptor.");
        var inside = ReadPixel(pixels, stride, width, height, 16, 16);
        var outside = ReadPixel(pixels, stride, width, height, 100, 100);
        var background = ReadPixel(pixels, stride, width, height, width - 1, height - 1);
        var changedPixels = CountPixelsDifferentFrom(pixels, stride, width, height, background);
        return new { sequence = actualSequence, width, height, stride, pixelProbe = new { inside, outside, changedPixels } };
    }

    private static int CountPixelsDifferentFrom(byte[] pixels, int stride, int width, int height, int[] background)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 4;
                if (pixels[offset] != background[0] || pixels[offset + 1] != background[1] ||
                    pixels[offset + 2] != background[2] || pixels[offset + 3] != background[3])
                    count++;
            }
        return count;
    }

    private static int[] ReadPixel(byte[] pixels, int stride, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        var offset = checked(y * stride + x * 4);
        return pixels.AsSpan(offset, 4).ToArray().Select(value => (int)value).ToArray();
    }

    private static long GetWorkerSequence(JsonElement payload)
        => payload.TryGetProperty("sequence", out var value) && value.TryGetInt64(out var sequence)
            ? sequence : throw new BrokerRequestException("engine_error", "Folio response did not contain its request sequence.");

    private static bool IsAllowedUrlLabel(string value)
        => value.Equals("about:blank", StringComparison.OrdinalIgnoreCase) || FixtureLabel.IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);

    private static string RequiredString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
            ? property.GetString()! : throw new BrokerRequestException("invalid_request", $"{name} is required.");

    private static string RequiredPayloadString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()! : throw new BrokerRequestException("engine_error", $"Folio response did not contain {name}.");

    private static JsonElement IdOrNull(JsonElement id, bool hasId)
        => hasId ? id : JsonSerializer.SerializeToElement<object?>(null);

    private static object ErrorResponse(JsonElement id, bool hasId, string code, string message)
        => new { id = IdOrNull(id, hasId), ok = false, error = new { code, message } };

    private static async Task WriteAsync(object response, CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class BrokerRequestException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed record SnapshotResponse(
        string Url, string Title, IReadOnlyList<SnapshotElement> Elements, bool Truncated, string Text, bool TextTruncated);

    private sealed record SnapshotElement(
        string Reference, string Tag, string Role, string Name, string? Href, string? Type, bool Disabled);
}

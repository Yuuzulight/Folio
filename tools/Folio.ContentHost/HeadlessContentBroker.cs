using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading.Channels;
using Folio;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Resources;

namespace Folio.ContentHost;

/// <summary>Private stdio broker for a single sandboxed Folio document.</summary>
internal sealed class HeadlessContentBroker : IAsyncDisposable
{
    private static readonly JsonElement NullId = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly Regex FixtureLabel = new("^fixture:[A-Za-z0-9._/-]{1,128}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private const long MaxTopLevelBytes = 25 * 1024 * 1024;
    private const long MaxDownloadBytes = 50 * 1024 * 1024;
    private const int MaxRedirects = 10;
    private const int MaxChainedNavigations = 5;

    private static readonly string SnapshotExpression = """
        (function() {
            var elements = [];
            var textParts = [];
            var totalControls = 0;
            var truncated = false;
            var maxControls = 500;
            var next = Number(window.__folioRefCounter || 0);
            var prefix = window.__folioRefPrefix || 'folio-e';
            var frameCounter = 0;
            function styleFor(doc, el) {
                try {
                    var view = doc && doc.defaultView;
                    if (view && typeof view.getComputedStyle === 'function') return view.getComputedStyle(el);
                    return typeof getComputedStyle === 'function' ? getComputedStyle(el) : null;
                } catch (e) {
                    return null;
                }
            }
            function isVisible(doc, el) {
                for (var node = el; node; node = node.parentElement) {
                    if (node.hasAttribute && (node.hasAttribute('hidden') || node.hasAttribute('inert') || node.getAttribute('aria-hidden') === 'true')) return false;
                    var style = styleFor(doc, node);
                    if (style && (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse')) return false;
                }
                return true;
            }
            function collectText(doc) {
                if (!doc) return '';
                if (doc.body) return (doc.body.innerText || doc.body.textContent || '');
                return doc.documentElement ? (doc.documentElement.textContent || '') : '';
            }
            function labelText(doc, el) {
                var labelled = '';
                var labelledBy = (el.getAttribute('aria-labelledby') || '').split(/\s+/);
                for (var i = 0; i < labelledBy.length; i++) {
                    if (!labelledBy[i]) continue;
                    var labelEl = doc.getElementById(labelledBy[i]);
                    if (labelEl) labelled += (labelEl.textContent || '') + ' ';
                }
                var labels = '';
                if (el.labels) {
                    for (var j = 0; j < el.labels.length; j++) labels += (el.labels[j].textContent || '') + ' ';
                }
                return el.getAttribute('aria-label') || labelled.trim() || labels.trim();
            }
            function visitDocument(doc, offsetX, offsetY, depth, frameId) {
                if (!doc || depth > 5) return;
                var txt = collectText(doc).trim();
                if (txt) textParts.push(txt);
                var controls = doc.querySelectorAll('a[href],button,input,textarea,select,option,[role="button"],[role="link"],[role="checkbox"],[role="radio"],[role="combobox"],[role="textbox"],[role="option"],[role="menuitem"],[role="tab"],[contenteditable="true"]');
                for (var i = 0; i < controls.length; i++) {
                    var el = controls[i];
                    if (!isVisible(doc, el)) continue;
                    totalControls++;
                    if (elements.length >= maxControls) { truncated = true; continue; }
                    var reference = el.getAttribute('data-folio-ref');
                    if (!reference || reference.indexOf(prefix) === -1) {
                        reference = (frameId > 0 ? ('f' + frameId + 'e-') : '') + prefix + (++next);
                        el.setAttribute('data-folio-ref', reference);
                    }
                    var tag = (el.tagName || '').toLowerCase();
                    var inputType = tag === 'input' ? String(el.type || el.getAttribute('type') || 'text').toLowerCase() : null;
                    var name = labelText(doc, el) || el.getAttribute('placeholder') ||
                        (tag === 'input' ? (el.getAttribute('title') || el.value || '') : (el.textContent || ''));
                    var inferredInputRole = inputType === 'checkbox' ? 'checkbox'
                        : (inputType === 'radio' ? 'radio'
                        : (inputType === 'number' ? 'spinbutton'
                        : (inputType === 'submit' || inputType === 'button' || inputType === 'reset' ? 'button' : 'input')));
                    var role = el.getAttribute('role') || ({ a: 'link', button: 'button', textarea: 'textbox', select: 'combobox', option: 'option', input: inferredInputRole }[tag] || 'textbox');
                    var isPassword = (tag === 'input' && inputType === 'password') || el.getAttribute('autocomplete') === 'current-password' || el.getAttribute('autocomplete') === 'new-password';
                    var sensitive = isPassword || !!(el.closest && el.closest('[data-sensitive="true"]')) || null;
                    var val = null;
                    if (tag === 'input' && inputType !== 'checkbox' && inputType !== 'radio') {
                        var rawVal = String(el.value || '');
                        val = sensitive && rawVal.length > 0 ? '[redacted]' : (rawVal.length > 0 ? rawVal.slice(0, 500) : null);
                    } else if (tag === 'textarea' || tag === 'select' || tag === 'option') {
                        var rawTextVal = String(el.value || '');
                        val = sensitive && rawTextVal.length > 0 ? '[redacted]' : rawTextVal.slice(0, 500);
                    } else if (el.getAttribute('contenteditable') === 'true') {
                        var editableVal = String(el.textContent || '');
                        val = sensitive && editableVal.length > 0 ? '[redacted]' : editableVal.slice(0, 500);
                    }
                    var checked = (tag === 'input' && (inputType === 'checkbox' || inputType === 'radio'))
                        ? !!el.checked
                        : (el.hasAttribute('aria-checked') ? el.getAttribute('aria-checked') === 'true' : null);
                    var selected = tag === 'option'
                        ? !!el.selected
                        : (el.hasAttribute('aria-selected') ? el.getAttribute('aria-selected') === 'true' : null);
                    var bounds = null;
                    try {
                        var rect = el.getBoundingClientRect ? el.getBoundingClientRect() : null;
                        if (rect && (rect.width > 0 || rect.height > 0)) {
                            bounds = { x: rect.left + offsetX, y: rect.top + offsetY, width: rect.width, height: rect.height };
                        }
                    } catch (e) {}
                    elements.push({
                        reference: reference,
                        tag: tag,
                        role: role,
                        name: String(name || '').replace(/\s+/g, ' ').trim().slice(0, 300),
                        href: tag === 'a' ? (el.href || el.getAttribute('href')) : null,
                        type: inputType,
                        disabled: !!el.disabled || el.getAttribute('aria-disabled') === 'true',
                        value: val,
                        checked: checked,
                        selected: selected,
                        sensitive: sensitive,
                        bounds: bounds
                    });
                }
                var frames = doc.querySelectorAll('iframe,frame');
                for (var f = 0; f < frames.length; f++) {
                    var frameEl = frames[f];
                    try {
                        var childDoc = frameEl.contentDocument;
                        if (!childDoc) continue;
                        var fRect = frameEl.getBoundingClientRect ? frameEl.getBoundingClientRect() : null;
                        var fx = fRect ? offsetX + fRect.left : offsetX;
                        var fy = fRect ? offsetY + fRect.top : offsetY;
                        visitDocument(childDoc, fx, fy, depth + 1, ++frameCounter);
                    } catch (e) {}
                }
            }
            visitDocument(document, 0, 0, 0, 0);
            window.__folioRefCounter = next;
            var bodyText = textParts.join('\n');
            var maxText = 12000;
            return JSON.stringify({
                url: document.location && document.location.href ? document.location.href : 'about:blank',
                title: document.title || '',
                elements: elements,
                truncated: truncated || totalControls > maxControls,
                text: bodyText.slice(0, maxText),
                textTruncated: bodyText.length > maxText
            });
        })()
        """;

    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly int _parentPid;
    private readonly bool _allowPrivateNetwork;
    private readonly bool _streamEvents;
    private readonly IReadOnlyList<AllowedOrigin> _allowedOrigins;
    private readonly string _referencePrefix = "folio-" + Guid.NewGuid().ToString("N")[..12] + "-e";
    private readonly IArtifactStorage _storage = new BrokerMemoryStorage(Environment.GetEnvironmentVariable("FOLIO_STORAGE_PATH"));
    private readonly List<BrokerInterceptionRule> _interceptionRules = [];
    private readonly object _interceptionLock = new();
    private readonly List<TaskCompletionSource> _idleWaiters = [];
    private readonly object _idleLock = new();
    private Channel<Func<CancellationToken, Task>>? _eventChannel;
    private Task? _eventPumpTask;
    private HttpClient? _httpClient;
    private ContentProcessClient? _client;
    private BrokerResourceLoader? _resourceLoader;
    private ContentResourcePolicy? _resourcePolicy;
    private TaskCompletionSource<ContentMessage>? _pendingReply;
    private PendingNavigation? _pendingNavigation;
    private string _urlLabel = "about:blank";
    private long _lastWorkerSequence;
    private long _lastBroadcastCanvasSequence;
    private int _referenceCounter;
    private int _inFlightRequests;
    private int _viewportWidth = 1280;
    private int _viewportHeight = 720;
    private double _pointerX;
    private double _pointerY;
    private bool _closed;
    private bool _disposed;

    public HeadlessContentBroker(int parentPid, IEnumerable<string>? allowedOrigins = null, bool allowPrivateNetwork = false, bool streamEvents = false)
    {
        if (parentPid <= 0 || parentPid == Environment.ProcessId)
            throw new ArgumentOutOfRangeException(nameof(parentPid), "The Arcana parent process ID must be a different positive process ID.");
        _parentPid = parentPid;
        _allowPrivateNetwork = allowPrivateNetwork;
        _streamEvents = streamEvents;
        _allowedOrigins = (allowedOrigins ?? [])
            .Select(origin => ParseAllowedOrigin(origin, _allowPrivateNetwork))
            .DistinctBy(origin => origin.Origin, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Process parent;
        try { parent = Process.GetProcessById(_parentPid); }
        catch (ArgumentException) { return; }
        using (parent)
        {
        var parentExit = WatchParentAsync(parent, lifetime);
        if (_streamEvents)
        {
            _eventChannel = Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });
            _eventPumpTask = PumpEventsAsync(_eventChannel.Reader, lifetime.Token);
        }
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                string? line;
                var readTask = Task.Run(() => ReadBoundedLineAsync(lifetime.Token), CancellationToken.None);
                if (await Task.WhenAny(readTask, parentExit).ConfigureAwait(false) == parentExit) break;
                try { line = await readTask.ConfigureAwait(false); }
                catch (InvalidDataException)
                {
                    await WriteAsync(ErrorResponse(default, false, "invalid_request", "Request exceeds the content protocol size limit."), lifetime.Token).ConfigureAwait(false);
                    continue;
                }
                if (line is null) break;

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
            _eventChannel?.Writer.TryComplete();
            if (_eventPumpTask is not null)
            {
                try { await _eventPumpTask.ConfigureAwait(false); } catch { }
            }
            await parentExit.ConfigureAwait(false);
        }
        }
    }

    private static async Task<string?> ReadBoundedLineAsync(CancellationToken cancellationToken)
    {
        var reader = Console.In;
        var buffer = new char[4096];
        var line = new StringBuilder();
        var byteCount = 0;
        var oversized = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (line.Length == 0 && !oversized) return null;
                break;
            }
            var segmentStart = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != '\n') continue;
                if (!oversized && index > segmentStart)
                {
                    var segmentBytes = Encoding.UTF8.GetByteCount(buffer, segmentStart, index - segmentStart);
                    if ((long)byteCount + segmentBytes > ContentProtocol.MaxPayloadBytes) oversized = true;
                    else { line.Append(buffer, segmentStart, index - segmentStart); byteCount += segmentBytes; }
                }
                if (oversized) throw new InvalidDataException("Request exceeds the content protocol size limit.");
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return line.ToString();
            }
            if (!oversized && count > segmentStart)
            {
                var segmentBytes = Encoding.UTF8.GetByteCount(buffer, segmentStart, count - segmentStart);
                if ((long)byteCount + segmentBytes > ContentProtocol.MaxPayloadBytes) oversized = true;
                else { line.Append(buffer, segmentStart, count - segmentStart); byteCount += segmentBytes; }
            }
            if (oversized)
            {
                // Keep draining only until newline; never retain the remainder of an oversized request.
                while (Array.IndexOf(buffer, '\n', 0, count) < 0)
                {
                    count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                }
                throw new InvalidDataException("Request exceeds the content protocol size limit.");
            }
        }
        if (oversized) throw new InvalidDataException("Request exceeds the content protocol size limit.");
        if (line.Length > 0 && line[^1] == '\r') line.Length--;
        return line.ToString();
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
        _eventChannel?.Writer.TryComplete();
        if (_eventPumpTask is not null)
        {
            try { await _eventPumpTask.ConfigureAwait(false); } catch { }
        }
        if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false);
        _httpClient?.Dispose();
        _requestGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task<object> DispatchAsync(JsonElement id, bool hasId, string operation, JsonElement request, CancellationToken cancellationToken)
    {
        if (_closed) throw new BrokerRequestException("invalid_state", "The broker has been closed.");
        if (operation.Equals("hello", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            var hello = await ExchangeAsync(ContentMessageKind.Hello, EmptyObject, ContentMessageKind.Hello, cancellationToken).ConfigureAwait(false);
            var protocol = hello.Payload.TryGetProperty("protocol", out var protocolValue) && protocolValue.TryGetInt32(out var version) ? version : ContentMessage.CurrentVersion;
            return new BrokerHelloResponse(IdOrNull(id, hasId), true, "v1", new BrokerEngine("Folio.ContentHost/native", protocol), true, _allowedOrigins.Count > 0 || _allowPrivateNetwork);
        }
        if (operation.Equals("navigate", StringComparison.OrdinalIgnoreCase))
            return await NavigateAsync(id, hasId, RequiredString(request, "url"), cancellationToken).ConfigureAwait(false);
        if (operation.Equals("load", StringComparison.OrdinalIgnoreCase))
        {
            var html = NormalizeFramesetHtml(RequiredString(request, "html"));
            var urlLabel = request.TryGetProperty("urlLabel", out var labelValue) && labelValue.ValueKind == JsonValueKind.String
                ? labelValue.GetString()! : "about:blank";
            if (!IsAllowedUrlLabel(urlLabel)) throw new BrokerRequestException("invalid_request", "urlLabel must be about:blank or a fixture: label.");
            await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _pendingNavigation, null);
            _urlLabel = urlLabel;
            _lastWorkerSequence = 0;
            var initializeReferences = $"window.__folioRefPrefix='{_referencePrefix}';window.__folioRefCounter={_referenceCounter.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            var frame = await ExchangeAsync(ContentMessageKind.Load, new JsonObject
            {
                ["html"] = html,
                ["script"] = initializeReferences,
                ["url"] = _urlLabel,
                ["capabilities"] = CreateCapabilitiesObject()
            }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            if (FindPolicyRefusal(frame.Payload) is { } loadRefusal)
                throw new BrokerRequestException("policy_refused", loadRefusal);
            var navReply = await FollowPendingNavigationAsync(id, hasId, cancellationToken).ConfigureAwait(false);
            if (navReply is not null) return navReply;
            return await FrameResponseAsync(id, hasId, frame, cancellationToken).ConfigureAwait(false);
        }
        if (operation.Equals("evaluate", StringComparison.OrdinalIgnoreCase))
        {
            await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
            EnsureLoaded();
            Volatile.Write(ref _pendingNavigation, null);
            var expression = RequiredString(request, "expression");
            var frame = await ExchangeAsync(ContentMessageKind.Load, new JsonObject { ["script"] = expression, ["url"] = _urlLabel }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            var frameReply = await FrameResponseAsync(id, hasId, frame, cancellationToken).ConfigureAwait(false);
            await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
            return frameReply;
        }
        if (operation.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
            EnsureLoaded();
            var frame = await ExchangeAsync(ContentMessageKind.Load, new JsonObject { ["script"] = SnapshotExpression, ["url"] = _urlLabel }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            var value = RequiredPayloadString(frame.Payload, "value");
            using var snapshotDocument = JsonDocument.Parse(value);
            var snapshot = snapshotDocument.RootElement.Clone();
            if (snapshot.ValueKind != JsonValueKind.Object ||
                !snapshot.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array ||
                !snapshot.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                throw new BrokerRequestException("engine_error", "Folio returned an invalid page snapshot.");
            var normalized = JsonSerializer.Deserialize(snapshot.GetRawText(), HeadlessContentHostBrokerJsonContext.Default.SnapshotResponse)
                ?? throw new BrokerRequestException("engine_error", "Folio returned an invalid page snapshot.");
            foreach (var element in normalized.Elements)
            {
                var prefixAt = element.Reference.IndexOf(_referencePrefix, StringComparison.Ordinal);
                if (prefixAt >= 0 &&
                    int.TryParse(element.Reference.AsSpan(prefixAt + _referencePrefix.Length), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var referenceNumber))
                    _referenceCounter = Math.Max(_referenceCounter, referenceNumber);
            }
            var sequence = GetWorkerSequence(frame.Payload);
            if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale content-process response.");
            _lastWorkerSequence = sequence;
            var resolvedSnapshotUrl = !string.IsNullOrWhiteSpace(normalized.Url) && !normalized.Url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                ? normalized.Url : _urlLabel;
            normalized = normalized with { Url = resolvedSnapshotUrl };
            return new BrokerSnapshotResponse(IdOrNull(id, hasId), true, normalized, sequence);
        }
        if (operation.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
        {
            await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
            EnsureLoaded();
            var frame = await ExchangeAsync(ContentMessageKind.Load,
                new JsonObject { ["script"] = "document.documentElement ? document.documentElement.outerHTML.length : 0", ["url"] = _urlLabel },
                ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
            var sequence = GetWorkerSequence(frame.Payload);
            if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale content-process response.");
            _lastWorkerSequence = sequence;
            var captured = CaptureCanvasFrame(frame.Payload)
                ?? throw new BrokerRequestException("engine_error", "Folio did not provide a rendered frame for this page.");
            if (captured.Pixels is null)
                throw new BrokerRequestException("engine_error", "Folio has not rendered a page frame yet.");
            var png = BrokerPngEncoder.EncodeRgba8(captured.Pixels, captured.Frame.Width, captured.Frame.Height, captured.Frame.Stride);
            return new BrokerScreenshotResponse(IdOrNull(id, hasId), true, "image/png", captured.Frame.Width,
                captured.Frame.Height, captured.Frame.Sequence, Convert.ToBase64String(png));
        }
        switch (operation.ToLowerInvariant())
        {
            case "click-at":
            {
                var x = RequiredFiniteNumber(request, "x");
                var y = RequiredFiniteNumber(request, "y");
                var (button, buttonsMask) = ParseMouseButton(request);
                var clickCount = ParseClickCount(request);
                await ClickCoordinatesAsync(x, y, button, buttonsMask, clickCount, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "move-at":
            {
                var x = RequiredFiniteNumber(request, "x");
                var y = RequiredFiniteNumber(request, "y");
                await SendInputAsync(new JsonObject { ["type"] = "pointermove", ["x"] = x, ["y"] = y, ["button"] = 0, ["buttons"] = 0 }, cancellationToken).ConfigureAwait(false);
                _pointerX = x;
                _pointerY = y;
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "press":
            {
                var key = RequiredString(request, "key");
                if (string.IsNullOrWhiteSpace(key)) throw new BrokerRequestException("invalid_request", "key must not be empty.");
                Volatile.Write(ref _pendingNavigation, null);
                await SendInputAsync(new JsonObject { ["type"] = "keydown", ["key"] = key }, cancellationToken).ConfigureAwait(false);
                await SendInputAsync(new JsonObject { ["type"] = "keyup", ["key"] = key }, cancellationToken).ConfigureAwait(false);
                await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "type":
            {
                Volatile.Write(ref _pendingNavigation, null);
                await SendInputAsync(new JsonObject { ["type"] = "textinput", ["text"] = RequiredPayloadString(request, "text") }, cancellationToken).ConfigureAwait(false);
                await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "scroll":
            {
                var sx = request.TryGetProperty("x", out var xProp) && xProp.ValueKind == JsonValueKind.Number ? xProp.GetDouble() : _pointerX;
                var sy = request.TryGetProperty("y", out var yProp) && yProp.ValueKind == JsonValueKind.Number ? yProp.GetDouble() : _pointerY;
                await SendInputAsync(new JsonObject
                {
                    ["type"] = "wheel",
                    ["x"] = sx,
                    ["y"] = sy,
                    ["deltaX"] = RequiredFiniteNumber(request, "deltaX"),
                    ["deltaY"] = RequiredFiniteNumber(request, "deltaY")
                }, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "hover":
            {
                EnsureLoaded();
                var reference = RequiredString(request, "reference");
                var (x, y, _) = await LocateReferenceTargetAsync(reference, requireEnabled: false, cancellationToken).ConfigureAwait(false);
                await SendInputAsync(new JsonObject { ["type"] = "pointermove", ["x"] = x, ["y"] = y, ["button"] = 0, ["buttons"] = 0 }, cancellationToken).ConfigureAwait(false);
                _pointerX = x;
                _pointerY = y;
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "click":
            {
                EnsureLoaded();
                var reference = RequiredString(request, "reference");
                var (button, buttonsMask) = ParseMouseButton(request);
                var clickCount = ParseClickCount(request);
                var (x, y, inChildFrame) = await LocateReferenceTargetAsync(reference, requireEnabled: true, cancellationToken).ConfigureAwait(false);
                await ClickCoordinatesAsync(x, y, button, buttonsMask, clickCount, cancellationToken).ConfigureAwait(false);
                if (inChildFrame)
                {
                    var childClickScript = BuildReferenceScript(reference, """
                        if (element.disabled || element.getAttribute('aria-disabled') === 'true') throw new Error('Element is disabled.');
                        if (typeof element.focus === 'function') element.focus();
                        var notCancelled = true;
                        if (typeof element.dispatchEvent === 'function') {
                            var ev;
                            try { ev = new MouseEvent('click', { bubbles: true, cancelable: true, view: ownerDoc.defaultView || undefined }); }
                            catch (e) { ev = new Event('click', { bubbles: true, cancelable: true }); }
                            notCancelled = element.dispatchEvent(ev);
                        }
                        if (notCancelled) {
                            var anchor = element.closest ? element.closest('a[href]') : ((element.tagName || '').toLowerCase() === 'a' && element.getAttribute('href') ? element : null);
                            if (anchor && ownerFrame) {
                                var rawHref = anchor.getAttribute('href');
                                if (rawHref) {
                                    var baseHref = (ownerDoc.location && ownerDoc.location.href) || (document.location && document.location.href) || 'about:blank';
                                    var resolved = new URL(rawHref, baseHref).href;
                                    if (resolved.indexOf('https://') === 0 || resolved.indexOf('http://') === 0) {
                                        ownerFrame.src = resolved;
                                        var _ = ownerFrame.contentDocument;
                                    }
                                }
                            }
                        }
                        return 'ok';
                        """);
                    await EvaluateActionScriptAsync(childClickScript, cancellationToken).ConfigureAwait(false);
                }
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "fill":
            {
                EnsureLoaded();
                var reference = RequiredString(request, "reference");
                var value = request.TryGetProperty("value", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "" : "";
                var script = BuildReferenceScript(reference, $$"""
                    if (element.disabled || element.getAttribute('aria-disabled') === 'true') throw new Error('Element is disabled.');
                    if (element.readOnly || element.hasAttribute('readonly')) throw new Error('Element is read-only.');
                    var val = {{JsonEncodeString(value)}};
                    var tag = (element.tagName || '').toLowerCase();
                    var type = tag === 'input' ? String(element.type || 'text').toLowerCase() : '';
                    if (tag === 'textarea' || (tag === 'input' && ['text','search','email','url','tel','password','number'].indexOf(type) >= 0)) {
                        if (typeof element.focus === 'function') element.focus();
                        element.value = val;
                        element.dispatchEvent(new Event('input', { bubbles: true }));
                        element.dispatchEvent(new Event('change', { bubbles: true }));
                        return 'ok';
                    }
                    if (element.getAttribute('contenteditable') === 'true') {
                        if (typeof element.focus === 'function') element.focus();
                        element.textContent = val;
                        element.dispatchEvent(new Event('input', { bubbles: true }));
                        element.dispatchEvent(new Event('change', { bubbles: true }));
                        return 'ok';
                    }
                    throw new Error('Element does not accept text input.');
                    """);
                await EvaluateActionScriptAsync(script, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "select":
            {
                EnsureLoaded();
                var reference = RequiredString(request, "reference");
                var value = request.TryGetProperty("value", out var vp) && vp.ValueKind == JsonValueKind.String ? vp.GetString() ?? "" : "";
                var script = BuildReferenceScript(reference, $$"""
                    if (element.disabled || element.getAttribute('aria-disabled') === 'true') throw new Error('Element is disabled.');
                    if ((element.tagName || '').toLowerCase() !== 'select') throw new Error('Element is not a select.');
                    var targetVal = {{JsonEncodeString(value)}};
                    var options = element.options || element.querySelectorAll('option');
                    var chosen = null;
                    for (var i = 0; i < options.length; i++) {
                        if (options[i].value === targetVal || String(options[i].textContent || '').trim() === targetVal) { chosen = options[i]; break; }
                    }
                    if (!chosen || chosen.disabled) throw new Error('Option unavailable.');
                    for (var j = 0; j < options.length; j++) options[j].selected = (options[j] === chosen);
                    element.value = chosen.value;
                    element.dispatchEvent(new Event('input', { bubbles: true }));
                    element.dispatchEvent(new Event('change', { bubbles: true }));
                    return 'ok';
                    """);
                await EvaluateActionScriptAsync(script, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "upload":
            {
                EnsureLoaded();
                var reference = RequiredString(request, "reference");
                var payloadJson = await BuildUploadFilesJsonAsync(request, cancellationToken).ConfigureAwait(false);
                var script = BuildReferenceScript(reference, $$"""
                    if (element.disabled || element.getAttribute('aria-disabled') === 'true') throw new Error('Element is disabled.');
                    if ((element.tagName || '').toLowerCase() !== 'input' || String(element.type || '').toLowerCase() !== 'file')
                        throw new Error('Element is not a file input.');
                    var items = {{payloadJson}};
                    element.__folioFiles = items;
                    var list = [];
                    for (var i = 0; i < items.length; i++) {
                        var item = items[i];
                        var raw = item.base64 ? atob(item.base64) : '';
                        var bytes = new Uint8Array(raw.length);
                        for (var k = 0; k < raw.length; k++) bytes[k] = raw.charCodeAt(k);
                        var blob = new Blob([bytes], { type: item.type || 'application/octet-stream' });
                        blob.name = item.name;
                        blob.lastModified = Date.now();
                        list.push(blob);
                    }
                    list.item = function(idx) { return idx >= 0 && idx < list.length ? list[idx] : null; };
                    try { Object.defineProperty(element, 'files', { configurable: true, get: function() { return list; } }); } catch (e) {}
                    try { element.value = items.length > 0 ? ('C:\\fakepath\\' + items[0].name) : ''; } catch (e) {}
                    element.dispatchEvent(new Event('input', { bubbles: true }));
                    element.dispatchEvent(new Event('change', { bubbles: true }));
                    return 'ok';
                    """);
                await EvaluateActionScriptAsync(script, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "viewport":
            {
                var width = (int)Math.Clamp(RequiredFiniteNumber(request, "width"), 1, 8192);
                var height = (int)Math.Clamp(RequiredFiniteNumber(request, "height"), 1, 8192);
                _viewportWidth = width;
                _viewportHeight = height;
                if (_client is not null)
                {
                    var expected = _lastWorkerSequence > 0 ? ContentMessageKind.Frame : ContentMessageKind.Viewport;
                    var reply = await ExchangeAsync(ContentMessageKind.Viewport, new JsonObject(), expected, cancellationToken, c => c.UpdateViewportAsync(width, height, 1, cancellationToken)).ConfigureAwait(false);
                    if (reply.Kind == ContentMessageKind.Frame)
                    {
                        var seq = GetWorkerSequence(reply.Payload);
                        if (seq > _lastWorkerSequence) _lastWorkerSequence = seq;
                    }
                }
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "wait-network-idle":
            {
                var idleMs = request.TryGetProperty("idleMs", out var idleEl) && idleEl.TryGetInt32(out var im) ? Math.Clamp(im, 50, 10000) : 300;
                var timeoutMs = request.TryGetProperty("timeoutMs", out var toEl) && toEl.TryGetInt32(out var tm) ? Math.Clamp(tm, 100, 30000) : 10000;
                await WaitForNetworkIdleAsync(idleMs, timeoutMs, cancellationToken).ConfigureAwait(false);
                await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "get-cookies":
            {
                var urlStr = request.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
                IReadOnlyList<StoredCookie> found = Uri.TryCreate(urlStr ?? _urlLabel, UriKind.Absolute, out var queryUri) &&
                    queryUri.Scheme is "https" or "http"
                    ? CookieJar.GetMatchingCookies(_storage, "", queryUri, includeHttpOnly: true)
                    : CookieJar.GetAllCookies(_storage, "");
                var items = found.Select(c => new BrokerCookieItem(c.Name, c.Value, c.Domain, c.Path, c.ExpiresUnixSeconds, c.HttpOnly, c.Secure, c.SameSite)).ToArray();
                return new BrokerCookiesResponse(IdOrNull(id, hasId), true, items);
            }
            case "set-cookies":
            {
                if (!request.TryGetProperty("cookies", out var cookiesEl) || cookiesEl.ValueKind != JsonValueKind.Array)
                    throw new BrokerRequestException("invalid_request", "cookies array is required.");
                var fallbackUrl = request.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : _urlLabel;
                Uri.TryCreate(fallbackUrl, UriKind.Absolute, out var fallbackUri);
                var parsed = CookieJar.ParseJsonArray(cookiesEl, fallbackUri);
                CookieJar.UpsertCookies(_storage, "", parsed, fallbackUri);
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "set-interception":
            {
                var parsedRules = new List<BrokerInterceptionRule>();
                if (request.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var rule in rulesEl.EnumerateArray())
                    {
                        if (rule.ValueKind != JsonValueKind.Object) continue;
                        var pattern = rule.TryGetProperty("urlPattern", out var patEl) && patEl.ValueKind == JsonValueKind.String ? patEl.GetString() ?? "*" : "*";
                        var action = rule.TryGetProperty("action", out var actEl) && actEl.ValueKind == JsonValueKind.String ? actEl.GetString() ?? "block" : "block";
                        var resourceType = rule.TryGetProperty("resourceType", out var rtEl) && rtEl.ValueKind == JsonValueKind.String ? rtEl.GetString() : null;
                        var status = rule.TryGetProperty("status", out var stEl) && stEl.TryGetInt32(out var st) ? st : 200;
                        var contentType = rule.TryGetProperty("contentType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString() ?? "text/plain" : "text/plain";
                        var bodyB64 = rule.TryGetProperty("bodyBase64", out var bbEl) && bbEl.ValueKind == JsonValueKind.String ? bbEl.GetString() : null;
                        var bodyStr = rule.TryGetProperty("body", out var bsEl) && bsEl.ValueKind == JsonValueKind.String ? bsEl.GetString() : null;
                        var bodyBytes = !string.IsNullOrEmpty(bodyB64) ? Convert.FromBase64String(bodyB64)
                            : bodyStr is not null ? Encoding.UTF8.GetBytes(bodyStr) : [];
                        parsedRules.Add(new BrokerInterceptionRule(pattern, action.ToLowerInvariant(), resourceType, status, contentType, bodyBytes));
                    }
                }
                lock (_interceptionLock)
                {
                    _interceptionRules.Clear();
                    _interceptionRules.AddRange(parsedRules);
                }
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            }
            case "close":
                _closed = true;
                return new BrokerSimpleResponse(IdOrNull(id, hasId), true);
            default:
                throw new BrokerRequestException("unsupported", $"The {operation} operation is not supported by this Folio broker.");
        }
    }

    private async Task ClickCoordinatesAsync(double x, double y, int button, int buttonsMask, int clickCount, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _pendingNavigation, null);
        await SendInputAsync(new JsonObject { ["type"] = "pointermove", ["x"] = x, ["y"] = y, ["button"] = 0, ["buttons"] = 0 }, cancellationToken).ConfigureAwait(false);
        for (var count = 1; count <= clickCount; count++)
        {
            await SendInputAsync(new JsonObject { ["type"] = "pointerdown", ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = buttonsMask, ["clickCount"] = count }, cancellationToken).ConfigureAwait(false);
            await SendInputAsync(new JsonObject { ["type"] = "pointerup", ["x"] = x, ["y"] = y, ["button"] = button, ["buttons"] = 0, ["clickCount"] = count }, cancellationToken).ConfigureAwait(false);
        }
        _pointerX = x;
        _pointerY = y;
        await FollowPendingNavigationAsync(null, false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(double X, double Y, bool InChildFrame)> LocateReferenceTargetAsync(string reference, bool requireEnabled, CancellationToken cancellationToken)
    {
        var checkEnabled = requireEnabled
            ? "if (element.disabled || element.getAttribute('aria-disabled') === 'true') throw new Error('Element is disabled.');"
            : "";
        var script = BuildReferenceScript(reference, $$"""
            {{checkEnabled}}
            if (typeof element.scrollIntoView === 'function') element.scrollIntoView();
            var r = element.getBoundingClientRect ? element.getBoundingClientRect() : null;
            if ((!r || (r.width <= 0 && r.height <= 0)) && !ownerFrame) throw new Error('The referenced element has no clickable bounds.');
            var cx = r ? (offsetX + r.left + r.width / 2) : offsetX;
            var cy = r ? (offsetY + r.top + r.height / 2) : offsetY;
            return JSON.stringify([cx, cy, ownerFrame ? 1 : 0]);
            """);
        var frame = await ExchangeAsync(ContentMessageKind.Load, new JsonObject { ["script"] = script, ["url"] = _urlLabel }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
        var sequence = GetWorkerSequence(frame.Payload);
        if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale content-process response.");
        _lastWorkerSequence = sequence;
        if (frame.Payload.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array && diags.GetArrayLength() > 0)
        {
            var err = diags[0].GetString();
            if (!string.IsNullOrWhiteSpace(err)) throw new BrokerRequestException("action_failed", err);
        }
        var raw = RequiredPayloadString(frame.Payload, "value");
        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() < 2)
            throw new BrokerRequestException("action_failed", "The referenced element has no clickable bounds.");
        var inChild = doc.RootElement.GetArrayLength() > 2 && doc.RootElement[2].GetInt32() != 0;
        return (doc.RootElement[0].GetDouble(), doc.RootElement[1].GetDouble(), inChild);
    }

    private async Task EvaluateActionScriptAsync(string script, CancellationToken cancellationToken)
    {
        var frame = await ExchangeAsync(ContentMessageKind.Load, new JsonObject { ["script"] = script, ["url"] = _urlLabel }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
        var sequence = GetWorkerSequence(frame.Payload);
        if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale content-process response.");
        _lastWorkerSequence = sequence;
        if (frame.Payload.TryGetProperty("diagnostics", out var diags) && diags.ValueKind == JsonValueKind.Array && diags.GetArrayLength() > 0)
        {
            var err = diags[0].GetString();
            if (!string.IsNullOrWhiteSpace(err)) throw new BrokerRequestException("action_failed", err);
        }
    }

    internal static string NormalizeFramesetHtml(string html)
    {
        if (!html.Contains("<frameset", StringComparison.OrdinalIgnoreCase)) return html;
        html = Regex.Replace(html, @"<frameset\b([^>]*)>", "<div data-frameset=\"true\"$1>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        html = Regex.Replace(html, @"</frameset\s*>", "</div>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        html = Regex.Replace(html, @"<frame\b([^>]*?)/?>", "<iframe$1></iframe>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return html;
    }

    private static string BuildReferenceScript(string reference, string actionBody)
    {
        var encodedRef = JsonEncodeString(reference);
        return $$"""
            (function() {
                var targetRef = {{encodedRef}};
                function findInDoc(doc, offsetX, offsetY, depth, ownerFrame) {
                    if (!doc || depth > 5) return null;
                    var all = doc.querySelectorAll('[data-folio-ref]');
                    for (var i = 0; i < all.length; i++) {
                        if (all[i].getAttribute('data-folio-ref') === targetRef)
                            return { el: all[i], doc: doc, offsetX: offsetX, offsetY: offsetY, ownerFrame: ownerFrame };
                    }
                    var frames = doc.querySelectorAll('iframe,frame');
                    for (var f = 0; f < frames.length; f++) {
                        try {
                            var childDoc = frames[f].contentDocument;
                            if (!childDoc) continue;
                            var fr = frames[f].getBoundingClientRect ? frames[f].getBoundingClientRect() : null;
                            var found = findInDoc(childDoc, fr ? offsetX + fr.left : offsetX, fr ? offsetY + fr.top : offsetY, depth + 1, frames[f]);
                            if (found) return found;
                        } catch (e) {}
                    }
                    return null;
                }
                var match = findInDoc(document, 0, 0, 0, null);
                if (!match) throw new Error('Stale element reference; capture a new snapshot.');
                var element = match.el, ownerDoc = match.doc, offsetX = match.offsetX, offsetY = match.offsetY, ownerFrame = match.ownerFrame;
                {{actionBody}}
            })()
            """;
    }

    private static string JsonEncodeString(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) writer.WriteStringValue(value);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task<string> BuildUploadFilesJsonAsync(JsonElement request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        if (request.TryGetProperty("paths", out var pathsEl) && pathsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in pathsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var filePath = item.GetString();
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    throw new BrokerRequestException("invalid_request", $"Upload file does not exist: {filePath}");
                var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
                var ext = Path.GetExtension(filePath).ToLowerInvariant();
                var mime = ext switch
                {
                    ".txt" => "text/plain",
                    ".json" => "application/json",
                    ".html" or ".htm" => "text/html",
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".pdf" => "application/pdf",
                    _ => "application/octet-stream"
                };
                writer.WriteStartObject();
                writer.WriteString("name", Path.GetFileName(filePath));
                writer.WriteString("type", mime);
                writer.WriteNumber("size", bytes.Length);
                writer.WriteString("base64", Convert.ToBase64String(bytes));
                writer.WriteEndObject();
            }
        }
        else if (request.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in filesEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String ? nEl.GetString() ?? "upload.bin" : "upload.bin";
                var mime = item.TryGetProperty("contentType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString()
                    : (item.TryGetProperty("type", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : "application/octet-stream");
                var b64 = item.TryGetProperty("base64", out var bEl) && bEl.ValueKind == JsonValueKind.String ? bEl.GetString() ?? "" : "";
                var len = string.IsNullOrEmpty(b64) ? 0 : Convert.FromBase64String(b64).Length;
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteString("type", mime ?? "application/octet-stream");
                writer.WriteNumber("size", len);
                writer.WriteString("base64", b64);
                writer.WriteEndObject();
            }
        }
        else
        {
            throw new BrokerRequestException("invalid_request", "upload requires paths or files.");
        }
        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (int Button, int ButtonsMask) ParseMouseButton(JsonElement request)
    {
        var button = 0;
        if (request.TryGetProperty("button", out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var num))
                button = Math.Clamp(num, 0, 2);
            else if (prop.ValueKind == JsonValueKind.String)
                button = (prop.GetString() ?? "left").ToLowerInvariant() switch
                {
                    "right" or "secondary" => 2,
                    "middle" or "auxiliary" => 1,
                    _ => 0
                };
        }
        var mask = button == 2 ? 2 : button == 1 ? 4 : 1;
        return (button, mask);
    }

    private static int ParseClickCount(JsonElement request)
        => request.TryGetProperty("clickCount", out var prop) && prop.TryGetInt32(out var count) ? Math.Clamp(count, 1, 3) : 1;

    private static JsonObject CreateCapabilitiesObject() => new()
    {
        ["iframes"] = true,
        ["webGl"] = true,
        ["workers"] = true,
        ["webAssembly"] = true
    };

    private async Task SendInputAsync(JsonObject input, CancellationToken cancellationToken)
    {
        EnsureLoaded();
        var frame = await ExchangeAsync(ContentMessageKind.Input, input, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
        var sequence = GetWorkerSequence(frame.Payload);
        if (sequence <= _lastWorkerSequence) throw new BrokerRequestException("engine_error", "Folio returned a stale input response.");
        _lastWorkerSequence = sequence;
    }

    private static double RequiredFiniteNumber(JsonElement request, string name)
    {
        if (request.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out var number) && double.IsFinite(number) && double.Abs(number) <= float.MaxValue)
            return number;
        throw new BrokerRequestException("invalid_request", $"{name} must be a finite viewport coordinate or wheel delta.");
    }

    private async Task EnsureClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null) return;
        if (!OperatingSystem.IsWindows()) throw new BrokerRequestException("sandbox_unavailable", "Folio's AppContainer content sandbox is only available on Windows.");
        if (string.Equals(Environment.GetEnvironmentVariable("FOLIO_UNSANDBOXED"), "1", StringComparison.Ordinal))
            throw new BrokerRequestException("sandbox_bypassed", "FOLIO_UNSANDBOXED disables the required content-process sandbox.");

        var processPath = Environment.ProcessPath;
        var appHostPath = Path.Combine(AppContext.BaseDirectory, "Folio.ContentHost.exe");
        var hostPath = !string.IsNullOrWhiteSpace(processPath) &&
            Path.GetFileName(processPath).Equals("Folio.ContentHost.exe", StringComparison.OrdinalIgnoreCase)
            ? processPath : appHostPath;
        if (!File.Exists(hostPath))
        {
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, "Folio.ContentHost.dll");
            if (!File.Exists(assemblyPath)) throw new BrokerRequestException("engine_error", "Unable to locate the Folio content-host build.");
            hostPath = assemblyPath;
        }
        if (_allowedOrigins.Count > 0 || _allowPrivateNetwork)
        {
            var innerLoader = ResourceLoaders.Allowlist(new AllowlistOptions
            {
                Origins = _allowedOrigins,
                AllowLoopbackHttp = _allowPrivateNetwork,
                AllowUnversioned = true,
                Timeout = RequestTimeout
            });
            _resourceLoader = new BrokerResourceLoader(this, innerLoader);
            _resourcePolicy = new ContentResourcePolicy
            {
                AllowedOrigins = _allowedOrigins
                    .Select(origin => origin.Origin.Equals("https://*", StringComparison.OrdinalIgnoreCase)
                        ? "https://*"
                        : new Uri(origin.Origin).GetLeftPart(UriPartial.Authority))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase),
                AllowLoopbackHttp = _allowPrivateNetwork
            };
        }
        _httpClient ??= new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        })
        {
            Timeout = RequestTimeout
        };
        _client = ContentProcessClient.Start(hostPath, limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(20)),
            resourceLoader: _resourceLoader, resourcePolicy: _resourcePolicy, artifactStorage: _storage);
        _client.MessageReceived += OnContentMessageAsync;
        _client.ScriptStopped += OnScriptStopped;
        if (_viewportWidth != 1280 || _viewportHeight != 720)
        {
            await ExchangeAsync(ContentMessageKind.Viewport, new JsonObject(), ContentMessageKind.Viewport, cancellationToken,
                c => c.UpdateViewportAsync(_viewportWidth, _viewportHeight, 1, cancellationToken)).ConfigureAwait(false);
        }
    }

    private Task<object> NavigateAsync(JsonElement id, bool hasId, string url, CancellationToken cancellationToken)
        => NavigateHttpAsync(id, hasId, url, "GET", null, null, cancellationToken);

    private async Task<object?> FollowPendingNavigationAsync(JsonElement? id, bool hasId, CancellationToken cancellationToken)
    {
        object? lastResponse = null;
        for (var hop = 0; hop < MaxChainedNavigations; hop++)
        {
            var nav = Interlocked.Exchange(ref _pendingNavigation, null);
            if (nav is null) break;
            lastResponse = await NavigateHttpAsync(id ?? default, id.HasValue && hasId, nav.Url, nav.Method, nav.ContentType, nav.BodyBytes, cancellationToken).ConfigureAwait(false);
        }
        return lastResponse;
    }

    private async Task<object> NavigateHttpAsync(JsonElement id, bool hasId, string url, string method, string? contentType, byte[]? bodyBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestedUrl) || !IsAllowed(requestedUrl, ResourceKind.Document))
            throw new BrokerRequestException("policy_refused", "Navigation requires an HTTPS URL covered by a configured allow-origin policy.");
        if (_allowedOrigins.Count == 0 && !_allowPrivateNetwork)
            throw new BrokerRequestException("policy_refused", "Navigation is disabled because no HTTPS origins were configured for this broker.");
        await EnsureClientAsync(cancellationToken).ConfigureAwait(false);
        var client = _httpClient ?? throw new BrokerRequestException("navigation_failed", "The configured HTTP client is unavailable.");

        var currentUri = requestedUrl;
        var currentMethod = string.IsNullOrWhiteSpace(method) ? HttpMethod.Get : new HttpMethod(method.ToUpperInvariant());
        var currentBody = bodyBytes;
        var currentContentType = contentType;
        byte[]? responseBytes = null;
        string? responseMediaType = null;

        TrackRequestStarted();
        try
        {
            for (var redirects = 0; redirects <= MaxRedirects; redirects++)
            {
                if (TryMatchInterception(currentUri.AbsoluteUri, "document", out var rule))
                {
                    if (rule.Action is "block" or "abort")
                    {
                        EmitNetworkEvent("failed", currentUri.AbsoluteUri, currentMethod.Method, 0, "Blocked by request interception.");
                        throw new BrokerRequestException("navigation_failed", "Blocked by request interception.");
                    }
                    if (rule.Action == "fulfill")
                    {
                        EmitNetworkEvent("response", currentUri.AbsoluteUri, currentMethod.Method, rule.Status, null);
                        responseBytes = rule.BodyBytes;
                        responseMediaType = rule.ContentType;
                        break;
                    }
                }

                EmitNetworkEvent("request", currentUri.AbsoluteUri, currentMethod.Method, 0, null);
                using var req = new HttpRequestMessage(currentMethod, currentUri);
                req.Headers.UserAgent.ParseAdd("Folio/1.0");
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                var cookieHeader = CookieJar.BuildCookieHeader(_storage, "", currentUri);
                if (!string.IsNullOrEmpty(cookieHeader))
                    req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                if (currentBody is not null && currentMethod == HttpMethod.Post)
                {
                    var content = new ByteArrayContent(currentBody);
                    if (!string.IsNullOrWhiteSpace(currentContentType))
                        content.Headers.TryAddWithoutValidation("Content-Type", currentContentType);
                    req.Content = content;
                }

                HttpResponseMessage resp;
                try
                {
                    resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    EmitNetworkEvent("failed", currentUri.AbsoluteUri, currentMethod.Method, 0, "Timed out.");
                    throw new BrokerRequestException("timeout", "The top-level document load timed out.");
                }
                catch (HttpRequestException ex)
                {
                    EmitNetworkEvent("failed", currentUri.AbsoluteUri, currentMethod.Method, 0, ex.Message);
                    throw new BrokerRequestException("navigation_failed", ex.Message);
                }

                using (resp)
                {
                    if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                    {
                        foreach (var sc in setCookies)
                            CookieJar.SetFromHeader(_storage, "", currentUri, sc, fromHttp: true);
                    }

                    var statusCode = (int)resp.StatusCode;
                    EmitNetworkEvent("response", currentUri.AbsoluteUri, currentMethod.Method, statusCode, null);

                    if (statusCode is >= 300 and < 400 && resp.Headers.Location is { } location)
                    {
                        if (redirects == MaxRedirects)
                            throw new BrokerRequestException("navigation_failed", "Too many redirects.");
                        var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        if (!IsAllowed(nextUri, ResourceKind.Document))
                            throw new BrokerRequestException("policy_refused", "Redirect target is outside the configured origin policy.");
                        if (statusCode == 303 || (statusCode is 301 or 302 && currentMethod == HttpMethod.Post))
                        {
                            currentMethod = HttpMethod.Get;
                            currentBody = null;
                            currentContentType = null;
                        }
                        currentUri = nextUri;
                        continue;
                    }

                    var disp = resp.Content.Headers.ContentDisposition;
                    if (disp?.DispositionType?.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var rawName = disp.FileNameStar ?? disp.FileName ?? Path.GetFileName(currentUri.LocalPath);
                        var safeName = Path.GetFileName(rawName?.Trim('"', ' ') ?? "");
                        if (string.IsNullOrWhiteSpace(safeName)) safeName = "download.bin";
                        var downloadsDir = Environment.GetEnvironmentVariable("FOLIO_DOWNLOADS_DIR");
                        if (string.IsNullOrWhiteSpace(downloadsDir))
                            downloadsDir = Path.Combine(Path.GetTempPath(), "folio-downloads");
                        Directory.CreateDirectory(downloadsDir);
                        var targetPath = Path.Combine(downloadsDir, safeName);
                        var dlBytes = await ReadBoundedBytesAsync(resp.Content, MaxDownloadBytes, cancellationToken).ConfigureAwait(false);
                        await File.WriteAllBytesAsync(targetPath, dlBytes, cancellationToken).ConfigureAwait(false);
                        EmitDownloadEvent(currentUri.AbsoluteUri, safeName, targetPath);
                        return new BrokerFrameResponse(IdOrNull(id, hasId), true, _urlLabel, null, 0, 0, [], _lastWorkerSequence, _lastWorkerSequence > 0, null);
                    }

                    responseMediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                    if (!resp.IsSuccessStatusCode && responseMediaType is not ("text/html" or "application/xhtml+xml"))
                        throw new BrokerRequestException("navigation_failed", $"The server answered {statusCode}.");
                    if (responseMediaType is not (null or "text/html" or "application/xhtml+xml" or "text/plain" or "application/xml" or "text/xml"))
                        throw new BrokerRequestException("navigation_failed", $"Unsupported document content type: {responseMediaType}.");

                    responseBytes = await ReadBoundedBytesAsync(resp.Content, MaxTopLevelBytes, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }
        }
        finally
        {
            TrackRequestFinished();
        }

        if (responseBytes is null)
            throw new BrokerRequestException("navigation_failed", "The navigation produced no response.");
        if (!IsAllowed(currentUri, ResourceKind.Document))
            throw new BrokerRequestException("policy_refused", "The final document URL is outside the configured origin policy.");

        Volatile.Write(ref _pendingNavigation, null);
        _urlLabel = currentUri.AbsoluteUri;
        _lastWorkerSequence = 0;
        _referenceCounter = 0;
        var html = NormalizeFramesetHtml(Encoding.UTF8.GetString(responseBytes));
        var initializeReferences = $"window.__folioRefPrefix='{_referencePrefix}';window.__folioRefCounter=0";
        var frame = await ExchangeAsync(ContentMessageKind.Load, new JsonObject
        {
            ["html"] = html,
            ["script"] = initializeReferences,
            ["url"] = _urlLabel,
            ["capabilities"] = CreateCapabilitiesObject()
        }, ContentMessageKind.Frame, cancellationToken).ConfigureAwait(false);
        if (FindPolicyRefusal(frame.Payload) is { } refusal)
            throw new BrokerRequestException("policy_refused", refusal);
        return await FrameResponseAsync(id, hasId, frame, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
            throw new BrokerRequestException("navigation_failed", $"Response exceeds {limit} bytes.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > limit)
                throw new BrokerRequestException("navigation_failed", $"Response exceeds {limit} bytes.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private bool IsAllowed(Uri url, ResourceKind kind)
    {
        if (url.UserInfo.Length > 0) return false;
        if (_allowPrivateNetwork && url.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(url.Host))
            return true;
        if (url.Scheme != Uri.UriSchemeHttps) return false;
        return _allowedOrigins.Any(allowed => allowed.Kinds.Contains(kind) &&
            (allowed.Origin.Equals("https://*", StringComparison.OrdinalIgnoreCase) ||
             (Uri.TryCreate(allowed.Origin, UriKind.Absolute, out var origin) && origin.Scheme == Uri.UriSchemeHttps &&
              string.Equals(origin.Host, url.Host, StringComparison.OrdinalIgnoreCase) && origin.Port == url.Port &&
              IsUnderPrefix(url.AbsolutePath, origin.AbsolutePath))));
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("::1", StringComparison.OrdinalIgnoreCase) ||
           host.Equals("[::1]", StringComparison.OrdinalIgnoreCase);

    private static AllowedOrigin ParseAllowedOrigin(string value, bool allowPrivateNetwork)
    {
        if (value.Equals("https://*", StringComparison.OrdinalIgnoreCase))
            return new AllowedOrigin("https://*", ResourceKind.Document, ResourceKind.Stylesheet, ResourceKind.Script,
                ResourceKind.Image, ResourceKind.SvgImage, ResourceKind.Font, ResourceKind.Media, ResourceKind.Fetch);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("allow-origin values must be HTTPS origins or path prefixes without credentials, query, or fragment.", nameof(value));
        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isAllowedLoopback = allowPrivateNetwork && uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host);
        if (!isHttps && !isAllowedLoopback)
            throw new ArgumentException("allow-origin values must be HTTPS origins or path prefixes without credentials, query, or fragment.", nameof(value));
        var normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        if (string.IsNullOrEmpty(normalized)) normalized = uri.GetLeftPart(UriPartial.Authority);
        return new AllowedOrigin(normalized, ResourceKind.Document, ResourceKind.Stylesheet, ResourceKind.Script,
            ResourceKind.Image, ResourceKind.SvgImage, ResourceKind.Font, ResourceKind.Media, ResourceKind.Fetch);
    }

    internal void TrackRequestStarted() => Interlocked.Increment(ref _inFlightRequests);

    internal void TrackRequestFinished()
    {
        if (Interlocked.Decrement(ref _inFlightRequests) <= 0)
        {
            lock (_idleLock)
            {
                foreach (var waiter in _idleWaiters) waiter.TrySetResult();
                _idleWaiters.Clear();
            }
        }
    }

    private async Task WaitForNetworkIdleAsync(int idleMs, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMs * (Stopwatch.Frequency / 1000.0));
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _inFlightRequests) <= 0)
            {
                await Task.Delay(idleMs, cancellationToken).ConfigureAwait(false);
                if (Volatile.Read(ref _inFlightRequests) <= 0) return;
                continue;
            }
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_idleLock)
            {
                if (Volatile.Read(ref _inFlightRequests) <= 0) tcs.TrySetResult();
                else _idleWaiters.Add(tcs);
            }
            var remainingMs = Math.Max(1, (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency));
            try { await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(remainingMs), cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { break; }
        }
    }

    internal bool TryMatchInterception(string url, string resourceType, out BrokerInterceptionRule rule)
    {
        lock (_interceptionLock)
        {
            for (var i = _interceptionRules.Count - 1; i >= 0; i--)
            {
                var candidate = _interceptionRules[i];
                if (!string.IsNullOrWhiteSpace(candidate.ResourceType) &&
                    !candidate.ResourceType.Equals(resourceType, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (MatchesWildcard(url, candidate.UrlPattern))
                {
                    rule = candidate;
                    return true;
                }
            }
        }
        rule = null!;
        return false;
    }

    private static bool MatchesWildcard(string input, string pattern)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*") return true;
        if (!pattern.Contains('*'))
            return input.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsUnderPrefix(string path, string prefix)
        => path.StartsWith(prefix, StringComparison.Ordinal) && (prefix.EndsWith('/') || path.Length == prefix.Length || path[prefix.Length] == '/');

    private static bool IsPolicyRefusal(string error)
        => error.Contains("not allowed", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("Blocked by content resource policy", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("Only HTTPS", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("credentials", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("pinned hash", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("integrity", StringComparison.OrdinalIgnoreCase) ||
           error.Contains("version", StringComparison.OrdinalIgnoreCase);

    private static string? FindPolicyRefusal(JsonElement payload)
    {
        if (!payload.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var item in diagnostics.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var message = item.GetString();
            if (!string.IsNullOrWhiteSpace(message) && IsPolicyRefusal(message)) return message;
        }
        return null;
    }

    private void EnsureLoaded()
    {
        if (_client is null) throw new BrokerRequestException("invalid_state", "Load a document before requesting page data.");
        if (_lastWorkerSequence == 0) throw new BrokerRequestException("invalid_state", "Load a document before requesting page data.");
    }

    private async Task<ContentMessage> ExchangeAsync(ContentMessageKind requestKind, object payload, ContentMessageKind expectedKind, CancellationToken cancellationToken, Func<ContentProcessClient, ValueTask>? send = null)
    {
        var client = _client ?? throw new BrokerRequestException("invalid_state", "The content process is not running.");
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingReply = completion;
            if (send is null) await client.SendAsync(requestKind, payload, cancellationToken).ConfigureAwait(false);
            else await send(client).ConfigureAwait(false);
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
        if (message.Kind == ContentMessageKind.Diagnostic)
        {
            var code = message.Payload.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String
                ? codeEl.GetString() : null;
            if (code is "Navigation")
            {
                var url = message.Payload.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
                var method = message.Payload.TryGetProperty("method", out var methEl) && methEl.ValueKind == JsonValueKind.String ? methEl.GetString() ?? "GET" : "GET";
                var contentType = message.Payload.TryGetProperty("contentType", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString() : null;
                var bodyBase64 = message.Payload.TryGetProperty("bodyBase64", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.String ? bodyEl.GetString() : null;
                byte[]? bodyBytes = string.IsNullOrEmpty(bodyBase64) ? null : Convert.FromBase64String(bodyBase64);
                if (!string.IsNullOrWhiteSpace(url))
                    Volatile.Write(ref _pendingNavigation, new PendingNavigation(url, method, contentType, bodyBytes));
                return ValueTask.CompletedTask;
            }
            if (code is "LocationChanged")
            {
                var nextUrl = message.Payload.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
                if (!string.IsNullOrWhiteSpace(nextUrl))
                    _urlLabel = nextUrl;
                return ValueTask.CompletedTask;
            }
            if (code is "Console")
            {
                var level = message.Payload.TryGetProperty("level", out var lvlEl) && lvlEl.ValueKind == JsonValueKind.String ? lvlEl.GetString() ?? "log" : "log";
                var text = message.Payload.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String ? msgEl.GetString() ?? "" : "";
                EmitConsoleEvent(level, text);
                return ValueTask.CompletedTask;
            }
            if (code is "Dialog")
            {
                var kind = message.Payload.TryGetProperty("kind", out var kEl) && kEl.ValueKind == JsonValueKind.String ? kEl.GetString() ?? "alert" : "alert";
                var text = message.Payload.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String ? msgEl.GetString() ?? "" : "";
                EmitConsoleEvent("warning", $"[{kind}] {text}");
                return ValueTask.CompletedTask;
            }
        }

        if (message.Kind == ContentMessageKind.Frame)
        {
            var seq = GetWorkerSequence(message.Payload);
            TryEnqueueScreencastFrame(message.Payload);
            if (seq == 0) return ValueTask.CompletedTask;
        }
        if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Diagnostic or ContentMessageKind.Hello or ContentMessageKind.Viewport)
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
        var canvasFrame = CaptureCanvasFrame(frame.Payload)?.Frame;
        return Task.FromResult<object>(new BrokerFrameResponse(IdOrNull(id, hasId), true, _urlLabel, value,
            displayItems, canvasCommands, diagnostics, sequence, displayItems > 0, canvasFrame));
    }

    private CapturedCanvasFrame? CaptureCanvasFrame(JsonElement payload)
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
        if (describedSequence <= 0) return new CapturedCanvasFrame(new BrokerCanvasFrame(describedSequence, width, height, stride, null), null);

        using var reader = new CanvasFrameStore(path!, width, height, stride);
        var pixels = reader.ReadLatest(out var actualSequence);
        // The content worker can publish another frame between acknowledging a request and our read.
        if (actualSequence < describedSequence) throw new BrokerRequestException("engine_error", "Folio's rendered-frame sequence did not match its descriptor.");
        var inside = ReadPixel(pixels, stride, width, height, 16, 16);
        var outside = ReadPixel(pixels, stride, width, height, 100, 100);
        var background = ReadPixel(pixels, stride, width, height, width - 1, height - 1);
        var changedPixels = CountPixelsDifferentFrom(pixels, stride, width, height, background);
        return new CapturedCanvasFrame(new BrokerCanvasFrame(actualSequence, width, height, stride,
            new BrokerPixelProbe(inside, outside, changedPixels)), pixels);
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
        => hasId ? id : NullId;

    private static object ErrorResponse(JsonElement id, bool hasId, string code, string message)
        => new BrokerErrorResponse(IdOrNull(id, hasId), false, new BrokerError(code, message));

    private async Task WriteAsync(object response, CancellationToken cancellationToken)
    {
        var json = response switch
        {
            BrokerHelloResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerHelloResponse),
            BrokerFrameResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerFrameResponse),
            BrokerSnapshotResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerSnapshotResponse),
            BrokerScreenshotResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerScreenshotResponse),
            BrokerCookiesResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerCookiesResponse),
            BrokerSimpleResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerSimpleResponse),
            BrokerErrorResponse value => JsonSerializer.Serialize(value, HeadlessContentHostBrokerJsonContext.Default.BrokerErrorResponse),
            _ => throw new InvalidOperationException("Unknown broker response type.")
        };
        await WriteJsonLineAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteJsonLineAsync(string json, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Console.Out.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await Console.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PumpEventsAsync(ChannelReader<Func<CancellationToken, Task>> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var work in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try { await work(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void EnqueueEvent(Func<CancellationToken, Task> work)
    {
        if (!_streamEvents || _closed) return;
        _eventChannel?.Writer.TryWrite(work);
    }

    internal void EmitConsoleEvent(string level, string text)
    {
        EnqueueEvent(ct =>
        {
            var json = JsonSerializer.Serialize(new BrokerConsoleEvent("Runtime.consoleAPICalled", level, text), HeadlessContentHostBrokerJsonContext.Default.BrokerConsoleEvent);
            return WriteJsonLineAsync(json, ct);
        });
    }

    internal void EmitNetworkEvent(string stage, string url, string method, int status, string? errorText)
    {
        var eventName = stage switch
        {
            "request" => "Network.requestWillBeSent",
            "response" => "Network.responseReceived",
            _ => "Network.loadingFailed"
        };
        EnqueueEvent(ct =>
        {
            var json = JsonSerializer.Serialize(new BrokerNetworkEvent(eventName, url, method, status > 0 ? status : null, errorText), HeadlessContentHostBrokerJsonContext.Default.BrokerNetworkEvent);
            return WriteJsonLineAsync(json, ct);
        });
    }

    internal void EmitDownloadEvent(string url, string suggestedFilename, string path)
    {
        EnqueueEvent(ct =>
        {
            var json = JsonSerializer.Serialize(new BrokerDownloadEvent("Page.downloadWillBegin", url, suggestedFilename, path, "completed"), HeadlessContentHostBrokerJsonContext.Default.BrokerDownloadEvent);
            return WriteJsonLineAsync(json, ct);
        });
    }

    private void TryEnqueueScreencastFrame(JsonElement payload)
    {
        if (!_streamEvents || _closed || _eventChannel is null) return;
        try
        {
            var captured = CaptureCanvasFrame(payload);
            if (captured?.Pixels is null) return;
            var seq = captured.Frame.Sequence;
            while (true)
            {
                var prev = Interlocked.Read(ref _lastBroadcastCanvasSequence);
                if (seq <= prev) return;
                if (Interlocked.CompareExchange(ref _lastBroadcastCanvasSequence, seq, prev) == prev) break;
            }
            var pixels = captured.Pixels;
            var width = captured.Frame.Width;
            var height = captured.Frame.Height;
            var stride = captured.Frame.Stride;
            EnqueueEvent(ct =>
            {
                var png = BrokerPngEncoder.EncodeRgba8(pixels, width, height, stride);
                var evt = new BrokerScreencastEvent("Page.screencastFrame", Convert.ToBase64String(png),
                    new BrokerScreencastMetadata(width, height, seq));
                var json = JsonSerializer.Serialize(evt, HeadlessContentHostBrokerJsonContext.Default.BrokerScreencastEvent);
                return WriteJsonLineAsync(json, ct);
            });
        }
        catch { }
    }

    private sealed class BrokerRequestException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed record CapturedCanvasFrame(BrokerCanvasFrame Frame, byte[]? Pixels);
}

internal sealed record PendingNavigation(string Url, string Method, string? ContentType, byte[]? BodyBytes);
internal sealed record BrokerInterceptionRule(string UrlPattern, string Action, string? ResourceType, int Status, string ContentType, byte[] BodyBytes);
internal sealed record SnapshotBounds(double X, double Y, double Width, double Height);
internal sealed record SnapshotResponse(
    string Url, string Title, IReadOnlyList<SnapshotElement> Elements, bool Truncated, string Text, bool TextTruncated);
internal sealed record SnapshotElement(
    string Reference, string Tag, string Role, string Name, string? Href, string? Type, bool Disabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Value = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Checked = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Selected = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Sensitive = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SnapshotBounds? Bounds = null);
internal sealed record BrokerEngine(string Product, int Protocol);
internal sealed record BrokerHelloResponse(JsonElement Id, bool Ok, string ApiVersion, BrokerEngine Engine, bool Sandboxed, bool NetworkEnabled);
internal sealed record BrokerFrameResponse(JsonElement Id, bool Ok, string Url, string? Value, int DisplayItems, int CanvasCommands,
    IReadOnlyList<string> Diagnostics, long Sequence, bool Rendered, BrokerCanvasFrame? CanvasFrame);
internal sealed record BrokerCanvasFrame(long Sequence, int Width, int Height, int Stride, BrokerPixelProbe? PixelProbe);
internal sealed record BrokerPixelProbe(int[] Inside, int[] Outside, int ChangedPixels);
internal sealed record BrokerSnapshotResponse(JsonElement Id, bool Ok, SnapshotResponse Snapshot, long Sequence);
internal sealed record BrokerScreenshotResponse(JsonElement Id, bool Ok, string MimeType, int Width, int Height, long Sequence, string Data);
internal sealed record BrokerCookieItem(string Name, string Value, string Domain, string Path, long? Expires, bool HttpOnly, bool Secure, string? SameSite);
internal sealed record BrokerCookiesResponse(JsonElement Id, bool Ok, IReadOnlyList<BrokerCookieItem> Cookies);
internal sealed record BrokerSimpleResponse(JsonElement Id, bool Ok);
internal sealed record BrokerError(string Code, string Message);
internal sealed record BrokerErrorResponse(JsonElement Id, bool Ok, BrokerError Error);
internal sealed record BrokerConsoleEvent(string Event, string Type, string Text);
internal sealed record BrokerNetworkEvent(string Event, string Url, string Method,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ErrorText);
internal sealed record BrokerDownloadEvent(string Event, string Url, string SuggestedFilename, string Path, string State);
internal sealed record BrokerScreencastMetadata(int Width, int Height, long Sequence);
internal sealed record BrokerScreencastEvent(string Event, string Data, BrokerScreencastMetadata Metadata);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SnapshotResponse))]
[JsonSerializable(typeof(SnapshotBounds))]
[JsonSerializable(typeof(BrokerHelloResponse))]
[JsonSerializable(typeof(BrokerFrameResponse))]
[JsonSerializable(typeof(BrokerSnapshotResponse))]
[JsonSerializable(typeof(BrokerScreenshotResponse))]
[JsonSerializable(typeof(BrokerCookiesResponse))]
[JsonSerializable(typeof(BrokerSimpleResponse))]
[JsonSerializable(typeof(BrokerErrorResponse))]
[JsonSerializable(typeof(BrokerConsoleEvent))]
[JsonSerializable(typeof(BrokerNetworkEvent))]
[JsonSerializable(typeof(BrokerDownloadEvent))]
[JsonSerializable(typeof(BrokerScreencastEvent))]
internal partial class HeadlessContentHostBrokerJsonContext : JsonSerializerContext;

internal static class BrokerPngEncoder
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static byte[] EncodeRgba8(byte[] rgba, int width, int height, int stride)
    {
        if (!CanvasFrameStore.IsValidDescriptor(width, height, stride) || rgba.Length != checked(stride * height))
            throw new InvalidDataException("Invalid RGBA frame for PNG encoding.");
        using var output = new MemoryStream();
        output.Write(Signature);
        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var rowBytes = checked(width * 4);
            var row = new byte[rowBytes];
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                var sourceOffset = checked(y * stride);
                for (var x = 0; x < width; x++)
                {
                    var source = sourceOffset + x * 4;
                    var destination = x * 4;
                    var alpha = rgba[source + 3];
                    row[destination] = Unpremultiply(rgba[source + 2], alpha);
                    row[destination + 1] = Unpremultiply(rgba[source + 1], alpha);
                    row[destination + 2] = Unpremultiply(rgba[source], alpha);
                    row[destination + 3] = alpha;
                }
                zlib.Write(row);
            }
        }
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static byte Unpremultiply(byte value, byte alpha)
        => alpha == 0 ? (byte)0 : (byte)Math.Min(255, (value * 255 + alpha / 2) / alpha);

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = 0xFFFFFFFFu;
        foreach (var value in typeBytes.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, ~crc);
        output.Write(checksum);
    }
}

internal sealed class BrokerResourceLoader(HeadlessContentBroker broker, IResourceLoader inner) : IResourceLoader
{
    public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken = default)
    {
        var kindName = request.Kind.ToString().ToLowerInvariant();
        if (broker.TryMatchInterception(request.Url, kindName, out var rule))
        {
            if (rule.Action is "block" or "abort")
            {
                broker.EmitNetworkEvent("failed", request.Url, "GET", 0, "Blocked by request interception.");
                return ResourceResponse.Refused("Blocked by request interception.");
            }
            if (rule.Action == "fulfill")
            {
                broker.EmitNetworkEvent("response", request.Url, "GET", rule.Status, null);
                return new ResourceResponse(rule.BodyBytes, rule.ContentType, null) { FinalUrl = request.Url, StatusCode = rule.Status };
            }
        }

        broker.TrackRequestStarted();
        broker.EmitNetworkEvent("request", request.Url, "GET", 0, null);
        try
        {
            var response = await inner.LoadAsync(request, cancellationToken).ConfigureAwait(false);
            if (response is { Succeeded: true })
            {
                broker.EmitNetworkEvent("response", request.Url, "GET", response.StatusCode ?? 200, null);
                if (request.Kind == ResourceKind.Document && response.Data is { Length: > 0 } docBytes &&
                    (response.ContentType is null || response.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase)))
                {
                    var text = Encoding.UTF8.GetString(docBytes);
                    var normalized = HeadlessContentBroker.NormalizeFramesetHtml(text);
                    if (!ReferenceEquals(text, normalized))
                        response = new ResourceResponse(Encoding.UTF8.GetBytes(normalized), response.ContentType, null) { FinalUrl = response.FinalUrl, StatusCode = response.StatusCode };
                }
            }
            else
                broker.EmitNetworkEvent("failed", request.Url, "GET", response?.StatusCode ?? 0, response?.Error ?? "Failed");
            return response;
        }
        finally
        {
            broker.TrackRequestFinished();
        }
    }
}

internal sealed class BrokerMemoryStorage : IArtifactStorage
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly string? _localPath;
    private readonly string? _cookiesPath;
    private readonly Mutex? _mutex;
    private readonly object _lock = new();

    public BrokerMemoryStorage(string? filePath = null)
    {
        _localPath = string.IsNullOrWhiteSpace(filePath) ? null : filePath;
        if (_localPath is not null)
        {
            var dir = Path.GetDirectoryName(_localPath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(_localPath);
            _cookiesPath = Path.Combine(dir, stem + "-cookies.json");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_localPath))))[..24];
            if (OperatingSystem.IsWindows())
                _mutex = new Mutex(false, "Local\\FolioBrokerStorage_" + hash);
            LoadStore(_localPath, IsLocalKey);
            LoadStore(_cookiesPath, IsCookieKey);
        }
    }

    private static bool IsLocalKey(string key) => key.Contains("/local/", StringComparison.Ordinal);

    private static bool IsCookieKey(string key) => key.StartsWith("folio/cookies/", StringComparison.Ordinal) || key.Contains("/cookies/", StringComparison.Ordinal);

    private string? PathForKey(string key) => IsLocalKey(key) ? _localPath : (IsCookieKey(key) ? _cookiesPath : null);

    private Func<string, bool>? PredicateForKey(string key) => IsLocalKey(key) ? IsLocalKey : (IsCookieKey(key) ? IsCookieKey : null);

    public string? Read(string key)
    {
        var path = PathForKey(key);
        var predicate = PredicateForKey(key);
        if (path is not null && predicate is not null)
        {
            LoadStore(path, predicate);
        }
        return _values.TryGetValue(key, out var value) ? value : null;
    }

    public void Write(string key, string value)
    {
        _values[key] = value;
        MutateDisk(key, value);
    }

    public void Delete(string key)
    {
        _values.TryRemove(key, out _);
        MutateDisk(key, null);
    }

    public IReadOnlyList<string> Keys(string prefix)
    {
        if (_localPath is not null && (prefix.Length == 0 || IsLocalKey(prefix) || prefix.Contains("local", StringComparison.Ordinal)))
            LoadStore(_localPath, IsLocalKey);
        if (_cookiesPath is not null && (prefix.Length == 0 || IsCookieKey(prefix) || prefix.Contains("cookies", StringComparison.Ordinal)))
            LoadStore(_cookiesPath, IsCookieKey);
        return _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
    }

    private void WithCrossProcessLock(Action action)
    {
        lock (_lock)
        {
            var acquired = false;
            try
            {
                if (_mutex is not null)
                {
                    try { acquired = _mutex.WaitOne(2000); }
                    catch (AbandonedMutexException) { acquired = true; }
                }
                action();
            }
            finally
            {
                if (acquired)
                {
                    try { _mutex?.ReleaseMutex(); } catch { }
                }
            }
        }
    }

    private void LoadStore(string? path, Func<string, bool> predicate)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        WithCrossProcessLock(() =>
        {
            if (!File.Exists(path)) return;
            try
            {
                var disk = ReadDictionaryFromFile(path, predicate);
                foreach (var existing in _values.Keys.Where(predicate).ToArray())
                {
                    if (!disk.ContainsKey(existing)) _values.TryRemove(existing, out _);
                }
                foreach (var pair in disk)
                {
                    _values[pair.Key] = pair.Value;
                }
            }
            catch { }
        });
    }

    private void MutateDisk(string key, string? newValue)
    {
        var path = PathForKey(key);
        var predicate = PredicateForKey(key);
        if (string.IsNullOrWhiteSpace(path) || predicate is null) return;
        WithCrossProcessLock(() =>
        {
            try
            {
                var disk = File.Exists(path)
                    ? ReadDictionaryFromFile(path, predicate)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                if (newValue is null) disk.Remove(key);
                else disk[key] = newValue;

                foreach (var existing in _values.Keys.Where(predicate).ToArray())
                {
                    if (!disk.ContainsKey(existing)) _values.TryRemove(existing, out _);
                }
                foreach (var pair in disk) _values[pair.Key] = pair.Value;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    foreach (var pair in disk) writer.WriteString(pair.Key, pair.Value);
                    writer.WriteEndObject();
                }
                var tmpPath = $"{path}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
                File.WriteAllBytes(tmpPath, stream.ToArray());
                File.Move(tmpPath, path, overwrite: true);
            }
            catch { }
        });
    }

    private static Dictionary<string, string> ReadDictionaryFromFile(string path, Func<string, bool> predicate)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String && predicate(property.Name))
                result[property.Name] = property.Value.GetString()!;
        }
        return result;
    }
}

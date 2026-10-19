using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Folio.Scripting.Ipc;

public sealed partial class ContentHostSession
{
    internal const int StorageChunkBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _storageRequests = new();
    private readonly SemaphoreSlim _storageCallGate = new(1, 1);
    private ContentCacheBroker? _cacheBroker;
    private int _storageLimit = 65 * 1024 * 1024;
    private MemoryStream? _upload;
    private string? _uploadToken;
    private string? _uploadHash;
    private int _uploadLength;
    private byte[]? _download;
    private string? _downloadToken;
    private long _stageStarted;
    internal bool IsConnectionClosed => _connectionClosed;
    internal void SetStorageDocumentUri(Uri? uri) => _cacheBroker?.SetDocumentUri(uri);

    internal void ConfigureStorage(IArtifactStorage storage, string artifact, long quota)
    {
        _cacheBroker = new(storage, artifact, quota);
        SetStorageLimit(quota);
    }
    internal void SetStorageLimit(long quota) => _storageLimit = (int)Math.Clamp(quota, 0, int.MaxValue - 1024 * 1024) + 1024 * 1024;

    private async ValueTask DispatchStorageAsync(ContentMessage message, CancellationToken cancellationToken)
    {
        if (_cacheBroker is null) throw new InvalidDataException("Storage was not granted to this content process.");
        // A host provider is synchronous and may block. Stop the requesting
        // context on an uncertain outcome; never report a recoverable failure
        // while the provider might still commit the requested transaction.
        var operation = Task.Run(() => StorageEnvelope(message.Payload), CancellationToken.None);
        object reply;
        try { reply = await operation.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
        catch (Exception error)
        {
            _storageOperationOutstanding = true;
            _supervisor.Stop("content storage operation did not complete");
            // Cleanup must follow the outstanding operation, not race its maps.
            _ = operation.ContinueWith(_ => { _storageOperationOutstanding = false; DisposeStorage(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (error is InvalidDataException or OperationCanceledException) throw;
            throw new InvalidDataException("The content storage service failed.", error);
        }
        await SendAsync(ContentMessageKind.StorageResponse, new { requestId = message.Sequence, result = reply }, cancellationToken).ConfigureAwait(false);
    }
    private volatile bool _storageOperationOutstanding;

    private object StorageEnvelope(JsonElement envelope)
    {
        if (_stageStarted != 0 && Environment.TickCount64 - _stageStarted > 15000) ClearStages();
        var operation = envelope.GetProperty("operation").GetString();
        switch (operation)
        {
            case "begin":
                if (_upload is not null || _download is not null) throw new InvalidDataException("A storage transfer is already active.");
                var length = envelope.GetProperty("length").GetInt32();
                var hash = envelope.GetProperty("hash").GetString();
                if (length <= 0 || length > _storageLimit || hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid storage transfer size or digest.");
                _upload = new(); _uploadLength = length; _uploadHash = hash;
                _stageStarted = Environment.TickCount64; _uploadToken = Guid.NewGuid().ToString("N");
                return new { token = _uploadToken };
            case "chunk":
                RequireToken(envelope, _uploadToken);
                var encoded = envelope.GetProperty("data");
                if (encoded.ValueKind != JsonValueKind.String || encoded.GetString()!.Length > ((StorageChunkBytes + 2) / 3) * 4)
                    throw new InvalidDataException("Storage chunk exceeds its limit.");
                var bytes = encoded.GetBytesFromBase64();
                if (_upload is null || envelope.GetProperty("offset").GetInt32() != _upload.Length || bytes.Length == 0
                    || bytes.Length > StorageChunkBytes || _upload.Length + bytes.Length > _uploadLength) throw new InvalidDataException("Invalid storage chunk.");
                _upload.Write(bytes); return new { };
            case "execute":
                RequireToken(envelope, _uploadToken);
                if (_upload is null || _upload.Length != _uploadLength) throw new InvalidDataException("Incomplete storage request.");
                var request = _upload.ToArray();
                if (!Convert.ToHexString(SHA256.HashData(request)).Equals(_uploadHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Storage digest mismatch.");
                _upload.Dispose(); _upload = null; _uploadToken = null;
                var result = _cacheBroker!.Execute(request);
                if (result.Length > _storageLimit) throw new InvalidDataException("Storage reply exceeds its limit.");
                if (result.Length <= StorageChunkBytes) { _stageStarted = 0; return new { data = result }; }
                _download = result; _downloadToken = Guid.NewGuid().ToString("N"); _stageStarted = Environment.TickCount64;
                return new { token = _downloadToken, length = result.Length, hash = Convert.ToHexString(SHA256.HashData(result)) };
            case "read":
                RequireToken(envelope, _downloadToken);
                var offset = envelope.GetProperty("offset").GetInt32();
                if (_download is null || offset < 0 || offset >= _download.Length) throw new InvalidDataException("Invalid storage reply offset.");
                return new { data = _download.AsSpan(offset, Math.Min(StorageChunkBytes, _download.Length - offset)).ToArray() };
            case "finish": RequireToken(envelope, _downloadToken); ClearStages(); return new { };
            default: throw new InvalidDataException("Invalid storage transport operation.");
        }
    }

    private static void RequireToken(JsonElement envelope, string? expected)
    {
        if (expected is null || envelope.GetProperty("token").GetString() != expected) throw new InvalidDataException("Invalid storage transfer token.");
    }
    private void ClearStages() { _upload?.Dispose(); _upload = null; _download = null; _uploadToken = _downloadToken = null; _stageStarted = 0; }
    private readonly object _storageCleanupGate = new();
    private bool _storageDisposed;
    private void DisposeStorage()
    {
        lock (_storageCleanupGate)
        {
            if (_storageOperationOutstanding || _storageDisposed) return;
            _storageDisposed = true; ClearStages(); _cacheBroker?.Dispose();
        }
    }
    private void CloseStorageRequests()
    {
        foreach (var request in _storageRequests.Values) request.TrySetException(new IOException("The content storage connection closed."));
    }
    private void CompleteStorage(ContentMessage message)
    {
        try
        {
            var id = message.Payload.GetProperty("requestId").GetInt64();
            if (id <= 0) throw new InvalidDataException("Invalid storage response correlation.");
            if (_storageRequests.TryGetValue(id, out var completion)) completion.TrySetResult(message.Payload.GetProperty("result").Clone());
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Invalid storage response.", error); }
    }
    private async Task<JsonElement> StorageRoundTrip(object payload, CancellationToken cancellationToken)
    {
        await _requestSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _storageRequests[sequence] = completion;
        try
        {
            if (_connectionClosed) throw new IOException("The content storage connection closed.");
            await WriteAsync(ContentMessageKind.StorageRequest, sequence, payload, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _storageRequests.TryRemove(sequence, out _); _requestSlots.Release(); }
    }
    internal async Task<byte[]> RequestStorageAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        if (request.Length > _storageLimit) throw WebIdlException.DomException("QuotaExceededError", "Storage request exceeds its limit.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _supervisor.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await _storageCallGate.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var begin = await StorageRoundTrip(new { operation = "begin", length = request.Length, hash = Convert.ToHexString(SHA256.HashData(request)) }, timeout.Token).ConfigureAwait(false);
            var token = begin.GetProperty("token").GetString();
            for (int offset = 0; offset < request.Length; offset += StorageChunkBytes)
                await StorageRoundTrip(new { operation = "chunk", token, offset, data = request.AsSpan(offset, Math.Min(StorageChunkBytes, request.Length - offset)).ToArray() }, timeout.Token).ConfigureAwait(false);
            var result = await StorageRoundTrip(new { operation = "execute", token }, timeout.Token).ConfigureAwait(false);
            if (result.TryGetProperty("data", out var inline)) return inline.GetBytesFromBase64();
            var length = result.GetProperty("length").GetInt32();
            if (length <= 0 || length > _storageLimit) throw new InvalidDataException("Invalid storage reply length.");
            var bytes = new byte[length]; token = result.GetProperty("token").GetString();
            for (int offset = 0; offset < length;)
            {
                var chunk = await StorageRoundTrip(new { operation = "read", token, offset }, timeout.Token).ConfigureAwait(false);
                var data = chunk.GetProperty("data").GetBytesFromBase64();
                if (data.Length != Math.Min(StorageChunkBytes, length - offset)) throw new InvalidDataException("Invalid storage reply chunk.");
                data.CopyTo(bytes, offset); offset += data.Length;
            }
            if (Convert.ToHexString(SHA256.HashData(bytes)) != result.GetProperty("hash").GetString()) throw new InvalidDataException("Storage reply digest mismatch.");
            await StorageRoundTrip(new { operation = "finish", token }, timeout.Token).ConfigureAwait(false);
            return bytes;
        }
        catch { _supervisor.Stop("content storage transaction interrupted"); throw; }
        finally { _storageCallGate.Release(); }
    }
}

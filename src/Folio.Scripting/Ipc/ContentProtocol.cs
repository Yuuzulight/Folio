using System.Buffers.Binary;
using System.Text.Json;

namespace Folio.Scripting.Ipc;

public enum ContentMessageKind
{
    Hello,
    Load,
    ResourceRequest,
    ResourceResponse,
    MediaDecodeRequest,
    MediaDecodeResponse,
    MediaAudioSubmit,
    MediaAudioLifecycle,
    MediaCaptureRequest,
    MediaCaptureResponse,
    Frame,
    Input,
    Diagnostic,
    MediaCaptureActivity,
    Heartbeat,
    Shutdown,
    Capabilities,
    StorageRequest,
    StorageResponse,
}

public sealed record ContentMessage(ContentMessageKind Kind, long Sequence, JsonElement Payload, int Version = 1)
{
    public const int CurrentVersion = 1;
}

/// <summary>Length-prefixed, bounded protocol codec used by the content-process transport.</summary>
public static class ContentProtocol
{
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public static byte[] Encode(ContentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidDataException("Content protocol payload exceeds the configured limit.");
        var frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> frame, out ContentMessage? message)
    {
        message = null;
        if (frame.Length < sizeof(int)) return false;
        var length = BinaryPrimitives.ReadInt32LittleEndian(frame);
        if (length < 0 || length > MaxPayloadBytes || frame.Length != sizeof(int) + length) return false;
        try
        {
            message = JsonSerializer.Deserialize<ContentMessage>(frame[sizeof(int)..]);
            return message is not null && message.Version == ContentMessage.CurrentVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class ContentProtocolReader
{
    private readonly MemoryStream _buffer = new();

    public IEnumerable<ContentMessage> Push(ReadOnlyMemory<byte> bytes)
    {
        _buffer.Write(bytes.Span);
        var data = _buffer.ToArray();
        var offset = 0;
        while (data.Length - offset >= sizeof(int))
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            if (length < 0 || length > ContentProtocol.MaxPayloadBytes) throw new InvalidDataException("Invalid content protocol frame length.");
            if (data.Length - offset < sizeof(int) + length) break;
            if (!ContentProtocol.TryDecode(data.AsSpan(offset, sizeof(int) + length), out var message) || message is null) throw new InvalidDataException("Invalid content protocol frame.");
            yield return message;
            offset += sizeof(int) + length;
        }
        if (offset > 0)
        {
            _buffer.SetLength(0);
            _buffer.Write(data.AsSpan(offset));
        }
    }
}

public static class ContentTransport
{
    public static async ValueTask WriteAsync(Stream stream, ContentMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var frame = ContentProtocol.Encode(message);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<ContentMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > ContentProtocol.MaxPayloadBytes) throw new InvalidDataException("Invalid content protocol frame length.");
        var frame = new byte[sizeof(int) + length];
        header.CopyTo(frame, 0);
        if (!await ReadExactlyAsync(stream, frame.AsMemory(sizeof(int)), cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException();
        if (!ContentProtocol.TryDecode(frame, out var message) || message is null) throw new InvalidDataException("Invalid content protocol frame.");
        return message;
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0) return offset == 0 ? false : throw new EndOfStreamException();
            offset += read;
        }
        return true;
    }
}

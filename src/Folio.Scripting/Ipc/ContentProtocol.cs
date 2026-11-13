using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    Viewport,
    IframeNavigationRequested,
    MediaCapabilitiesRequest,
    MediaCapabilitiesResponse,
    /// <summary>
    /// Content process to host: its GPU worker channel was lost. Host to content process: a replacement
    /// channel. Host-owned; the host replaces the worker a bounded number of times per content process.
    /// </summary>
    GpuRestore,
    /// <summary>Content process to host: a host-mediated UDP command (open, send, close). Handled by the host's UdpBroker; see docs/webrtc-host-udp.md.</summary>
    UdpCommand,
    /// <summary>Host to content process: a host-mediated UDP event (opened, failed, datagram, closed).</summary>
    UdpEvent,
}

public sealed record ContentMessage(ContentMessageKind Kind, long Sequence, JsonElement Payload, int Version = 1)
{
    public const int CurrentVersion = 1;
}

[JsonSerializable(typeof(ContentMessage))]
[JsonSerializable(typeof(ContentFramePayload))]
[JsonSerializable(typeof(ContentViewport))]
[JsonSerializable(typeof(ResourceLimits))]
internal partial class ContentProtocolJsonContext : JsonSerializerContext;

/// <summary>Length-prefixed, bounded protocol codec used by the content-process transport.</summary>
public static class ContentProtocol
{
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    public static byte[] Encode(ContentMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, ContentProtocolJsonContext.Default.ContentMessage);
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
            message = JsonSerializer.Deserialize(frame[sizeof(int)..], ContentProtocolJsonContext.Default.ContentMessage);
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

    /// <summary>The largest UDP command or event frame accepted. Larger frames of those kinds are refused before they are allocated.</summary>
    public const int MaxUdpMessageBytes = 8 * 1024;

    // Reads the number in {"Kind":N, ...} from the start of a frame payload.
    private static bool TryReadLeadingKind(ReadOnlySpan<byte> payload, out int kind)
    {
        kind = 0;
        ReadOnlySpan<byte> start = "{\"Kind\":"u8;
        if (!payload.StartsWith(start)) return false;
        var digits = 0;
        for (var i = start.Length; i < payload.Length && payload[i] is >= (byte)'0' and <= (byte)'9' && digits < 4; i++, digits++)
            kind = kind * 10 + (payload[i] - '0');
        return digits > 0 && start.Length + digits < payload.Length && payload[start.Length + digits] == (byte)',';
    }

    public static async ValueTask<ContentMessage?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[sizeof(int)];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false)) return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > ContentProtocol.MaxPayloadBytes) throw new InvalidDataException("Invalid content protocol frame length.");
        byte[] frame;
        if (length > MaxUdpMessageBytes)
        {
            // UDP traffic is tiny. Our serializer writes the kind first, so a large frame must say what it is before
            // the rest is allocated; a large UDP frame, or one that hides its kind, closes the connection.
            var prefix = new byte[Math.Min(length, 32)];
            if (!await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException();
            if (!TryReadLeadingKind(prefix, out var kind) || kind == (int)ContentMessageKind.UdpCommand || kind == (int)ContentMessageKind.UdpEvent)
                throw new InvalidDataException("Oversize or unclassifiable UDP content frame.");
            frame = new byte[sizeof(int) + length];
            prefix.CopyTo(frame, sizeof(int));
            if (length > prefix.Length && !await ReadExactlyAsync(stream, frame.AsMemory(sizeof(int) + prefix.Length), cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException();
        }
        else
        {
            frame = new byte[sizeof(int) + length];
            if (!await ReadExactlyAsync(stream, frame.AsMemory(sizeof(int)), cancellationToken).ConfigureAwait(false)) throw new EndOfStreamException();
        }
        header.CopyTo(frame, 0);
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

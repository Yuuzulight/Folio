using System.Buffers.Binary;
using System.Text;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned SCTP data channel engine (RFC 4960 / RFC 8831 / RFC 8832) operating over DTLS.
/// Implements DCEP channel negotiation, CRC32c validation, chunk framing, and ordered/unordered delivery.
/// </summary>
internal sealed class SctpDataChannel : IDisposable
{
    // Leave room for SCTP common/DATA headers inside one 16 KiB DTLS record.
    internal const int MaxUnfragmentedMessageBytes = 16356;
    private const ushort SctpPort = 5000;

    private const byte ChunkData = 0;
    private const byte ChunkInit = 1;
    private const byte ChunkInitAck = 2;
    private const byte ChunkSack = 3;
    private const byte ChunkHeartbeat = 4;
    private const byte ChunkHeartbeatAck = 5;
    private const byte ChunkAbort = 6;
    private const byte ChunkShutdown = 7;
    private const byte ChunkCookieEcho = 10;
    private const byte ChunkCookieAck = 11;

    private const uint PpidDcep = 50;
    private const uint PpidString = 51;
    private const uint PpidBinary = 53;
    private const uint PpidStringEmpty = 56;
    private const uint PpidBinaryEmpty = 57;

    private const byte DcepOpen = 0x03;
    private const byte DcepAck = 0x02;

    public uint LocalVerificationTag { get; }
    public uint RemoteVerificationTag { get; private set; } = 0;
    public string State { get; private set; } = "new";

    public event Action<byte[]>? OnPacketToSend;
    public event Action<ChannelState>? OnDataChannelCreated;

    private uint _localTsn = 1;
    private uint _remoteTsn = 0;
    private ushort _nextStreamId = 0;

    private readonly Dictionary<ushort, ChannelState> _channels = [];
    private readonly List<ChannelState> _pendingChannels = [];
    private bool? _isClient;
    private readonly object _gate = new();
    private bool _disposed;

    public SctpDataChannel()
    {
        LocalVerificationTag = (uint)Random.Shared.Next(1, int.MaxValue);
    }

    public void StartAssociation(bool isClient)
    {
        lock (_gate)
        {
            if (_disposed || State != "new") return;
            _isClient = isClient;
            _nextStreamId = (ushort)(isClient ? 0 : 1);
            State = "connecting";

            if (isClient)
            {
                SendInit();
            }
        }
    }

    private void SendInit()
    {
        // INIT chunk: Initiate Tag, a_rwnd (131072), OS (1024), MIS (1024), Initial TSN
        var chunk = new byte[20];
        chunk[0] = ChunkInit;
        chunk[1] = 0; // flags
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), 20); // length
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), LocalVerificationTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), 131072); // a_rwnd
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(12), 1024); // OS
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(14), 1024); // MIS
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), _localTsn);

        SendSctpPacket(0, [chunk]);
    }

    private void SendInitAck()
    {
        var chunk = new byte[20];
        chunk[0] = ChunkInitAck;
        chunk[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), 20);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), LocalVerificationTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), 131072);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(12), 1024);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(14), 1024);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), _localTsn);

        SendSctpPacket(RemoteVerificationTag, [chunk]);
    }

    public void ProcessIncomingPacket(ReadOnlySpan<byte> packet)
    {
        lock (_gate)
        {
            if (_disposed || packet.Length < 12) return;

            var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(0, 2));
            var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
            var vTag = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
            var checksum = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4));

            // Verify CRC32c by recomputing over packet with zeroed checksum field
            var copy = packet.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(8, 4), 0);
            if (ComputeCrc32c(copy) != checksum) return;

            var offset = 12;
            while (offset + 4 <= packet.Length)
            {
                var chunkType = packet[offset];
                var chunkFlags = packet[offset + 1];
                var chunkLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));

                if (chunkLength < 4 || offset + chunkLength > packet.Length) break;
                var chunkPayload = packet.Slice(offset + 4, chunkLength - 4);

                ProcessChunk(chunkType, chunkFlags, chunkPayload, vTag);

                // Chunks are padded to 4-byte boundaries
                offset += (chunkLength + 3) & ~3;
            }
        }
    }

    private void ProcessChunk(byte type, byte flags, ReadOnlySpan<byte> payload, uint vTag)
    {
        switch (type)
        {
            case ChunkInit:
                if (payload.Length >= 16)
                {
                    RemoteVerificationTag = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
                    _remoteTsn = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4));
                    SendInitAck();
                }
                break;

            case ChunkInitAck:
                if (payload.Length >= 16)
                {
                    RemoteVerificationTag = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
                    _remoteTsn = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4));
                    // Send CookieEcho
                    var echo = new byte[8];
                    echo[0] = ChunkCookieEcho;
                    BinaryPrimitives.WriteUInt16BigEndian(echo.AsSpan(2), 8);
                    SendSctpPacket(RemoteVerificationTag, [echo]);

                    // Wait for COOKIE_ACK before opening pending channels.
                }
                break;

            case ChunkCookieEcho:
                // Send CookieAck
                var ack = new byte[4];
                ack[0] = ChunkCookieAck;
                BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2), 4);
                SendSctpPacket(RemoteVerificationTag, [ack]);
                AssociationConnected();
                break;

            case ChunkCookieAck:
                AssociationConnected();
                break;

            case ChunkHeartbeat:
                // Echo back heartbeat ack
                var hbAck = new byte[4 + payload.Length];
                hbAck[0] = ChunkHeartbeatAck;
                BinaryPrimitives.WriteUInt16BigEndian(hbAck.AsSpan(2), (ushort)hbAck.Length);
                payload.CopyTo(hbAck.AsSpan(4));
                SendSctpPacket(RemoteVerificationTag, [hbAck]);
                break;

            case ChunkData:
                ProcessDataChunk(flags, payload);
                break;
        }
    }

    private void ProcessDataChunk(byte flags, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return;
        var tsn = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
        var streamId = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(4, 2));
        var streamSeq = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(6, 2));
        var ppid = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(8, 4));
        var userData = payload.Slice(12);

        // Send SACK
        SendSack(tsn);

        if (ppid == PpidDcep)
        {
            ProcessDcep(streamId, userData);
        }
        else
        {
            if (_channels.TryGetValue(streamId, out var channel))
            {
                if (ppid is PpidString or PpidStringEmpty)
                {
                    var text = ppid == PpidStringEmpty ? "" : Encoding.UTF8.GetString(userData);
                    channel.DeliverMessage(text);
                }
                else if (ppid is PpidBinary or PpidBinaryEmpty)
                {
                    var bytes = ppid == PpidBinaryEmpty ? [] : userData.ToArray();
                    channel.DeliverMessage(bytes);
                }
            }
        }
    }

    private void ProcessDcep(ushort streamId, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return;
        var msgType = data[0];

        if (msgType == DcepOpen)
        {
            if (data.Length < 12) return;
            var channelType = data[1];
            var priority = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
            var reliability = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
            var labelLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
            var protoLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10, 2));

            if (data.Length < 12 + labelLen + protoLen) return;
            var label = Encoding.UTF8.GetString(data.Slice(12, labelLen));
            var protocol = Encoding.UTF8.GetString(data.Slice(12 + labelLen, protoLen));

            var channel = new ChannelState(this, streamId, label, protocol);
            _channels[streamId] = channel;

            // Send DCEP ACK
            SendDcepAck(streamId);
            channel.SetOpen();
            OnDataChannelCreated?.Invoke(channel);
        }
        else if (msgType == DcepAck)
        {
            if (_channels.TryGetValue(streamId, out var channel))
            {
                channel.SetOpen();
            }
        }
    }

    private void SendDcepAck(ushort streamId)
    {
        var ack = new byte[1];
        ack[0] = DcepAck;
        SendData(streamId, PpidDcep, ack);
    }

    private void SendSack(uint cumulativeTsn)
    {
        var sack = new byte[16];
        sack[0] = ChunkSack;
        BinaryPrimitives.WriteUInt16BigEndian(sack.AsSpan(2), 16);
        BinaryPrimitives.WriteUInt32BigEndian(sack.AsSpan(4), cumulativeTsn);
        BinaryPrimitives.WriteUInt32BigEndian(sack.AsSpan(8), 131072); // a_rwnd
        BinaryPrimitives.WriteUInt16BigEndian(sack.AsSpan(12), 0); // num gap ack
        BinaryPrimitives.WriteUInt16BigEndian(sack.AsSpan(14), 0); // num dup tsn
        SendSctpPacket(RemoteVerificationTag, [sack]);
    }

    public ChannelState CreateDataChannel(string label, string protocol = "", ushort? explicitId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_channels.Count >= 1024 || (long)Encoding.UTF8.GetByteCount(label) + Encoding.UTF8.GetByteCount(protocol) > MaxUnfragmentedMessageBytes - 12)
                throw new InvalidOperationException("SCTP data channel limits exceeded.");
            // Before the DTLS role is selected, reserve a channel object and
            // assign its parity-correct stream identifier when association starts.
            var id = explicitId ?? _nextStreamId;
            if (_channels.ContainsKey(id)) throw new InvalidOperationException("SCTP stream id is already in use.");
            if (explicitId is null)
            {
                if (_nextStreamId > ushort.MaxValue - 2) throw new InvalidOperationException("SCTP stream ids exhausted.");
                _nextStreamId += 2;
            }
            var channel = new ChannelState(this, id, label, protocol);
            _channels[id] = channel;
            channel.AutomaticId = explicitId is null;
            if (State == "connected") SendChannelOpen(channel);
            else _pendingChannels.Add(channel);
            return channel;
        }
    }

    private void AssociationConnected()
    {
        if (State == "connected") return;
        State = "connected";
        var pending = _pendingChannels.ToArray();
        _pendingChannels.Clear();
        foreach (var channel in pending)
        {
            if (channel.ReadyState == "closed") continue;
            if (channel.AutomaticId && _isClient is { } client && (channel.Id & 1) != (client ? 0 : 1))
            {
                _channels.Remove(channel.Id);
                channel.Id++;
                if (_channels.ContainsKey(channel.Id)) throw new InvalidOperationException("SCTP role-selected stream collision.");
                _channels.Add(channel.Id, channel);
            }
            SendChannelOpen(channel);
        }
        if (pending.Length > 0)
            _nextStreamId = (ushort)Math.Min(ushort.MaxValue, Math.Max(_nextStreamId, pending.Max(channel => (int)channel.Id) + 2));
    }

    private void SendChannelOpen(ChannelState channel)
    {
            var labelBytes = Encoding.UTF8.GetBytes(channel.Label);
            var protoBytes = Encoding.UTF8.GetBytes(channel.Protocol);
            if (labelBytes.Length + protoBytes.Length > MaxUnfragmentedMessageBytes - 12)
                throw new InvalidOperationException("DCEP message requires fragmentation.");

            var openMsg = new byte[12 + labelBytes.Length + protoBytes.Length];
            openMsg[0] = DcepOpen;
            openMsg[1] = 0; // reliable ordered
            BinaryPrimitives.WriteUInt16BigEndian(openMsg.AsSpan(2), 0); // priority
            BinaryPrimitives.WriteUInt32BigEndian(openMsg.AsSpan(4), 0); // reliability
            BinaryPrimitives.WriteUInt16BigEndian(openMsg.AsSpan(8), (ushort)labelBytes.Length);
            BinaryPrimitives.WriteUInt16BigEndian(openMsg.AsSpan(10), (ushort)protoBytes.Length);
            labelBytes.CopyTo(openMsg.AsSpan(12));
            protoBytes.CopyTo(openMsg.AsSpan(12 + labelBytes.Length));

            SendData(channel.Id, PpidDcep, openMsg);
    }

    internal void SendData(ushort streamId, uint ppid, byte[] data)
    {
        if (data.Length > MaxUnfragmentedMessageBytes)
            throw new InvalidOperationException("SCTP message requires fragmentation.");
        var tsn = _localTsn++;
        var chunk = new byte[16 + data.Length];
        chunk[0] = ChunkData;
        chunk[1] = 0x03; // B + E flags (unordered/ordered complete message)
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)chunk.Length);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), tsn);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(8), streamId);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(10), 0); // stream seq
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), ppid);
        data.CopyTo(chunk.AsSpan(16));

        SendSctpPacket(RemoteVerificationTag, [chunk]);
    }

    private void SendSctpPacket(uint vTag, IReadOnlyList<byte[]> chunks)
    {
        var totalChunksLen = 0;
        foreach (var c in chunks) totalChunksLen += (c.Length + 3) & ~3;

        var packet = new byte[12 + totalChunksLen];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), SctpPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), SctpPort);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), vTag);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0); // checksum zeroed for computation

        var offset = 12;
        foreach (var c in chunks)
        {
            c.CopyTo(packet.AsSpan(offset));
            offset += (c.Length + 3) & ~3;
        }

        var crc = ComputeCrc32c(packet);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), crc);

        OnPacketToSend?.Invoke(packet);
    }

    private static uint ComputeCrc32c(ReadOnlySpan<byte> bytes)
    {
        // CRC-32c (Castagnoli) 0x1EDC6F41 polynomial
        var crc = uint.MaxValue;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0x82F63B78u);
        }
        return ~crc;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            State = "closed";
            foreach (var ch in _channels.Values) ch.Close();
            _channels.Clear();
            _pendingChannels.Clear();
        }
    }

    internal sealed class ChannelState(SctpDataChannel owner, ushort id, string label, string protocol)
    {
        public ushort Id { get; internal set; } = id;
        internal bool AutomaticId { get; set; }
        public string Label { get; } = label;
        public string Protocol { get; } = protocol;
        public string ReadyState { get; private set; } = "connecting";
        public long BufferedAmount { get; private set; } = 0;
        public long BufferedAmountLowThreshold { get; set; } = 0;

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action<object>? OnMessage;

        public void Send(string text)
        {
            if (ReadyState != "open") throw new InvalidOperationException("Data channel is not open.");
            if (Encoding.UTF8.GetByteCount(text) > MaxUnfragmentedMessageBytes)
                throw new InvalidOperationException("SCTP message requires fragmentation.");
            var bytes = Encoding.UTF8.GetBytes(text);
            owner.SendData(Id, bytes.Length == 0 ? PpidStringEmpty : PpidString, bytes);
        }

        public void Send(byte[] data)
        {
            if (ReadyState != "open") throw new InvalidOperationException("Data channel is not open.");
            owner.SendData(Id, data.Length == 0 ? PpidBinaryEmpty : PpidBinary, data);
        }

        internal void DeliverMessage(object message)
        {
            OnMessage?.Invoke(message);
        }

        internal void SetOpen()
        {
            ReadyState = "open";
            OnOpen?.Invoke();
        }

        public void Close()
        {
            if (ReadyState == "closed") return;
            ReadyState = "closed";
            OnClose?.Invoke();
        }
    }
}

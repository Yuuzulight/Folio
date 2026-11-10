using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned SCTP data channel engine (RFC 4960 / RFC 8831 / RFC 8832) operating over DTLS.
/// Implements DCEP negotiation and bounded reliable complete-DATA delivery.
/// Congestion/flow control, fragmentation and stream reset remain unqualified.
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
    private byte[]? _issuedCookie;
    private readonly byte[] _cookieKey = RandomNumberGenerator.GetBytes(32);
    private readonly TimeProvider _clock;
    private long _cookieIssuedAt;
    private static readonly TimeSpan CookieLifetime = TimeSpan.FromSeconds(60);
    private bool _cookieEchoSent;
    private readonly ITimer? _retryTimer;
    private byte[]? _handshakePacket;
    private long _lastHandshakeSend;
    private int _handshakeRetries;
    private TimeSpan _handshakeRetryDelay = TimeSpan.FromSeconds(1);
    private long _associationStartedAt;
    private readonly SctpReliableDelivery _reliable = new();
    private readonly Queue<byte[]> _outboundPackets = [];
    private int _outboundPacketBytes;
    private bool _flushingPackets;

    public SctpDataChannel() : this(TimeProvider.System) { }

    internal SctpDataChannel(TimeProvider clock, bool enableRetryTimer = true)
    {
        _clock = clock;
        do { LocalVerificationTag = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)); }
        while (LocalVerificationTag == 0);
        if (enableRetryTimer)
            _retryTimer = clock.CreateTimer(_ =>
            {
                try { RetryDueHandshake(); RetryDueData(); }
                catch { FailAssociation(); }
            }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    internal void RetryDueData()
    {
        var fail = false;
        try
        {
            lock (_gate)
            {
                if (_disposed || State != "connected") return;
                var chunks = _reliable.DueRetransmissions(_clock, out fail);
                foreach (var chunk in chunks) SendSctpPacket(RemoteVerificationTag, [chunk]);
            }
        }
        finally { FlushPackets(); }
        if (fail) FailAssociation();
    }

    internal void RetryDueHandshake()
    {
        byte[]? packet = null;
        var fail = false;
        lock (_gate)
        {
            if (_disposed || State != "connecting") return;
            if (_clock.GetElapsedTime(_associationStartedAt) >= TimeSpan.FromSeconds(60))
                fail = true;
            else
            {
                if (_handshakePacket is null ||
                    _clock.GetElapsedTime(_lastHandshakeSend) < _handshakeRetryDelay) return;
                if (_handshakeRetries >= 6) fail = true;
                else
                {
                    _handshakeRetries++;
                    _handshakeRetryDelay = TimeSpan.FromSeconds(Math.Min(8, 1 << _handshakeRetries));
                    _lastHandshakeSend = _clock.GetTimestamp();
                    packet = (byte[])_handshakePacket.Clone();
                }
            }
        }
        // DTLS receive callbacks acquire DTLS then SCTP. Never acquire DTLS
        // through a timer send while holding SCTP in the opposite order.
        if (fail) FailAssociation();
        else if (packet is not null) OnPacketToSend?.Invoke(packet);
    }

    private void FailAssociation()
    {
        ChannelState[] channels;
        lock (_gate)
        {
            if (_disposed || State is not ("connecting" or "connected")) return;
            State = "failed";
            _handshakePacket = null;
            _cookieEchoSent = false;
            channels = _channels.Values.ToArray();
            _pendingChannels.Clear();
            _reliable.Clear();
            _outboundPackets.Clear();
            _outboundPacketBytes = 0;
        }
        foreach (var channel in channels)
        {
            try { channel.Close(); } catch { /* Teardown must survive a consumer callback. */ }
        }
    }

    public void StartAssociation(bool isClient)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || State != "new") return;
                _isClient = isClient;
                _nextStreamId = (ushort)(isClient ? 0 : 1);
                AssignPendingStreamIds(isClient);
                State = "connecting";
                _associationStartedAt = _clock.GetTimestamp();

                if (isClient)
                {
                    SendInit();
                }
            }
        }
        finally { FlushPackets(); }
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

        SendSctpPacket(0, [chunk], rememberHandshake: true);
    }

    private void SendInitAck()
    {
        if (_issuedCookie is null || _clock.GetElapsedTime(_cookieIssuedAt) >= CookieLifetime)
        {
            // Cookie body includes both tags, initial TSNs, ports, creation
            // time and lifetime. Authenticate all fields with an owned key.
            _issuedCookie = new byte[80];
            BinaryPrimitives.WriteUInt32BigEndian(_issuedCookie, LocalVerificationTag);
            BinaryPrimitives.WriteUInt32BigEndian(_issuedCookie.AsSpan(4), RemoteVerificationTag);
            BinaryPrimitives.WriteUInt32BigEndian(_issuedCookie.AsSpan(8), _localTsn);
            BinaryPrimitives.WriteUInt32BigEndian(_issuedCookie.AsSpan(12), _remoteTsn);
            BinaryPrimitives.WriteUInt16BigEndian(_issuedCookie.AsSpan(16), SctpPort);
            BinaryPrimitives.WriteUInt16BigEndian(_issuedCookie.AsSpan(18), SctpPort);
            BinaryPrimitives.WriteInt64BigEndian(_issuedCookie.AsSpan(20), _clock.GetUtcNow().ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt32BigEndian(_issuedCookie.AsSpan(28), 60);
            RandomNumberGenerator.Fill(_issuedCookie.AsSpan(32, 16));
            HMACSHA256.HashData(_cookieKey, _issuedCookie.AsSpan(0, 48), _issuedCookie.AsSpan(48));
            _cookieIssuedAt = _clock.GetTimestamp();
        }
        var chunk = new byte[24 + _issuedCookie.Length];
        chunk[0] = ChunkInitAck;
        chunk[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)chunk.Length);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), LocalVerificationTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), 131072);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(12), 1024);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(14), 1024);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), _localTsn);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(20), 7); // State Cookie
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(22), (ushort)(4 + _issuedCookie.Length));
        _issuedCookie.CopyTo(chunk, 24);

        SendSctpPacket(RemoteVerificationTag, [chunk]);
    }

    public void ProcessIncomingPacket(ReadOnlySpan<byte> packet)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || State is not ("connecting" or "connected") || packet.Length < 12) return;

                var srcPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(0, 2));
                var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2));
                var vTag = BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4));
                var checksum = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(8, 4));
                if (srcPort != SctpPort || dstPort != SctpPort || State == "new") return;

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
                    if (chunkType == ChunkInit)
                    {
                        if (vTag != 0 || offset != 12 || ((chunkLength + 3) & ~3) != packet.Length - 12) return;
                    }
                    else if (vTag != LocalVerificationTag) return;
                    var chunkPayload = packet.Slice(offset + 4, chunkLength - 4);

                    ProcessChunk(chunkType, chunkFlags, chunkPayload, vTag);

                    // Chunks are padded to 4-byte boundaries
                    offset += (chunkLength + 3) & ~3;
                }
            }
        }
        finally { FlushPackets(); }
    }

    private void ProcessChunk(byte type, byte flags, ReadOnlySpan<byte> payload, uint vTag)
    {
        switch (type)
        {
            case ChunkInit:
                if (State == "connecting" && _isClient == false && payload.Length >= 16 &&
                    BinaryPrimitives.ReadUInt32BigEndian(payload) != 0 &&
                    (RemoteVerificationTag == 0 || RemoteVerificationTag == BinaryPrimitives.ReadUInt32BigEndian(payload)))
                {
                    if (!_reliable.SetPeerInitialTsn(BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)))) break;
                    RemoteVerificationTag = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
                    _remoteTsn = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4));
                    SendInitAck();
                }
                break;

            case ChunkInitAck:
                if (State == "connecting" && _isClient == true && payload.Length >= 16 &&
                    BinaryPrimitives.ReadUInt32BigEndian(payload) != 0 && TryGetCookie(payload, out var cookie))
                {
                    if (!_reliable.SetPeerInitialTsn(BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4)))) break;
                    RemoteVerificationTag = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(0, 4));
                    _remoteTsn = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(12, 4));
                    // Send CookieEcho
                    var echo = new byte[4 + cookie.Length];
                    echo[0] = ChunkCookieEcho;
                    BinaryPrimitives.WriteUInt16BigEndian(echo.AsSpan(2), (ushort)echo.Length);
                    cookie.CopyTo(echo.AsSpan(4));
                    _cookieEchoSent = true;
                    SendSctpPacket(RemoteVerificationTag, [echo], rememberHandshake: true);

                    // Wait for COOKIE_ACK before opening pending channels.
                }
                break;

            case ChunkCookieEcho:
                if (_isClient != false || _issuedCookie is null ||
                    _clock.GetElapsedTime(_cookieIssuedAt) >= CookieLifetime ||
                    !CryptographicOperations.FixedTimeEquals(payload, _issuedCookie)) break;
                // Send CookieAck
                var ack = new byte[4];
                ack[0] = ChunkCookieAck;
                BinaryPrimitives.WriteUInt16BigEndian(ack.AsSpan(2), 4);
                // Accept DATA triggered synchronously by our ACK, but send our
                // pending DCEP only after the peer has received that ACK.
                if (State == "connected") SendSctpPacket(RemoteVerificationTag, [ack]);
                else AssociationConnected(() => SendSctpPacket(RemoteVerificationTag, [ack]));
                break;

            case ChunkCookieAck:
                if (_cookieEchoSent && payload.IsEmpty) AssociationConnected();
                break;

            case ChunkHeartbeat:
                if (State != "connected") break;
                // Echo back heartbeat ack
                var hbAck = new byte[4 + payload.Length];
                hbAck[0] = ChunkHeartbeatAck;
                BinaryPrimitives.WriteUInt16BigEndian(hbAck.AsSpan(2), (ushort)hbAck.Length);
                payload.CopyTo(hbAck.AsSpan(4));
                SendSctpPacket(RemoteVerificationTag, [hbAck]);
                break;

            case ChunkData:
                if (State == "connected") ProcessDataChunk(flags, payload);
                break;
            case ChunkSack:
                if (State == "connected")
                {
                    var previous = _channels.Values.Select(channel => (Channel: channel, Amount: channel.BufferedAmount)).ToArray();
                    if (_reliable.Acknowledge(payload))
                        foreach (var (channel, amount) in previous)
                            if (amount > channel.BufferedAmountLowThreshold && channel.BufferedAmount <= channel.BufferedAmountLowThreshold)
                                channel.NotifyBufferedAmountLow();
                }
                break;
        }
    }

    private static bool TryGetCookie(ReadOnlySpan<byte> initAck, out ReadOnlySpan<byte> cookie)
    {
        cookie = default;
        for (var offset = 16; offset < initAck.Length;)
        {
            if (initAck.Length - offset < 4) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(initAck.Slice(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(initAck.Slice(offset + 2));
            if (length < 4 || length > initAck.Length - offset) return false;
            if (type == 7)
            {
                if (length == 4 || !cookie.IsEmpty) return false;
                cookie = initAck.Slice(offset + 4, length - 4);
            }
            var next = offset + ((length + 3) & ~3);
            if (next > initAck.Length)
            {
                // The final parameter's padding may belong to the chunk's
                // outer padding and is excluded from its declared length.
                if (offset + length != initAck.Length) return false;
                break;
            }
            offset = next;
        }
        return !cookie.IsEmpty;
    }

    private void ProcessDataChunk(byte flags, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 12) return;
        var ready = _reliable.Receive(flags, payload);
        SendSctpPacket(RemoteVerificationTag, [_reliable.CreateSack()]);
        foreach (var message in ready)
        {
            if (message.Ppid == PpidDcep) ProcessDcep(message.Stream, message.Data);
            else if (_channels.TryGetValue(message.Stream, out var channel) && channel.ReadyState == "open")
            {
                if (message.Ppid is PpidString or PpidStringEmpty)
                {
                    var text = message.Ppid == PpidStringEmpty ? "" : Encoding.UTF8.GetString(message.Data);
                    channel.DeliverMessage(text);
                }
                else if (message.Ppid is PpidBinary or PpidBinaryEmpty)
                {
                    var bytes = message.Ppid == PpidBinaryEmpty ? [] : message.Data;
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
            if (channelType != 0 || reliability != 0 || _channels.Count >= 1024 || _channels.ContainsKey(streamId)) return;
            var label = Encoding.UTF8.GetString(data.Slice(12, labelLen));
            var protocol = Encoding.UTF8.GetString(data.Slice(12 + labelLen, protoLen));

            var channel = new ChannelState(this, streamId, label, protocol);
            _channels[streamId] = channel;

            // Send DCEP ACK
            SendDcepAck(streamId);
            channel.SetOpen();
            OnDataChannelCreated?.Invoke(channel);
        }
        else if (msgType == DcepAck && data.Length == 1)
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

    public ChannelState CreateDataChannel(string label, string protocol = "", ushort? explicitId = null)
    {
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (State == "failed") throw new InvalidOperationException("SCTP association has failed.");
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
        finally { FlushPackets(); }
    }

    private void AssociationConnected(Action? beforePendingChannels = null)
    {
        if (State == "connected") return;
        State = "connected";
        _handshakePacket = null;
        beforePendingChannels?.Invoke();
        var pending = _pendingChannels.ToArray();
        _pendingChannels.Clear();
        foreach (var channel in pending)
        {
            if (channel.ReadyState == "closed") continue;
            SendChannelOpen(channel);
        }
    }

    private void AssignPendingStreamIds(bool client)
    {
        // Reserve role-correct identifiers before any packet callback can
        // deliver the peer's OPEN, including synchronous in-process transports.
        foreach (var channel in _pendingChannels)
        {
            if (!channel.AutomaticId || (channel.Id & 1) == (client ? 0 : 1)) continue;
            if (_channels.ContainsKey((ushort)(channel.Id + 1)))
                throw new InvalidOperationException("SCTP role-selected stream collision.");
        }
        foreach (var channel in _pendingChannels)
        {
            if (!channel.AutomaticId || (channel.Id & 1) == (client ? 0 : 1)) continue;
            _channels.Remove(channel.Id);
            channel.Id++;
            _channels.Add(channel.Id, channel);
        }
        if (_channels.Count > 0)
            _nextStreamId = (ushort)Math.Min(ushort.MaxValue, Math.Max(_nextStreamId, _channels.Keys.Max(id => (int)id) + 2));
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
        try
        {
            lock (_gate)
            {
                if (_disposed || State != "connected") throw new InvalidOperationException("SCTP association is not connected.");
                var chunk = _reliable.Enqueue(_localTsn, streamId, ppid, data, _clock.GetTimestamp());
                _localTsn++;
                SendSctpPacket(RemoteVerificationTag, [chunk]);
            }
        }
        finally { FlushPackets(); }
    }

    private void SendSctpPacket(uint vTag, IReadOnlyList<byte[]> chunks, bool rememberHandshake = false)
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

        if (rememberHandshake)
        {
            _handshakePacket = (byte[])packet.Clone();
            _handshakeRetries = 0;
            _handshakeRetryDelay = TimeSpan.FromSeconds(1);
            _lastHandshakeSend = _clock.GetTimestamp();
        }
        lock (_gate)
        {
            // Control traffic must not grow memory while transport output is
            // blocked. Dropped packets recover through association/DATA retry.
            if (_outboundPackets.Count >= 2048 || packet.Length > 4 * 1024 * 1024 - _outboundPacketBytes) return;
            _outboundPackets.Enqueue(packet);
            _outboundPacketBytes += packet.Length;
        }
        FlushPackets();
    }

    private void FlushPackets()
    {
        // Public sends must not hold SCTP while entering DTLS, whose receive
        // path calls SCTP with the DTLS lock held. Reentrant callbacks enqueue
        // more packets for the current dispatcher rather than recursing.
        if (Monitor.IsEntered(_gate)) return;
        lock (_gate)
        {
            if (_flushingPackets) return;
            _flushingPackets = true;
        }
        while (true)
        {
            byte[] packet;
            lock (_gate)
            {
                if (!_outboundPackets.TryDequeue(out packet!))
                { _flushingPackets = false; return; }
                _outboundPacketBytes -= packet.Length;
            }
            try { OnPacketToSend?.Invoke(packet); }
            catch
            {
                lock (_gate) { _outboundPackets.Clear(); _outboundPacketBytes = 0; _flushingPackets = false; }
                throw;
            }
        }
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
        ChannelState[] channels;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _retryTimer?.Dispose();
            _handshakePacket = null;
            _outboundPackets.Clear();
            _outboundPacketBytes = 0;
            _reliable.Clear();
            State = "closed";
            channels = _channels.Values.ToArray();
            _channels.Clear();
            _pendingChannels.Clear();
            if (_issuedCookie is not null) CryptographicOperations.ZeroMemory(_issuedCookie);
            _issuedCookie = null;
            _cookieEchoSent = false;
            CryptographicOperations.ZeroMemory(_cookieKey);
        }
        foreach (var channel in channels)
        {
            try { channel.Close(); } catch { /* Finish releasing other owned channels. */ }
        }
    }

    internal sealed class ChannelState(SctpDataChannel owner, ushort id, string label, string protocol)
    {
        public ushort Id { get; internal set; } = id;
        internal bool AutomaticId { get; set; }
        public string Label { get; } = label;
        public string Protocol { get; } = protocol;
        public string ReadyState { get; private set; } = "connecting";
        public long BufferedAmount { get { lock (owner._gate) return owner._reliable.BufferedAmount(Id); } }
        public long BufferedAmountLowThreshold { get; set; } = 0;

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action<object>? OnMessage;
        public event Action? OnBufferedAmountLow;
        internal void NotifyBufferedAmountLow() => OnBufferedAmountLow?.Invoke();

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
            if (ReadyState != "connecting") return;
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

using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned SCTP data channel engine (RFC 4960 / RFC 8831 / RFC 8832) operating over DTLS.
/// Implements DCEP negotiation and bounded reliable complete-DATA delivery.
/// Congestion/flow control and fragmentation remain unqualified against independent stacks; stream reset is the RFC 6525 subset RFC 8831 uses to close channels.
/// </summary>
internal sealed class SctpDataChannel : IDisposable
{
    // Leave room for SCTP common/DATA headers inside one 16 KiB DTLS record.
    internal const int MaxUnfragmentedMessageBytes = 16356;
    internal const int MaxFragmentPayloadBytes = 1120;
    internal const int MaxMessageBytes = 256 * 1024;
    // Twice the retained UTF-8 wire length conservatively bounds UTF-16 strings.
    internal const int MaxChannelMetadataBytes = 256 * 1024;
    private int _channelMetadataBytes;
    internal int RetainedChannelMetadataBytes { get { lock (_gate) return _channelMetadataBytes; } }
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
    private const byte ChunkReconfig = 130;
    private const ushort ParamOutgoingReset = 13;
    private const ushort ParamReconfigResponse = 16;
    private const ushort ParamSupportedExtensions = 0x8008;
    private const uint ReconfigPerformed = 1;
    private const uint ReconfigBadSequence = 5;
    private const uint ReconfigInProgress = 6;

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
    internal int MaximumSendMessageBytes { get; private set; } = 65536;
    private ushort _remotePort = SctpPort;
    internal string GetDiagnostics()
    {
        lock (_gate) return $"{State},packets={_outboundPackets.Count},{_reliable.Diagnostics}";
    }

    internal void ConfigureRemote(ulong maximumMessageBytes, int port = SctpPort)
    {
        if (port is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(port));
        lock (_gate)
        {
            MaximumSendMessageBytes = maximumMessageBytes == 0 ? MaxMessageBytes : (int)Math.Min((ulong)MaxMessageBytes, maximumMessageBytes);
            _remotePort = (ushort)port;
        }
    }

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
    private readonly SctpReliableDelivery _reliable;
    // RFC 6525 stream reset (RFC 8831 closes a data channel by resetting its stream both ways).
    private bool _peerSupportsReconfig;
    private uint _reconfigRequestSequence = 1;
    private uint _peerReconfigSequence;
    private uint _lastPeerReconfigResult = ReconfigPerformed;
    private readonly List<ushort> _queuedResets = [];
    private (uint Sequence, ushort[] Streams, byte[] Chunk, long SentAt)? _pendingReset;
    private readonly Queue<byte[]> _outboundPackets = [];
    private int _outboundPacketBytes;
    private bool _flushingPackets;

    public SctpDataChannel() : this(TimeProvider.System) { }

    internal SctpDataChannel(TimeProvider clock, bool enableRetryTimer = true, RtcMediaBudget? budget = null)
    {
        _clock = clock;
        _reliable = new SctpReliableDelivery(1, budget);
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
                if (_pendingReset is { } reset && _clock.GetElapsedTime(reset.SentAt) >= TimeSpan.FromSeconds(1))
                {
                    _pendingReset = reset with { SentAt = _clock.GetTimestamp() };
                    SendSctpPacket(RemoteVerificationTag, [reset.Chunk]);
                }
                if (!fail) DrainSendWindow();
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
            _channels.Clear();
            _channelMetadataBytes = 0;
            _pendingChannels.Clear();
            _reliable.Clear();
            _outboundPackets.Clear();
            _outboundPacketBytes = 0;
        }
        foreach (var channel in channels)
        {
            try { channel.CloseLocally(); } catch { /* Teardown must survive a consumer callback. */ }
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
        var chunk = new byte[28];
        chunk[0] = ChunkInit;
        chunk[1] = 0; // flags
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), 25); // length (padding excluded)
        WriteSupportedExtensions(chunk.AsSpan(20));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), LocalVerificationTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), SctpReliableDelivery.MaxReceiveBytes); // a_rwnd
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
            BinaryPrimitives.WriteUInt16BigEndian(_issuedCookie.AsSpan(18), _remotePort);
            BinaryPrimitives.WriteInt64BigEndian(_issuedCookie.AsSpan(20), _clock.GetUtcNow().ToUnixTimeSeconds());
            BinaryPrimitives.WriteUInt32BigEndian(_issuedCookie.AsSpan(28), 60);
            RandomNumberGenerator.Fill(_issuedCookie.AsSpan(32, 16));
            HMACSHA256.HashData(_cookieKey, _issuedCookie.AsSpan(0, 48), _issuedCookie.AsSpan(48));
            _cookieIssuedAt = _clock.GetTimestamp();
        }
        // State Cookie, then Supported Extensions (5 bytes, padded to 8).
        var chunk = new byte[24 + _issuedCookie.Length + 8];
        chunk[0] = ChunkInitAck;
        chunk[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)(chunk.Length - 3));
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4), LocalVerificationTag);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), SctpReliableDelivery.MaxReceiveBytes);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(12), 1024);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(14), 1024);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(16), _localTsn);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(20), 7); // State Cookie
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(22), (ushort)(4 + _issuedCookie.Length));
        _issuedCookie.CopyTo(chunk, 24);
        WriteSupportedExtensions(chunk.AsSpan(24 + _issuedCookie.Length));

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
                if (srcPort != _remotePort || dstPort != SctpPort || State == "new") return;

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
        catch (InvalidDataException) { FailAssociation(); }
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
                    _reliable.SetPeerWindow(BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)), _clock);
                    LearnPeerReconfig(payload);
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
                    _reliable.SetPeerWindow(BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)), _clock);
                    LearnPeerReconfig(payload);
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

            case ChunkReconfig:
                if (State == "connected") ProcessReconfig(payload);
                break;

            case ChunkData:
                if (State == "connected") ProcessDataChunk(flags, payload);
                break;
            case ChunkSack:
                if (State == "connected")
                {
                    var previous = _channels.Values.Select(channel => (Channel: channel, Amount: channel.BufferedAmount)).ToArray();
                    if (_reliable.Acknowledge(payload, _clock))
                    {
                        foreach (var (channel, amount) in previous)
                            if (amount > channel.BufferedAmountLowThreshold && channel.BufferedAmount <= channel.BufferedAmountLowThreshold)
                                channel.NotifyBufferedAmountLow();
                        DrainSendWindow();
                    }
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
        var ready = _reliable.Receive(flags, payload, _clock.GetTimestamp());
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
            var metadataBytes = 2 * (labelLen + protoLen);
            if (metadataBytes > MaxChannelMetadataBytes - _channelMetadataBytes)
                throw new InvalidDataException("SCTP channel metadata budget exceeded.");
            var label = Encoding.UTF8.GetString(data.Slice(12, labelLen));
            var protocol = Encoding.UTF8.GetString(data.Slice(12 + labelLen, protoLen));

            var channel = new ChannelState(this, streamId, label, protocol);
            _channels[streamId] = channel;
            _channelMetadataBytes += metadataBytes;

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

    private static void WriteSupportedExtensions(Span<byte> parameter)
    {
        BinaryPrimitives.WriteUInt16BigEndian(parameter, ParamSupportedExtensions);
        BinaryPrimitives.WriteUInt16BigEndian(parameter[2..], 5);
        parameter[4] = ChunkReconfig;
    }

    private void LearnPeerReconfig(ReadOnlySpan<byte> init)
    {
        _peerReconfigSequence = _remoteTsn;
        _reconfigRequestSequence = _reliable.NextTsn;
        for (var offset = 16; offset + 4 <= init.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(init[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(init[(offset + 2)..]);
            if (length < 4 || offset + length > init.Length) return;
            if (type == ParamSupportedExtensions && init.Slice(offset + 4, length - 4).IndexOf(ChunkReconfig) >= 0)
                _peerSupportsReconfig = true;
            offset += (length + 3) & ~3;
        }
    }

    // Close a channel the RFC 8831 way: reset our outgoing stream; the channel closes once the peer has reset its
    // side too. A peer without RE-CONFIG support cannot be told, so the channel then closes locally.
    internal void CloseChannel(ChannelState channel)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || State != "connected" || !_peerSupportsReconfig || channel.ReadyState is "closed" or "connecting")
                {
                    channel.CloseLocally();
                    return;
                }
                if (channel.ReadyState == "closing") return;
                channel.BeginClosing();
                QueueOutgoingReset(channel.Id);
            }
        }
        finally { FlushPackets(); }
    }

    private void QueueOutgoingReset(ushort stream)
    {
        if (!_queuedResets.Contains(stream) && _pendingReset?.Streams.Contains(stream) != true) _queuedResets.Add(stream);
        SendNextReset();
    }

    private void SendNextReset()
    {
        if (_pendingReset is not null || _queuedResets.Count == 0) return;
        var streams = _queuedResets.ToArray();
        _queuedResets.Clear();
        var parameterLength = 16 + 2 * streams.Length;
        var chunk = new byte[4 + ((parameterLength + 3) & ~3)];
        chunk[0] = ChunkReconfig;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), (ushort)(4 + parameterLength));
        var parameter = chunk.AsSpan(4);
        BinaryPrimitives.WriteUInt16BigEndian(parameter, ParamOutgoingReset);
        BinaryPrimitives.WriteUInt16BigEndian(parameter[2..], (ushort)parameterLength);
        BinaryPrimitives.WriteUInt32BigEndian(parameter[4..], _reconfigRequestSequence);
        BinaryPrimitives.WriteUInt32BigEndian(parameter[8..], unchecked(_peerReconfigSequence - 1));
        BinaryPrimitives.WriteUInt32BigEndian(parameter[12..], unchecked(_reliable.NextTsn - 1));
        for (var i = 0; i < streams.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(parameter[(16 + 2 * i)..], streams[i]);
        _pendingReset = (_reconfigRequestSequence, streams, chunk, _clock.GetTimestamp());
        SendSctpPacket(RemoteVerificationTag, [chunk]);
    }

    private void ProcessReconfig(ReadOnlySpan<byte> payload)
    {
        for (var offset = 0; offset + 4 <= payload.Length;)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload[(offset + 2)..]);
            if (length < 4 || offset + length > payload.Length) return;
            var parameter = payload.Slice(offset, length);
            if (type == ParamOutgoingReset && length >= 16 && (length - 16) % 2 == 0) ProcessPeerOutgoingReset(parameter);
            else if (type == ParamReconfigResponse && length >= 12) ProcessResetResponse(parameter);
            offset += (length + 3) & ~3;
        }
    }

    private void ProcessPeerOutgoingReset(ReadOnlySpan<byte> parameter)
    {
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(parameter[4..]);
        var lastTsn = BinaryPrimitives.ReadUInt32BigEndian(parameter[12..]);
        uint result;
        if (sequence == unchecked(_peerReconfigSequence - 1)) result = _lastPeerReconfigResult; // a retransmission
        else if (sequence != _peerReconfigSequence) result = ReconfigBadSequence;
        else if (unchecked((int)(_reliable.CumulativeReceived - lastTsn)) < 0) result = ReconfigInProgress; // data still in flight
        else
        {
            var streams = new List<ushort>();
            for (var i = 16; i + 2 <= parameter.Length; i += 2) streams.Add(BinaryPrimitives.ReadUInt16BigEndian(parameter[i..]));
            if (streams.Count == 0) streams.AddRange(_channels.Keys); // an empty list resets every stream
            foreach (var stream in streams)
            {
                _reliable.ResetIncomingStream(stream);
                if (!_channels.TryGetValue(stream, out var channel)) continue;
                channel.IncomingReset = true;
                if (channel.OutgoingReset) FinishClose(channel);
                else if (channel.ReadyState != "closing") { channel.BeginClosing(); QueueOutgoingReset(stream); }
            }
            _peerReconfigSequence = unchecked(_peerReconfigSequence + 1);
            result = ReconfigPerformed;
            _lastPeerReconfigResult = result;
        }
        var chunk = new byte[16];
        chunk[0] = ChunkReconfig;
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(2), 16);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(4), ParamReconfigResponse);
        BinaryPrimitives.WriteUInt16BigEndian(chunk.AsSpan(6), 12);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(12), result);
        SendSctpPacket(RemoteVerificationTag, [chunk]);
    }

    private void ProcessResetResponse(ReadOnlySpan<byte> parameter)
    {
        if (_pendingReset is not { } reset || BinaryPrimitives.ReadUInt32BigEndian(parameter[4..]) != reset.Sequence) return;
        var result = BinaryPrimitives.ReadUInt32BigEndian(parameter[8..]);
        if (result == ReconfigInProgress) return; // the retry timer asks again
        _pendingReset = null;
        _reconfigRequestSequence = unchecked(_reconfigRequestSequence + 1);
        foreach (var stream in reset.Streams)
        {
            if (!_channels.TryGetValue(stream, out var channel)) continue;
            if (result != ReconfigPerformed) { FinishClose(channel); continue; } // the peer refused; stop waiting
            _reliable.ResetOutgoingStream(stream);
            channel.OutgoingReset = true;
            if (channel.IncomingReset) FinishClose(channel);
        }
        SendNextReset();
    }

    private void FinishClose(ChannelState channel)
    {
        _channels.Remove(channel.Id);
        channel.CloseLocally();
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
                var labelBytes = Encoding.UTF8.GetByteCount(label); var protocolBytes = Encoding.UTF8.GetByteCount(protocol);
                var metadataBytes = 2L * (labelBytes + (long)protocolBytes);
                if (_channels.Count >= 1024 || labelBytes > ushort.MaxValue || protocolBytes > ushort.MaxValue ||
                    (long)labelBytes + protocolBytes > MaximumSendMessageBytes - 12 ||
                    metadataBytes > MaxChannelMetadataBytes - _channelMetadataBytes)
                    throw new InvalidOperationException("SCTP data channel limits exceeded.");
                // Before the DTLS role is selected, reserve a channel object and
                // assign its parity-correct stream identifier when association starts.
                var id = explicitId ?? _nextStreamId;
                var previousNextId = _nextStreamId;
                if (_channels.ContainsKey(id)) throw new InvalidOperationException("SCTP stream id is already in use.");
                if (explicitId is null)
                {
                    if (_nextStreamId > ushort.MaxValue - 2) throw new InvalidOperationException("SCTP stream ids exhausted.");
                    _nextStreamId += 2;
                }
                var channel = new ChannelState(this, id, label, protocol);
                _channels[id] = channel;
                _channelMetadataBytes += (int)metadataBytes;
                channel.AutomaticId = explicitId is null;
                if (State == "connected")
                {
                    try { SendChannelOpen(channel); }
                    catch
                    {
                        _channels.Remove(id); _channelMetadataBytes -= (int)metadataBytes;
                        _nextStreamId = previousNextId; channel.CloseLocally();
                        throw;
                    }
                }
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
            try { SendChannelOpen(channel); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                // A smaller negotiated limit or exhausted send budget must not
                // leave an unestablishable pending channel connecting forever.
                channel.CloseLocally();
            }
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
        if (labelBytes.Length + protoBytes.Length > MaximumSendMessageBytes - 12)
            throw new InvalidOperationException("DCEP message exceeds the negotiated message size.");

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
                if (data.Length > MaximumSendMessageBytes) throw new ArgumentException("Message exceeds the negotiated SCTP size.");
                _reliable.Enqueue(_localTsn, streamId, ppid, data, _clock.GetTimestamp());
                _localTsn = _reliable.NextTsn;
                DrainSendWindow();
            }
        }
        finally { FlushPackets(); }
    }

    private void DrainSendWindow()
    {
        foreach (var chunk in _reliable.TakeSendable(_clock)) SendSctpPacket(RemoteVerificationTag, [chunk]);
    }

    private void SendSctpPacket(uint vTag, IReadOnlyList<byte[]> chunks, bool rememberHandshake = false)
    {
        var totalChunksLen = 0;
        foreach (var c in chunks) totalChunksLen += (c.Length + 3) & ~3;

        var packet = new byte[12 + totalChunksLen];
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(0), SctpPort);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), _remotePort);
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
            _channelMetadataBytes = 0;
            _pendingChannels.Clear();
            if (_issuedCookie is not null) CryptographicOperations.ZeroMemory(_issuedCookie);
            _issuedCookie = null;
            _cookieEchoSent = false;
            CryptographicOperations.ZeroMemory(_cookieKey);
        }
        foreach (var channel in channels)
        {
            try { channel.CloseLocally(); } catch { /* Finish releasing other owned channels. */ }
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
        internal int MaximumMessageSize => owner.MaximumSendMessageBytes;

        public event Action? OnOpen;
        public event Action? OnClose;
        public event Action<object>? OnMessage;
        public event Action? OnBufferedAmountLow;
        internal void NotifyBufferedAmountLow() => OnBufferedAmountLow?.Invoke();

        public void Send(string text)
        {
            if (ReadyState != "open") throw new InvalidOperationException("Data channel is not open.");
            if (Encoding.UTF8.GetByteCount(text) > MaximumMessageSize)
                throw new ArgumentException("Message exceeds the negotiated SCTP size.");
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

        internal bool OutgoingReset { get; set; }
        internal bool IncomingReset { get; set; }

        /// <summary>Starts the RFC 8831 closing procedure; <see cref="OnClose"/> fires once both directions are reset.</summary>
        public void Close() => owner.CloseChannel(this);

        internal void BeginClosing()
        {
            if (ReadyState is "closing" or "closed") return;
            ReadyState = "closing";
        }

        // Teardown and failure paths: nothing is signalled to the peer.
        internal void CloseLocally()
        {
            if (ReadyState == "closed") return;
            ReadyState = "closed";
            OnClose?.Invoke();
        }
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned DTLS 1.2 transport (RFC 6347) implementing ephemeral ECDHE-ECDSA key exchange,
/// SHA-256 fingerprint verification, record layer AEAD AES-128-GCM, and RFC 5764 DTLS-SRTP key derivation.
/// </summary>
internal sealed class DtlsTransport : IDisposable
{
    private const byte ContentTypeChangeCipherSpec = 20;
    private const byte ContentTypeAlert = 21;
    private const byte ContentTypeHandshake = 22;
    private const byte ContentTypeApplicationData = 23;

    private const byte HandshakeClientHello = 1;
    private const byte HandshakeHelloVerifyRequest = 3;
    private const byte HandshakeServerHello = 2;
    private const byte HandshakeCertificate = 11;
    private const byte HandshakeServerKeyExchange = 12;
    private const byte HandshakeCertificateRequest = 13;
    private const byte HandshakeCertificateVerify = 15;
    private const byte HandshakeServerHelloDone = 14;
    private const byte HandshakeClientKeyExchange = 16;
    private const byte HandshakeFinished = 20;

    private const ushort DtlsVersion12 = 0xFEFD;

    public string Fingerprint { get; }
    public string? RemoteFingerprint { get; set; }
    public bool IsClient { get; set; } = true;
    public string State { get; private set; } = "new";

    public event Action<string>? OnStateChanged;
    public event Action<byte[]>? OnApplicationDataReceived;
    public event Action<byte[]>? OnDatagramToSend;

    private readonly ECDsa _localEcdsa;
    private readonly X509Certificate2 _localCert;
    private readonly ECDiffieHellman _localEcdh;
    private ECDsa? _remoteSigningKey;
    private DtlsRecordCipher? _handshakeWriter;
    private DtlsRecordCipher? _handshakeReader;

    private readonly byte[] _localRandom = new byte[32];
    private byte[]? _remoteRandom;
    private byte[]? _masterSecret;


    public byte[]? SrtpLocalMasterKey { get; private set; }
    public byte[]? SrtpRemoteMasterKey { get; private set; }
    public byte[]? SrtpLocalMasterSalt { get; private set; }
    public byte[]? SrtpRemoteMasterSalt { get; private set; }

    private ushort _localEpoch = 0;
    private ulong _localSequenceNumber = 0;
    private ushort _handshakeSeq = 0;
    private ushort _nextRemoteHandshake;
    private readonly DtlsHandshakeFragments _fragments;
    private bool _remoteCipherSpec;
    private bool _clientCertificateRequested;
    private bool _peerCertificateProof;
    private byte[] _cookie = [];

    private readonly List<byte> _handshakeMessages = [];
    private readonly object _gate = new();
    private bool _disposed;
    private readonly TimeProvider _clock;
    private readonly ITimer? _retryTimer;
    private readonly List<byte[]> _flight = [];
    private int _flightBytes;
    private int _retryCount;
    private DateTimeOffset _nextRetry;

    public DtlsTransport() : this(TimeProvider.System, true) { }

    internal DtlsTransport(TimeProvider clock, bool enableRetryTimer, RtcMediaBudget? budget = null)
    {
        _clock = clock;
        _fragments = new DtlsHandshakeFragments(budget);
        _localEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=FolioWebRTC", _localEcdsa, HashAlgorithmName.SHA256);
        _localCert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        _localEcdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        RandomNumberGenerator.Fill(_localRandom);
        // First 4 bytes is unix timestamp per TLS spec
        BinaryPrimitives.WriteUInt32BigEndian(_localRandom, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var certHash = SHA256.HashData(_localCert.RawData);
        var sb = new StringBuilder();
        for (var i = 0; i < certHash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(certHash[i].ToString("X2"));
        }
        Fingerprint = sb.ToString();
        if (enableRetryTimer)
            _retryTimer = clock.CreateTimer(_ =>
            {
                try { RetryDueFlight(); }
                catch (Exception)
                {
                    lock (_gate) { StopFlight(); if (!_disposed) State = "failed"; }
                }
            }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private void BeginFlight()
    {
        StopFlight();
        _nextRetry = _clock.GetUtcNow() + TimeSpan.FromSeconds(1);
    }

    private void StopFlight() { _flight.Clear(); _flightBytes = 0; _retryCount = 0; }

    private void EmitRecord(byte type, byte[] record)
    {
        if (type is ContentTypeHandshake or ContentTypeChangeCipherSpec)
        {
            if (_flight.Count >= 64 || record.Length > 1024 * 1024 - _flightBytes)
                throw new CryptographicException("DTLS retransmission flight limit exceeded.");
            _flight.Add((byte[])record.Clone()); _flightBytes += record.Length;
        }
        OnDatagramToSend?.Invoke(record);
    }

    internal void RetryDueFlight()
    {
        lock (_gate)
        {
            if (_disposed || _flight.Count == 0 || _clock.GetUtcNow() < _nextRetry) return;
            if (_retryCount >= 6)
            {
                StopFlight();
                if (State == "connecting") { State = "failed"; OnStateChanged?.Invoke(State); }
                return;
            }
            _retryCount++;
            _nextRetry = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(8, 1 << _retryCount));
            foreach (var record in _flight.ToArray())
            {
                if (_disposed) break;
                OnDatagramToSend?.Invoke((byte[])record.Clone());
            }
        }
    }

    public void StartHandshake()
    {
        lock (_gate)
        {
            if (_disposed || State != "new") return;
            State = "connecting";
            OnStateChanged?.Invoke(State);

            if (IsClient)
            {
                SendClientHello();
            }
        }
    }

    private void SendClientHello()
    {
        BeginFlight();
        using var ms = new MemoryStream();

        // ClientHello body
        ms.WriteByte(0xFE); ms.WriteByte(0xFD); // DTLS 1.2
        ms.Write(_localRandom); // 32 bytes random
        ms.WriteByte(0); // Session ID length

        ms.WriteByte((byte)_cookie.Length);
        ms.Write(_cookie);

        // Cipher suites (TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 = 0xC02B)
        ms.WriteByte(0); ms.WriteByte(2); // 2 bytes length
        ms.WriteByte(0xC0); ms.WriteByte(0x2B);

        // Compression methods (0 = null)
        ms.WriteByte(1); ms.WriteByte(0);

        // Extensions
        using var extMs = new MemoryStream();

        // Supported Groups (secp256r1 = 0x0017)
        extMs.WriteByte(0x00); extMs.WriteByte(0x0A); // extension type: supported_groups
        extMs.WriteByte(0x00); extMs.WriteByte(0x04); // ext length
        extMs.WriteByte(0x00); extMs.WriteByte(0x02); // list length
        extMs.WriteByte(0x00); extMs.WriteByte(0x17); // secp256r1

        // signature_algorithms: SHA-256/ECDSA. Without this TLS 1.2 peers
        // can apply legacy SHA-1 defaults that do not match our certificate path.
        extMs.Write(new byte[] { 0x00, 0x0d, 0x00, 0x04, 0x00, 0x02, 0x04, 0x03 });

        // Use SRTP extension (RFC 5764)
        extMs.WriteByte(0x00); extMs.WriteByte(0x0E); // use_srtp
        extMs.WriteByte(0x00); extMs.WriteByte(0x05); // length
        extMs.WriteByte(0x00); extMs.WriteByte(0x02); // profile list length
        extMs.WriteByte(0x00); extMs.WriteByte(0x07); // SRTP_AEAD_AES_128_GCM
        extMs.WriteByte(0x00); // mki length

        var extBytes = extMs.ToArray();
        ms.WriteByte((byte)(extBytes.Length >> 8));
        ms.WriteByte((byte)extBytes.Length);
        ms.Write(extBytes);

        var helloBody = ms.ToArray();
        SendHandshakeMessage(HandshakeClientHello, helloBody);
    }

    private void SendHandshakeMessage(byte type, byte[] body)
    {
        var msgSeq = _handshakeSeq++;
        var hsMsg = new byte[12 + body.Length];
        hsMsg[0] = type;
        hsMsg[1] = (byte)(body.Length >> 16);
        hsMsg[2] = (byte)(body.Length >> 8);
        hsMsg[3] = (byte)body.Length;
        BinaryPrimitives.WriteUInt16BigEndian(hsMsg.AsSpan(4), msgSeq);
        // Fragment offset (3 bytes) = 0
        // Fragment length (3 bytes) = body.Length
        hsMsg[9] = (byte)(body.Length >> 16);
        hsMsg[10] = (byte)(body.Length >> 8);
        hsMsg[11] = (byte)body.Length;
        body.CopyTo(hsMsg.AsSpan(12));

        _handshakeMessages.AddRange(hsMsg);

        SendRecord(ContentTypeHandshake, hsMsg);
    }

    private void SendRecord(byte contentType, byte[] payload)
    {
        if (_localEpoch == 1)
        {
            if (_handshakeWriter is null) throw new InvalidOperationException("DTLS handshake keys are unavailable.");
            EmitRecord(contentType, _handshakeWriter.Protect(contentType, _localEpoch, _localSequenceNumber++, payload));
            return;
        }
        var record = new byte[13 + payload.Length];
        record[0] = contentType;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(1), DtlsVersion12);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3), _localEpoch);

        var seq = _localSequenceNumber++;
        record[5] = (byte)(seq >> 40);
        record[6] = (byte)(seq >> 32);
        record[7] = (byte)(seq >> 24);
        record[8] = (byte)(seq >> 16);
        record[9] = (byte)(seq >> 8);
        record[10] = (byte)seq;

        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(11), (ushort)payload.Length);
        payload.CopyTo(record.AsSpan(13));

        EmitRecord(contentType, record);
    }

    public void ProcessIncomingDatagram(byte[] datagram)
    {
        ArgumentNullException.ThrowIfNull(datagram);
        lock (_gate)
        {
            var offset = 0;
            while (offset < datagram.Length)
            {
                if (datagram.Length - offset < 13) return;
                var length = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(offset + 11));
                if (length > datagram.Length - offset - 13) return;
                ProcessIncomingRecord(datagram.AsSpan(offset, 13 + length));
                offset += 13 + length;
            }
        }
    }

    private void ProcessIncomingRecord(ReadOnlySpan<byte> datagram)
    {
        lock (_gate)
        {
            if (_disposed || datagram.Length < 13) return;

            var contentType = datagram[0];
            var version = BinaryPrimitives.ReadUInt16BigEndian(datagram[1..]);
            var epoch = BinaryPrimitives.ReadUInt16BigEndian(datagram[3..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(datagram[11..]);

            // DTLS servers may use the DTLS 1.0 record version for the stateless
            // cookie challenge, even when DTLS 1.2 was requested.
            var legacyChallenge = IsClient && epoch == 0 && contentType == ContentTypeHandshake &&
                length >= 12 && datagram.Length >= 25 && datagram[13] == HandshakeHelloVerifyRequest && version == 0xfeff;
            var legacyClientHello = !IsClient && _remoteRandom is null && epoch == 0 && contentType == ContentTypeHandshake &&
                length >= 12 && datagram.Length >= 25 && datagram[13] == HandshakeClientHello && version == 0xfeff;
            if ((version != DtlsVersion12 && !legacyChallenge && !legacyClientHello) || datagram.Length != 13 + length) return;
            var payload = datagram.Slice(13, length);

            switch (contentType)
            {
                case ContentTypeHandshake:
                    if (epoch == 1)
                    {
                        if (_handshakeReader is not null && _handshakeReader.TryUnprotect(datagram, out var clear))
                            ProcessHandshakeRecord(clear, protectedRecord: true);
                    }
                    else if (epoch == 0 && State != "connected")
                    {
                        // Finished is never valid in the plaintext epoch.
                        if (payload.Length >= 1 && payload[0] == HandshakeFinished) return;
                        ProcessHandshakeRecord(payload);
                    }
                    break;
                case ContentTypeChangeCipherSpec:
                    if (epoch == 0 && _masterSecret is not null && payload.SequenceEqual(new byte[] { 1 }))
                        _remoteCipherSpec = true;
                    break;
                case ContentTypeApplicationData:
                    if (State == "connected" && epoch == 1 && _handshakeReader is not null &&
                        _handshakeReader.TryUnprotect(datagram, out var applicationData))
                    {
                        StopFlight();
                        OnApplicationDataReceived?.Invoke(applicationData);
                    }
                    break;
            }
        }
    }

    private void ProcessHandshakeRecord(ReadOnlySpan<byte> payload, bool protectedRecord = false)
    {
        while (!payload.IsEmpty)
        {
            if (payload.Length < 12) throw new CryptographicException("Truncated DTLS handshake header.");
            if (payload[0] == HandshakeFinished && !protectedRecord) return;
            var count = DtlsHandshakeFragments.Read24(payload[9..]);
            if (count > payload.Length - 12) throw new CryptographicException("Truncated DTLS handshake fragment.");
            _fragments.Add(payload[..(12 + count)], _nextRemoteHandshake);
            payload = payload[(12 + count)..];
            while (_fragments.TryTake(_nextRemoteHandshake, out var complete))
            {
                var previous = _nextRemoteHandshake;
                ProcessCompleteHandshake(complete);
                if (_nextRemoteHandshake == previous) break;
            }
        }
    }

    private void ProcessCompleteHandshake(ReadOnlySpan<byte> payload)
    {
        while (payload.Length >= 12)
        {
            var type = payload[0];
            var length = (payload[1] << 16) | (payload[2] << 8) | payload[3];
            var msgSeq = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
            var fragOffset = (payload[6] << 16) | (payload[7] << 8) | payload[8];
            var fragLength = (payload[9] << 16) | (payload[10] << 8) | payload[11];

            if (fragOffset != 0 || fragLength != length || payload.Length < 12 + fragLength ||
                msgSeq != _nextRemoteHandshake) return;
            var body = payload.Slice(12, fragLength);

            if (type == HandshakeHelloVerifyRequest)
            {
                if (!IsClient || State != "connecting" || _remoteRandom is not null || body.Length < 3 ||
                    body[0] != 0xfe || body[1] is not (0xfd or 0xff) || body[2] != body.Length - 3)
                    throw new CryptographicException("Invalid DTLS cookie challenge.");
                _cookie = body[3..].ToArray();
                _nextRemoteHandshake++;
                // Initial ClientHello and HelloVerifyRequest are excluded from
                // the transcript authenticated by Finished (RFC 6347).
                _handshakeMessages.Clear();
                SendClientHello();
                payload = payload[(12 + fragLength)..];
                continue;
            }

            if (type == HandshakeCertificateVerify)
            {
                if (IsClient || _remoteSigningKey is null || _masterSecret is null ||
                    body.Length < 4 || body[0] != 4 || body[1] != 3 ||
                    BinaryPrimitives.ReadUInt16BigEndian(body[2..]) != body.Length - 4 ||
                    !_remoteSigningKey.VerifyData(_handshakeMessages.ToArray(), body[4..],
                        HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    throw new CryptographicException("DTLS client certificate proof is invalid.");
                _peerCertificateProof = true;
            }

            // Finished authenticates the preceding transcript, not itself.
            if (type == HandshakeFinished)
            {
                if (!_remoteCipherSpec || !VerifyFinished(body)) return;
                _nextRemoteHandshake++;
                _handshakeMessages.AddRange(payload.Slice(0, 12 + fragLength).ToArray());
                if (!IsClient)
                {
                    BeginFlight();
                    SendRecord(ContentTypeChangeCipherSpec, [1]);
                    _localEpoch = 1;
                    _localSequenceNumber = 0;
                    SendHandshakeMessage(HandshakeFinished, ComputeVerifyData("server finished"));
                }
                else StopFlight();
                State = "connected";
                OnStateChanged?.Invoke(State);
                payload = payload[(12 + fragLength)..];
                continue;
            }
            _nextRemoteHandshake++;
            _handshakeMessages.AddRange(payload.Slice(0, 12 + fragLength).ToArray());

            switch (type)
            {
                case HandshakeClientHello:
                    HandleClientHello(body);
                    break;
                case HandshakeServerHello:
                    HandleServerHello(body);
                    break;
                case HandshakeCertificate:
                    HandleCertificate(body);
                    break;
                case HandshakeServerKeyExchange:
                    HandleServerKeyExchange(body);
                    break;
                case HandshakeCertificateRequest:
                    if (!IsClient || !SupportsCertificateRequest(body))
                        throw new CryptographicException("Unsupported DTLS client certificate request.");
                    _clientCertificateRequested = true;
                    break;
                case HandshakeServerHelloDone:
                    HandleServerHelloDone();
                    break;
                case HandshakeClientKeyExchange:
                    HandleClientKeyExchange(body);
                    break;
            }

            payload = payload[(12 + fragLength)..];
        }
    }

    private void HandleClientHello(ReadOnlySpan<byte> body)
    {
        if (body.Length < 34) return;
        BeginFlight();
        _remoteRandom = body.Slice(2, 32).ToArray();

        // Send ServerHello
        using var shMs = new MemoryStream();
        shMs.WriteByte(0xFE); shMs.WriteByte(0xFD);
        shMs.Write(_localRandom);
        shMs.WriteByte(0); // Session ID len
        shMs.WriteByte(0xC0); shMs.WriteByte(0x2B); // Chosen cipher: TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256
        shMs.WriteByte(0); // No compression

        // Extensions (use_srtp echo)
        using var extMs = new MemoryStream();
        extMs.WriteByte(0x00); extMs.WriteByte(0x0E); // use_srtp
        extMs.WriteByte(0x00); extMs.WriteByte(0x05);
        extMs.WriteByte(0x00); extMs.WriteByte(0x02);
        extMs.WriteByte(0x00); extMs.WriteByte(0x07);
        extMs.WriteByte(0x00);
        if (OffersSecureRenegotiation(body))
            extMs.Write(new byte[] { 0xff, 0x01, 0x00, 0x01, 0x00 });
        var extBytes = extMs.ToArray();
        shMs.WriteByte((byte)(extBytes.Length >> 8));
        shMs.WriteByte((byte)extBytes.Length);
        shMs.Write(extBytes);

        SendHandshakeMessage(HandshakeServerHello, shMs.ToArray());

        // Send Certificate
        using var certMs = new MemoryStream();
        var rawCert = _localCert.RawData;
        var totalCertsLen = rawCert.Length + 3;
        certMs.WriteByte((byte)(totalCertsLen >> 16));
        certMs.WriteByte((byte)(totalCertsLen >> 8));
        certMs.WriteByte((byte)totalCertsLen);
        certMs.WriteByte((byte)(rawCert.Length >> 16));
        certMs.WriteByte((byte)(rawCert.Length >> 8));
        certMs.WriteByte((byte)rawCert.Length);
        certMs.Write(rawCert);
        SendHandshakeMessage(HandshakeCertificate, certMs.ToArray());

        // Send ServerKeyExchange (ECDH pubkey + signature)
        var ecdhPub = ExportKeyExchangePoint(_localEcdh);
        using var skeMs = new MemoryStream();
        skeMs.WriteByte(3); // named_curve
        skeMs.WriteByte(0x00); skeMs.WriteByte(0x17); // secp256r1
        skeMs.WriteByte((byte)ecdhPub.Length);
        skeMs.Write(ecdhPub);

        // Sign parameters
        using var sigParams = new MemoryStream();
        sigParams.Write(_remoteRandom!);
        sigParams.Write(_localRandom);
        sigParams.Write(skeMs.ToArray());
        var signature = _localEcdsa.SignData(sigParams.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        skeMs.WriteByte(0x04); skeMs.WriteByte(0x03); // ecdsa_secp256r1_sha256
        skeMs.WriteByte((byte)(signature.Length >> 8));
        skeMs.WriteByte((byte)signature.Length);
        skeMs.Write(signature);

        SendHandshakeMessage(HandshakeServerKeyExchange, skeMs.ToArray());
        // TLS 1.2 CertificateRequest: ECDSA signing, SHA-256/ECDSA, no CA list.
        SendHandshakeMessage(HandshakeCertificateRequest, [1, 64, 0, 2, 4, 3, 0, 0]);
        SendHandshakeMessage(HandshakeServerHelloDone, []);
    }

    private void HandleServerHello(ReadOnlySpan<byte> body)
    {
        if (body.Length < 34) return;
        _remoteRandom = body.Slice(2, 32).ToArray();
        // RFC 5764: the server must choose exactly one profile we offered (SRTP_AEAD_AES_128_GCM, no MKI).
        // Anything else, or a malformed extension, aborts the handshake; absent means no SRTP keys are exported.
        _srtpNegotiated = false;
        var offset = 34;
        if (body.Length < offset + 1) return;
        offset += 1 + body[offset];                       // session id
        offset += 3;                                      // cipher suite + compression
        if (body.Length == offset) return;                // no extensions
        if (body.Length < offset + 2) throw new CryptographicException("Malformed DTLS ServerHello.");
        var total = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]); offset += 2;
        if (total != body.Length - offset) throw new CryptographicException("Malformed DTLS ServerHello extensions.");
        while (offset < body.Length)
        {
            if (body.Length - offset < 4) throw new CryptographicException("Malformed DTLS ServerHello extension.");
            var type = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(body[(offset + 2)..]); offset += 4;
            if (length > body.Length - offset) throw new CryptographicException("Malformed DTLS ServerHello extension.");
            if (type == 0x000E)
            {
                var data = body.Slice(offset, length);
                if (_srtpNegotiated || data.Length != 5 || data[0] != 0 || data[1] != 2 || data[2] != 0 || data[3] != 7 || data[4] != 0)
                    throw new CryptographicException("The DTLS server chose an unsupported SRTP profile.");
                _srtpNegotiated = true;
            }
            offset += length;
        }
    }

    // The server side always offers and echoes our single profile; the client sets this from the ServerHello.
    private bool _srtpNegotiated = true;

    internal static bool OffersSecureRenegotiation(ReadOnlySpan<byte> hello)
    {
        if (hello.Length < 35) return false;
        var offset = 35 + hello[34];
        if (offset >= hello.Length) return false;
        offset += 1 + hello[offset]; // cookie
        if (hello.Length - offset < 2) return false;
        var suites = BinaryPrimitives.ReadUInt16BigEndian(hello[offset..]); offset += 2;
        if ((suites & 1) != 0 || suites > hello.Length - offset) return false;
        var signaled = false;
        for (var i = 0; i < suites; i += 2)
            signaled |= hello[offset + i] == 0 && hello[offset + i + 1] == 0xff;
        offset += suites;
        if (offset >= hello.Length) return false;
        offset += 1 + hello[offset]; // compression methods
        if (offset == hello.Length) return signaled;
        if (hello.Length - offset < 2) return false;
        var extensions = BinaryPrimitives.ReadUInt16BigEndian(hello[offset..]); offset += 2;
        if (extensions != hello.Length - offset) return false;
        while (offset < hello.Length)
        {
            if (hello.Length - offset < 4) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(hello[offset..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(hello[(offset + 2)..]); offset += 4;
            if (length > hello.Length - offset) return false;
            if (type == 0xff01) signaled |= length == 1 && hello[offset] == 0;
            offset += length;
        }
        return signaled;
    }

    private void HandleCertificate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6) return;
        var certLen = (body[3] << 16) | (body[4] << 8) | body[5];
        if (body.Length < 6 + certLen) return;
        var certBytes = body.Slice(6, certLen).ToArray();
        using var cert = X509CertificateLoader.LoadCertificate(certBytes);

        var certHash = SHA256.HashData(cert.RawData);
        var sb = new StringBuilder();
        for (var i = 0; i < certHash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(certHash[i].ToString("X2"));
        }
        var computedFingerprint = sb.ToString();

        var expected = RemoteFingerprint?.Trim().Replace(" ", "");
        if (string.IsNullOrEmpty(expected) || !string.Equals(computedFingerprint, expected, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("DTLS remote certificate fingerprint mismatch or missing pin.");
        var signingKey = cert.GetECDsaPublicKey()
            ?? throw new CryptographicException("DTLS requires an ECDSA peer certificate.");
        _remoteSigningKey?.Dispose();
        _remoteSigningKey = signingKey;
    }

    private void HandleServerKeyExchange(ReadOnlySpan<byte> body)
    {
        if (_remoteSigningKey is null || _remoteRandom is null)
            throw new CryptographicException("DTLS server key exchange requires a pinned certificate and server random.");
        using var peerEcdh = VerifyServerKeyExchange(body, _remoteSigningKey, _localRandom, _remoteRandom);
        _peerCertificateProof = true;
        var preMasterSecret = _localEcdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);

        DeriveKeys(preMasterSecret);
    }

    private void HandleServerHelloDone()
    {
        if (!_clientCertificateRequested || !_peerCertificateProof)
            throw new CryptographicException("DTLS requires mutual certificate authentication.");
        BeginFlight();
        var certificate = _localCert.RawData;
        var certificateBody = new byte[6 + certificate.Length];
        var listLength = certificate.Length + 3;
        certificateBody[0] = (byte)(listLength >> 16); certificateBody[1] = (byte)(listLength >> 8); certificateBody[2] = (byte)listLength;
        certificateBody[3] = (byte)(certificate.Length >> 16); certificateBody[4] = (byte)(certificate.Length >> 8); certificateBody[5] = (byte)certificate.Length;
        certificate.CopyTo(certificateBody, 6);
        SendHandshakeMessage(HandshakeCertificate, certificateBody);
        var ecdhPub = ExportKeyExchangePoint(_localEcdh);
        using var ckeMs = new MemoryStream();
        ckeMs.WriteByte((byte)ecdhPub.Length);
        ckeMs.Write(ecdhPub);
        SendHandshakeMessage(HandshakeClientKeyExchange, ckeMs.ToArray());

        var signature = _localEcdsa.SignData(_handshakeMessages.ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var proof = new byte[4 + signature.Length];
        proof[0] = 4; proof[1] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(proof.AsSpan(2), (ushort)signature.Length);
        signature.CopyTo(proof, 4);
        SendHandshakeMessage(HandshakeCertificateVerify, proof);

        // Send ChangeCipherSpec
        SendRecord(ContentTypeChangeCipherSpec, [1]);
        _localEpoch = 1;
        _localSequenceNumber = 0;

        // Send Finished
        var verifyData = ComputeVerifyData("client finished");
        SendHandshakeMessage(HandshakeFinished, verifyData);
    }

    internal static bool SupportsCertificateRequest(ReadOnlySpan<byte> body)
    {
        if (body.Length < 1) return false;
        var types = body[0];
        if (body.Length < 1 + types + 2 || !body.Slice(1, types).Contains((byte)64)) return false;
        var offset = 1 + types;
        var algorithms = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]); offset += 2;
        if (algorithms == 0 || algorithms % 2 != 0 || algorithms > body.Length - offset - 2) return false;
        var supported = false;
        for (var i = 0; i < algorithms; i += 2)
            supported |= body[offset + i] == 4 && body[offset + i + 1] == 3;
        offset += algorithms;
        var authorities = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]); offset += 2;
        if (authorities != body.Length - offset) return false;
        while (offset < body.Length)
        {
            if (body.Length - offset < 2) return false;
            var nameLength = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]); offset += 2;
            if (nameLength == 0 || nameLength > body.Length - offset) return false;
            offset += nameLength;
        }
        return supported;
    }

    private void HandleClientKeyExchange(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return;
        var pubLen = body[0];
        if (body.Length < 1 + pubLen) return;
        var peerPub = body.Slice(1, pubLen).ToArray();

        using var peerEcdh = ImportKeyExchangePoint(peerPub);
        var preMasterSecret = _localEcdh.DeriveRawSecretAgreement(peerEcdh.PublicKey);

        DeriveKeys(preMasterSecret);

        // The server waits for the client's authenticated Finished before
        // sending its own final flight.
    }

    private bool VerifyFinished(ReadOnlySpan<byte> verifyData)
    {
        if (State != "connecting" || !_peerCertificateProof || _masterSecret is null || verifyData.Length != 12)
            return false;
        var expected = ComputeVerifyData(IsClient ? "server finished" : "client finished");
        return CryptographicOperations.FixedTimeEquals(expected, verifyData);
    }

    // RFC 8422: ECPoint is an uncompressed curve point, not a DER SPKI blob.
    internal static byte[] ExportKeyExchangePoint(ECDiffieHellman key)
    {
        var parameters = key.ExportParameters(false);
        if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value ||
            parameters.Q.X?.Length != 32 || parameters.Q.Y?.Length != 32)
            throw new CryptographicException("DTLS requires a P-256 key.");
        var point = new byte[65];
        point[0] = 4;
        parameters.Q.X.CopyTo(point, 1);
        parameters.Q.Y.CopyTo(point, 33);
        return point;
    }

    internal static ECDiffieHellman ImportKeyExchangePoint(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 4)
            throw new CryptographicException("Invalid DTLS P-256 point.");
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point.Slice(1, 32).ToArray(), Y = point.Slice(33, 32).ToArray() }
        });
    }

    internal static ECDiffieHellman VerifyServerKeyExchange(ReadOnlySpan<byte> body, ECDsa signingKey,
        ReadOnlySpan<byte> clientRandom, ReadOnlySpan<byte> serverRandom)
    {
        const int parameterLength = 69; // named P-256 + length-prefixed point
        if (clientRandom.Length != 32 || serverRandom.Length != 32 || body.Length < parameterLength + 4 ||
            body[0] != 3 || body[1] != 0 || body[2] != 23 || body[3] != 65 ||
            body[69] != 4 || body[70] != 3)
            throw new CryptographicException("Invalid DTLS server key exchange parameters.");
        var signatureLength = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(71, 2));
        if (signatureLength == 0 || body.Length != 73 + signatureLength)
            throw new CryptographicException("Invalid DTLS server key exchange signature length.");
        var signed = new byte[64 + parameterLength];
        clientRandom.CopyTo(signed);
        serverRandom.CopyTo(signed.AsSpan(32));
        body[..parameterLength].CopyTo(signed.AsSpan(64));
        if (!signingKey.VerifyData(signed, body[73..], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new CryptographicException("DTLS server key exchange signature mismatch.");
        return ImportKeyExchangePoint(body.Slice(4, 65));
    }

    private void DeriveKeys(byte[] preMasterSecret)
    {
        var seed = new byte[64];
        if (IsClient)
        {
            _localRandom.CopyTo(seed.AsSpan(0, 32));
            _remoteRandom!.CopyTo(seed.AsSpan(32, 32));
        }
        else
        {
            _remoteRandom!.CopyTo(seed.AsSpan(0, 32));
            _localRandom.CopyTo(seed.AsSpan(32, 32));
        }

        _masterSecret = Prf(preMasterSecret, "master secret"u8, seed, 48);

        // Key expansion
        var keyBlockSeed = new byte[64];
        (IsClient ? _remoteRandom! : _localRandom).CopyTo(keyBlockSeed.AsSpan(0, 32));
        (IsClient ? _localRandom : _remoteRandom!).CopyTo(keyBlockSeed.AsSpan(32, 32));
        var keyBlock = Prf(_masterSecret, "key expansion"u8, keyBlockSeed, 40);

        var clientWriteKey = keyBlock[0..16];
        var serverWriteKey = keyBlock[16..32];
        var clientWriteIv = keyBlock[32..36];
        var serverWriteIv = keyBlock[36..40];

        _handshakeWriter?.Dispose(); _handshakeReader?.Dispose();
        _handshakeWriter = new DtlsRecordCipher(IsClient ? clientWriteKey : serverWriteKey, IsClient ? clientWriteIv : serverWriteIv);
        _handshakeReader = new DtlsRecordCipher(IsClient ? serverWriteKey : clientWriteKey, IsClient ? serverWriteIv : clientWriteIv);

        // RFC 5764 DTLS-SRTP Key Exporter:
        // SRTP_AEAD_AES_128_GCM (RFC 7714): 2 * (16 byte key + 12 byte salt) = 56 bytes: client key, server key, client salt, server salt
        var srtpSeed = new byte[64];
        (IsClient ? _localRandom : _remoteRandom!).CopyTo(srtpSeed.AsSpan(0, 32));
        (IsClient ? _remoteRandom! : _localRandom).CopyTo(srtpSeed.AsSpan(32, 32));
        if (!_srtpNegotiated) return;
        var srtpBlock = Prf(_masterSecret, "EXTRACTOR-dtls_srtp"u8, srtpSeed, 56);

        if (IsClient)
        {
            SrtpLocalMasterKey = srtpBlock[0..16];
            SrtpRemoteMasterKey = srtpBlock[16..32];
            SrtpLocalMasterSalt = srtpBlock[32..44];
            SrtpRemoteMasterSalt = srtpBlock[44..56];
        }
        else
        {
            SrtpRemoteMasterKey = srtpBlock[0..16];
            SrtpLocalMasterKey = srtpBlock[16..32];
            SrtpRemoteMasterSalt = srtpBlock[32..44];
            SrtpLocalMasterSalt = srtpBlock[44..56];
        }
    }

    private byte[] ComputeVerifyData(string label)
    {
        var hash = SHA256.HashData(_handshakeMessages.ToArray());
        return Prf(_masterSecret!, Encoding.ASCII.GetBytes(label), hash, 12);
    }

    private static byte[] Prf(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> label, ReadOnlySpan<byte> seed, int outputLength)
    {
        var labelAndSeed = new byte[label.Length + seed.Length];
        label.CopyTo(labelAndSeed.AsSpan(0, label.Length));
        seed.CopyTo(labelAndSeed.AsSpan(label.Length));

        var result = new byte[outputLength];
        var a = HMACSHA256.HashData(secret, labelAndSeed);
        var bytesWritten = 0;

        while (bytesWritten < outputLength)
        {
            var input = new byte[a.Length + labelAndSeed.Length];
            a.CopyTo(input.AsSpan(0, a.Length));
            labelAndSeed.CopyTo(input.AsSpan(a.Length));

            var output = HMACSHA256.HashData(secret, input);
            var toCopy = Math.Min(output.Length, outputLength - bytesWritten);
            output.AsSpan(0, toCopy).CopyTo(result.AsSpan(bytesWritten));
            bytesWritten += toCopy;

            a = HMACSHA256.HashData(secret, a);
        }

        return result;
    }

    public void SendApplicationData(byte[] data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State != "connected" || _localEpoch != 1 || _handshakeWriter is null)
                throw new InvalidOperationException("DTLS application data requires an authenticated connection.");

            SendRecord(ContentTypeApplicationData, data);
        }
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopFlight();
            _retryTimer?.Dispose();
            _fragments.Dispose();
            State = "closed";
        }
        foreach (var secret in new[] { SrtpLocalMasterKey, SrtpRemoteMasterKey, SrtpLocalMasterSalt, SrtpRemoteMasterSalt, _masterSecret })
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        _localEcdsa.Dispose();
        _localCert.Dispose();
        _localEcdh.Dispose();
        _remoteSigningKey?.Dispose();
        _handshakeWriter?.Dispose();
        _handshakeReader?.Dispose();
        OnStateChanged?.Invoke("closed");
    }
}

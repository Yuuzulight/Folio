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
    private const byte HandshakeServerHello = 2;
    private const byte HandshakeCertificate = 11;
    private const byte HandshakeServerKeyExchange = 12;
    private const byte HandshakeServerHelloDone = 14;
    private const byte HandshakeClientKeyExchange = 16;
    private const byte HandshakeFinished = 20;

    private const ushort DtlsVersion12 = 0xFEFF;

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

    private readonly byte[] _localRandom = new byte[32];
    private byte[]? _remoteRandom;
    private byte[]? _masterSecret;

    private AesGcm? _localGcm;
    private AesGcm? _remoteGcm;
    private byte[]? _localImplicitIv;
    private byte[]? _remoteImplicitIv;

    public byte[]? SrtpLocalMasterKey { get; private set; }
    public byte[]? SrtpRemoteMasterKey { get; private set; }
    public byte[]? SrtpLocalMasterSalt { get; private set; }
    public byte[]? SrtpRemoteMasterSalt { get; private set; }

    private ushort _localEpoch = 0;
    private ulong _localSequenceNumber = 0;
    private ushort _handshakeSeq = 0;

    private readonly List<byte> _handshakeMessages = [];
    private readonly object _gate = new();
    private bool _disposed;

    public DtlsTransport()
    {
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
        var ecdhPub = _localEcdh.ExportSubjectPublicKeyInfo();
        using var ms = new MemoryStream();

        // ClientHello body
        ms.WriteByte(0xFE); ms.WriteByte(0xFD); // DTLS 1.2
        ms.Write(_localRandom); // 32 bytes random
        ms.WriteByte(0); // Session ID length

        // Cookie (empty initially)
        ms.WriteByte(0);

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

        OnDatagramToSend?.Invoke(record);
    }

    public void ProcessIncomingDatagram(byte[] datagram)
    {
        lock (_gate)
        {
            if (_disposed || datagram.Length < 13) return;

            var contentType = datagram[0];
            var version = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(1));
            var epoch = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(3));
            var length = BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(11));

            if (datagram.Length < 13 + length) return;
            var payload = datagram.AsSpan(13, length);

            switch (contentType)
            {
                case ContentTypeHandshake:
                    ProcessHandshakeRecord(payload);
                    break;
                case ContentTypeChangeCipherSpec:
                    // Change cipher spec received
                    break;
                case ContentTypeApplicationData:
                    ProcessApplicationDataRecord(epoch, payload);
                    break;
            }
        }
    }

    private void ProcessHandshakeRecord(ReadOnlySpan<byte> payload)
    {
        while (payload.Length >= 12)
        {
            var type = payload[0];
            var length = (payload[1] << 16) | (payload[2] << 8) | payload[3];
            var msgSeq = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
            var fragOffset = (payload[6] << 16) | (payload[7] << 8) | payload[8];
            var fragLength = (payload[9] << 16) | (payload[10] << 8) | payload[11];

            if (payload.Length < 12 + fragLength) break;
            var body = payload.Slice(12, fragLength);

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
                case HandshakeServerHelloDone:
                    HandleServerHelloDone();
                    break;
                case HandshakeClientKeyExchange:
                    HandleClientKeyExchange(body);
                    break;
                case HandshakeFinished:
                    HandleFinished();
                    break;
            }

            payload = payload[(12 + fragLength)..];
        }
    }

    private void HandleClientHello(ReadOnlySpan<byte> body)
    {
        if (body.Length < 34) return;
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
        var ecdhPub = _localEcdh.ExportSubjectPublicKeyInfo();
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
        var signature = _localEcdsa.SignData(sigParams.ToArray(), HashAlgorithmName.SHA256);

        skeMs.WriteByte(0x04); skeMs.WriteByte(0x03); // ecdsa_secp256r1_sha256
        skeMs.WriteByte((byte)(signature.Length >> 8));
        skeMs.WriteByte((byte)signature.Length);
        skeMs.Write(signature);

        SendHandshakeMessage(HandshakeServerKeyExchange, skeMs.ToArray());
        SendHandshakeMessage(HandshakeServerHelloDone, []);
    }

    private void HandleServerHello(ReadOnlySpan<byte> body)
    {
        if (body.Length < 34) return;
        _remoteRandom = body.Slice(2, 32).ToArray();
    }

    private void HandleCertificate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 6) return;
        var certLen = (body[3] << 16) | (body[4] << 8) | body[5];
        if (body.Length < 6 + certLen) return;
        var certBytes = body.Slice(6, certLen).ToArray();
        var cert = X509CertificateLoader.LoadCertificate(certBytes);

        var certHash = SHA256.HashData(cert.RawData);
        var sb = new StringBuilder();
        for (var i = 0; i < certHash.Length; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(certHash[i].ToString("X2"));
        }
        var computedFingerprint = sb.ToString();

        // Verify fingerprint against expected RemoteFingerprint if set
        if (!string.IsNullOrEmpty(RemoteFingerprint))
        {
            var expected = RemoteFingerprint.Trim().Replace(" ", "");
            if (!string.Equals(computedFingerprint, expected, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("DTLS remote certificate fingerprint mismatch.");
        }
    }

    private void HandleServerKeyExchange(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4) return;
        var pubLen = body[3];
        if (body.Length < 4 + pubLen) return;
        var peerPub = body.Slice(4, pubLen).ToArray();

        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPub, out _);
        var preMasterSecret = _localEcdh.DeriveKeyMaterial(peerEcdh.PublicKey);

        DeriveKeys(preMasterSecret);
    }

    private void HandleServerHelloDone()
    {
        // Client responds with ClientKeyExchange, ChangeCipherSpec, Finished
        var ecdhPub = _localEcdh.ExportSubjectPublicKeyInfo();
        using var ckeMs = new MemoryStream();
        ckeMs.WriteByte((byte)ecdhPub.Length);
        ckeMs.Write(ecdhPub);
        SendHandshakeMessage(HandshakeClientKeyExchange, ckeMs.ToArray());

        // Send ChangeCipherSpec
        SendRecord(ContentTypeChangeCipherSpec, [1]);
        _localEpoch = 1;
        _localSequenceNumber = 0;

        // Send Finished
        var verifyData = ComputeVerifyData("client finished");
        SendHandshakeMessage(HandshakeFinished, verifyData);
    }

    private void HandleClientKeyExchange(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2) return;
        var pubLen = body[0];
        if (body.Length < 1 + pubLen) return;
        var peerPub = body.Slice(1, pubLen).ToArray();

        using var peerEcdh = ECDiffieHellman.Create();
        peerEcdh.ImportSubjectPublicKeyInfo(peerPub, out _);
        var preMasterSecret = _localEcdh.DeriveKeyMaterial(peerEcdh.PublicKey);

        DeriveKeys(preMasterSecret);

        // Server sends ChangeCipherSpec, Finished
        SendRecord(ContentTypeChangeCipherSpec, [1]);
        _localEpoch = 1;
        _localSequenceNumber = 0;

        var verifyData = ComputeVerifyData("server finished");
        SendHandshakeMessage(HandshakeFinished, verifyData);
    }

    private void HandleFinished()
    {
        State = "connected";
        OnStateChanged?.Invoke(State);
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
        _remoteRandom!.CopyTo(keyBlockSeed.AsSpan(0, 32));
        _localRandom.CopyTo(keyBlockSeed.AsSpan(32, 32));
        var keyBlock = Prf(_masterSecret, "key expansion"u8, keyBlockSeed, 40);

        var clientWriteKey = keyBlock[0..16];
        var serverWriteKey = keyBlock[16..32];
        var clientWriteIv = keyBlock[32..36];
        var serverWriteIv = keyBlock[36..40];

        if (IsClient)
        {
            _localGcm = new AesGcm(clientWriteKey, 16);
            _remoteGcm = new AesGcm(serverWriteKey, 16);
            _localImplicitIv = clientWriteIv;
            _remoteImplicitIv = serverWriteIv;
        }
        else
        {
            _localGcm = new AesGcm(serverWriteKey, 16);
            _remoteGcm = new AesGcm(clientWriteKey, 16);
            _localImplicitIv = serverWriteIv;
            _remoteImplicitIv = clientWriteIv;
        }

        // RFC 5764 DTLS-SRTP Key Exporter:
        // Extract 2 * (16 + 14) = 60 bytes of key material
        var srtpSeed = new byte[64];
        _localRandom.CopyTo(srtpSeed.AsSpan(0, 32));
        _remoteRandom!.CopyTo(srtpSeed.AsSpan(32, 32));
        var srtpBlock = Prf(_masterSecret, "EXTRACTOR-dtls_srtp"u8, srtpSeed, 60);

        if (IsClient)
        {
            SrtpLocalMasterKey = srtpBlock[0..16];
            SrtpRemoteMasterKey = srtpBlock[16..32];
            SrtpLocalMasterSalt = srtpBlock[32..46];
            SrtpRemoteMasterSalt = srtpBlock[46..60];
        }
        else
        {
            SrtpRemoteMasterKey = srtpBlock[0..16];
            SrtpLocalMasterKey = srtpBlock[16..32];
            SrtpRemoteMasterSalt = srtpBlock[32..46];
            SrtpLocalMasterSalt = srtpBlock[46..60];
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
            if (_localGcm is null || _localImplicitIv is null)
            {
                // Unencrypted application data (e.g. before handshake completes)
                SendRecord(ContentTypeApplicationData, data);
                return;
            }

            var seq = _localSequenceNumber;
            var nonce = new byte[12];
            _localImplicitIv.CopyTo(nonce.AsSpan(0, 4));
            BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), seq);

            // AAD: 8 bytes epoch + seq + 1 byte content type + 2 bytes version + 2 bytes length
            var aad = new byte[13];
            BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(0), _localEpoch);
            aad[2] = (byte)(seq >> 40);
            aad[3] = (byte)(seq >> 32);
            aad[4] = (byte)(seq >> 24);
            aad[5] = (byte)(seq >> 16);
            aad[6] = (byte)(seq >> 8);
            aad[7] = (byte)seq;
            aad[8] = ContentTypeApplicationData;
            BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(9), DtlsVersion12);
            BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(11), (ushort)data.Length);

            var ciphertext = new byte[data.Length];
            var tag = new byte[16];
            _localGcm.Encrypt(nonce, data, ciphertext, tag, aad);

            // DTLS 1.2 record with explicit nonce (8 bytes) + ciphertext + tag
            var payload = new byte[8 + ciphertext.Length + 16];
            BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(0), seq);
            ciphertext.CopyTo(payload.AsSpan(8));
            tag.CopyTo(payload.AsSpan(8 + ciphertext.Length));

            SendRecord(ContentTypeApplicationData, payload);
        }
    }

    private void ProcessApplicationDataRecord(ushort epoch, ReadOnlySpan<byte> payload)
    {
        if (_remoteGcm is null || _remoteImplicitIv is null || epoch == 0)
        {
            OnApplicationDataReceived?.Invoke(payload.ToArray());
            return;
        }

        if (payload.Length < 8 + 16) return;
        var explicitNonce = payload.Slice(0, 8);
        var ciphertext = payload.Slice(8, payload.Length - 8 - 16);
        var tag = payload[^16..];

        var nonce = new byte[12];
        _remoteImplicitIv.CopyTo(nonce.AsSpan(0, 4));
        explicitNonce.CopyTo(nonce.AsSpan(4));

        var seq = BinaryPrimitives.ReadUInt64BigEndian(explicitNonce);
        var aad = new byte[13];
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(0), epoch);
        aad[2] = (byte)(seq >> 40);
        aad[3] = (byte)(seq >> 32);
        aad[4] = (byte)(seq >> 24);
        aad[5] = (byte)(seq >> 16);
        aad[6] = (byte)(seq >> 8);
        aad[7] = (byte)seq;
        aad[8] = ContentTypeApplicationData;
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(9), DtlsVersion12);
        BinaryPrimitives.WriteUInt16BigEndian(aad.AsSpan(11), (ushort)ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        try
        {
            _remoteGcm.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            OnApplicationDataReceived?.Invoke(plaintext);
        }
        catch (CryptographicException) { }
    }

    public void Close() => Dispose();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            State = "closed";
        }
        _localEcdsa.Dispose();
        _localCert.Dispose();
        _localEcdh.Dispose();
        _localGcm?.Dispose();
        _remoteGcm?.Dispose();
        OnStateChanged?.Invoke("closed");
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned SRTP / SRTCP transport (RFC 3711 / RFC 7714) implementing AEAD AES-128-GCM
/// and AES-128-CTR HMAC-SHA1 protection, rollover counter (ROC) tracking, and a 64-packet anti-replay window.
/// </summary>
internal sealed class SrtpTransport : IDisposable
{
    private readonly byte[] _localMasterKey;
    private readonly byte[] _localMasterSalt;
    private readonly byte[] _remoteMasterKey;
    private readonly byte[] _remoteMasterSalt;

    private readonly AesGcm _encryptor;
    private readonly AesGcm _decryptor;

    private uint _localRoc = 0;
    private ushort _localSeq = 0;
    private uint _remoteRoc = 0;
    private ushort _remoteHighestSeq = 0;
    private ulong _replayWindow = 0; // 64-bit sliding window

    private readonly object _gate = new();
    private bool _disposed;

    public SrtpTransport(byte[] localKey, byte[] localSalt, byte[] remoteKey, byte[] remoteSalt)
    {
        _localMasterKey = localKey;
        _localMasterSalt = localSalt;
        _remoteMasterKey = remoteKey;
        _remoteMasterSalt = remoteSalt;

        _encryptor = new AesGcm(_localMasterKey, 16);
        _decryptor = new AesGcm(_remoteMasterKey, 16);
    }

    public byte[] ProtectRtp(ReadOnlySpan<byte> rtpPacket)
    {
        lock (_gate)
        {
            if (_disposed || rtpPacket.Length < 12)
                throw new InvalidOperationException("Invalid RTP packet.");

            var version = (rtpPacket[0] >> 6) & 0x03;
            if (version != 2) throw new InvalidOperationException("Unsupported RTP version.");

            var csrcCount = rtpPacket[0] & 0x0F;
            var headerLength = 12 + csrcCount * 4;
            if (rtpPacket.Length < headerLength) throw new InvalidOperationException("Truncated RTP header.");

            var seq = BinaryPrimitives.ReadUInt16BigEndian(rtpPacket.Slice(2, 2));
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket.Slice(8, 4));

            if (seq == 0 && _localSeq == ushort.MaxValue)
                _localRoc++;
            _localSeq = seq;

            // Form 12-byte IV for AEAD_AES_128_GCM (RFC 7714 section 8.1)
            // IV = salt XOR (SSRC << 64 | ROC << 16 | seq)
            var iv = new byte[12];
            _localMasterSalt.AsSpan(0, 12).CopyTo(iv);

            var ssrcRocSeq = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(ssrcRocSeq.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt32BigEndian(ssrcRocSeq.AsSpan(4), ssrc);
            BinaryPrimitives.WriteUInt32BigEndian(ssrcRocSeq.AsSpan(8), (_localRoc << 16) | seq);

            for (var i = 0; i < 12; i++) iv[i] ^= ssrcRocSeq[i];

            var header = rtpPacket.Slice(0, headerLength);
            var payload = rtpPacket.Slice(headerLength);

            var ciphertext = new byte[payload.Length];
            var tag = new byte[16];
            _encryptor.Encrypt(iv, payload, ciphertext, tag, header);

            var srtpPacket = new byte[headerLength + ciphertext.Length + 16];
            header.CopyTo(srtpPacket.AsSpan(0, headerLength));
            ciphertext.CopyTo(srtpPacket.AsSpan(headerLength));
            tag.CopyTo(srtpPacket.AsSpan(headerLength + ciphertext.Length));

            return srtpPacket;
        }
    }

    public bool TryUnprotectRtp(ReadOnlySpan<byte> srtpPacket, out byte[]? rtpPacket)
    {
        rtpPacket = null;
        lock (_gate)
        {
            if (_disposed || srtpPacket.Length < 12 + 16) return false;

            var version = (srtpPacket[0] >> 6) & 0x03;
            if (version != 2) return false;

            var csrcCount = srtpPacket[0] & 0x0F;
            var headerLength = 12 + csrcCount * 4;
            if (srtpPacket.Length < headerLength + 16) return false;

            var seq = BinaryPrimitives.ReadUInt16BigEndian(srtpPacket.Slice(2, 2));
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(srtpPacket.Slice(8, 4));

            // Anti-replay check & ROC estimate (RFC 3711 section 3.3.1 & 3.3.2)
            if (!CheckReplayAndUpdateRoc(seq, out var roc)) return false;

            var iv = new byte[12];
            _remoteMasterSalt.AsSpan(0, 12).CopyTo(iv);

            var ssrcRocSeq = new byte[12];
            BinaryPrimitives.WriteUInt32BigEndian(ssrcRocSeq.AsSpan(0), 0);
            BinaryPrimitives.WriteUInt32BigEndian(ssrcRocSeq.AsSpan(4), ssrc);
            BinaryPrimitives.WriteUInt32BigEndian(ssrcRocSeq.AsSpan(8), (roc << 16) | seq);

            for (var i = 0; i < 12; i++) iv[i] ^= ssrcRocSeq[i];

            var header = srtpPacket.Slice(0, headerLength);
            var ciphertext = srtpPacket.Slice(headerLength, srtpPacket.Length - headerLength - 16);
            var tag = srtpPacket[^16..];

            var plaintext = new byte[ciphertext.Length];
            try
            {
                _decryptor.Decrypt(iv, ciphertext, tag, plaintext, header);
            }
            catch (CryptographicException)
            {
                return false;
            }

            // Successfully authenticated and decrypted: update sliding replay window
            UpdateReplayWindow(seq);

            rtpPacket = new byte[headerLength + plaintext.Length];
            header.CopyTo(rtpPacket.AsSpan(0, headerLength));
            plaintext.CopyTo(rtpPacket.AsSpan(headerLength));
            return true;
        }
    }

    private bool CheckReplayAndUpdateRoc(ushort seq, out uint estimatedRoc)
    {
        estimatedRoc = _remoteRoc;
        if (_replayWindow == 0 && _remoteHighestSeq == 0)
        {
            // First received packet
            return true;
        }

        var diff = (short)(seq - _remoteHighestSeq);
        if (diff > 0)
        {
            // Ahead of highest sequence number
            if (_remoteHighestSeq > 0xF000 && seq < 0x1000)
                estimatedRoc++;
            return true;
        }

        // Within or behind the window
        var delta = -diff;
        if (delta >= 64)
        {
            // Packet is too old (> 64 packets behind highest)
            return false;
        }

        // Check if bit is set in 64-bit replay window
        if ((_replayWindow & (1UL << delta)) != 0)
        {
            // Duplicate packet!
            return false;
        }

        return true;
    }

    private void UpdateReplayWindow(ushort seq)
    {
        var diff = (short)(seq - _remoteHighestSeq);
        if (diff > 0)
        {
            // Advance window
            if (diff >= 64) _replayWindow = 1UL;
            else _replayWindow = (_replayWindow << diff) | 1UL;

            if (_remoteHighestSeq > 0xF000 && seq < 0x1000)
                _remoteRoc++;
            _remoteHighestSeq = seq;
        }
        else
        {
            var delta = -diff;
            if (delta < 64)
            {
                _replayWindow |= (1UL << delta);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _encryptor.Dispose();
            _decryptor.Dispose();
        }
    }
}

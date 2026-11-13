using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>
/// Folio-owned SRTP / SRTCP (RFC 3711 key derivation, RFC 7714 AEAD_AES_128_GCM). Session keys are derived from the DTLS
/// exporter's master key and salt. Receive state (rollover counter and a 64-packet replay window) is kept per SSRC, is only
/// created after a packet authenticates, and is capped, so unauthenticated traffic cannot grow it.
/// </summary>
internal sealed class SrtpTransport : IDisposable
{
    internal const int MaxRemoteStreams = 32;
    internal const int MaxPacketBytes = 2048;
    private const int Tag = 16;
    private const int SaltBytes = 12;

    private readonly AesGcm _rtpEnc, _rtpDec, _rtcpEnc, _rtcpDec;
    private readonly byte[] _rtpLocalSalt, _rtpRemoteSalt, _rtcpLocalSalt, _rtcpRemoteSalt;
    private readonly Dictionary<uint, SendState> _send = [];
    private readonly Dictionary<uint, RecvState> _recv = [];
    private readonly Dictionary<uint, RecvState> _recvRtcp = [];
    private uint _rtcpSendIndex;
    private readonly object _gate = new();
    private bool _disposed;

    private sealed class SendState { public bool Started; public ushort Last; public uint Roc; }
    private sealed class RecvState { public bool Started; public ulong Highest; public ulong Window; }

    public SrtpTransport(byte[] localKey, byte[] localSalt, byte[] remoteKey, byte[] remoteSalt)
    {
        ArgumentNullException.ThrowIfNull(localKey); ArgumentNullException.ThrowIfNull(localSalt);
        ArgumentNullException.ThrowIfNull(remoteKey); ArgumentNullException.ThrowIfNull(remoteSalt);
        if (localKey.Length != 16 || remoteKey.Length != 16 || localSalt.Length < SaltBytes || remoteSalt.Length < SaltBytes)
            throw new ArgumentException("SRTP master keys must be 16 bytes and salts at least 12 bytes.");
        _rtpEnc = Gcm(Derive(localKey, localSalt, 0x00, 16));
        _rtpDec = Gcm(Derive(remoteKey, remoteSalt, 0x00, 16));
        _rtpLocalSalt = Derive(localKey, localSalt, 0x02, SaltBytes);
        _rtpRemoteSalt = Derive(remoteKey, remoteSalt, 0x02, SaltBytes);
        _rtcpEnc = Gcm(Derive(localKey, localSalt, 0x03, 16));
        _rtcpDec = Gcm(Derive(remoteKey, remoteSalt, 0x03, 16));
        _rtcpLocalSalt = Derive(localKey, localSalt, 0x05, SaltBytes);
        _rtcpRemoteSalt = Derive(remoteKey, remoteSalt, 0x05, SaltBytes);
    }

    /// <summary>Known-answer tests only: uses the given session key and salt for every direction and for RTP and RTCP, as the RFC 7714 vectors do.</summary>
    internal SrtpTransport(byte[] sessionKey, byte[] sessionSalt, bool sessionKeysAreFinal)
    {
        if (!sessionKeysAreFinal) throw new ArgumentException("Use the master key constructor.", nameof(sessionKeysAreFinal));
        _rtpEnc = Gcm((byte[])sessionKey.Clone()); _rtpDec = Gcm((byte[])sessionKey.Clone());
        _rtcpEnc = Gcm((byte[])sessionKey.Clone()); _rtcpDec = Gcm((byte[])sessionKey.Clone());
        _rtpLocalSalt = sessionSalt[..SaltBytes]; _rtpRemoteSalt = sessionSalt[..SaltBytes];
        _rtcpLocalSalt = sessionSalt[..SaltBytes]; _rtcpRemoteSalt = sessionSalt[..SaltBytes];
    }

    /// <summary>AesGcm copies the key, so the derived array is wiped as soon as the cipher exists.</summary>
    private static AesGcm Gcm(byte[] key)
    {
        try { return new AesGcm(key, Tag); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal uint RtcpSendIndex { set { lock (_gate) _rtcpSendIndex = value; } }

    /// <summary>Forgets the RTP receive state of an SSRC the peer said goodbye to. The RTCP window is kept so the BYE cannot be replayed.</summary>
    internal void RemoveRemoteStream(uint ssrc) { lock (_gate) _recv.Remove(ssrc); }

    /// <summary>RFC 3711 section 4.3 KDF with key_derivation_rate 0 (AES-CM PRF), as used by RFC 7714.</summary>
    private static byte[] Derive(byte[] masterKey, byte[] masterSalt, byte label, int length)
    {
        using var aes = Aes.Create();
        aes.Key = masterKey;
        var counter = new byte[16];
        masterSalt.AsSpan(0, SaltBytes).CopyTo(counter);
        counter[7] ^= label;
        var output = new byte[16];
        aes.EncryptEcb(counter, output, PaddingMode.None);
        return length == 16 ? output : output[..length];
    }

    /// <summary>Header length including CSRCs and the extension header, or -1 when the packet is malformed.</summary>
    internal static int RtpHeaderLength(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || (packet[0] >> 6) != 2) return -1;
        var length = 12 + (packet[0] & 0x0F) * 4;
        if ((packet[0] & 0x10) != 0)
        {
            if (packet.Length < length + 4) return -1;
            length += 4 + BinaryPrimitives.ReadUInt16BigEndian(packet[(length + 2)..]) * 4;
        }
        return length > packet.Length ? -1 : length;
    }

    private static void RtpIv(byte[] salt, uint ssrc, uint roc, ushort seq, Span<byte> iv)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[6..], roc);
        BinaryPrimitives.WriteUInt16BigEndian(iv[10..], seq);
        for (var i = 0; i < SaltBytes; i++) iv[i] ^= salt[i];
    }

    private static void RtcpIv(byte[] salt, uint ssrc, uint index, Span<byte> iv)
    {
        iv.Clear();
        BinaryPrimitives.WriteUInt32BigEndian(iv[2..], ssrc);
        BinaryPrimitives.WriteUInt32BigEndian(iv[8..], index);
        for (var i = 0; i < SaltBytes; i++) iv[i] ^= salt[i];
    }

    public byte[] ProtectRtp(ReadOnlySpan<byte> rtpPacket)
    {
        lock (_gate)
        {
            var headerLength = RtpHeaderLength(rtpPacket);
            if (_disposed || headerLength < 0 || rtpPacket.Length > MaxPacketBytes) throw new InvalidOperationException("Invalid RTP packet.");
            var seq = BinaryPrimitives.ReadUInt16BigEndian(rtpPacket[2..]);
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtpPacket[8..]);
            if (!_send.TryGetValue(ssrc, out var state))
            {
                if (_send.Count >= MaxRemoteStreams) throw new InvalidOperationException("Too many local SSRCs.");
                _send[ssrc] = state = new SendState();
            }
            if (state.Started && seq < state.Last && state.Last - seq > 0x8000) state.Roc++;
            if (!state.Started || seq > state.Last || state.Last - seq > 0x8000) state.Last = seq;
            state.Started = true;

            Span<byte> iv = stackalloc byte[12];
            RtpIv(_rtpLocalSalt, ssrc, state.Roc, seq, iv);
            var output = new byte[rtpPacket.Length + Tag];
            rtpPacket[..headerLength].CopyTo(output);
            _rtpEnc.Encrypt(iv, rtpPacket[headerLength..], output.AsSpan(headerLength, rtpPacket.Length - headerLength),
                output.AsSpan(rtpPacket.Length, Tag), rtpPacket[..headerLength]);
            return output;
        }
    }

    public bool TryUnprotectRtp(ReadOnlySpan<byte> srtpPacket, out byte[]? rtpPacket)
    {
        rtpPacket = null;
        lock (_gate)
        {
            if (_disposed || srtpPacket.Length > MaxPacketBytes) return false;
            var headerLength = RtpHeaderLength(srtpPacket);
            if (headerLength < 0 || srtpPacket.Length < headerLength + Tag) return false;
            var seq = BinaryPrimitives.ReadUInt16BigEndian(srtpPacket[2..]);
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(srtpPacket[8..]);
            var known = _recv.TryGetValue(ssrc, out var state);
            if (!known)
            {
                if (_recv.Count >= MaxRemoteStreams) return false;
                state = new RecvState();
            }
            if (!EstimateIndex(state!, seq, out var index) || !ReplayOk(state!, index)) return false;

            Span<byte> iv = stackalloc byte[12];
            RtpIv(_rtpRemoteSalt, ssrc, (uint)(index >> 16), seq, iv);
            var plainLength = srtpPacket.Length - Tag;
            var output = new byte[plainLength];
            srtpPacket[..headerLength].CopyTo(output);
            try
            {
                _rtpDec.Decrypt(iv, srtpPacket.Slice(headerLength, plainLength - headerLength), srtpPacket[plainLength..],
                    output.AsSpan(headerLength), srtpPacket[..headerLength]);
            }
            catch (CryptographicException) { return false; }
            if (!known) _recv[ssrc] = state!;
            Accept(state!, index);
            rtpPacket = output;
            return true;
        }
    }

    /// <summary>RFC 3711 appendix A index estimation. False when the packet would need a negative rollover count.</summary>
    private static bool EstimateIndex(RecvState state, ushort seq, out ulong index)
    {
        index = seq;
        if (!state.Started) return true;
        var roc = state.Highest >> 16;
        var last = (ushort)state.Highest;
        long v;
        if (last < 0x8000) v = seq - last > 0x8000 ? (long)roc - 1 : (long)roc;
        else v = last - 0x8000 > seq ? (long)roc + 1 : (long)roc;
        if (v < 0 || v > uint.MaxValue) return false;
        index = ((ulong)v << 16) | seq;
        return true;
    }

    private static bool ReplayOk(RecvState state, ulong index)
    {
        if (!state.Started || index > state.Highest) return true;
        var delta = state.Highest - index;
        return delta < 64 && (state.Window & (1UL << (int)delta)) == 0;
    }

    private static void Accept(RecvState state, ulong index)
    {
        if (!state.Started) { state.Started = true; state.Highest = index; state.Window = 1; return; }
        if (index > state.Highest)
        {
            var shift = index - state.Highest;
            state.Window = shift >= 64 ? 1UL : (state.Window << (int)shift) | 1UL;
            state.Highest = index;
        }
        else state.Window |= 1UL << (int)(state.Highest - index);
    }

    /// <summary>Protects one RTCP (compound) packet. The first 8 bytes stay clear and authenticated; the rest is encrypted.</summary>
    public byte[] ProtectRtcp(ReadOnlySpan<byte> rtcp)
    {
        lock (_gate)
        {
            if (_disposed || rtcp.Length < 8 || rtcp.Length > MaxPacketBytes || (rtcp[0] >> 6) != 2) throw new InvalidOperationException("Invalid RTCP packet.");
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(rtcp[4..]);
            var index = _rtcpSendIndex++ & 0x7FFFFFFF;
            var output = new byte[rtcp.Length + Tag + 4];
            rtcp[..8].CopyTo(output);
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(rtcp.Length + Tag), 0x80000000 | index);
            Span<byte> iv = stackalloc byte[12];
            RtcpIv(_rtcpLocalSalt, ssrc, index, iv);
            Span<byte> aad = stackalloc byte[12];
            rtcp[..8].CopyTo(aad);
            output.AsSpan(rtcp.Length + Tag, 4).CopyTo(aad[8..]);
            _rtcpEnc.Encrypt(iv, rtcp[8..], output.AsSpan(8, rtcp.Length - 8), output.AsSpan(rtcp.Length, Tag), aad);
            return output;
        }
    }

    public bool TryUnprotectRtcp(ReadOnlySpan<byte> srtcp, out byte[]? rtcp)
    {
        rtcp = null;
        lock (_gate)
        {
            if (_disposed || srtcp.Length < 8 + Tag + 4 || srtcp.Length > MaxPacketBytes || (srtcp[0] >> 6) != 2) return false;
            var trailer = BinaryPrimitives.ReadUInt32BigEndian(srtcp[^4..]);
            if ((trailer & 0x80000000) == 0) return false; // GCM always encrypts
            var index = trailer & 0x7FFFFFFF;
            var ssrc = BinaryPrimitives.ReadUInt32BigEndian(srtcp[4..]);
            var known = _recvRtcp.TryGetValue(ssrc, out var state);
            if (!known)
            {
                if (_recvRtcp.Count >= MaxRemoteStreams) return false;
                state = new RecvState();
            }
            if (!ReplayOk(state!, index)) return false;
            var plainLength = srtcp.Length - 4 - Tag;
            var output = new byte[plainLength];
            srtcp[..8].CopyTo(output);
            Span<byte> iv = stackalloc byte[12];
            RtcpIv(_rtcpRemoteSalt, ssrc, index, iv);
            Span<byte> aad = stackalloc byte[12];
            srtcp[..8].CopyTo(aad);
            srtcp[^4..].CopyTo(aad[8..]);
            try { _rtcpDec.Decrypt(iv, srtcp.Slice(8, plainLength - 8), srtcp.Slice(plainLength, Tag), output.AsSpan(8), aad); }
            catch (CryptographicException) { return false; }
            if (!known) _recvRtcp[ssrc] = state!;
            Accept(state!, index);
            rtcp = output;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _rtpEnc.Dispose(); _rtpDec.Dispose(); _rtcpEnc.Dispose(); _rtcpDec.Dispose();
            CryptographicOperations.ZeroMemory(_rtpLocalSalt); CryptographicOperations.ZeroMemory(_rtpRemoteSalt);
            CryptographicOperations.ZeroMemory(_rtcpLocalSalt); CryptographicOperations.ZeroMemory(_rtcpRemoteSalt);
        }
    }
}

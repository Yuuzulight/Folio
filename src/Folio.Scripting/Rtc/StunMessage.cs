using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>Bounded STUN framing and short-term HMAC-SHA1 authentication for ICE and TURN.</summary>
internal sealed class StunMessage
{
    internal const uint Cookie = 0x2112a442;
    internal const ushort BindingRequest = 0x0001, BindingResponse = 0x0101, BindingErrorResponse = 0x0111,
        AllocateRequest = 0x0003, AllocateResponse = 0x0103, RefreshRequest = 0x0004, RefreshResponse = 0x0104,
        SendIndication = 0x0016, DataIndication = 0x0017, CreatePermissionRequest = 0x0008, CreatePermissionResponse = 0x0108,
        ChannelBindRequest = 0x0009, ChannelBindResponse = 0x0109;

    internal const ushort MappedAddress = 0x0001, Username = 0x0006, Integrity = 0x0008, ErrorCode = 0x0009,
        UnknownAttributes = 0x000a, Lifetime = 0x000d, XorPeerAddress = 0x0012, Data = 0x0013, Realm = 0x0014,
        Nonce = 0x0015, XorRelayedAddress = 0x0016, EvenPort = 0x0018, RequestedTransport = 0x0019,
        DontFragment = 0x001a, XorMappedAddress = 0x0020, Priority = 0x0024, UseCandidate = 0x0025,
        Software = 0x8022, Fingerprint = 0x8028, IceControlled = 0x8029, IceControlling = 0x802a;

    private readonly byte[] _wire;
    private readonly List<(ushort Type, int Offset, int Length)> _attributes;
    private StunMessage(byte[] wire, List<(ushort, int, int)> attributes) => (_wire, _attributes) = (wire, attributes);
    internal ushort Type => BinaryPrimitives.ReadUInt16BigEndian(_wire);
    internal ReadOnlyMemory<byte> TransactionId => _wire.AsMemory(8, 12);

    internal static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out StunMessage? message)
    {
        message = null;
        if (bytes.Length < 20 || bytes.Length > 65552 || (bytes[0] & 0xc0) != 0
            || BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) != Cookie) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if ((length & 3) != 0 || length != bytes.Length - 20) return false;
        var attributes = new List<(ushort, int, int)>();
        var integritySeen = false;
        var fingerprintSeen = false;
        for (var offset = 20; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 4 || attributes.Count >= 256) return false;
            var type = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            var size = BinaryPrimitives.ReadUInt16BigEndian(bytes[(offset + 2)..]);
            var padded = (size + 3) & ~3;
            if (padded > bytes.Length - offset - 4) return false;
            if (type == Integrity) { if (integritySeen || size != 20) return false; integritySeen = true; }
            if (type == Fingerprint) { if (fingerprintSeen || size != 4 || offset + 8 != bytes.Length) return false; fingerprintSeen = true; }
            attributes.Add((type, offset, size));
            offset += 4 + padded;
        }
        message = new StunMessage(bytes.ToArray(), attributes);
        return true;
    }

    internal bool VerifyIntegritySha1(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty) return false;
        var index = _attributes.FindIndex(a => a.Type == Integrity);
        if (index < 0) return false;
        // Never authenticate fields supplied after MESSAGE-INTEGRITY. ICE permits only the final fingerprint here.
        if (_attributes.Skip(index + 1).Any(a => a.Type != Fingerprint)) return false;
        var attribute = _attributes[index];
        var authenticated = _wire.AsSpan(0, attribute.Offset).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(authenticated.AsSpan(2), checked((ushort)(attribute.Offset + 24 - 20)));
        Span<byte> computed = stackalloc byte[20];
        HMACSHA1.HashData(key, authenticated, computed);
        return CryptographicOperations.FixedTimeEquals(computed, _wire.AsSpan(attribute.Offset + 4, 20));
    }

    internal bool VerifyFingerprint()
    {
        var attribute = _attributes.Find(a => a.Type == Fingerprint);
        return attribute.Type == Fingerprint && BinaryPrimitives.ReadUInt32BigEndian(_wire.AsSpan(attribute.Offset + 4))
            == (Crc32(_wire.AsSpan(0, attribute.Offset)) ^ 0x5354554e);
    }

    internal bool TryAttribute(ushort type, out ReadOnlyMemory<byte> value)
    {
        value = default;
        var matches = _attributes.Where(a => a.Type == type).ToArray();
        if (matches.Length != 1) return false;
        value = _wire.AsMemory(matches[0].Offset + 4, matches[0].Length);
        return true;
    }

    internal bool TryXorMappedAddress(out IPEndPoint? endpoint) => TryDecodeXorAddress(XorMappedAddress, out endpoint);
    internal bool TryXorRelayedAddress(out IPEndPoint? endpoint) => TryDecodeXorAddress(XorRelayedAddress, out endpoint);

    private bool TryDecodeXorAddress(ushort attrType, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (!TryAttribute(attrType, out var memory)) return false;
        var value = memory.Span;
        if (value.Length < 4 || value[0] != 0) return false;
        var size = value[1] == 1 ? 4 : value[1] == 2 ? 16 : 0;
        if (size == 0 || value.Length != size + 4) return false;
        Span<byte> address = stackalloc byte[16];
        for (var i = 0; i < size; i++) address[i] = (byte)(value[4 + i] ^ _wire[4 + i]);
        endpoint = new IPEndPoint(new IPAddress(address[..size]), BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ 0x2112);
        return true;
    }

    internal static byte[] Encode(ushort type, ReadOnlySpan<byte> transactionId,
        IReadOnlyList<(ushort Type, byte[] Value)> attributes, ReadOnlySpan<byte> integrityKey, bool fingerprint = true)
    {
        if ((type & 0xc000) != 0 || transactionId.Length != 12 || attributes.Count > 254 || integrityKey.IsEmpty)
            throw new ArgumentException("Invalid STUN message inputs.");
        var length = 20 + 24 + (fingerprint ? 8 : 0);
        foreach (var attribute in attributes)
        {
            if (attribute.Type is Integrity or Fingerprint || attribute.Value is null || attribute.Value.Length > ushort.MaxValue)
                throw new ArgumentException("Invalid STUN attribute.");
            length = checked(length + 4 + ((attribute.Value.Length + 3) & ~3));
            if (length > 65552) throw new ArgumentException("STUN message is too large.");
        }
        var result = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(result, type);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), Cookie);
        transactionId.CopyTo(result.AsSpan(8));
        var offset = 20;
        foreach (var attribute in attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), attribute.Type);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), (ushort)attribute.Value.Length);
            attribute.Value.CopyTo(result.AsSpan(offset + 4));
            offset += 4 + ((attribute.Value.Length + 3) & ~3);
        }
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), Integrity);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), 20);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)(offset + 24 - 20));
        HMACSHA1.HashData(integrityKey, result.AsSpan(0, offset), result.AsSpan(offset + 4, 20));
        offset += 24;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)(length - 20));
        if (fingerprint)
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), Fingerprint);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), 4);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset + 4), Crc32(result.AsSpan(0, offset)) ^ 0x5354554e);
        }
        return result;
    }

    internal static byte[] EncodeRequest(ushort type, ReadOnlySpan<byte> transactionId,
        IReadOnlyList<(ushort Type, byte[] Value)>? attributes = null, bool fingerprint = true)
    {
        attributes ??= [];
        if ((type & 0xc000) != 0 || transactionId.Length != 12 || attributes.Count > 254)
            throw new ArgumentException("Invalid STUN message inputs.");
        var length = 20 + (fingerprint ? 8 : 0);
        foreach (var attribute in attributes)
        {
            if (attribute.Type is Integrity or Fingerprint || attribute.Value is null || attribute.Value.Length > ushort.MaxValue)
                throw new ArgumentException("Invalid STUN attribute.");
            length = checked(length + 4 + ((attribute.Value.Length + 3) & ~3));
            if (length > 65552) throw new ArgumentException("STUN message is too large.");
        }
        var result = new byte[length];
        BinaryPrimitives.WriteUInt16BigEndian(result, type);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), (ushort)(length - 20));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), Cookie);
        transactionId.CopyTo(result.AsSpan(8));
        var offset = 20;
        foreach (var attribute in attributes)
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), attribute.Type);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), (ushort)attribute.Value.Length);
            attribute.Value.CopyTo(result.AsSpan(offset + 4));
            offset += 4 + ((attribute.Value.Length + 3) & ~3);
        }
        if (fingerprint)
        {
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset), Fingerprint);
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset + 2), 4);
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(offset + 4), Crc32(result.AsSpan(0, offset)) ^ 0x5354554e);
        }
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        return ~crc;
    }
}

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Folio.Scripting.Rtc;

/// <summary>DTLS 1.2 AES-GCM records with authenticated headers and a 64-record replay window.</summary>
internal sealed class DtlsRecordCipher : IDisposable
{
    private readonly AesGcm _cipher;
    private readonly byte[] _salt;
    private ulong _highest;
    private ulong _window;

    public DtlsRecordCipher(byte[] key, byte[] salt)
    {
        if (key.Length != 16 || salt.Length != 4) throw new ArgumentException("Invalid DTLS AES-128-GCM key material.");
        _cipher = new AesGcm(key, 16);
        _salt = (byte[])salt.Clone();
    }

    public byte[] Protect(byte type, ushort epoch, ulong sequence, ReadOnlySpan<byte> plaintext)
    {
        if (epoch != 1 || sequence >= 1UL << 48 || plaintext.Length > 16384)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        var record = new byte[13 + 8 + plaintext.Length + 16];
        record[0] = type; record[1] = 0xfe; record[2] = 0xfd;
        BinaryPrimitives.WriteUInt64BigEndian(record.AsSpan(3), ((ulong)epoch << 48) | sequence);
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(11), (ushort)(record.Length - 13));
        // The explicit nonce includes epoch and sequence, preventing reuse on epoch changes.
        record.AsSpan(3, 8).CopyTo(record.AsSpan(13, 8));
        Span<byte> nonce = stackalloc byte[12];
        _salt.CopyTo(nonce); record.AsSpan(13, 8).CopyTo(nonce[4..]);
        Span<byte> aad = stackalloc byte[13];
        record.AsSpan(3, 8).CopyTo(aad); aad[8] = type; aad[9] = 0xfe; aad[10] = 0xfd;
        BinaryPrimitives.WriteUInt16BigEndian(aad[11..], (ushort)plaintext.Length);
        _cipher.Encrypt(nonce, plaintext, record.AsSpan(21, plaintext.Length), record.AsSpan(21 + plaintext.Length, 16), aad);
        return record;
    }

    public bool TryUnprotect(ReadOnlySpan<byte> record, out byte[] plaintext)
    {
        plaintext = [];
        if (record.Length < 37 || record[1] != 0xfe || record[2] != 0xfd ||
            BinaryPrimitives.ReadUInt16BigEndian(record[3..]) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(record[11..]) != record.Length - 13 || record.Length > 37 + 16384)
            return false;
        var sequence = BinaryPrimitives.ReadUInt64BigEndian(record[3..]) & 0xffffffffffffUL;
        if (_window != 0 && sequence <= _highest && (_highest - sequence >= 64 || (_window & (1UL << (int)(_highest - sequence))) != 0))
            return false;
        var length = record.Length - 37;
        Span<byte> nonce = stackalloc byte[12];
        _salt.CopyTo(nonce); record.Slice(13, 8).CopyTo(nonce[4..]);
        Span<byte> aad = stackalloc byte[13];
        record.Slice(3, 8).CopyTo(aad); aad[8] = record[0]; aad[9] = record[1]; aad[10] = record[2];
        BinaryPrimitives.WriteUInt16BigEndian(aad[11..], (ushort)length);
        var clear = new byte[length];
        try { _cipher.Decrypt(nonce, record.Slice(21, length), record[^16..], clear, aad); }
        catch (CryptographicException) { return false; }
        // Only authenticated records advance the window.
        if (_window == 0) { _highest = sequence; _window = 1; }
        else if (sequence > _highest)
        {
            var delta = sequence - _highest;
            _window = delta >= 64 ? 1 : (_window << (int)delta) | 1;
            _highest = sequence;
        }
        else _window |= 1UL << (int)(_highest - sequence);
        plaintext = clear;
        return true;
    }

    public void Dispose() { _cipher.Dispose(); CryptographicOperations.ZeroMemory(_salt); }
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class DtlsHandshakeFragmentsTests
{
    [Fact]
    public void ReassemblesReorderedAndOverlappingFragmentsOnce()
    {
        var fragments = new DtlsHandshakeFragments();
        fragments.Add(Fragment(0, 5, 2, [3, 4, 5]), 0);
        Assert.False(fragments.TryTake(0, out _));
        fragments.Add(Fragment(0, 5, 0, [1, 2, 3]), 0);
        Assert.True(fragments.TryTake(0, out var complete));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, complete[12..]);
        Assert.Equal(0, DtlsHandshakeFragments.Read24(complete.AsSpan(6)));
        Assert.Equal(5, DtlsHandshakeFragments.Read24(complete.AsSpan(9)));
        fragments.Add(Fragment(0, 5, 0, [1, 2, 3]), 1);
        Assert.False(fragments.TryTake(0, out _));
    }

    [Fact]
    public void RejectsConflictingOverlapWithoutChangingAcceptedBytes()
    {
        var fragments = new DtlsHandshakeFragments();
        fragments.Add(Fragment(0, 3, 0, [1, 2]), 0);
        Assert.Throws<CryptographicException>(() => fragments.Add(Fragment(0, 3, 1, [99, 3]), 0));
        fragments.Add(Fragment(0, 3, 1, [2, 3]), 0);
        Assert.True(fragments.TryTake(0, out var complete));
        Assert.Equal(new byte[] { 1, 2, 3 }, complete[12..]);
    }

    [Fact]
    public void BoundsAdvertisedAllocationsAndPendingMessages()
    {
        var fragments = new DtlsHandshakeFragments();
        Assert.Throws<CryptographicException>(() => fragments.Add(Fragment(0, 0xffffff, 0, []), 0));
        Assert.Throws<CryptographicException>(() => fragments.Add(Fragment(0, 1, 1, [1]), 0));
        for (ushort sequence = 0; sequence < 16; sequence++)
            fragments.Add(Fragment(sequence, 1, 0, []), 0);
        Assert.Throws<CryptographicException>(() => fragments.Add(Fragment(16, 1, 0, []), 0));
    }

    private static byte[] Fragment(ushort sequence, int length, int offset, byte[] bytes)
    {
        var fragment = new byte[12 + bytes.Length]; fragment[0] = 11;
        Write24(fragment.AsSpan(1), length);
        BinaryPrimitives.WriteUInt16BigEndian(fragment.AsSpan(4), sequence);
        Write24(fragment.AsSpan(6), offset); Write24(fragment.AsSpan(9), bytes.Length);
        bytes.CopyTo(fragment, 12);
        return fragment;
    }
    private static void Write24(Span<byte> bytes, int value)
    { bytes[0] = (byte)(value >> 16); bytes[1] = (byte)(value >> 8); bytes[2] = (byte)value; }
}

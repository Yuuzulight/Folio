using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class DtlsRecordCipherTests
{
    [Theory]
    [InlineData(22)]
    [InlineData(23)]
    public void ProtectsRecordsAndRejectsReplay(byte contentType)
    {
        using var sender = new DtlsRecordCipher(new byte[16], new byte[4]);
        using var receiver = new DtlsRecordCipher(new byte[16], new byte[4]);
        var record = sender.Protect(contentType, 1, 0, [1, 2, 3]);
        Assert.Equal(0xfd, record[2]);
        Assert.True(receiver.TryUnprotect(record, out var clear));
        Assert.Equal(new byte[] { 1, 2, 3 }, clear);
        Assert.False(receiver.TryUnprotect(record, out _));
    }

    [Fact]
    public void AuthenticatesHeadersNonceCiphertextAndTagWithoutPoisoningReplayWindow()
    {
        using var sender = new DtlsRecordCipher(new byte[16], new byte[4]);
        using var receiver = new DtlsRecordCipher(new byte[16], new byte[4]);
        var record = sender.Protect(22, 1, 100, [1, 2, 3]);
        foreach (var offset in new[] { 0, 1, 2, 3, 10, 11, 13, 21, record.Length - 1 })
        {
            var changed = (byte[])record.Clone();
            changed[offset] ^= 1;
            Assert.False(receiver.TryUnprotect(changed, out _));
        }
        Assert.False(receiver.TryUnprotect(record[..^1], out _));
        Assert.True(receiver.TryUnprotect(record, out _));
    }

    [Fact]
    public void AcceptsReorderedRecordsWithinWindowAndRejectsOlderRecords()
    {
        using var sender = new DtlsRecordCipher(new byte[16], new byte[4]);
        using var receiver = new DtlsRecordCipher(new byte[16], new byte[4]);
        Assert.True(receiver.TryUnprotect(sender.Protect(22, 1, 100, []), out _));
        Assert.True(receiver.TryUnprotect(sender.Protect(22, 1, 37, []), out _));
        Assert.False(receiver.TryUnprotect(sender.Protect(22, 1, 36, []), out _));
        Assert.True(receiver.TryUnprotect(sender.Protect(22, 1, 200, []), out _));
        Assert.False(receiver.TryUnprotect(sender.Protect(22, 1, 100, []), out _));
    }
}

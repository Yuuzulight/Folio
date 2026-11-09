using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class DtlsRecordCipherTests
{
    [Fact]
    public void TransportApplicationDataUsesAuthenticatedRecordsAndSharedReplayWindow()
    {
        using var transport = new DtlsTransport();
        using var peer = new DtlsRecordCipher(new byte[16], new byte[4]);
        // Isolate the post-handshake record boundary. This fixture does not prove
        // handshake completion; real peer qualification remains a release gate.
        SetField(transport, "_handshakeReader", new DtlsRecordCipher(new byte[16], new byte[4]));
        SetField(transport, "_handshakeWriter", new DtlsRecordCipher(new byte[16], new byte[4]));
        SetField(transport, "_localEpoch", (ushort)1);
        SetField(transport, "<State>k__BackingField", "connected");
        var delivered = new List<byte[]>();
        transport.OnApplicationDataReceived += delivered.Add;
        var record = peer.Protect(23, 1, 5, [42]);
        var corrupt = (byte[])record.Clone(); corrupt[^1] ^= 1;
        transport.ProcessIncomingDatagram(corrupt);
        Assert.Empty(delivered);
        transport.ProcessIncomingDatagram(record);
        transport.ProcessIncomingDatagram(record);
        Assert.Single(delivered);
        Assert.Equal(new byte[] { 42 }, delivered[0]);
        // A handshake record consumes the same sequence space, so replaying it
        // under application content type cannot bypass the replay window.
        transport.ProcessIncomingDatagram(peer.Protect(22, 1, 6, []));
        transport.ProcessIncomingDatagram(peer.Protect(23, 1, 6, [99]));
        Assert.Single(delivered);
        byte[]? sent = null;
        transport.OnDatagramToSend += bytes => sent = bytes;
        transport.SendApplicationData([7, 8]);
        Assert.NotNull(sent);
        Assert.True(peer.TryUnprotect(sent, out var clear));
        Assert.Equal(new byte[] { 7, 8 }, clear);
    }

    private static void SetField(DtlsTransport target, string name, object value) =>
        typeof(DtlsTransport).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);

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

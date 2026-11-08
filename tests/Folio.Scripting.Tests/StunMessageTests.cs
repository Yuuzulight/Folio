using System.Net;
using System.Text;
using Folio.Scripting.Rtc;

namespace Folio.Scripting.Tests;

public sealed class StunMessageTests
{
    // Numeric interoperability vectors from RFC 5769 sections 2.1–2.3:
    // https://www.rfc-editor.org/rfc/rfc5769 (no implementation source copied).
    private const string Request = "000100582112a442b7e7a701bc34d686fa87dfae"
        + "802200105354554e207465737420636c69656e74"
        + "002400046e0001ff80290008932ff9b151263b36"
        + "000600096576746a3a68367659202020"
        + "000800149aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a2"
        + "80280004e57a3bcf";
    private const string V4 = "0101003c2112a442b7e7a701bc34d686fa87dfae"
        + "8022000b7465737420766563746f7220"
        + "002000080001a147e112a643"
        + "000800142b91f599fd9e90c38c7489f92af9ba53f06be7d7"
        + "80280004c07d4c96";
    private const string V6 = "010100482112a442b7e7a701bc34d686fa87dfae"
        + "8022000b7465737420766563746f7220"
        + "002000140002a1470113a9faa5d3f179bc25f4b5bed2b9d9"
        + "00080014a382954e4be67bf11784c97c8292c275bfe3ed41"
        + "80280004c8fb0b4c";
    private static byte[] Key => Encoding.UTF8.GetBytes("VOkJxbRl1RmTxUk/WvJxBt");

    [Theory]
    [InlineData(Request)]
    [InlineData(V4)]
    [InlineData(V6)]
    public void OfficialVectorsVerifyIntegrityAndFingerprintIncludingNonzeroPadding(string vector)
    {
        Assert.True(StunMessage.TryParse(Convert.FromHexString(vector), out var message));
        Assert.True(message.VerifyIntegritySha1(Key));
        Assert.True(message.VerifyFingerprint());
        Assert.False(message.VerifyIntegritySha1("wrong key"u8));
    }

    [Theory]
    [InlineData(V4, "192.0.2.1")]
    [InlineData(V6, "2001:db8:1234:5678:11:2233:4455:6677")]
    public void OfficialMappedAddressVectorsDecodeBothFamilies(string vector, string address)
    {
        Assert.True(StunMessage.TryParse(Convert.FromHexString(vector), out var message));
        Assert.True(message.TryXorMappedAddress(out var endpoint));
        Assert.Equal(new IPEndPoint(IPAddress.Parse(address), 32853), endpoint);
    }

    [Fact]
    public void EncoderAuthenticatesItsHeaderAndAttributesAndParserOwnsItsInput()
    {
        var transaction = Convert.FromHexString("0123456789abcdef01234567");
        var bytes = StunMessage.Encode(0x0001, transaction,
            [(StunMessage.Username, Encoding.UTF8.GetBytes("remote:local"))], Key);
        Assert.True(StunMessage.TryParse(bytes, out var parsed));
        Assert.Equal(transaction, parsed.TransactionId.ToArray());
        Assert.True(parsed.TryAttribute(StunMessage.Username, out var name));
        Assert.Equal("remote:local", Encoding.UTF8.GetString(name.Span));
        Assert.True(parsed.VerifyIntegritySha1(Key));
        Assert.True(parsed.VerifyFingerprint());
        bytes[25] ^= 1;
        Assert.True(parsed.VerifyIntegritySha1(Key));
        Assert.True(StunMessage.TryParse(bytes, out var tampered));
        Assert.False(tampered.VerifyIntegritySha1(Key));
        Assert.False(tampered.VerifyFingerprint());
    }

    [Fact]
    public void FingerprintIsIndependentAndUnauthenticatedSuffixesAreNotAccepted()
    {
        var bytes = Convert.FromHexString(Request);
        bytes[^1] ^= 1;
        Assert.True(StunMessage.TryParse(bytes, out var parsed));
        Assert.True(parsed.VerifyIntegritySha1(Key));
        Assert.False(parsed.VerifyFingerprint());
        // Replacing the final fingerprint with an attacker-controlled attribute must not authorize it.
        bytes[^8] = 0x80; bytes[^7] = 0x23;
        Assert.True(StunMessage.TryParse(bytes, out var suffix));
        Assert.False(suffix.VerifyIntegritySha1(Key));
    }

    [Fact]
    public void MalformedLengthsCookieAndTruncationAreRejected()
    {
        var bytes = Convert.FromHexString(Request);
        for (var size = 0; size < bytes.Length; size++) Assert.False(StunMessage.TryParse(bytes.AsSpan(0, size), out _));
        bytes[4] ^= 1;
        Assert.False(StunMessage.TryParse(bytes, out _));
        bytes = Convert.FromHexString(Request); bytes[0] |= 0x80;
        Assert.False(StunMessage.TryParse(bytes, out _));
        bytes = Convert.FromHexString(Request); bytes[22] = 0xff; bytes[23] = 0xff;
        Assert.False(StunMessage.TryParse(bytes, out _));
    }
}

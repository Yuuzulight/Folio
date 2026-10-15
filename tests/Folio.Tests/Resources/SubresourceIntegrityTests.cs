using System.Security.Cryptography;
using System.Text;
using Folio.Resources;

namespace Folio.Tests.Resources;

public class SubresourceIntegrityTests
{
    private static readonly byte[] Data = Encoding.UTF8.GetBytes("console.log(1);");

    private static string Token(string algorithm) => algorithm + "-" + Convert.ToBase64String(algorithm switch
    {
        "sha256" => SHA256.HashData(Data),
        "sha384" => SHA384.HashData(Data),
        _ => SHA512.HashData(Data),
    });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("md5-abc not-a-hash")]
    public void NoUsableMetadataMeansNoCheck(string? metadata) => Assert.True(SubresourceIntegrity.Matches(Data, metadata));

    [Theory]
    [InlineData("sha256")]
    [InlineData("sha384")]
    [InlineData("sha512")]
    public void EachAlgorithmMatchesItsOwnDigest(string algorithm) => Assert.True(SubresourceIntegrity.Matches(Data, Token(algorithm)));

    [Fact]
    public void AWrongDigestDoesNotMatch() => Assert.False(SubresourceIntegrity.Matches(Data, "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));

    [Fact]
    public void OnlyTheStrongestAlgorithmCounts()
    {
        // A correct sha256 does not rescue a wrong sha512, and a wrong sha256 does not fail a correct sha512.
        Assert.False(SubresourceIntegrity.Matches(Data, $"{Token("sha256")} sha512-AAAA"));
        Assert.True(SubresourceIntegrity.Matches(Data, $"sha256-AAAA {Token("sha512")}"));
    }

    [Fact]
    public void AnyOfTheStrongestTokensMatches() =>
        Assert.True(SubresourceIntegrity.Matches(Data, $"sha384-AAAA {Token("sha384")} sha384-BBBB"));

    [Fact]
    public void OptionsAfterAQuestionMarkAreIgnored() =>
        Assert.True(SubresourceIntegrity.Matches(Data, Token("sha384") + "?ct=application/javascript"));
}

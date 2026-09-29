using System.Text;
using Folio.Resources;

namespace Folio.Tests.Resources;

public sealed class LocalFolderTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("folio-loader-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ReadsFilesUnderAnAllowedFolder()
    {
        File.WriteAllText(Path.Combine(_folder, "a.css"), "p{}");
        var loader = new ResourceLoader([_folder]);

        var response = loader.Load(new ResourceRequest(new Uri(Path.Combine(_folder, "a.css")).AbsoluteUri, ResourceKind.Stylesheet));

        Assert.True(response.Succeeded, response.Error);
        Assert.Equal("p{}", Encoding.UTF8.GetString(response.Data!));
        Assert.Null(response.ContentType);
    }

    [Fact]
    public void AllowedFoldersNeverEnableTheNetwork() =>
        Assert.False(new ResourceLoader([_folder]).Load(new ResourceRequest("https://example.invalid/a.css", ResourceKind.Stylesheet)).Succeeded);

    [Fact]
    public void RefusesPathsOutsideTheFolder()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "inner")).FullName;
        File.WriteAllText(Path.Combine(_folder, "secret.css"), "p{}");
        File.WriteAllText(Path.Combine(_folder, "inner-sibling.css"), "p{}");
        var folder = new LocalFolder(inner);

        Assert.False(folder.Read(Path.Combine(inner, "..", "secret.css"), 1024).Succeeded);
        Assert.False(folder.Read(Path.Combine(_folder, "secret.css"), 1024).Succeeded);
        Assert.False(folder.Read(inner + "-sibling.css", 1024).Succeeded); // shares the folder's name as a prefix
        Assert.False(folder.Read(inner, 1024).Succeeded);
    }

    [Theory]
    [InlineData(@"\\server\share\a.css")]
    [InlineData("//server/share/a.css")]
    [InlineData(@"\\?\C:\a.css")]
    [InlineData(@"\\.\pipe\a")]
    public void RefusesNetworkAndDevicePaths(string path) =>
        Assert.False(new LocalFolder(_folder).Read(path, 1024).Succeeded);

    [Theory]
    [InlineData("a.css:stream")]
    [InlineData("CON")]
    [InlineData("nul.css")]
    [InlineData("com1.txt")]
    public void RefusesStreamsAndReservedNames(string name) =>
        Assert.False(new LocalFolder(_folder).Read(Path.Combine(_folder, name), 1024).Succeeded);

    [Fact]
    public void RefusesLinksThatLeaveTheFolder()
    {
        var inner = Directory.CreateDirectory(Path.Combine(_folder, "inner")).FullName;
        var target = Path.Combine(_folder, "secret.css");
        File.WriteAllText(target, "p{}");
        try
        {
            File.CreateSymbolicLink(Path.Combine(inner, "link.css"), target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("This machine does not allow creating symbolic links.");
        }

        Assert.False(new LocalFolder(inner).Read(Path.Combine(inner, "link.css"), 1024).Succeeded);
    }

    [Fact]
    public void EnforcesTheSizeLimit()
    {
        File.WriteAllText(Path.Combine(_folder, "big.css"), new string('a', 100));
        var url = new Uri(Path.Combine(_folder, "big.css")).AbsoluteUri;

        Assert.False(new ResourceLoader([_folder], maxBytes: 99).Load(new ResourceRequest(url, ResourceKind.Stylesheet)).Succeeded);
        Assert.True(new ResourceLoader([_folder], maxBytes: 100).Load(new ResourceRequest(url, ResourceKind.Stylesheet)).Succeeded);
    }
}

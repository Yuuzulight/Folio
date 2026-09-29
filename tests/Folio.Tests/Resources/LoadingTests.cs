using System.Text;
using Folio.Resources;

namespace Folio.Tests.Resources;

public class LoadingTests
{
    [Theory]
    [InlineData("data:,Hello%2C%20World%21", "Hello, World!", "text/plain;charset=us-ascii")]
    [InlineData("data:text/css,p{}", "p{}", "text/css")]
    [InlineData("data:TEXT/CSS;charset=utf-8,a", "a", "text/css;charset=utf-8")]
    [InlineData("data:text/css;base64,cHt9", "p{}", "text/css")]
    [InlineData("data:text/css;base64,c H t 9", "p{}", "text/css")]
    [InlineData("data:text/css;base64,cHs", "p{", "text/css")]
    [InlineData("data:;base64,cHt9", "p{}", "text/plain;charset=us-ascii")]
    [InlineData("data:text/css,a#fragment", "a", "text/css")]
    public void DecodesDataUrls(string url, string text, string type)
    {
        var response = ResourceLoader.DataUrlsOnly.Load(new ResourceRequest(url, ResourceKind.Stylesheet));
        Assert.True(response.Succeeded, response.Error);
        Assert.Equal(text, Encoding.UTF8.GetString(response.Data!));
        Assert.Equal(type, response.ContentType);
    }

    [Theory]
    [InlineData("data:text/css")]
    [InlineData("data:text/css;base64,c")]
    [InlineData("data:text/css;base64,cH*9")]
    public void RefusesMalformedDataUrls(string url) =>
        Assert.False(ResourceLoader.DataUrlsOnly.Load(new ResourceRequest(url, ResourceKind.Stylesheet)).Succeeded);

    [Theory]
    [InlineData("http://example.invalid/a.css")]
    [InlineData("https://example.invalid/a.css")]
    [InlineData("ftp://example.invalid/a.css")]
    [InlineData("file:///etc/hosts")]
    public void LoadsNothingButDataUrlsByDefault(string url)
    {
        var response = ResourceLoader.DataUrlsOnly.Load(new ResourceRequest(url, ResourceKind.Stylesheet));
        Assert.False(response.Succeeded);
        Assert.Contains("is not allowed", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EnforcesTheSizeLimit()
    {
        Assert.False(new ResourceLoader(maxBytes: 2).Load(new ResourceRequest("data:,abc", ResourceKind.Stylesheet)).Succeeded);
        Assert.True(new ResourceLoader(maxBytes: 3).Load(new ResourceRequest("data:,abc", ResourceKind.Stylesheet)).Succeeded);
    }

    [Theory]
    [InlineData("file:///site/index.html", "a.css", "file:///site/a.css")]
    [InlineData("file:///site/index.html", "../a.css", "file:///a.css")]
    [InlineData("file:///site/index.html", "/a.css", "file:///a.css")]
    [InlineData("file:///site/index.html", "  data:,x ", "data:,x")]
    [InlineData(null, "https://example.invalid/a.css", "https://example.invalid/a.css")]
    [InlineData(null, "data:,x", "data:,x")]
    [InlineData(null, "a.css", null)]
    [InlineData(null, "/a.css", null)]
    [InlineData(null, "C:/a.css", null)]
    public void ResolvesReferences(string? baseUrl, string reference, string? expected) =>
        Assert.Equal(expected, ResourceLoader.Resolve(baseUrl, reference));
}

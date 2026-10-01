using System.Net;
using System.Security.Cryptography;
using Folio.Resources;

namespace Folio.Tests.Resources;

public class AllowlistTests
{
    private static readonly byte[] Font = [0, 1, 0, 0, 1, 2, 3];

    // Answers from a table of URLs (redirects included) and records every request it sees.
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Ok(byte[] body, string type)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return response;
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    }

    private static (AllowlistHttpLoader Loader, FakeServer Server) Loader(AllowlistOptions? options = null)
    {
        var server = new FakeServer();
        return (new AllowlistHttpLoader(options ?? new AllowlistOptions { Origins = [new AllowedOrigin("https://fonts.example", ResourceKind.Font)] }, server), server);
    }

    private static ResourceResponse? Load(IResourceLoader loader, string url, ResourceKind kind = ResourceKind.Font) =>
        loader.LoadAsync(new ResourceRequest(url, kind), CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void FetchesAllowedOriginsAndKindsOnlyOverHttps()
    {
        var (loader, server) = Loader();
        server.Routes["https://fonts.example/v2/a.woff2"] = () => Ok(Font, "font/woff2");

        Assert.Equal(Font, Load(loader, "https://fonts.example/v2/a.woff2")!.Data);
        Assert.False(Load(loader, "https://other.example/v2/a.woff2")!.Succeeded);                       // origin
        Assert.False(Load(loader, "https://fonts.example/v2/a.css", ResourceKind.Stylesheet)!.Succeeded); // kind
        Assert.False(Load(loader, "http://fonts.example/v2/a.woff2")!.Succeeded);                        // not HTTPS
        Assert.Null(Load(loader, "file:///C:/fonts/a.woff2"));                                            // not this loader's
        Assert.Single(server.Requests);
    }

    [Fact]
    public void SendsNoCookiesCredentialsOrReferrer()
    {
        var (loader, server) = Loader();
        server.Routes["https://fonts.example/v2/a.woff2"] = () => Ok(Font, "font/woff2");

        Load(loader, "https://fonts.example/v2/a.woff2");

        var headers = server.Requests[0].Headers;
        Assert.False(headers.Contains("Cookie"));
        Assert.False(headers.Contains("Authorization"));
        Assert.Null(headers.Referrer);
        Assert.Equal("Folio/1", headers.UserAgent.ToString());
    }

    [Fact]
    public void FollowsRedirectsOnlyToAllowedOrigins()
    {
        var (loader, server) = Loader();
        server.Routes["https://fonts.example/v1/a.woff2"] = () => Redirect("https://fonts.example/v1/b.woff2");
        server.Routes["https://fonts.example/v1/b.woff2"] = () => Ok(Font, "font/woff2");
        server.Routes["https://fonts.example/v1/c.woff2"] = () => Redirect("https://tracker.example/v1/c.woff2");

        Assert.True(Load(loader, "https://fonts.example/v1/a.woff2")!.Succeeded);
        Assert.Contains("not allowed", Load(loader, "https://fonts.example/v1/c.woff2")!.Error);
        Assert.DoesNotContain(server.Requests, r => r.RequestUri!.Host == "tracker.example");
    }

    [Fact]
    public void ChecksMimeTypesAndPathPrefixes()
    {
        var (loader, server) = Loader(new AllowlistOptions
        {
            Origins = [new AllowedOrigin("https://cdn.example/fonts", ResourceKind.Font), new AllowedOrigin("https://css.example", ResourceKind.Stylesheet)],
            AllowUnversioned = true,
        });
        server.Routes["https://css.example/a.css"] = () => Ok("body{}"u8.ToArray(), "text/html");
        server.Routes["https://cdn.example/fonts/a.woff2"] = () => Ok(Font, "application/octet-stream");

        Assert.Contains("MIME type", Load(loader, "https://css.example/a.css", ResourceKind.Stylesheet)!.Error);
        Assert.True(Load(loader, "https://cdn.example/fonts/a.woff2")!.Succeeded);
        Assert.False(Load(loader, "https://cdn.example/fontsx/a.woff2")!.Succeeded);
    }

    [Fact]
    public void RefusesUnversionedUrlsUnlessAllowed()
    {
        var (loader, server) = Loader();
        server.Routes["https://fonts.example/a.woff2"] = () => Ok(Font, "font/woff2");

        Assert.Contains("version", Load(loader, "https://fonts.example/a.woff2")!.Error);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void PinnedHashesMustMatch()
    {
        var pin = "sha256-" + Convert.ToBase64String(SHA256.HashData(Font));
        var (loader, server) = Loader(new AllowlistOptions
        {
            Origins = [new AllowedOrigin("https://fonts.example", ResourceKind.Font)],
            PinnedHashes = new Dictionary<string, string> { ["https://fonts.example/a.woff2"] = pin, ["https://fonts.example/b.woff2"] = pin },
            RequirePinnedHashes = true,
        });
        server.Routes["https://fonts.example/a.woff2"] = () => Ok(Font, "font/woff2");
        server.Routes["https://fonts.example/b.woff2"] = () => Ok([9, 9], "font/woff2");

        Assert.True(Load(loader, "https://fonts.example/a.woff2")!.Succeeded);   // pinned, so it needs no version either
        Assert.Contains("pinned hash", Load(loader, "https://fonts.example/b.woff2")!.Error);
        Assert.Contains("no pinned hash", Load(loader, "https://fonts.example/v1/c.woff2")!.Error);
    }

    [Fact]
    public void CachesVersionedResponsesThatPassedTheirChecks()
    {
        var folder = Directory.CreateTempSubdirectory("folio-cache").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "host-file.txt"), "not the cache's");
            var options = new AllowlistOptions { Origins = [new AllowedOrigin("https://fonts.example", ResourceKind.Font)], CacheFolder = folder, MaxCacheBytes = 10 };
            var (first, server) = Loader(options);
            server.Routes["https://fonts.example/v1/a.woff2"] = () => Ok(Font, "font/woff2");
            server.Routes["https://fonts.example/v1/b.woff2"] = () => Ok(Font, "font/woff2");
            Load(first, "https://fonts.example/v1/a.woff2");

            var (second, offline) = Loader(options);
            Assert.Equal(Font, Load(second, "https://fonts.example/v1/a.woff2")!.Data);   // from the cache
            Assert.Empty(offline.Requests);

            Load(first, "https://fonts.example/v1/b.woff2");                             // over 10 bytes: a's entry goes
            Assert.Equal(2, Directory.GetFiles(folder).Length);                           // b's entry and the host's file
            Assert.True(File.Exists(Path.Combine(folder, "host-file.txt")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheDocumentLoaderRefusesWhatTheHostLoaderFailsOrPasses()
    {
        var request = new ResourceRequest("https://fonts.example/v1/a.woff2", ResourceKind.Font);

        Assert.Equal("The loader failed.", new ResourceLoader(host: new Throwing()).Load(request).Error);
        Assert.Contains("not allowed", new ResourceLoader(host: new Passing()).Load(request).Error);
    }

    private sealed class Throwing : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }

    private sealed class Passing : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => Task.FromResult<ResourceResponse?>(null);
    }
}

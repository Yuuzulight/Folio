using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Folio.Resources;

/// <summary>An origin a host lets documents load from, and for which kinds of resource.</summary>
/// <param name="Origin">An HTTPS origin, such as <c>https://fonts.gstatic.com</c>; a path after it limits loads to that prefix.</param>
public sealed record AllowedOrigin(string Origin, params ResourceKind[] Kinds);

/// <summary>What <see cref="ResourceLoaders.Allowlist"/> may fetch and how (docs/study/16-resources-and-security.md).</summary>
public sealed record AllowlistOptions
{
    /// <summary>The origins loads may come from. Only HTTPS is ever fetched.</summary>
    public required IReadOnlyList<AllowedOrigin> Origins { get; init; }

    /// <summary>
    /// A folder for the cache, which keeps every response that passed its checks, by the SHA-256 of its URL. Null keeps
    /// nothing, so each document loads again.
    /// </summary>
    public string? CacheFolder { get; init; }

    /// <summary>The cache's size; the least recently written entries go first when it is over.</summary>
    public long MaxCacheBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    /// Hashes pinned per URL, in Subresource Integrity form (<c>sha256-</c>, <c>sha384-</c> or <c>sha512-</c> and the
    /// base64 digest). A pinned URL loads only when its bytes match.
    /// </summary>
    public IReadOnlyDictionary<string, string> PinnedHashes { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether a URL without a pinned hash is refused.</summary>
    public bool RequirePinnedHashes { get; init; }

    /// <summary>
    /// Whether URLs with no version in their path (such as a font stylesheet chosen by query string) load. They are
    /// never cached, since what they serve can change. Versioned and pinned URLs are cached as immutable.
    /// </summary>
    public bool AllowUnversioned { get; init; }

    /// <summary>How long one request, redirects included, may take.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Fetches allowlisted HTTPS URLs (the study's AllowlistHttpLoader): no cookies, credentials or Referer, a fixed
/// user agent, redirects only to allowlisted origins, a size cap, a timeout, MIME type checks per resource kind, pinned
/// hashes, and a cache of responses that passed every check.
/// </summary>
internal sealed class AllowlistHttpLoader : IResourceLoader
{
    private const int MaxRedirects = 5;
    private readonly AllowlistOptions _options;
    private readonly HttpClient _client;

    public AllowlistHttpLoader(AllowlistOptions options) : this(options, new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = options.Timeout,
    })
    {
    }

    /// <summary>For tests: requests go to <paramref name="handler"/>.</summary>
    internal AllowlistHttpLoader(AllowlistOptions options, HttpMessageHandler handler)
    {
        _options = options;
        _client = new HttpClient(handler) { Timeout = options.Timeout, MaxResponseContentBufferSize = ResourceLoader.MaxResourceBytes };
    }

    public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http"))
            return null;
        if (Refusal(url, request.Kind) is { } refusal)
            return ResourceResponse.Refused(refusal);
        var pinned = _options.PinnedHashes.GetValueOrDefault(request.Url);
        if (pinned is null && _options.RequirePinnedHashes)
            return ResourceResponse.Refused("The URL has no pinned hash.");
        var versioned = pinned is not null || IsVersioned(url);
        if (!versioned && !_options.AllowUnversioned)
            return ResourceResponse.Refused("The URL carries no version, so what it serves could change.");

        var cached = versioned ? CachePath(request.Url) : null;
        if (cached is not null && ReadCache(cached) is { } bytes && Matches(bytes, pinned))
            return ResourceResponse.Loaded(bytes, ContentTypeOf(request.Kind));

        var response = await Fetch(url, request.Kind, cancellationToken).ConfigureAwait(false);
        if (response.Data is not { } data)
            return response;
        if (!Matches(data, pinned))
            return ResourceResponse.Refused("The response does not match its pinned hash.");
        if (cached is not null)
            WriteCache(cached, data);
        return response;
    }

    // Why a URL may not be fetched for a kind of resource, or null when it may.
    private string? Refusal(Uri url, ResourceKind kind)
    {
        if (url.Scheme != "https")
            return "Only HTTPS URLs are fetched.";
        if (url.UserInfo.Length > 0)
            return "URLs with credentials are not fetched.";
        foreach (var allowed in _options.Origins)
        {
            if (Uri.TryCreate(allowed.Origin, UriKind.Absolute, out var origin) && origin.Scheme == "https"
                && string.Equals(origin.Host, url.Host, StringComparison.OrdinalIgnoreCase) && origin.Port == url.Port
                && UnderPrefix(url.AbsolutePath, origin.AbsolutePath) && allowed.Kinds.Contains(kind))
                return null;
        }
        return $"{url.GetLeftPart(UriPartial.Authority)} is not allowed for {kind.ToString().ToLowerInvariant()} loads.";
    }

    // A path under a prefix, at a segment boundary: /fonts/a.woff2 is under /fonts, /fontsx/a.woff2 is not.
    private static bool UnderPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && (prefix.EndsWith('/') || path.Length == prefix.Length || path[prefix.Length] == '/');

    private async Task<ResourceResponse> Fetch(Uri url, ResourceKind kind, CancellationToken cancellationToken)
    {
        try
        {
            for (var redirects = 0; ; redirects++)
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, url);
                message.Headers.UserAgent.Add(new ProductInfoHeaderValue("Folio", "1"));
                message.Headers.TryAddWithoutValidation("Accept", kind switch { ResourceKind.Stylesheet => "text/css", ResourceKind.Font => "font/woff2, font/woff, font/ttf, font/otf, */*;q=0.1", _ => "*/*" });
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = new Uri(url, location);
                    if (redirects == MaxRedirects)
                        return ResourceResponse.Refused("Too many redirects.");
                    if (Refusal(url, kind) is { } refusal)
                        return ResourceResponse.Refused("Redirected to a URL that is not allowed: " + refusal);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return ResourceResponse.Refused($"The server answered {(int)response.StatusCode}.");
                var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (!TypeFits(kind, type))
                    return ResourceResponse.Refused($"A {kind.ToString().ToLowerInvariant()} cannot have the MIME type {type ?? "(none)"}.");
                if (response.Content.Headers.ContentLength > ResourceLoader.MaxResourceBytes)
                    return ResourceResponse.Refused($"The resource is larger than {ResourceLoader.MaxResourceBytes} bytes.");
                return ResourceResponse.Loaded(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false), type);
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return ResourceResponse.Refused(e is OperationCanceledException ? "The load timed out." : "The request failed: " + e.Message);
        }
    }

    // Stylesheets must say they are CSS; fonts may come as any font type or as plain bytes; images as images.
    private static bool TypeFits(ResourceKind kind, string? type) => kind switch
    {
        ResourceKind.Stylesheet => type == "text/css",
        ResourceKind.Font => type is null or "application/octet-stream" or "application/vnd.ms-opentype"
                             || type.StartsWith("font/", StringComparison.Ordinal) || type.StartsWith("application/font-", StringComparison.Ordinal)
                             || type.StartsWith("application/x-font-", StringComparison.Ordinal),
        ResourceKind.Image or ResourceKind.SvgImage => type is not null && type.StartsWith("image/", StringComparison.Ordinal),
        _ => false,
    };

    private static string? ContentTypeOf(ResourceKind kind) => kind switch
    {
        ResourceKind.Stylesheet => "text/css",
        ResourceKind.SvgImage => "image/svg+xml",
        _ => null,
    };

    // A path segment that is a version (v13, 1.2.3, 2024-05) or ends in one (name@1.2.3).
    private static readonly Regex Version = new(@"(^|@)v?\d+([.\-]\d+)*$", RegexOptions.CultureInvariant);

    private static bool IsVersioned(Uri url) => url.Segments.Any(s => Version.IsMatch(s.TrimEnd('/')));

    private static bool Matches(byte[] data, string? pinned)
    {
        if (pinned is null)
            return true;
        var dash = pinned.IndexOf('-');
        if (dash < 0)
            return false;
        byte[] digest = pinned[..dash] switch
        {
            "sha256" => SHA256.HashData(data),
            "sha384" => SHA384.HashData(data),
            "sha512" => SHA512.HashData(data),
            _ => [],
        };
        return digest.Length > 0 && CryptographicOperations.FixedTimeEquals(digest, TryBase64(pinned[(dash + 1)..]));
    }

    private static byte[] TryBase64(string text)
    {
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var length) ? buffer[..length] : [];
    }

    private string? CachePath(string url) =>
        _options.CacheFolder is { } folder ? Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))) : null;

    private static byte[]? ReadCache(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Written whole, then moved into place, so a reader never sees half an entry; the oldest entries go when over size.
    private void WriteCache(string path, byte[] data)
    {
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
            // Only the cache's own entries (64 hex digits, no extension) count and are ever deleted.
            var entries = new DirectoryInfo(folder).GetFiles().Where(f => f.Name.Length == 64 && f.Name.All(char.IsAsciiHexDigitLower))
                .OrderBy(f => f.LastWriteTimeUtc).ToList();
            var total = entries.Sum(f => f.Length);
            foreach (var entry in entries)
            {
                if (total <= _options.MaxCacheBytes)
                    break;
                total -= entry.Length;
                entry.Delete();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written only costs a reload next time.
        }
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using Folio.Resources;

namespace Folio.RenderTests;

/// <summary>One library file in the conformance cache.</summary>
/// <param name="Url">The URL artifacts load it from.</param>
/// <param name="File">Its name under <c>files</c>.</param>
/// <param name="Integrity">Its pin: <c>sha384-</c> and the base64 digest, as an <c>integrity</c> attribute spells it.</param>
/// <param name="LicenceFile">Its licence text under <c>licenses</c>.</param>
public sealed record CachedLibrary(string Url, string File, string Integrity, string Licence, string LicenceFile);

/// <summary>
/// The pinned library files conformance artifacts load (issue #487). CI has no network, so the runner serves these by URL
/// and RefCapture captures references from the same bytes: Folio and the reference browser run identical library code.
/// <c>tools/Add-ConformanceLibrary.ps1</c> adds a file.
/// </summary>
public static class ConformanceCache
{
    public static string Root { get; } = Path.Combine(RepoPaths.Tests, "conformance-cache");

    public static IReadOnlyList<CachedLibrary> Entries(string? root = null)
    {
        var path = Path.Combine(root ?? Root, "cache.json");
        if (!File.Exists(path))
            return [];
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException($"{path}: empty manifest.");
        return manifest.Entries;
    }

    /// <summary>What is wrong with the cache: a file missing, a file that no longer matches its pin, a licence missing. Empty when it is sound.</summary>
    public static IReadOnlyList<string> Problems(string? root = null)
    {
        root ??= Root;
        var problems = new List<string>();
        foreach (var entry in Entries(root))
        {
            var file = Path.Combine(root, "files", entry.File);
            if (!File.Exists(file))
            {
                problems.Add($"{entry.Url}: {entry.File} is missing");
                continue;
            }
            var digest = "sha384-" + Convert.ToBase64String(SHA384.HashData(File.ReadAllBytes(file)));
            if (digest != entry.Integrity)
                problems.Add($"{entry.Url}: {entry.File} no longer matches its pin ({digest} instead of {entry.Integrity})");
            if (string.IsNullOrWhiteSpace(entry.Licence) || !File.Exists(Path.Combine(root, "licenses", entry.LicenceFile)))
                problems.Add($"{entry.Url}: no licence recorded next to {entry.File}");
        }
        return problems;
    }

    /// <summary>Serves the cached libraries by URL, with no network; every other URL is refused with a reason that names the cache.</summary>
    public static IResourceLoader Loader { get; } = new CacheLoader();

    private sealed class CacheLoader : IResourceLoader
    {
        private readonly Dictionary<string, CachedLibrary> _byUrl = Entries().ToDictionary(e => e.Url, StringComparer.Ordinal);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (!_byUrl.TryGetValue(request.Url, out var entry))
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused($"{request.Url} is not in the conformance cache (tests/conformance-cache)"));
            var bytes = File.ReadAllBytes(Path.Combine(Root, "files", entry.File));
            var type = Path.GetExtension(entry.File).ToLowerInvariant() switch
            {
                ".js" or ".mjs" => "text/javascript",
                ".css" => "text/css",
                ".json" => "application/json",
                ".woff2" => "font/woff2",
                _ => "application/octet-stream",
            };
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(bytes, type));
        }
    }

    private sealed record Manifest(List<CachedLibrary> Entries);
}

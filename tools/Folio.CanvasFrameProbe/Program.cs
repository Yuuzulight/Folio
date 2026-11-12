using System.Text.Json;
using Folio.Resources;
using Folio.Typography;

namespace Folio.RenderTests;

// The slice of tests/Folio.RenderTests/Conformance.cs that CanvasFrameBenchmark uses, without Skia, Jint or reflection JSON.

public sealed record ConformanceArtifact(string Root, string Category, string Name)
{
    public string Folder => Path.Combine(Root, Category, Name);

    public string Id => Category + "/" + Name;
}

public sealed record ConformanceManifest(int Viewport);

public static class Conformance
{
    private static readonly string Cache = Path.Combine(RepoPaths.Tests, "conformance-cache");

    public static FolioOptions Options { get; } = new()
    {
        Fonts = new FontSettings
        {
            Source = new FontFolderSource(Path.Combine(RepoPaths.Tests, "fonts")),
            GenericFamilies = new[] { "serif", "sans-serif", "monospace", "system-ui", "cursive", "fantasy", "emoji" }
                .ToDictionary(g => g, _ => (IReadOnlyList<string>)["Folio Box"], StringComparer.OrdinalIgnoreCase)
                .Concat(new[] { new KeyValuePair<string, IReadOnlyList<string>>("Arial", ["Source Sans 3"]) })
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
        },
        Raster = Folio.Painting.SoftwareRasterBackend.Instance,
        UserStyleSheet = "*, *::before, *::after { font-family: \"Source Sans 3\", \"Noto Color Emoji\", \"Noto Sans JP\", \"Noto Sans SC\", \"Noto Sans Arabic\", \"Noto Sans Hebrew\", \"Folio Box\" !important; }",
        Scripting = ScriptingPolicy.ContentProcess,
        Capabilities = new FeatureCapabilities
        {
            Iframes = true, WebGl = true, Media = true, Workers = true, WebAssembly = true,
            WebRtc = true, Camera = true, Microphone = true, ServiceWorkers = true,
        },
        ResourceLoader = new CacheLoader(),
    };

    public static IEnumerable<ConformanceArtifact> Discover()
    {
        var root = Path.Combine(RepoPaths.Tests, "conformance");
        return Directory.EnumerateFiles(root, "index.html", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, Path.GetDirectoryName(file)!).Split(Path.DirectorySeparatorChar))
            .Where(parts => parts.Length == 2)
            .Select(parts => new ConformanceArtifact(root, parts[0], parts[1]))
            .OrderBy(a => a.Id, StringComparer.Ordinal);
    }

    public static ConformanceManifest ManifestOf(ConformanceArtifact artifact)
    {
        var path = Path.Combine(artifact.Folder, "manifest.json");
        if (!File.Exists(path))
            return new(800);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return new(json.RootElement.TryGetProperty("viewport", out var v) ? v.GetInt32() : 800);
    }

    // Serves the pinned libraries from tests/conformance-cache by URL, like ConformanceCache.Loader.
    private sealed class CacheLoader : IResourceLoader
    {
        private readonly Dictionary<string, string> _files = Load();

        private static Dictionary<string, string> Load()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var path = Path.Combine(Cache, "cache.json");
            if (!File.Exists(path))
                return map;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var entry in json.RootElement.GetProperty("entries").EnumerateArray())
                map[entry.GetProperty("url").GetString()!] = entry.GetProperty("file").GetString()!;
            return map;
        }

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (!_files.TryGetValue(request.Url, out var file))
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused($"{request.Url} is not in the conformance cache (tests/conformance-cache)"));
            var type = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".js" or ".mjs" => "text/javascript",
                ".css" => "text/css",
                ".json" => "application/json",
                ".woff2" => "font/woff2",
                _ => "application/octet-stream",
            };
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(File.ReadAllBytes(Path.Combine(Cache, "files", file)), type));
        }
    }
}

public static class Program
{
    public static int Main(string[] args) =>
        CanvasFrameBenchmark.Run(args.Length > 0 && int.TryParse(args[0], out var frames) ? Math.Max(3, frames) : 30);
}

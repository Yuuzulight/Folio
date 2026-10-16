using System.Security.Cryptography;
using System.Text;
using Folio.Resources;

namespace Folio.RenderTests;

/// <summary>The conformance cache (issue #487): the pins hold, and every library an artifact loads is cached and pinned.</summary>
public class ConformanceCacheTests
{
    [Fact]
    public void EveryCachedFileMatchesItsPinAndHasALicence() => Assert.Empty(ConformanceCache.Problems());

    [Fact]
    public void ACachedFileThatNoLongerMatchesItsPinIsReported()
    {
        var root = Directory.CreateTempSubdirectory("folio-cache-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "files"));
            Directory.CreateDirectory(Path.Combine(root, "licenses"));
            var bytes = Encoding.UTF8.GetBytes("var lib = 1;");
            File.WriteAllBytes(Path.Combine(root, "files", "lib.js"), bytes);
            File.WriteAllText(Path.Combine(root, "licenses", "lib.txt"), "MIT");
            var pin = "sha384-" + Convert.ToBase64String(SHA384.HashData(bytes));
            File.WriteAllText(Path.Combine(root, "cache.json"),
                $$"""{"entries":[{"url":"https://example.com/lib.js","file":"lib.js","integrity":"{{pin}}","licence":"MIT","licenceFile":"lib.txt"}]}""");
            Assert.Empty(ConformanceCache.Problems(root));

            File.WriteAllText(Path.Combine(root, "files", "lib.js"), "var lib = 2;");
            Assert.Contains(ConformanceCache.Problems(root), p => p.Contains("no longer matches its pin"));

            File.Delete(Path.Combine(root, "files", "lib.js"));
            Assert.Contains(ConformanceCache.Problems(root), p => p.Contains("is missing"));

            File.WriteAllBytes(Path.Combine(root, "files", "lib.js"), bytes);
            File.Delete(Path.Combine(root, "licenses", "lib.txt"));
            Assert.Contains(ConformanceCache.Problems(root), p => p.Contains("no licence"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TheLoaderServesCachedLibrariesWithoutTheNetworkAndRefusesTheRest()
    {
        var library = ConformanceCache.Entries().FirstOrDefault();
        Assert.SkipWhen(library is null, "The cache is empty.");

        var served = await ConformanceCache.Loader.LoadAsync(new ResourceRequest(library!.Url, ResourceKind.Script), CancellationToken.None);
        Assert.True(served!.Succeeded);
        Assert.Equal(library.Integrity, "sha384-" + Convert.ToBase64String(SHA384.HashData(served.Data!)));

        var refused = await ConformanceCache.Loader.LoadAsync(new ResourceRequest("https://example.com/not-cached.js", ResourceKind.Script), CancellationToken.None);
        Assert.False(refused!.Succeeded);
        Assert.Contains("conformance cache", refused.Error);
    }

    [Fact]
    public void EveryExternalLibraryAnArtifactLoadsIsCachedAndPinnedByItsIntegrityAttribute()
    {
        var cache = ConformanceCache.Entries().ToDictionary(e => e.Url, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var artifact in Conformance.Discover())
        {
            using var document = Document.Parse(File.ReadAllText(Path.Combine(artifact.Folder, "index.html")));
            foreach (var element in document.Node.Descendants.OfType<Folio.Dom.ElementNode>())
            {
                var url = element.LocalName switch
                {
                    "script" => element.GetAttribute("src"),
                    "link" when (element.GetAttribute("rel") ?? "").Contains("stylesheet", StringComparison.OrdinalIgnoreCase) => element.GetAttribute("href"),
                    _ => null,
                };
                if (url is null || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!cache.TryGetValue(url, out var entry))
                    problems.Add($"{artifact.Id}: {url} is not in the conformance cache");
                else if (element.GetAttribute("integrity")?.Trim() != entry.Integrity)
                    problems.Add($"{artifact.Id}: {url} needs integrity=\"{entry.Integrity}\"");
            }
        }
        Assert.Empty(problems);
    }
}

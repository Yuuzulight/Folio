namespace Folio.Resources;

/// <summary>
/// Fetches the resources a document asks for beyond <c>data:</c> URLs, which Folio decodes itself; the host decides
/// what is allowed (docs/study/16-resources-and-security.md, option B). Set one on <see cref="FolioOptions.ResourceLoader"/>;
/// without one, nothing but <c>data:</c> URLs loads.
/// </summary>
public interface IResourceLoader
{
    /// <summary>
    /// Loads a resource: its bytes, a refusal with the reason, or null when this loader does not handle the URL (the
    /// next loader of <see cref="ResourceLoaders.FirstOf"/> is asked; with none left, the load is refused).
    /// </summary>
    Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken);
}

/// <summary>The loaders Folio ships, for a host to choose and combine.</summary>
public static class ResourceLoaders
{
    /// <summary>
    /// Serves <c>file:</c> URLs of files under one folder, refusing anything that could leave it: parent references,
    /// UNC and device paths, alternate data streams, reserved device names and links that point outside it.
    /// </summary>
    public static IResourceLoader LocalFolder(string root) => new LocalFolderLoader(root);

    /// <summary>
    /// Fetches HTTPS URLs from the origins the host allows, for the resource kinds it allows each, with an optional
    /// cache and pinned hashes (<see cref="AllowlistOptions"/>).
    /// </summary>
    public static IResourceLoader Allowlist(AllowlistOptions options) => new AllowlistHttpLoader(options);

    /// <summary>Asks each loader in turn; the first that handles a URL answers for it.</summary>
    public static IResourceLoader FirstOf(params IResourceLoader[] loaders) => new FirstOfLoader([.. loaders]);

    private sealed class LocalFolderLoader(string root) : IResourceLoader
    {
        private readonly LocalFolder _folder = new(root);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(request.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                            && Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.IsFile
                ? _folder.Read(uri.LocalPath, ResourceLoader.MaxResourceBytes)
                : null);
    }

    private sealed class FirstOfLoader(IResourceLoader[] loaders) : IResourceLoader
    {
        public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            foreach (var loader in loaders)
            {
                if (await loader.LoadAsync(request, cancellationToken).ConfigureAwait(false) is { } response)
                    return response;
            }
            return null;
        }
    }
}

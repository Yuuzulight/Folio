namespace Folio.Scripting.Ipc;

/// <summary>Explicit allowlist for resources requested by a content process.</summary>
public sealed class ContentResourcePolicy
{
    public static ContentResourcePolicy DenyAll { get; } = new();

    public bool AllowFile { get; init; }
    public bool AllowLoopbackHttp { get; init; }
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)) return AllowFile;
        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            return uri.IsLoopback && (AllowLoopbackHttp || AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority)));
        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return false;
        if (AllowedOrigins.Contains("https://*")) return true;
        return AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority));
    }
}

namespace Folio.Scripting.Ipc;

/// <summary>Explicit allowlist for resources requested by a content process.</summary>
public sealed class ContentResourcePolicy
{
    public static ContentResourcePolicy DenyAll { get; } = new();

    public bool AllowFile { get; init; }
    public IReadOnlySet<string> AllowedOrigins { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsAllowed(Uri uri)
    {
        if (uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase)) return true;
        if (uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase)) return AllowFile;
        return AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority));
    }
}

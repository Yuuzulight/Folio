namespace Folio;

/// <summary>The colour scheme the host reports to <c>prefers-color-scheme</c>.</summary>
public enum ColorScheme
{
    Light,
    Dark,
}

/// <summary>
/// Limits on the work one document may cause (docs/study/16-resources-and-security.md). Exceeding one stops that part
/// of the work, keeps what is done and adds a <see cref="DiagnosticCode.LimitExceeded"/> diagnostic; it never throws.
/// </summary>
public sealed record ResourceLimits
{
    public static ResourceLimits Default { get; } = new();

    /// <summary>HTML input size: bytes for a stream, characters for a string.</summary>
    public int MaxInputSize { get; init; } = 16 * 1024 * 1024;

    /// <summary>DOM nodes created by the parser.</summary>
    public int MaxNodes { get; init; } = 200_000;

    /// <summary>ElementNode nesting depth; deeper content attaches to the deepest allowed element.</summary>
    public int MaxNestingDepth { get; init; } = 512;

    /// <summary>Rules per stylesheet.</summary>
    public int MaxStyleRules { get; init; } = 100_000;
}

/// <summary>How a host configures a document. Members for resource loading and scripting arrive with those features.</summary>
public sealed class FolioOptions
{
    /// <summary>Resolves relative URLs; null for documents without a location.</summary>
    public Uri? BaseUri { get; init; }

    public ColorScheme ColorScheme { get; init; } = ColorScheme.Light;

    public bool ReducedMotion { get; init; }

    /// <summary>A user stylesheet, applied between the user-agent and author styles.</summary>
    public string? UserStyleSheet { get; init; }

    /// <summary>Where text finds its fonts, and what generic family names stand for. By default there are no fonts.</summary>
    public Typography.FontSettings Fonts { get; init; } = Typography.FontSettings.Default;

    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>
    /// Loads stylesheets, images and fonts beyond <c>data:</c> URLs, which load without it. Null (the default) loads
    /// nothing else, so no request ever leaves the process. <see cref="Resources.ResourceLoaders"/> has local folders and
    /// allowlisted HTTPS origins.
    /// </summary>
    public Resources.IResourceLoader? ResourceLoader { get; init; }

    /// <summary>Whether parse errors are recorded in <see cref="Document.Diagnostics"/>; limit hits always are.</summary>
    public bool CollectDiagnostics { get; init; } = true;
}

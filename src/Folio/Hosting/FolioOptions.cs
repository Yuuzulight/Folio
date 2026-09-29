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

    /// <summary>Element nesting depth; deeper content attaches to the deepest allowed element.</summary>
    public int MaxNestingDepth { get; init; } = 512;

    /// <summary>Rules per stylesheet.</summary>
    public int MaxStyleRules { get; init; } = 100_000;
}

/// <summary>How a host configures a document. Members for resource loading, fonts and scripting arrive with those features.</summary>
public sealed class FolioOptions
{
    /// <summary>Resolves relative URLs; null for documents without a location.</summary>
    public Uri? BaseUri { get; init; }

    public ColorScheme ColorScheme { get; init; } = ColorScheme.Light;

    public bool ReducedMotion { get; init; }

    /// <summary>A user stylesheet, applied between the user-agent and author styles.</summary>
    public string? UserStyleSheet { get; init; }

    public ResourceLimits Limits { get; init; } = ResourceLimits.Default;

    /// <summary>Whether parse errors are recorded in <see cref="Document.Diagnostics"/>; limit hits always are.</summary>
    public bool CollectDiagnostics { get; init; } = true;
}

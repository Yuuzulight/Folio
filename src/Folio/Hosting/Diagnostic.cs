namespace Folio;

public enum DiagnosticCode
{
    /// <summary>Malformed markup or CSS, recovered as the specifications define.</summary>
    ParseError,

    /// <summary>A resource limit stopped part of the work.</summary>
    LimitExceeded,

    /// <summary>The document named a character encoding Folio does not decode; it was read as UTF-8.</summary>
    UnsupportedEncoding,

    /// <summary>A resource was refused by the loader, failed to load or could not be decoded; the document renders without it.</summary>
    ResourceNotLoaded,
}

public enum Severity
{
    Info,
    Warning,
    Error,
}

/// <summary>A 1-based position in the source (after newline normalisation).</summary>
public readonly record struct SourceLocation(int Line, int Column);

/// <summary>
/// Something a host may want to know about a document, for example to report back to the model that wrote an
/// artifact. <paramref name="Feature"/> names the spec feature involved, when there is one.
/// </summary>
public sealed record Diagnostic(DiagnosticCode Code, Severity Severity, string Message, SourceLocation? Location, string? Feature);

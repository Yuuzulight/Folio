namespace Folio.Scripting;

/// <summary>
/// Exception thrown when a script violates configured limits (execution time, statement count,
/// recursion depth, or memory limits).
/// </summary>
public sealed class ScriptLimitExceededException : Exception
{
    public ScriptLimitExceededException(string limitName, string message)
        : base(message)
    {
        LimitName = limitName;
    }

    /// <summary>The name of the limit that was breached ("Timeout", "Statements", "Recursion", "Memory").</summary>
    public string LimitName { get; }
}

/// <summary>
/// Exception thrown when a script is explicitly interrupted via <see cref="IScriptEngine.Interrupt"/>.
/// Callers can differentiate this termination from normal script runtime errors.
/// </summary>
public sealed class ScriptInterruptedException : Exception
{
    public ScriptInterruptedException(string message = "Script execution was interrupted.")
        : base(message)
    {
    }
}

/// <summary>
/// Exception thrown when script execution fails due to a standard JavaScript exception (TypeError, SyntaxError, Error, etc.).
/// </summary>
public sealed class ScriptRuntimeException : Exception
{
    public ScriptRuntimeException(string message, string? stackTrace = null, int lineNumber = 0, int columnNumber = 0, string? source = null)
        : base(message)
    {
        ScriptStackTrace = stackTrace;
        LineNumber = lineNumber;
        ColumnNumber = columnNumber;
        SourceLocation = source;
    }

    public string? ScriptStackTrace { get; }
    public int LineNumber { get; }
    public int ColumnNumber { get; }
    public string? SourceLocation { get; }
}

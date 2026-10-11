namespace Folio.Scripting;

/// <summary>
/// Execution limits configured on an <see cref="IScriptEngine"/> to prevent denial of service,
/// infinite loops, and excessive memory usage by untrusted scripts.
/// </summary>
public sealed record ScriptLimits
{
    /// <summary>Default execution limits suitable for sandboxed artifacts.</summary>
    public static readonly ScriptLimits Default = new();

    /// <summary>Maximum elapsed execution time allowed for a single script evaluation or call task.</summary>
    public TimeSpan? Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum number of statements allowed per task execution.</summary>
    public int? MaxStatements { get; init; } = 2_000_000;

    /// <summary>Maximum recursion / call stack depth allowed.</summary>
    public int? MaxRecursionDepth { get; init; } = 256;

    /// <summary>Maximum memory allocation in bytes permitted by the script engine.</summary>
    public long? MemoryLimitBytes { get; init; } = 64 * 1024 * 1024; // 64 MB
}

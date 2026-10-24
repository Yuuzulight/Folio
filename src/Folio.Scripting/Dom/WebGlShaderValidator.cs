using System.Text.RegularExpressions;

namespace Folio.Scripting.Dom;

public readonly record struct WebGlShaderValidationResult(bool IsValid, string InfoLog, int LineNumber = 0);

/// <summary>
/// Validates GLSL ES 1.00 and 3.00 shader source text for WebGL contexts.
/// Ensures limits, syntax validity, entry points, and prevents crashes on arbitrary or malformed input.
/// </summary>
public static partial class WebGlShaderValidator
{
    private const int MaxSourceLength = 1_000_000;

    [GeneratedRegex(@"\bvoid\s+main\s*\(\s*(void)?\s*\)\s*\{", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex MainEntryPointRegex();

    [GeneratedRegex(@"^#\s*version\s+([^\r\n]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex VersionDirectiveRegex();

    public static WebGlShaderValidationResult Validate(int shaderType, string? source)
    {
        if (source is null || source.Length == 0)
            return new WebGlShaderValidationResult(false, "ERROR: 0:1: Shader source is empty.", 1);

        if (source.Length > MaxSourceLength)
            return new WebGlShaderValidationResult(false, "ERROR: 0:1: Shader source exceeds the host limit of 1,000,000 characters.", 1);

        // Scan for null bytes or illegal control characters outside whitespace
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\0')
                return new WebGlShaderValidationResult(false, "ERROR: 0:1: Shader source contains a null character.", 1);
            if (c < 32 && c is not ('\t' or '\n' or '\r' or '\v' or '\f'))
                return new WebGlShaderValidationResult(false, "ERROR: 0:1: Shader source contains invalid control characters.", 1);
        }

        // Strip comments while preserving line breaks for accurate line number reporting
        var lines = source.Split('\n');
        var cleanLines = new string[lines.Length];
        var inBlockComment = false;

        for (var lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            var line = lines[lineIdx];
            var clean = new char[line.Length];
            var cleanLen = 0;
            var i = 0;

            while (i < line.Length)
            {
                if (inBlockComment)
                {
                    var end = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (end != -1)
                    {
                        inBlockComment = false;
                        i = end + 2;
                    }
                    else
                    {
                        break;
                    }
                }
                else
                {
                    if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
                    {
                        break; // single-line comment to end of line
                    }
                    if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
                    {
                        inBlockComment = true;
                        i += 2;
                    }
                    else
                    {
                        clean[cleanLen++] = line[i++];
                    }
                }
            }
            cleanLines[lineIdx] = new string(clean, 0, cleanLen);
        }

        if (inBlockComment)
            return new WebGlShaderValidationResult(false, $"ERROR: 0:{lines.Length}: Unterminated multi-line comment.", lines.Length);

        // Validate version directive if present
        var versionMatch = VersionDirectiveRegex().Match(source);
        if (versionMatch.Success)
        {
            var versionArgs = versionMatch.Groups[1].Value.Trim();
            if (!string.Equals(versionArgs, "300 es", StringComparison.Ordinal) &&
                !string.Equals(versionArgs, "100", StringComparison.Ordinal) &&
                !string.Equals(versionArgs, "100 es", StringComparison.Ordinal))
            {
                var lineNum = ComputeLineNumber(source, versionMatch.Index);
                return new WebGlShaderValidationResult(false, $"ERROR: 0:{lineNum}: Unsupported GLSL ES version '{versionArgs}'.", lineNum);
            }
        }

        // Validate characters outside comments
        for (var lineIdx = 0; lineIdx < cleanLines.Length; lineIdx++)
        {
            var line = cleanLines[lineIdx];
            for (var col = 0; col < line.Length; col++)
            {
                var c = line[col];
                if (c is '@' or '$' or '`')
                {
                    var lineNum = lineIdx + 1;
                    return new WebGlShaderValidationResult(false, $"ERROR: 0:{lineNum}: Invalid character '{c}' in shader source.", lineNum);
                }
            }
        }

        // Validate balanced brackets, braces, parentheses
        var stack = new Stack<(char Delim, int Line)>();
        for (var lineIdx = 0; lineIdx < cleanLines.Length; lineIdx++)
        {
            var line = cleanLines[lineIdx];
            for (var col = 0; col < line.Length; col++)
            {
                var c = line[col];
                if (c is '(' or '[' or '{')
                {
                    stack.Push((c, lineIdx + 1));
                }
                else if (c is ')' or ']' or '}')
                {
                    if (stack.Count == 0)
                        return new WebGlShaderValidationResult(false, $"ERROR: 0:{lineIdx + 1}: Unexpected closing delimiter '{c}'.", lineIdx + 1);

                    var open = stack.Pop();
                    var expected = open.Delim switch
                    {
                        '(' => ')',
                        '[' => ']',
                        '{' => '}',
                        _ => '\0'
                    };
                    if (c != expected)
                        return new WebGlShaderValidationResult(false, $"ERROR: 0:{lineIdx + 1}: Mismatched delimiter: expected '{expected}', found '{c}'.", lineIdx + 1);
                }
            }
        }

        if (stack.Count > 0)
        {
            var unclosed = stack.Pop();
            return new WebGlShaderValidationResult(false, $"ERROR: 0:{unclosed.Line}: Unclosed delimiter '{unclosed.Delim}'.", unclosed.Line);
        }

        // Validate entry point: void main()
        var cleanFull = string.Join('\n', cleanLines);
        if (!MainEntryPointRegex().IsMatch(cleanFull))
            return new WebGlShaderValidationResult(false, "ERROR: 0:1: Shader source is missing a valid 'void main()' entry point.", 1);

        return new WebGlShaderValidationResult(true, "");
    }

    private static int ComputeLineNumber(string source, int charIndex)
    {
        var line = 1;
        for (var i = 0; i < charIndex && i < source.Length; i++)
        {
            if (source[i] == '\n') line++;
        }
        return line;
    }
}

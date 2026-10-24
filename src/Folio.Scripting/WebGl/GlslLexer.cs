using System.Globalization;
using System.Text;

namespace Folio.Scripting.WebGl;

internal enum GlslTokenKind { Ident, Int, Float, Punct, End }

internal readonly record struct GlslToken(GlslTokenKind Kind, string Text, int Line, double Number = 0)
{
    public bool Is(string text) => Kind is GlslTokenKind.Punct or GlslTokenKind.Ident && Text == text;
}

internal sealed class GlslCompileException(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
}

/// <summary>The GLSL ES preprocessor and lexer: comments, line continuations, <c>#define</c> (object and function-like), conditionals, then tokens.</summary>
internal sealed class GlslLexer
{
    private sealed record Macro(string[]? Parameters, List<GlslToken> Body);

    private readonly Dictionary<string, Macro> _macros = [];
    private readonly List<GlslToken> _output = [];

    public int Version { get; private set; } = 100;

    public static List<GlslToken> Lex(string source, out int version)
    {
        var lexer = new GlslLexer();
        lexer._macros["GL_ES"] = new Macro(null, [new GlslToken(GlslTokenKind.Int, "1", 0, 1)]);
        lexer._macros["GL_FRAGMENT_PRECISION_HIGH"] = new Macro(null, [new GlslToken(GlslTokenKind.Int, "1", 0, 1)]);
        lexer.Run(source);
        version = lexer.Version;
        lexer._output.Add(new GlslToken(GlslTokenKind.End, "<end>", lexer._lastLine));
        return lexer._output;
    }

    private int _lastLine = 1;

    private void Run(string source)
    {
        // Comments become spaces (a block comment keeps its newlines so line numbers stay right); a backslash-newline joins lines.
        var clean = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                i--;
            }
            else if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) throw new GlslCompileException("unterminated comment", clean.ToString().Count(ch => ch == '\n') + 1);
                for (var j = i; j < end; j++) if (source[j] == '\n') clean.Append('\n');
                clean.Append(' ');
                i = end + 1;
            }
            else if (c == '\\' && i + 1 < source.Length && (source[i + 1] == '\n' || (source[i + 1] == '\r' && i + 2 < source.Length && source[i + 2] == '\n')))
            {
                i += source[i + 1] == '\r' ? 2 : 1;
                clean.Append('\u0001'); // a joined line still counts toward line numbers
            }
            else
            {
                clean.Append(c);
            }
        }

        var lines = clean.ToString().Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var active = new Stack<(bool Parent, bool Taken, bool Current)>();
        var on = true;
        var lineNo = 0;
        foreach (var rawLine in lines)
        {
            lineNo++;
            _lastLine = lineNo;
            var joined = rawLine.Count(ch => ch == '\u0001');
            var line = rawLine.Replace("\u0001", "");
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var directive = trimmed[1..].TrimStart();
                var nameEnd = 0;
                while (nameEnd < directive.Length && (char.IsLetter(directive[nameEnd]) || directive[nameEnd] == '_')) nameEnd++;
                var name = directive[..nameEnd];
                var rest = directive[nameEnd..].Trim();
                switch (name)
                {
                    case "if":
                    case "ifdef":
                    case "ifndef":
                    {
                        var parent = on;
                        var value = parent && (name == "if" ? EvaluateCondition(rest, lineNo) != 0 : _macros.ContainsKey(rest.Split(' ', '\t')[0]) == (name == "ifdef"));
                        active.Push((parent, value, value));
                        on = value;
                        break;
                    }
                    case "elif":
                    {
                        if (active.Count == 0) throw new GlslCompileException("#elif without #if", lineNo);
                        var (parent, taken, _) = active.Pop();
                        var value = parent && !taken && EvaluateCondition(rest, lineNo) != 0;
                        active.Push((parent, taken || value, value));
                        on = value;
                        break;
                    }
                    case "else":
                    {
                        if (active.Count == 0) throw new GlslCompileException("#else without #if", lineNo);
                        var (parent, taken, _) = active.Pop();
                        var value = parent && !taken;
                        active.Push((parent, true, value));
                        on = value;
                        break;
                    }
                    case "endif":
                    {
                        if (active.Count == 0) throw new GlslCompileException("#endif without #if", lineNo);
                        var (parent, _, _) = active.Pop();
                        on = parent;
                        break;
                    }
                    case "define" when on:
                        Define(rest, lineNo);
                        break;
                    case "undef" when on:
                        _macros.Remove(rest.Split(' ', '\t')[0]);
                        break;
                    case "version" when on:
                    {
                        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
                        Version = int.TryParse(digits, out var v) ? v : 100;
                        if (Version != 100 && Version != 300) throw new GlslCompileException($"unsupported version {Version}", lineNo);
                        _macros["__VERSION__"] = new Macro(null, [new GlslToken(GlslTokenKind.Int, digits, lineNo, Version)]);
                        break;
                    }
                    case "extension" or "pragma" or "line" or "error" when on:
                        if (name == "error") throw new GlslCompileException("#error " + rest, lineNo);
                        break;
                }
                lineNo += joined;
                continue;
            }
            if (!on)
            {
                lineNo += joined;
                continue;
            }
            Tokenize(line, lineNo, _output, expand: true);
            lineNo += joined;
        }
        if (active.Count > 0) throw new GlslCompileException("unterminated #if", lineNo);
    }

    private void Define(string rest, int line)
    {
        var i = 0;
        while (i < rest.Length && (char.IsLetterOrDigit(rest[i]) || rest[i] == '_')) i++;
        if (i == 0) throw new GlslCompileException("#define needs a name", line);
        var name = rest[..i];
        string[]? parameters = null;
        if (i < rest.Length && rest[i] == '(')
        {
            var close = rest.IndexOf(')', i);
            if (close < 0) throw new GlslCompileException("#define: missing ')'", line);
            parameters = rest[(i + 1)..close].Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
            i = close + 1;
        }
        var body = new List<GlslToken>();
        Tokenize(rest[i..], line, body, expand: false);
        _macros[name] = new Macro(parameters, body);
    }

    private long EvaluateCondition(string text, int line)
    {
        // defined(X) / defined X first, then macros, then the integer expression.
        var tokens = new List<GlslToken>();
        var replaced = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            if (string.CompareOrdinal(text, i, "defined", 0, 7) == 0 && (i + 7 >= text.Length || !char.IsLetterOrDigit(text[i + 7])))
            {
                var j = i + 7;
                while (j < text.Length && text[j] == ' ') j++;
                var paren = j < text.Length && text[j] == '(';
                if (paren) j++;
                var start = j;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;
                var id = text[start..j];
                if (paren)
                {
                    while (j < text.Length && text[j] == ' ') j++;
                    if (j < text.Length && text[j] == ')') j++;
                }
                replaced.Append(_macros.ContainsKey(id) ? " 1 " : " 0 ");
                i = j;
            }
            else
            {
                replaced.Append(text[i++]);
            }
        }
        Tokenize(replaced.ToString(), line, tokens, expand: true);
        var position = 0;
        return Or();

        GlslToken Peek() => position < tokens.Count ? tokens[position] : new GlslToken(GlslTokenKind.End, "", line);
        long Or()
        {
            var value = And();
            while (Peek().Is("||")) { position++; var r = And(); value = value != 0 || r != 0 ? 1 : 0; }
            return value;
        }
        long And()
        {
            var value = Equality();
            while (Peek().Is("&&")) { position++; var r = Equality(); value = value != 0 && r != 0 ? 1 : 0; }
            return value;
        }
        long Equality()
        {
            var value = Relational();
            while (Peek().Is("==") || Peek().Is("!="))
            {
                var op = tokens[position++].Text;
                var r = Relational();
                value = (op == "==" ? value == r : value != r) ? 1 : 0;
            }
            return value;
        }
        long Relational()
        {
            var value = Additive();
            while (Peek().Is("<") || Peek().Is(">") || Peek().Is("<=") || Peek().Is(">="))
            {
                var op = tokens[position++].Text;
                var r = Additive();
                value = (op switch { "<" => value < r, ">" => value > r, "<=" => value <= r, _ => value >= r }) ? 1 : 0;
            }
            return value;
        }
        long Additive()
        {
            var value = Multiplicative();
            while (Peek().Is("+") || Peek().Is("-"))
            {
                var op = tokens[position++].Text;
                var r = Multiplicative();
                value = op == "+" ? value + r : value - r;
            }
            return value;
        }
        long Multiplicative()
        {
            var value = Unary();
            while (Peek().Is("*") || Peek().Is("/") || Peek().Is("%"))
            {
                var op = tokens[position++].Text;
                var r = Unary();
                if (op != "*" && r == 0) throw new GlslCompileException("division by zero in #if", line);
                value = op == "*" ? value * r : op == "/" ? value / r : value % r;
            }
            return value;
        }
        long Unary()
        {
            var t = Peek();
            if (t.Is("!")) { position++; return Unary() == 0 ? 1 : 0; }
            if (t.Is("-")) { position++; return -Unary(); }
            if (t.Is("+")) { position++; return Unary(); }
            if (t.Is("(")) { position++; var v = Or(); if (Peek().Is(")")) position++; return v; }
            if (t.Kind == GlslTokenKind.Int) { position++; return (long)t.Number; }
            if (t.Kind == GlslTokenKind.Ident) { position++; return 0; }
            throw new GlslCompileException("invalid #if expression", line);
        }
    }

    private const int MaxTokens = 400_000;

    private void Tokenize(string text, int line, List<GlslToken> into, bool expand, HashSet<string>? hideSet = null)
    {
        if (_output.Count > MaxTokens || into.Count > MaxTokens)
            throw new GlslCompileException("the shader is too large after macro expansion", line);
        var tokens = new List<GlslToken>();
        ScanTokens(text, line, tokens);
        if (!expand)
        {
            into.AddRange(tokens);
            return;
        }
        Expand(tokens, into, hideSet ?? []);
    }

    private void Expand(List<GlslToken> tokens, List<GlslToken> into, HashSet<string> hide)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (into.Count > MaxTokens || _output.Count + into.Count > MaxTokens)
                throw new GlslCompileException("the shader is too large after macro expansion", token.Line);
            if (token.Kind != GlslTokenKind.Ident || hide.Contains(token.Text) || !_macros.TryGetValue(token.Text, out var macro))
            {
                into.Add(token);
                continue;
            }
            if (macro.Parameters is null)
            {
                var inner = new HashSet<string>(hide) { token.Text };
                Expand(macro.Body.Select(b => b with { Line = token.Line }).ToList(), into, inner);
                continue;
            }
            // Function-like: only expanded when followed by '('.
            if (i + 1 >= tokens.Count || !tokens[i + 1].Is("("))
            {
                into.Add(token);
                continue;
            }
            var args = new List<List<GlslToken>>();
            var depth = 0;
            var current = new List<GlslToken>();
            var j = i + 2;
            for (; j < tokens.Count; j++)
            {
                var t = tokens[j];
                if (t.Is("(")) depth++;
                else if (t.Is(")"))
                {
                    if (depth == 0) break;
                    depth--;
                }
                if (t.Is(",") && depth == 0)
                {
                    args.Add(current);
                    current = [];
                    continue;
                }
                current.Add(t);
            }
            if (j >= tokens.Count) throw new GlslCompileException($"unterminated macro call '{token.Text}'", token.Line);
            if (current.Count > 0 || args.Count > 0) args.Add(current);
            if (args.Count != macro.Parameters.Length)
                throw new GlslCompileException($"macro '{token.Text}' takes {macro.Parameters.Length} arguments", token.Line);
            var substituted = new List<GlslToken>();
            foreach (var b in macro.Body)
            {
                var index = b.Kind == GlslTokenKind.Ident ? Array.IndexOf(macro.Parameters, b.Text) : -1;
                if (index >= 0) substituted.AddRange(args[index]);
                else substituted.Add(b with { Line = token.Line });
            }
            var inner2 = new HashSet<string>(hide) { token.Text };
            Expand(substituted, into, inner2);
            i = j;
        }
    }

    private static readonly string[] Operators =
    [
        "<<=", ">>=", "<<", ">>", "<=", ">=", "==", "!=", "&&", "||", "^^", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=",
        "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "&", "|", "^", "?", ":", ";", ",", ".", "(", ")", "[", "]", "{", "}",
    ];

    private static void ScanTokens(string text, int line, List<GlslToken> into)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                into.Add(new GlslToken(GlslTokenKind.Ident, text[start..i], line));
                continue;
            }
            if (Digit(c) || (c == '.' && i + 1 < text.Length && Digit(text[i + 1])))
            {
                into.Add(ScanNumber(text, ref i, line));
                continue;
            }
            var op = Operators.FirstOrDefault(o => string.CompareOrdinal(text, i, o, 0, o.Length) == 0);
            if (op is null) throw new GlslCompileException($"invalid character '{c}'", line);
            into.Add(new GlslToken(GlslTokenKind.Punct, op, line));
            i += op.Length;
        }
    }

    private static bool Digit(char c) => c is >= '0' and <= '9';

    private static GlslToken ScanNumber(string text, ref int i, int line)
    {
        var start = i;
        if (text[i] == '0' && i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
        {
            i += 2;
            while (i < text.Length && Uri.IsHexDigit(text[i])) i++;
            var hex = text[(start + 2)..i];
            if (i < text.Length && text[i] is 'u' or 'U') i++;
            if (!long.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hexValue))
                throw new GlslCompileException($"'{text[start..i]}' : invalid hexadecimal constant", line);
            return new GlslToken(GlslTokenKind.Int, text[start..i], line, hexValue);
        }
        var isFloat = false;
        while (i < text.Length && Digit(text[i])) i++;
        if (i < text.Length && text[i] == '.') { isFloat = true; i++; while (i < text.Length && Digit(text[i])) i++; }
        if (i < text.Length && text[i] is 'e' or 'E')
        {
            var save = i;
            i++;
            if (i < text.Length && text[i] is '+' or '-') i++;
            if (i < text.Length && Digit(text[i])) { isFloat = true; while (i < text.Length && Digit(text[i])) i++; }
            else i = save;
        }
        var number = text[start..i];
        if (i < text.Length && text[i] is 'f' or 'F') { isFloat = true; i++; }
        else if (i < text.Length && text[i] is 'u' or 'U') i++;
        if (isFloat)
        {
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue))
                throw new GlslCompileException($"'{number}' : invalid floating-point constant", line);
            return new GlslToken(GlslTokenKind.Float, number, line, floatValue);
        }
        // Octal literals start with 0.
        long value;
        try
        {
            value = number.Length > 1 && number[0] == '0' ? Convert.ToInt64(number, 8) : long.Parse(number, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            throw new GlslCompileException($"'{number}' : invalid integer constant", line);
        }
        return new GlslToken(GlslTokenKind.Int, number, line, value);
    }
}

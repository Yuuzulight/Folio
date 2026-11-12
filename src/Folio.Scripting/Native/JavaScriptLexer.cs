using System.Buffers;

namespace Folio.Scripting.Native;

/// <summary>Token kinds used by the Folio-owned JavaScript front end.</summary>
internal enum JavaScriptTokenKind
{
    End,
    Identifier,
    Number,
    String,
    Template,
    Regex,
    Punctuation,
}

/// <summary>A bounded lexical token with a source location.</summary>
internal readonly record struct JavaScriptToken(JavaScriptTokenKind Kind, string Text, int Offset, int Line, int Column);

/// <summary>
/// Small, allocation-bounded lexical front end for the future Folio-owned JavaScript VM.
/// It deliberately does not evaluate code or guess at syntax: unsupported constructs are
/// represented as punctuation and can be rejected by the parser with a precise location.
/// </summary>
internal sealed class JavaScriptLexer
{
    private const int TokenTextCacheThreshold = 16 * 1024;
    private const int MaxCachedTokenTexts = 4096;
    private const int MaxCachedTokenLength = 64;
    private static readonly SearchValues<char> SingleQuoteStops = SearchValues.Create("'\\\r\n");
    private static readonly SearchValues<char> DoubleQuoteStops = SearchValues.Create("\"\\\r\n");

    private readonly string _source;
    private readonly int _maxTokens;
    private readonly Dictionary<string, string>? _tokenTexts;
    private int _index;
    private int _line = 1;
    private int _column = 1;
    private int _tokens;
    private Stack<bool>? _parentheses;
    private bool _afterMemberAccess;
    private bool _controlHead;
    private bool _closedControlHead;
    private bool _expressionKeyword;

    internal JavaScriptLexer(string source, int maxTokens = 1_000_000)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (maxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        _maxTokens = maxTokens;
        if (source.Length >= TokenTextCacheThreshold)
            _tokenTexts = new(StringComparer.Ordinal);
    }

    internal JavaScriptToken Next()
    {
        var token = ReadNext();
        _closedControlHead = false;
        if (token.Kind == JavaScriptTokenKind.Punctuation)
        {
            if (token.Text == "(")
                (_parentheses ??= new()).Push(_controlHead);
            else if (token.Text == ")" && _parentheses is { } parentheses && parentheses.TryPop(out var control))
                _closedControlHead = control;
        }
        var keyword = token.Kind == JavaScriptTokenKind.Identifier && !_afterMemberAccess;
        _controlHead = keyword && token.Text is ("if" or "while" or "for" or "with");
        _expressionKeyword = keyword && token.Text is ("return" or "throw" or "case" or "else" or "do");
        _afterMemberAccess = token.Kind == JavaScriptTokenKind.Punctuation && token.Text is ("." or "?.");
        return token;
    }

    private JavaScriptToken ReadNext()
    {
        SkipTrivia();
        if (++_tokens > _maxTokens)
            throw Error("The script contains too many tokens.");
        var offset = _index;
        var line = _line;
        var column = _column;
        if (_index >= _source.Length)
            return new(JavaScriptTokenKind.End, string.Empty, offset, line, column);

        var c = _source[_index];
        if (IsIdentifierStart(c)) return ReadIdentifier(offset, line, column);
        if (char.IsDigit(c) || c == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
            return ReadNumber(offset, line, column);
        if (c is '\'' or '"') return ReadString(offset, line, column, c);
        if (c == '`') return ReadTemplate(offset, line, column);
        if (c == '/' && IsRegexStart()) return ReadRegex(offset, line, column);

        var rem = _source.AsSpan(_index);
        var longOperator = c switch
        {
            '>' when rem.StartsWith(">>>=") => ">>>=",
            '>' when rem.StartsWith(">>>") => ">>>",
            '>' when rem.StartsWith(">>=") => ">>=",
            '<' when rem.StartsWith("<<=") => "<<=",
            '=' when rem.StartsWith("===") => "===",
            '!' when rem.StartsWith("!==") => "!==",
            '.' when rem.StartsWith("...") => "...",
            '&' when rem.StartsWith("&&=") => "&&=",
            '|' when rem.StartsWith("||=") => "||=",
            '?' when rem.StartsWith("??=") => "??=",
            _ => null,
        };
        if (longOperator is not null)
        {
            _index += longOperator.Length;
            _column += longOperator.Length;
            return new(JavaScriptTokenKind.Punctuation, longOperator, offset, line, column);
        }
        var optionalChain = c == '?' && rem.StartsWith("?.") && !(rem.Length > 2 && char.IsDigit(rem[2]));
        if (optionalChain)
        {
            Advance(); Advance();
            return new(JavaScriptTokenKind.Punctuation, "?.", offset, line, column);
        }
        if (rem.Length >= 2)
        {
            var twoOp = rem[..2] switch
            {
                "=>" => "=>",
                "==" => "==",
                "!=" => "!=",
                "<=" => "<=",
                ">=" => ">=",
                "&&" => "&&",
                "||" => "||",
                "??" => "??",
                "++" => "++",
                "--" => "--",
                "**" => "**",
                "<<" => "<<",
                ">>" => ">>",
                "+=" => "+=",
                "-=" => "-=",
                "*=" => "*=",
                "/=" => "/=",
                "%=" => "%=",
                "&=" => "&=",
                "|=" => "|=",
                "^=" => "^=",
                _ => null
            };
            if (twoOp is not null)
            {
                Advance(); Advance();
                return new(JavaScriptTokenKind.Punctuation, twoOp, offset, line, column);
            }
        }
        Advance();
        var single = c switch
        {
            ';' => ";",
            '(' => "(",
            ')' => ")",
            '{' => "{",
            '}' => "}",
            '[' => "[",
            ']' => "]",
            ',' => ",",
            '.' => ".",
            ':' => ":",
            '?' => "?",
            '+' => "+",
            '-' => "-",
            '*' => "*",
            '/' => "/",
            '%' => "%",
            '=' => "=",
            '<' => "<",
            '>' => ">",
            '!' => "!",
            '&' => "&",
            '|' => "|",
            '^' => "^",
            '~' => "~",
            _ => _source[offset.._index]
        };
        return new(JavaScriptTokenKind.Punctuation, single, offset, line, column);
    }

    private JavaScriptToken ReadIdentifier(int offset, int line, int column)
    {
        Advance();
        while (_index < _source.Length && IsIdentifierPart(_source[_index])) Advance();
        var span = _source.AsSpan(offset, _index - offset);
        var text = span switch
        {
            "function" => "function",
            "return" => "return",
            "var" => "var",
            "let" => "let",
            "const" => "const",
            "if" => "if",
            "else" => "else",
            "for" => "for",
            "while" => "while",
            "do" => "do",
            "switch" => "switch",
            "case" => "case",
            "default" => "default",
            "break" => "break",
            "continue" => "continue",
            "this" => "this",
            "new" => "new",
            "true" => "true",
            "false" => "false",
            "null" => "null",
            "undefined" => "undefined",
            "typeof" => "typeof",
            "instanceof" => "instanceof",
            "in" => "in",
            "of" => "of",
            "try" => "try",
            "catch" => "catch",
            "finally" => "finally",
            "throw" => "throw",
            "class" => "class",
            "extends" => "extends",
            "super" => "super",
            "static" => "static",
            "async" => "async",
            "await" => "await",
            "yield" => "yield",
            "get" => "get",
            "set" => "set",
            "prototype" => "prototype",
            "constructor" => "constructor",
            "length" => "length",
            "name" => "name",
            "value" => "value",
            "target" => "target",
            "document" => "document",
            "window" => "window",
            "globalThis" => "globalThis",
            _ => ReadTokenText(span)
        };
        return new(JavaScriptTokenKind.Identifier, text, offset, line, column);
    }

    private string ReadTokenText(ReadOnlySpan<char> span)
    {
        if (_tokenTexts is null || span.Length > MaxCachedTokenLength)
            return span.ToString();
        if (_tokenTexts.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(span, out var cached))
            return cached;
        var text = span.ToString();
        if (_tokenTexts.Count < MaxCachedTokenTexts)
            _tokenTexts.Add(text, text);
        return text;
    }

    private JavaScriptToken ReadNumber(int offset, int line, int column)
    {
        if (_source[_index] == '0' && _index + 1 < _source.Length && _source[_index + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            Advance(); Advance();
            while (_index < _source.Length && (char.IsAsciiLetterOrDigit(_source[_index]) || _source[_index] == '_')) Advance();
        }
        else
        {
            while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_')) Advance();
            if (_index < _source.Length && _source[_index] == '.')
            {
                Advance();
                while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_')) Advance();
            }
            if (_index < _source.Length && _source[_index] is 'e' or 'E')
            {
                Advance();
                if (_index < _source.Length && _source[_index] is '+' or '-') Advance();
                while (_index < _source.Length && (char.IsDigit(_source[_index]) || _source[_index] == '_')) Advance();
            }
        }
        if (_index < _source.Length && _source[_index] == 'n') Advance();
        return new(JavaScriptTokenKind.Number, ReadTokenText(_source.AsSpan(offset, _index - offset)), offset, line, column);
    }

    private JavaScriptToken ReadString(int offset, int line, int column, char quote)
    {
        Advance();
        var remaining = _source.AsSpan(_index);
        var length = remaining.IndexOfAny(quote == '\'' ? SingleQuoteStops : DoubleQuoteStops);
        if (length < 0)
        {
            _index += remaining.Length;
            _column += remaining.Length;
            throw Error("The string literal is unterminated.");
        }
        _index += length;
        _column += length;
        if (remaining[length] == quote)
        {
            Advance();
            return new(JavaScriptTokenKind.String, ReadTokenText(remaining[..length]), offset, line, column);
        }
        if (remaining[length] is '\r' or '\n')
            throw Error("A string literal cannot contain an unescaped line break.");
        var value = new System.Text.StringBuilder();
        value.Append(remaining[..length]);
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (c == quote) { Advance(); return new(JavaScriptTokenKind.String, value.ToString(), offset, line, column); }
            if (c is '\r' or '\n') throw Error("A string literal cannot contain an unescaped line break.");
            if (c != '\\') { value.Append(c); Advance(); continue; }
            Advance();
            if (_index >= _source.Length) throw Error("The string escape is incomplete.");
            var escaped = _source[_index];
            Advance();
            if (escaped is 'u' or 'x')
            {
                var digits = escaped == 'u' ? 4 : 2;
                if (_index + digits > _source.Length) throw Error("The Unicode escape is incomplete.");
                var codePoint = 0;
                for (var i = 0; i < digits; i++)
                {
                    var digit = HexValue(_source[_index]);
                    if (digit < 0) throw Error("The Unicode escape is invalid.");
                    codePoint = (codePoint << 4) | digit;
                    Advance();
                }
                value.Append((char)codePoint);
                continue;
            }
            value.Append(escaped switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v',
                '\\' => '\\', '\'' => '\'', '"' => '"', '0' => '\0',
                _ => escaped,
            });
        }
        throw Error("The string literal is unterminated.");
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };

    private JavaScriptToken ReadTemplate(int offset, int line, int column)
    {
        Advance();
        var start = _index;
        var interpolationBraces = 0;
        while (_index < _source.Length)
        {
            var current = _source[_index];
            if (current == '`' && interpolationBraces == 0) break;
            if (current == '{') interpolationBraces++;
            else if (current == '}' && interpolationBraces > 0) interpolationBraces--;
            Advance();
        }
        var text = _source[start.._index];
        if (_index < _source.Length) Advance();
        return new(JavaScriptTokenKind.Template, text, offset, line, column);
    }

    private bool IsRegexStart()
    {
        if (_closedControlHead || _expressionKeyword) return true;
        var previous = _index - 1;
        while (previous >= 0 && char.IsWhiteSpace(_source[previous])) previous--;
        return previous < 0 || "=(:,[!&|?{;".Contains(_source[previous]);
    }

    private JavaScriptToken ReadRegex(int offset, int line, int column)
    {
        Advance();
        var escaped = false;
        var inClass = false;
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (c is '\r' or '\n' or '\u2028' or '\u2029')
                throw Error("A regular expression cannot contain a line break.");
            if (!escaped)
            {
                if (c == '/' && !inClass)
                {
                    Advance();
                    while (_index < _source.Length && char.IsAsciiLetter(_source[_index])) Advance();
                    return new(JavaScriptTokenKind.Regex, _source[offset.._index], offset, line, column);
                }
                if (c == '[') inClass = true;
                else if (c == ']') inClass = false;
            }
            escaped = !escaped && c == '\\';
            Advance();
        }
        throw Error("Unterminated regular expression literal.");
    }

    private void SkipTrivia()
    {
        while (_index < _source.Length)
        {
            if (char.IsWhiteSpace(_source[_index])) { Advance(); continue; }
            if (_source[_index] != '/' || _index + 1 >= _source.Length || _source[_index + 1] is not ('/' or '*')) return;
            if (_source[_index + 1] == '/')
            {
                Advance(); Advance();
                while (_index < _source.Length && _source[_index] is not ('\r' or '\n')) Advance();
            }
            else
            {
                Advance(); Advance();
                while (_index + 1 < _source.Length && !(_source[_index] == '*' && _source[_index + 1] == '/')) Advance();
                if (_index + 1 >= _source.Length) throw Error("The block comment is unterminated.");
                Advance(); Advance();
            }
        }
    }

    private void Advance()
    {
        if (_index >= _source.Length) return;
        var c = _source[_index++];
        if (c == '\n') { _line++; _column = 1; }
        else _column++;
    }

    private InvalidDataException Error(string message) => new($"{message} (line {_line}, column {_column}).");
    private static bool IsIdentifierStart(char c) => c == '_' || c == '$' || char.IsLetter(c);
    private static bool IsIdentifierPart(char c) => IsIdentifierStart(c) || char.IsDigit(c);
}

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
    private readonly string _source;
    private readonly int _maxTokens;
    private int _index;
    private int _line = 1;
    private int _column = 1;
    private int _tokens;

    internal JavaScriptLexer(string source, int maxTokens = 1_000_000)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        if (maxTokens <= 0) throw new ArgumentOutOfRangeException(nameof(maxTokens));
        _maxTokens = maxTokens;
    }

    internal JavaScriptToken Next()
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

        var two = _index + 1 < _source.Length ? _source.Substring(_index, 2) : string.Empty;
        var three = _index + 2 < _source.Length ? _source.Substring(_index, 3) : string.Empty;
        var four = _index + 3 < _source.Length ? _source.Substring(_index, 4) : string.Empty;
        if (four == ">>>=")
        {
            Advance(); Advance(); Advance(); Advance();
            return new(JavaScriptTokenKind.Punctuation, four, offset, line, column);
        }
        if (three == ">>>")
        {
            Advance(); Advance(); Advance();
            return new(JavaScriptTokenKind.Punctuation, three, offset, line, column);
        }
        if (three is "===" or "!==" or "...")
        {
            Advance(); Advance(); Advance();
            return new(JavaScriptTokenKind.Punctuation, three, offset, line, column);
        }
        if (three is "&&=" or "||=" or "??=" or "<<=" or ">>=")
        {
            Advance(); Advance(); Advance();
            return new(JavaScriptTokenKind.Punctuation, three, offset, line, column);
        }
        if (two is "=>" or "==" or "!=" or "<=" or ">=" or "&&" or "||" or "??" or "++" or "--" or "**" or "<<" or ">>" or "+=" or "-=" or "*=" or "/=" or "%=" or "&=" or "|=" or "^=")
        {
            Advance(); Advance();
            return new(JavaScriptTokenKind.Punctuation, two, offset, line, column);
        }
        Advance();
        return new(JavaScriptTokenKind.Punctuation, _source[offset.._index], offset, line, column);
    }

    private JavaScriptToken ReadIdentifier(int offset, int line, int column)
    {
        Advance();
        while (_index < _source.Length && IsIdentifierPart(_source[_index])) Advance();
        return new(JavaScriptTokenKind.Identifier, _source[offset.._index], offset, line, column);
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
        return new(JavaScriptTokenKind.Number, _source[offset.._index], offset, line, column);
    }

    private JavaScriptToken ReadString(int offset, int line, int column, char quote)
    {
        Advance();
        var value = new System.Text.StringBuilder();
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
            value.Append(escaped switch
            {
                'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', 'v' => '\v',
                '\\' => '\\', '\'' => '\'', '"' => '"', '0' => '\0',
                _ => escaped,
            });
        }
        throw Error("The string literal is unterminated.");
    }

    private JavaScriptToken ReadTemplate(int offset, int line, int column)
    {
        Advance();
        var start = _index;
        while (_index < _source.Length && _source[_index] != '`') Advance();
        var text = _source[start.._index];
        if (_index < _source.Length) Advance();
        return new(JavaScriptTokenKind.Template, text, offset, line, column);
    }

    private bool IsRegexStart()
    {
        var previous = _index - 1;
        while (previous >= 0 && char.IsWhiteSpace(_source[previous])) previous--;
        return previous < 0 || "=(:,[!&|?{".Contains(_source[previous]);
    }

    private JavaScriptToken ReadRegex(int offset, int line, int column)
    {
        Advance();
        var escaped = false;
        while (_index < _source.Length)
        {
            var c = _source[_index];
            if (!escaped && c == '/') { Advance(); break; }
            if (!escaped && c is '\r' or '\n') throw Error("A regular expression cannot contain an unescaped line break.");
            escaped = !escaped && c == '\\';
            if (c != '\\') escaped = false;
            Advance();
        }
        while (_index < _source.Length && char.IsAsciiLetter(_source[_index])) Advance();
        return new(JavaScriptTokenKind.Regex, _source[offset.._index], offset, line, column);
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

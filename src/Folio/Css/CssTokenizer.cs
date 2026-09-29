using System.Globalization;
using System.Text;

namespace Folio.Css;

/// <summary>Token types of https://www.w3.org/TR/css-syntax-3/#tokenization.</summary>
internal enum CssTokenKind
{
    Ident,
    Function,
    AtKeyword,
    Hash,
    String,
    BadString,
    Url,
    BadUrl,
    Delim,
    Number,
    Percentage,
    Dimension,
    Whitespace,
    Cdo,
    Cdc,
    Colon,
    Semicolon,
    Comma,
    LeftBracket,
    RightBracket,
    LeftParen,
    RightParen,
    LeftBrace,
    RightBrace,
    EndOfFile,
}

/// <param name="Value">Name, string or URL contents, hash name, unit, or the delimiter as a one-character string.</param>
/// <param name="Start">Offset of the token in the preprocessed source; <paramref name="End"/> is exclusive.</param>
internal readonly record struct CssToken(
    CssTokenKind Kind,
    int Start,
    int End,
    string Value = "",
    double Number = 0,
    bool IsInteger = false,
    bool HasSign = false,
    bool IsIdHash = false)
{
    public bool Is(CssTokenKind kind) => Kind == kind;

    public bool IsDelim(char c) => Kind == CssTokenKind.Delim && Value.Length == 1 && Value[0] == c;

    public bool IsIdent(string name) => Kind == CssTokenKind.Ident && Value.Equals(name, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The CSS tokenizer (https://www.w3.org/TR/css-syntax-3/#tokenization), including input preprocessing.
/// </summary>
internal sealed class CssTokenizer
{
    private readonly string _s;
    private int _pos;

    public CssTokenizer(string source) => _s = Preprocess(source);

    /// <summary>The preprocessed source that token offsets refer to.</summary>
    public string Source => _s;

    // https://www.w3.org/TR/css-syntax-3/#input-preprocessing
    public static string Preprocess(string source)
    {
        if (source.AsSpan().IndexOfAny("\r\f\0") < 0 && !HasLoneSurrogate(source))
            return source;
        var result = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '\r')
            {
                result.Append('\n');
                if (i + 1 < source.Length && source[i + 1] == '\n')
                    i++;
            }
            else if (c == '\f')
            {
                result.Append('\n');
            }
            else if (c == '\0')
            {
                result.Append('\uFFFD');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
            {
                result.Append(c).Append(source[++i]);
            }
            else
            {
                result.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
            }
        }
        return result.ToString();
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                i++;
            else if (char.IsSurrogate(s[i]))
                return true;
        }
        return false;
    }

    public List<CssToken> TokenizeAll()
    {
        var tokens = new List<CssToken>();
        CssToken token;
        do
        {
            token = Next();
            tokens.Add(token);
        }
        while (token.Kind != CssTokenKind.EndOfFile);
        return tokens;
    }

    private const int Eof = -1;

    private int Peek(int offset = 0) => _pos + offset < _s.Length ? _s[_pos + offset] : Eof;

    private static bool IsDigit(int c) => c is >= '0' and <= '9';
    private static bool IsHexDigit(int c) => IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static bool IsWhitespace(int c) => c is '\n' or '\t' or ' ';
    private static bool IsIdentStart(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' || c >= 0x80;
    private static bool IsIdentChar(int c) => IsIdentStart(c) || IsDigit(c) || c == '-';
    private static bool IsNonPrintable(int c) => c is (>= 0 and <= 8) or 0xB or (>= 0xE and <= 0x1F) or 0x7F;

    // https://www.w3.org/TR/css-syntax-3/#starts-with-a-valid-escape
    private static bool IsValidEscape(int first, int second) => first == '\\' && second != '\n';

    // https://www.w3.org/TR/css-syntax-3/#would-start-an-identifier
    private static bool StartsIdent(int a, int b, int c) => a switch
    {
        '-' => IsIdentStart(b) || b == '-' || IsValidEscape(b, c),
        '\\' => IsValidEscape(a, b),
        _ => IsIdentStart(a),
    };

    // https://www.w3.org/TR/css-syntax-3/#starts-with-a-number
    private static bool StartsNumber(int a, int b, int c) => a switch
    {
        '+' or '-' => IsDigit(b) || (b == '.' && IsDigit(c)),
        '.' => IsDigit(b),
        _ => IsDigit(a),
    };

    private CssToken Token(CssTokenKind kind, int start, string value = "") => new(kind, start, _pos, value);

    // https://www.w3.org/TR/css-syntax-3/#consume-token
    public CssToken Next()
    {
        ConsumeComments();
        var start = _pos;
        var c = Peek();
        if (c == Eof)
            return Token(CssTokenKind.EndOfFile, start);
        _pos++;

        switch (c)
        {
            case '\n' or '\t' or ' ':
                while (IsWhitespace(Peek()))
                    _pos++;
                return Token(CssTokenKind.Whitespace, start);
            case '"' or '\'':
                return ConsumeString(start, (char)c);
            case '#':
                if (IsIdentChar(Peek()) || IsValidEscape(Peek(), Peek(1)))
                {
                    var isId = StartsIdent(Peek(), Peek(1), Peek(2));
                    var name = ConsumeIdentSequence();
                    return new CssToken(CssTokenKind.Hash, start, _pos, name, IsIdHash: isId);
                }
                return Token(CssTokenKind.Delim, start, "#");
            case '(':
                return Token(CssTokenKind.LeftParen, start);
            case ')':
                return Token(CssTokenKind.RightParen, start);
            case '+' or '.':
                if (StartsNumber(c, Peek(), Peek(1)))
                {
                    _pos--;
                    return ConsumeNumeric(start);
                }
                return Token(CssTokenKind.Delim, start, ((char)c).ToString());
            case ',':
                return Token(CssTokenKind.Comma, start);
            case '-':
                if (StartsNumber(c, Peek(), Peek(1)))
                {
                    _pos--;
                    return ConsumeNumeric(start);
                }
                if (Peek() == '-' && Peek(1) == '>')
                {
                    _pos += 2;
                    return Token(CssTokenKind.Cdc, start);
                }
                if (StartsIdent(c, Peek(), Peek(1)))
                {
                    _pos--;
                    return ConsumeIdentLike(start);
                }
                return Token(CssTokenKind.Delim, start, "-");
            case ':':
                return Token(CssTokenKind.Colon, start);
            case ';':
                return Token(CssTokenKind.Semicolon, start);
            case '<':
                if (Peek() == '!' && Peek(1) == '-' && Peek(2) == '-')
                {
                    _pos += 3;
                    return Token(CssTokenKind.Cdo, start);
                }
                return Token(CssTokenKind.Delim, start, "<");
            case '@':
                if (StartsIdent(Peek(), Peek(1), Peek(2)))
                {
                    var name = ConsumeIdentSequence();
                    return Token(CssTokenKind.AtKeyword, start, name);
                }
                return Token(CssTokenKind.Delim, start, "@");
            case '[':
                return Token(CssTokenKind.LeftBracket, start);
            case '\\':
                if (IsValidEscape(c, Peek()))
                {
                    _pos--;
                    return ConsumeIdentLike(start);
                }
                return Token(CssTokenKind.Delim, start, "\\"); // parse error
            case ']':
                return Token(CssTokenKind.RightBracket, start);
            case '{':
                return Token(CssTokenKind.LeftBrace, start);
            case '}':
                return Token(CssTokenKind.RightBrace, start);
        }

        if (IsDigit(c))
        {
            _pos--;
            return ConsumeNumeric(start);
        }
        if (IsIdentStart(c))
        {
            _pos--;
            return ConsumeIdentLike(start);
        }
        return Token(CssTokenKind.Delim, start, char.ConvertFromUtf32(ReadCodePoint(c)));
    }

    // Keeps astral characters as one delimiter.
    private int ReadCodePoint(int c)
    {
        if (char.IsHighSurrogate((char)c) && char.IsLowSurrogate((char)Peek()))
            return char.ConvertToUtf32((char)c, (char)_s[_pos++]);
        return c;
    }

    private void ConsumeComments()
    {
        while (Peek() == '/' && Peek(1) == '*')
        {
            var end = _s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
            _pos = end < 0 ? _s.Length : end + 2; // an unclosed comment is a parse error and runs to the end
        }
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-numeric-token
    private CssToken ConsumeNumeric(int start)
    {
        var (number, isInteger, hasSign) = ConsumeNumber();
        if (StartsIdent(Peek(), Peek(1), Peek(2)))
        {
            var unit = ConsumeIdentSequence();
            return new CssToken(CssTokenKind.Dimension, start, _pos, unit, number, isInteger, hasSign);
        }
        if (Peek() == '%')
        {
            _pos++;
            return new CssToken(CssTokenKind.Percentage, start, _pos, "", number, isInteger, hasSign);
        }
        return new CssToken(CssTokenKind.Number, start, _pos, "", number, isInteger, hasSign);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-number
    private (double Value, bool IsInteger, bool HasSign) ConsumeNumber()
    {
        var start = _pos;
        var isInteger = true;
        var hasSign = Peek() is '+' or '-';
        if (hasSign)
            _pos++;
        while (IsDigit(Peek()))
            _pos++;
        if (Peek() == '.' && IsDigit(Peek(1)))
        {
            isInteger = false;
            _pos++;
            while (IsDigit(Peek()))
                _pos++;
        }
        if (Peek() is 'e' or 'E' && (IsDigit(Peek(1)) || (Peek(1) is '+' or '-' && IsDigit(Peek(2)))))
        {
            isInteger = false;
            _pos += IsDigit(Peek(1)) ? 1 : 2;
            while (IsDigit(Peek()))
                _pos++;
        }
        var value = double.Parse(_s.AsSpan(start, _pos - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        return (value, isInteger, hasSign);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-ident-like-token
    private CssToken ConsumeIdentLike(int start)
    {
        var name = ConsumeIdentSequence();
        if (name.Equals("url", StringComparison.OrdinalIgnoreCase) && Peek() == '(')
        {
            _pos++;
            while (IsWhitespace(Peek()) && IsWhitespace(Peek(1)))
                _pos++;
            if (Peek() is '"' or '\'' || (IsWhitespace(Peek()) && Peek(1) is '"' or '\''))
                return Token(CssTokenKind.Function, start, name);
            return ConsumeUrl(start);
        }
        if (Peek() == '(')
        {
            _pos++;
            return Token(CssTokenKind.Function, start, name);
        }
        return Token(CssTokenKind.Ident, start, name);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-string-token
    private CssToken ConsumeString(int start, char ending)
    {
        var value = new StringBuilder();
        while (true)
        {
            var c = Peek();
            if (c == Eof)
                return Token(CssTokenKind.String, start, value.ToString()); // parse error
            _pos++;
            if (c == ending)
                return Token(CssTokenKind.String, start, value.ToString());
            if (c == '\n')
            {
                _pos--; // parse error
                return Token(CssTokenKind.BadString, start);
            }
            if (c == '\\')
            {
                if (Peek() == Eof)
                    continue;
                if (Peek() == '\n')
                {
                    _pos++;
                    continue;
                }
                AppendEscape(value);
                continue;
            }
            value.Append((char)c);
        }
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-url-token
    private CssToken ConsumeUrl(int start)
    {
        var value = new StringBuilder();
        while (IsWhitespace(Peek()))
            _pos++;
        while (true)
        {
            var c = Peek();
            if (c == Eof)
                return Token(CssTokenKind.Url, start, value.ToString()); // parse error
            _pos++;
            if (c == ')')
                return Token(CssTokenKind.Url, start, value.ToString());
            if (IsWhitespace(c))
            {
                while (IsWhitespace(Peek()))
                    _pos++;
                if (Peek() == ')' || Peek() == Eof)
                {
                    if (Peek() == ')')
                        _pos++;
                    return Token(CssTokenKind.Url, start, value.ToString());
                }
                return ConsumeBadUrl(start);
            }
            if (c is '"' or '\'' or '(' || IsNonPrintable(c))
                return ConsumeBadUrl(start);
            if (c == '\\')
            {
                if (IsValidEscape(c, Peek()))
                {
                    AppendEscape(value);
                    continue;
                }
                return ConsumeBadUrl(start);
            }
            value.Append((char)c);
        }
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-remnants-of-bad-url
    private CssToken ConsumeBadUrl(int start)
    {
        while (true)
        {
            var c = Peek();
            if (c == Eof)
                break;
            _pos++;
            if (c == ')')
                break;
            if (IsValidEscape(c, Peek()))
                _pos++; // skip the escaped character so an escaped ')' does not end the url
        }
        return Token(CssTokenKind.BadUrl, start);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-name
    private string ConsumeIdentSequence()
    {
        var result = new StringBuilder();
        while (true)
        {
            var c = Peek();
            if (IsIdentChar(c))
            {
                result.Append((char)c);
                _pos++;
            }
            else if (IsValidEscape(c, Peek(1)))
            {
                _pos++;
                AppendEscape(result);
            }
            else
            {
                return result.ToString();
            }
        }
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-escaped-code-point (the backslash is already consumed)
    private void AppendEscape(StringBuilder into)
    {
        var c = Peek();
        if (c == Eof)
        {
            into.Append('\uFFFD'); // parse error
            return;
        }
        if (!IsHexDigit(c))
        {
            _pos++;
            into.Append(char.ConvertFromUtf32(ReadCodePoint(c)));
            return;
        }
        var value = 0;
        for (var n = 0; n < 6 && IsHexDigit(Peek()); n++)
        {
            var h = _s[_pos++];
            value = value * 16 + (h <= '9' ? h - '0' : (h | 0x20) - 'a' + 10);
        }
        if (IsWhitespace(Peek()))
            _pos++;
        if (value == 0 || value is >= 0xD800 and <= 0xDFFF || value > 0x10FFFF)
            value = 0xFFFD;
        into.Append(char.ConvertFromUtf32(value));
    }
}

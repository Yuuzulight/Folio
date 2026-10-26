using System.Globalization;
using System.Text;

namespace Folio.Scripting.Native;

public sealed class NativeTokenizer
{
    private readonly string _source;
    private int _index;
    private int _line = 1;
    private int _column = 1;

    public NativeTokenizer(string source)
    {
        _source = source ?? "";
    }

    public List<NativeToken> Tokenize()
    {
        var tokens = new List<NativeToken>();
        while (true)
        {
            SkipWhitespaceAndComments();
            if (_index >= _source.Length)
            {
                tokens.Add(new NativeToken(NativeTokenType.Eof, "", 0, _line, _column));
                break;
            }

            var startLine = _line;
            var startCol = _column;
            var ch = _source[_index];

            if (char.IsDigit(ch) || (ch == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1])))
            {
                tokens.Add(ReadNumber(startLine, startCol));
            }
            else if (ch is '"' or '\'' or '`')
            {
                tokens.Add(ReadString(ch, startLine, startCol));
            }
            else if (char.IsLetter(ch) || ch is '_' or '$')
            {
                tokens.Add(ReadIdentifierOrKeyword(startLine, startCol));
            }
            else
            {
                tokens.Add(ReadOperatorOrPunctuation(startLine, startCol));
            }
        }
        return tokens;
    }

    private void SkipWhitespaceAndComments()
    {
        while (_index < _source.Length)
        {
            var ch = _source[_index];
            if (ch is ' ' or '\t' or '\v' or '\f')
            {
                Advance();
            }
            else if (ch is '\r' or '\n')
            {
                AdvanceLine();
            }
            else if (ch == '/' && _index + 1 < _source.Length && _source[_index + 1] == '/')
            {
                Advance(2);
                while (_index < _source.Length && _source[_index] is not ('\r' or '\n'))
                {
                    Advance();
                }
            }
            else if (ch == '/' && _index + 1 < _source.Length && _source[_index + 1] == '*')
            {
                Advance(2);
                while (_index < _source.Length)
                {
                    if (_source[_index] == '*' && _index + 1 < _source.Length && _source[_index + 1] == '/')
                    {
                        Advance(2);
                        break;
                    }
                    if (_source[_index] is '\r' or '\n')
                        AdvanceLine();
                    else
                        Advance();
                }
            }
            else
            {
                break;
            }
        }
    }

    private void Advance(int count = 1)
    {
        _index += count;
        _column += count;
    }

    private void AdvanceLine()
    {
        if (_source[_index] == '\r' && _index + 1 < _source.Length && _source[_index + 1] == '\n')
            _index += 2;
        else
            _index++;
        _line++;
        _column = 1;
    }

    private NativeToken ReadNumber(int line, int col)
    {
        var start = _index;
        if (_source[_index] == '0' && _index + 1 < _source.Length && (_source[_index + 1] is 'x' or 'X'))
        {
            Advance(2);
            while (_index < _source.Length && IsHexDigit(_source[_index]))
                Advance();
            var hexStr = _source.Substring(start + 2, _index - start - 2);
            double hexVal = ulong.TryParse(hexStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u) ? u : 0;
            return new NativeToken(NativeTokenType.Number, _source[start.._index], hexVal, line, col);
        }

        if (_source[_index] == '0' && _index + 1 < _source.Length && (_source[_index + 1] is 'b' or 'B'))
        {
            Advance(2);
            while (_index < _source.Length && _source[_index] is '0' or '1')
                Advance();
            var binStr = _source.Substring(start + 2, _index - start - 2);
            double binVal = 0;
            try { binVal = Convert.ToUInt64(binStr, 2); } catch { }
            return new NativeToken(NativeTokenType.Number, _source[start.._index], binVal, line, col);
        }

        while (_index < _source.Length && char.IsDigit(_source[_index]))
            Advance();

        if (_index < _source.Length && _source[_index] == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
        {
            Advance();
            while (_index < _source.Length && char.IsDigit(_source[_index]))
                Advance();
        }

        if (_index < _source.Length && (_source[_index] is 'e' or 'E'))
        {
            Advance();
            if (_index < _source.Length && (_source[_index] is '+' or '-'))
                Advance();
            while (_index < _source.Length && char.IsDigit(_source[_index]))
                Advance();
        }

        var text = _source[start.._index];
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var num);
        return new NativeToken(NativeTokenType.Number, text, num, line, col);
    }

    private static bool IsHexDigit(char c) =>
        char.IsDigit(c) || c is (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private NativeToken ReadString(char quote, int line, int col)
    {
        Advance(); // skip opening quote
        var sb = new StringBuilder();
        while (_index < _source.Length && _source[_index] != quote)
        {
            var ch = _source[_index];
            if (ch == '\\' && _index + 1 < _source.Length)
            {
                Advance();
                var esc = _source[_index];
                switch (esc)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '\\': sb.Append('\\'); break;
                    case '\'': sb.Append('\''); break;
                    case '"': sb.Append('"'); break;
                    case '`': sb.Append('`'); break;
                    case '0': sb.Append('\0'); break;
                    case 'x' when _index + 2 < _source.Length && IsHexDigit(_source[_index + 1]) && IsHexDigit(_source[_index + 2]):
                        var hx = _source.Substring(_index + 1, 2);
                        sb.Append((char)Convert.ToByte(hx, 16));
                        Advance(2);
                        break;
                    case 'u' when _index + 4 < _source.Length:
                        var uStr = _source.Substring(_index + 1, 4);
                        if (ushort.TryParse(uStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var uCode))
                        {
                            sb.Append((char)uCode);
                            Advance(4);
                        }
                        else
                        {
                            sb.Append(esc);
                        }
                        break;
                    default:
                        sb.Append(esc);
                        break;
                }
                Advance();
            }
            else if (ch is '\r' or '\n')
            {
                if (quote == '`')
                {
                    sb.Append(ch);
                    AdvanceLine();
                }
                else
                {
                    throw new FormatException($"Unterminated string literal at line {line}, col {col}");
                }
            }
            else
            {
                sb.Append(ch);
                Advance();
            }
        }

        if (_index < _source.Length && _source[_index] == quote)
            Advance();

        return new NativeToken(NativeTokenType.String, sb.ToString(), 0, line, col);
    }

    private NativeToken ReadIdentifierOrKeyword(int line, int col)
    {
        var start = _index;
        while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] is '_' or '$'))
            Advance();

        var text = _source[start.._index];
        var type = text switch
        {
            "var" => NativeTokenType.Var,
            "let" => NativeTokenType.Let,
            "const" => NativeTokenType.Const,
            "function" => NativeTokenType.Function,
            "return" => NativeTokenType.Return,
            "if" => NativeTokenType.If,
            "else" => NativeTokenType.Else,
            "while" => NativeTokenType.While,
            "do" => NativeTokenType.Do,
            "for" => NativeTokenType.For,
            "switch" => NativeTokenType.Switch,
            "case" => NativeTokenType.Case,
            "default" => NativeTokenType.Default,
            "break" => NativeTokenType.Break,
            "continue" => NativeTokenType.Continue,
            "throw" => NativeTokenType.Throw,
            "try" => NativeTokenType.Try,
            "catch" => NativeTokenType.Catch,
            "finally" => NativeTokenType.Finally,
            "new" => NativeTokenType.New,
            "this" => NativeTokenType.This,
            "typeof" => NativeTokenType.TypeOf,
            "delete" => NativeTokenType.Delete,
            "true" => NativeTokenType.True,
            "false" => NativeTokenType.False,
            "null" => NativeTokenType.Null,
            "undefined" => NativeTokenType.Undefined,
            _ => NativeTokenType.Identifier,
        };

        return new NativeToken(type, text, 0, line, col);
    }

    private NativeToken ReadOperatorOrPunctuation(int line, int col)
    {
        var ch = _source[_index];
        var next = _index + 1 < _source.Length ? _source[_index + 1] : '\0';
        var next2 = _index + 2 < _source.Length ? _source[_index + 2] : '\0';

        if (ch == '=' && next == '=' && next2 == '=') { Advance(3); return new NativeToken(NativeTokenType.EqualEqualEqual, "===", 0, line, col); }
        if (ch == '!' && next == '=' && next2 == '=') { Advance(3); return new NativeToken(NativeTokenType.ExclamationDoubleEqual, "!==", 0, line, col); }

        if (ch == '=' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.EqualEqual, "==", 0, line, col); }
        if (ch == '!' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.ExclamationEqual, "!=", 0, line, col); }
        if (ch == '<' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.LessEqual, "<=", 0, line, col); }
        if (ch == '>' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.GreaterEqual, ">=", 0, line, col); }
        if (ch == '=' && next == '>') { Advance(2); return new NativeToken(NativeTokenType.Arrow, "=>", 0, line, col); }
        if (ch == '&' && next == '&') { Advance(2); return new NativeToken(NativeTokenType.AmpersandAmpersand, "&&", 0, line, col); }
        if (ch == '|' && next == '|') { Advance(2); return new NativeToken(NativeTokenType.PipePipe, "||", 0, line, col); }
        if (ch == '+' && next == '+') { Advance(2); return new NativeToken(NativeTokenType.PlusPlus, "++", 0, line, col); }
        if (ch == '-' && next == '-') { Advance(2); return new NativeToken(NativeTokenType.MinusMinus, "--", 0, line, col); }
        if (ch == '+' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.PlusEqual, "+=", 0, line, col); }
        if (ch == '-' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.MinusEqual, "-=", 0, line, col); }
        if (ch == '*' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.StarEqual, "*=", 0, line, col); }
        if (ch == '/' && next == '=') { Advance(2); return new NativeToken(NativeTokenType.SlashEqual, "/=", 0, line, col); }
        if (ch == '*' && next == '*') { Advance(2); return new NativeToken(NativeTokenType.DoubleStar, "**", 0, line, col); }

        Advance();
        return ch switch
        {
            '+' => new NativeToken(NativeTokenType.Plus, "+", 0, line, col),
            '-' => new NativeToken(NativeTokenType.Minus, "-", 0, line, col),
            '*' => new NativeToken(NativeTokenType.Star, "*", 0, line, col),
            '/' => new NativeToken(NativeTokenType.Slash, "/", 0, line, col),
            '%' => new NativeToken(NativeTokenType.Percent, "%", 0, line, col),
            '=' => new NativeToken(NativeTokenType.Equal, "=", 0, line, col),
            '<' => new NativeToken(NativeTokenType.Less, "<", 0, line, col),
            '>' => new NativeToken(NativeTokenType.Greater, ">", 0, line, col),
            '!' => new NativeToken(NativeTokenType.Exclamation, "!", 0, line, col),
            '?' => new NativeToken(NativeTokenType.Question, "?", 0, line, col),
            ':' => new NativeToken(NativeTokenType.Colon, ":", 0, line, col),
            '.' => new NativeToken(NativeTokenType.Dot, ".", 0, line, col),
            ',' => new NativeToken(NativeTokenType.Comma, ",", 0, line, col),
            ';' => new NativeToken(NativeTokenType.Semicolon, ";", 0, line, col),
            '(' => new NativeToken(NativeTokenType.OpenParen, "(", 0, line, col),
            ')' => new NativeToken(NativeTokenType.CloseParen, ")", 0, line, col),
            '{' => new NativeToken(NativeTokenType.OpenBrace, "{", 0, line, col),
            '}' => new NativeToken(NativeTokenType.CloseBrace, "}", 0, line, col),
            '[' => new NativeToken(NativeTokenType.OpenBracket, "[", 0, line, col),
            ']' => new NativeToken(NativeTokenType.CloseBracket, "]", 0, line, col),
            _ => throw new FormatException($"Unexpected character '{ch}' at line {line}, col {col}"),
        };
    }
}

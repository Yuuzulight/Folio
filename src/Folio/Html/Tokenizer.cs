using System.Text;
using S = Folio.Html.TokenizerState;

namespace Folio.Html;

/// <summary>States of https://html.spec.whatwg.org/multipage/parsing.html#tokenization, in spec order.</summary>
internal enum TokenizerState
{
    Data,
    RcData,
    RawText,
    ScriptData,
    PlainText,
    TagOpen,
    EndTagOpen,
    TagName,
    RcDataLessThanSign,
    RcDataEndTagOpen,
    RcDataEndTagName,
    RawTextLessThanSign,
    RawTextEndTagOpen,
    RawTextEndTagName,
    ScriptDataLessThanSign,
    ScriptDataEndTagOpen,
    ScriptDataEndTagName,
    ScriptDataEscapeStart,
    ScriptDataEscapeStartDash,
    ScriptDataEscaped,
    ScriptDataEscapedDash,
    ScriptDataEscapedDashDash,
    ScriptDataEscapedLessThanSign,
    ScriptDataEscapedEndTagOpen,
    ScriptDataEscapedEndTagName,
    ScriptDataDoubleEscapeStart,
    ScriptDataDoubleEscaped,
    ScriptDataDoubleEscapedDash,
    ScriptDataDoubleEscapedDashDash,
    ScriptDataDoubleEscapedLessThanSign,
    ScriptDataDoubleEscapeEnd,
    BeforeAttributeName,
    AttributeName,
    AfterAttributeName,
    BeforeAttributeValue,
    AttributeValueDoubleQuoted,
    AttributeValueSingleQuoted,
    AttributeValueUnquoted,
    AfterAttributeValueQuoted,
    SelfClosingStartTag,
    BogusComment,
    MarkupDeclarationOpen,
    CommentStart,
    CommentStartDash,
    Comment,
    CommentLessThanSign,
    CommentLessThanSignBang,
    CommentLessThanSignBangDash,
    CommentLessThanSignBangDashDash,
    CommentEndDash,
    CommentEnd,
    CommentEndBang,
    Doctype,
    BeforeDoctypeName,
    DoctypeName,
    AfterDoctypeName,
    AfterDoctypePublicKeyword,
    BeforeDoctypePublicIdentifier,
    DoctypePublicIdentifierDoubleQuoted,
    DoctypePublicIdentifierSingleQuoted,
    AfterDoctypePublicIdentifier,
    BetweenDoctypePublicAndSystemIdentifiers,
    AfterDoctypeSystemKeyword,
    BeforeDoctypeSystemIdentifier,
    DoctypeSystemIdentifierDoubleQuoted,
    DoctypeSystemIdentifierSingleQuoted,
    AfterDoctypeSystemIdentifier,
    BogusDoctype,
    CdataSection,
    CdataSectionBracket,
    CdataSectionEnd,
    CharacterReference,
    NamedCharacterReference,
    AmbiguousAmpersand,
    NumericCharacterReference,
    HexadecimalCharacterReferenceStart,
    DecimalCharacterReferenceStart,
    HexadecimalCharacterReference,
    DecimalCharacterReference,
    NumericCharacterReferenceEnd,
}

/// <summary>
/// The HTML tokenizer (https://html.spec.whatwg.org/multipage/parsing.html#tokenization), implemented state by state.
/// Pushes tokens to an <see cref="ITokenSink"/>; the tree builder drives <see cref="State"/>,
/// <see cref="LastStartTagName"/> and <see cref="CdataAllowed"/> as the spec describes.
/// </summary>
internal sealed class Tokenizer
{
    private const int Eof = -1;

    private readonly string _input;
    private readonly ITokenSink _sink;
    private readonly Token _token = new();
    private int _pos;
    private bool _done;

    private readonly StringBuilder _text = new();      // pending character tokens
    private readonly StringBuilder _name = new();      // tag or DOCTYPE name
    private readonly StringBuilder _data = new();      // comment data
    private readonly StringBuilder _attrName = new();
    private readonly StringBuilder _attrValue = new();
    private readonly StringBuilder _publicId = new();
    private readonly StringBuilder _systemId = new();
    private readonly StringBuilder _temp = new();      // the spec's temporary buffer

    private bool _isEndTag;
    private bool _selfClosing;
    private bool _inAttribute;
    private string? _pendingAttrName;                  // set when leaving the attribute name state; null if dropped
    private readonly HashSet<string> _attrNames = new(StringComparer.Ordinal); // keeps duplicate checks linear
    private bool _hasDoctypeName, _hasPublicId, _hasSystemId, _forceQuirks;
    private S _returnState;
    private int _charRefCode;

    /// <param name="input">Decoded input; newlines are normalised here (input stream preprocessing).</param>
    public Tokenizer(string input, ITokenSink sink)
    {
        _input = input.Contains('\r') ? input.Replace("\r\n", "\n").Replace('\r', '\n') : input;
        _sink = sink;
    }

    public TokenizerState State { get; set; }

    /// <summary>Set by the tree builder: the adjusted current node is not in the HTML namespace.</summary>
    public bool CdataAllowed { get; set; }

    /// <summary>Name of the last start tag emitted, for "appropriate end tag token" checks.</summary>
    public string? LastStartTagName { get; set; }

    /// <summary>Receives each parse error's spec code and input offset; null skips reporting.</summary>
    public Action<string, int>? ParseError { get; set; }

    /// <summary>Offset of the next input character, for diagnostics.</summary>
    public int Position => _pos;

    public void Run()
    {
        while (!_done)
            Step();
    }

    /// <summary>Stops after the current token (used when a parser limit is reached).</summary>
    public void Stop() => _done = true;

    private int Next() => _pos < _input.Length ? _input[_pos++] : Eof;

    private void Reconsume(int c, S state)
    {
        if (c != Eof)
            _pos--;
        State = state;
    }

    private void Error(string code) => ParseError?.Invoke(code, Math.Max(0, _pos - 1));

    private static bool IsWhitespace(int c) => c is '\t' or '\n' or '\f' or ' ';
    private static bool IsUpper(int c) => c is >= 'A' and <= 'Z';
    private static bool IsLower(int c) => c is >= 'a' and <= 'z';
    private static bool IsAlpha(int c) => IsUpper(c) || IsLower(c);
    private static bool IsDigit(int c) => c is >= '0' and <= '9';
    private static bool IsHexDigit(int c) => IsDigit(c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    private static bool IsAlphanumeric(int c) => IsAlpha(c) || IsDigit(c);
    private static char Lower(int c) => (char)(IsUpper(c) ? c + 0x20 : c);

    private void Step()
    {
        var c = Next();
        switch (State)
        {
            case S.Data:
                switch (c)
                {
                    case '&': _returnState = S.Data; State = S.CharacterReference; break;
                    case '<': State = S.TagOpen; break;
                    case 0: Error("unexpected-null-character"); Emit('\0'); break;
                    case Eof: EmitEof(); break;
                    default: Emit((char)c); break;
                }
                break;

            case S.RcData:
                switch (c)
                {
                    case '&': _returnState = S.RcData; State = S.CharacterReference; break;
                    case '<': State = S.RcDataLessThanSign; break;
                    case 0: Error("unexpected-null-character"); Emit('\uFFFD'); break;
                    case Eof: EmitEof(); break;
                    default: Emit((char)c); break;
                }
                break;

            case S.RawText:
                RawTextLike(c, S.RawTextLessThanSign);
                break;

            case S.ScriptData:
                RawTextLike(c, S.ScriptDataLessThanSign);
                break;

            case S.PlainText:
                switch (c)
                {
                    case 0: Error("unexpected-null-character"); Emit('\uFFFD'); break;
                    case Eof: EmitEof(); break;
                    default: Emit((char)c); break;
                }
                break;

            case S.TagOpen:
                if (c == '!')
                {
                    State = S.MarkupDeclarationOpen;
                }
                else if (c == '/')
                {
                    State = S.EndTagOpen;
                }
                else if (IsAlpha(c))
                {
                    StartTag(isEnd: false);
                    Reconsume(c, S.TagName);
                }
                else if (c == '?')
                {
                    Error("unexpected-question-mark-instead-of-tag-name");
                    _data.Clear();
                    Reconsume(c, S.BogusComment);
                }
                else if (c == Eof)
                {
                    Error("eof-before-tag-name");
                    Emit('<');
                    EmitEof();
                }
                else
                {
                    Error("invalid-first-character-of-tag-name");
                    Emit('<');
                    Reconsume(c, S.Data);
                }
                break;

            case S.EndTagOpen:
                if (IsAlpha(c))
                {
                    StartTag(isEnd: true);
                    Reconsume(c, S.TagName);
                }
                else if (c == '>')
                {
                    Error("missing-end-tag-name");
                    State = S.Data;
                }
                else if (c == Eof)
                {
                    Error("eof-before-tag-name");
                    Emit("</");
                    EmitEof();
                }
                else
                {
                    Error("invalid-first-character-of-tag-name");
                    _data.Clear();
                    Reconsume(c, S.BogusComment);
                }
                break;

            case S.TagName:
                if (IsWhitespace(c)) State = S.BeforeAttributeName;
                else if (c == '/') State = S.SelfClosingStartTag;
                else if (c == '>') { State = S.Data; EmitTag(); }
                else if (c == 0) { Error("unexpected-null-character"); _name.Append('\uFFFD'); }
                else if (c == Eof) { Error("eof-in-tag"); EmitEof(); }
                else _name.Append(Lower(c));
                break;

            case S.RcDataLessThanSign:
                LessThanSign(c, S.RcDataEndTagOpen, S.RcData);
                break;
            case S.RcDataEndTagOpen:
                EndTagOpen(c, S.RcDataEndTagName, S.RcData);
                break;
            case S.RcDataEndTagName:
                EndTagName(c, S.RcData);
                break;

            case S.RawTextLessThanSign:
                LessThanSign(c, S.RawTextEndTagOpen, S.RawText);
                break;
            case S.RawTextEndTagOpen:
                EndTagOpen(c, S.RawTextEndTagName, S.RawText);
                break;
            case S.RawTextEndTagName:
                EndTagName(c, S.RawText);
                break;

            case S.ScriptDataLessThanSign:
                if (c == '/')
                {
                    _temp.Clear();
                    State = S.ScriptDataEndTagOpen;
                }
                else if (c == '!')
                {
                    State = S.ScriptDataEscapeStart;
                    Emit("<!");
                }
                else
                {
                    Emit('<');
                    Reconsume(c, S.ScriptData);
                }
                break;
            case S.ScriptDataEndTagOpen:
                EndTagOpen(c, S.ScriptDataEndTagName, S.ScriptData);
                break;
            case S.ScriptDataEndTagName:
                EndTagName(c, S.ScriptData);
                break;

            case S.ScriptDataEscapeStart:
                if (c == '-') { State = S.ScriptDataEscapeStartDash; Emit('-'); }
                else Reconsume(c, S.ScriptData);
                break;
            case S.ScriptDataEscapeStartDash:
                if (c == '-') { State = S.ScriptDataEscapedDashDash; Emit('-'); }
                else Reconsume(c, S.ScriptData);
                break;

            case S.ScriptDataEscaped:
                switch (c)
                {
                    case '-': State = S.ScriptDataEscapedDash; Emit('-'); break;
                    case '<': State = S.ScriptDataEscapedLessThanSign; break;
                    case 0: Error("unexpected-null-character"); Emit('\uFFFD'); break;
                    case Eof: Error("eof-in-script-html-comment-like-text"); EmitEof(); break;
                    default: Emit((char)c); break;
                }
                break;
            case S.ScriptDataEscapedDash:
                switch (c)
                {
                    case '-': State = S.ScriptDataEscapedDashDash; Emit('-'); break;
                    case '<': State = S.ScriptDataEscapedLessThanSign; break;
                    case 0: Error("unexpected-null-character"); State = S.ScriptDataEscaped; Emit('\uFFFD'); break;
                    case Eof: Error("eof-in-script-html-comment-like-text"); EmitEof(); break;
                    default: State = S.ScriptDataEscaped; Emit((char)c); break;
                }
                break;
            case S.ScriptDataEscapedDashDash:
                switch (c)
                {
                    case '-': Emit('-'); break;
                    case '<': State = S.ScriptDataEscapedLessThanSign; break;
                    case '>': State = S.ScriptData; Emit('>'); break;
                    case 0: Error("unexpected-null-character"); State = S.ScriptDataEscaped; Emit('\uFFFD'); break;
                    case Eof: Error("eof-in-script-html-comment-like-text"); EmitEof(); break;
                    default: State = S.ScriptDataEscaped; Emit((char)c); break;
                }
                break;
            case S.ScriptDataEscapedLessThanSign:
                if (c == '/')
                {
                    _temp.Clear();
                    State = S.ScriptDataEscapedEndTagOpen;
                }
                else if (IsAlpha(c))
                {
                    _temp.Clear();
                    Emit('<');
                    Reconsume(c, S.ScriptDataDoubleEscapeStart);
                }
                else
                {
                    Emit('<');
                    Reconsume(c, S.ScriptDataEscaped);
                }
                break;
            case S.ScriptDataEscapedEndTagOpen:
                EndTagOpen(c, S.ScriptDataEscapedEndTagName, S.ScriptDataEscaped);
                break;
            case S.ScriptDataEscapedEndTagName:
                EndTagName(c, S.ScriptDataEscaped);
                break;

            case S.ScriptDataDoubleEscapeStart:
                DoubleEscapeBoundary(c, ifScript: S.ScriptDataDoubleEscaped, otherwise: S.ScriptDataEscaped);
                break;
            case S.ScriptDataDoubleEscaped:
                switch (c)
                {
                    case '-': State = S.ScriptDataDoubleEscapedDash; Emit('-'); break;
                    case '<': State = S.ScriptDataDoubleEscapedLessThanSign; Emit('<'); break;
                    case 0: Error("unexpected-null-character"); Emit('\uFFFD'); break;
                    case Eof: Error("eof-in-script-html-comment-like-text"); EmitEof(); break;
                    default: Emit((char)c); break;
                }
                break;
            case S.ScriptDataDoubleEscapedDash:
                switch (c)
                {
                    case '-': State = S.ScriptDataDoubleEscapedDashDash; Emit('-'); break;
                    case '<': State = S.ScriptDataDoubleEscapedLessThanSign; Emit('<'); break;
                    case 0: Error("unexpected-null-character"); State = S.ScriptDataDoubleEscaped; Emit('\uFFFD'); break;
                    case Eof: Error("eof-in-script-html-comment-like-text"); EmitEof(); break;
                    default: State = S.ScriptDataDoubleEscaped; Emit((char)c); break;
                }
                break;
            case S.ScriptDataDoubleEscapedDashDash:
                switch (c)
                {
                    case '-': Emit('-'); break;
                    case '<': State = S.ScriptDataDoubleEscapedLessThanSign; Emit('<'); break;
                    case '>': State = S.ScriptData; Emit('>'); break;
                    case 0: Error("unexpected-null-character"); State = S.ScriptDataDoubleEscaped; Emit('\uFFFD'); break;
                    case Eof: Error("eof-in-script-html-comment-like-text"); EmitEof(); break;
                    default: State = S.ScriptDataDoubleEscaped; Emit((char)c); break;
                }
                break;
            case S.ScriptDataDoubleEscapedLessThanSign:
                if (c == '/')
                {
                    _temp.Clear();
                    State = S.ScriptDataDoubleEscapeEnd;
                    Emit('/');
                }
                else
                {
                    Reconsume(c, S.ScriptDataDoubleEscaped);
                }
                break;
            case S.ScriptDataDoubleEscapeEnd:
                DoubleEscapeBoundary(c, ifScript: S.ScriptDataEscaped, otherwise: S.ScriptDataDoubleEscaped);
                break;

            case S.BeforeAttributeName:
                if (IsWhitespace(c))
                {
                }
                else if (c is '/' or '>' or Eof)
                {
                    Reconsume(c, S.AfterAttributeName);
                }
                else if (c == '=')
                {
                    Error("unexpected-equals-sign-before-attribute-name");
                    StartAttribute();
                    _attrName.Append('=');
                    State = S.AttributeName;
                }
                else
                {
                    StartAttribute();
                    Reconsume(c, S.AttributeName);
                }
                break;

            case S.AttributeName:
                if (IsWhitespace(c) || c is '/' or '>' or Eof)
                {
                    LeaveAttributeName();
                    Reconsume(c, S.AfterAttributeName);
                }
                else if (c == '=')
                {
                    LeaveAttributeName();
                    State = S.BeforeAttributeValue;
                }
                else if (c == 0)
                {
                    Error("unexpected-null-character");
                    _attrName.Append('\uFFFD');
                }
                else
                {
                    if (c is '"' or '\'' or '<')
                        Error("unexpected-character-in-attribute-name");
                    _attrName.Append(Lower(c));
                }
                break;

            case S.AfterAttributeName:
                if (IsWhitespace(c))
                {
                }
                else if (c == '/') State = S.SelfClosingStartTag;
                else if (c == '=') State = S.BeforeAttributeValue;
                else if (c == '>') { State = S.Data; EmitTag(); }
                else if (c == Eof) { Error("eof-in-tag"); EmitEof(); }
                else
                {
                    StartAttribute();
                    Reconsume(c, S.AttributeName);
                }
                break;

            case S.BeforeAttributeValue:
                if (IsWhitespace(c))
                {
                }
                else if (c == '"') State = S.AttributeValueDoubleQuoted;
                else if (c == '\'') State = S.AttributeValueSingleQuoted;
                else if (c == '>')
                {
                    Error("missing-attribute-value");
                    State = S.Data;
                    EmitTag();
                }
                else Reconsume(c, S.AttributeValueUnquoted);
                break;

            case S.AttributeValueDoubleQuoted:
                QuotedAttributeValue(c, '"');
                break;
            case S.AttributeValueSingleQuoted:
                QuotedAttributeValue(c, '\'');
                break;

            case S.AttributeValueUnquoted:
                if (IsWhitespace(c)) State = S.BeforeAttributeName;
                else if (c == '&') { _returnState = S.AttributeValueUnquoted; State = S.CharacterReference; }
                else if (c == '>') { State = S.Data; EmitTag(); }
                else if (c == 0) { Error("unexpected-null-character"); _attrValue.Append('\uFFFD'); }
                else if (c == Eof) { Error("eof-in-tag"); EmitEof(); }
                else
                {
                    if (c is '"' or '\'' or '<' or '=' or '`')
                        Error("unexpected-character-in-unquoted-attribute-value");
                    _attrValue.Append((char)c);
                }
                break;

            case S.AfterAttributeValueQuoted:
                if (IsWhitespace(c)) State = S.BeforeAttributeName;
                else if (c == '/') State = S.SelfClosingStartTag;
                else if (c == '>') { State = S.Data; EmitTag(); }
                else if (c == Eof) { Error("eof-in-tag"); EmitEof(); }
                else
                {
                    Error("missing-whitespace-between-attributes");
                    Reconsume(c, S.BeforeAttributeName);
                }
                break;

            case S.SelfClosingStartTag:
                if (c == '>')
                {
                    _selfClosing = true;
                    State = S.Data;
                    EmitTag();
                }
                else if (c == Eof) { Error("eof-in-tag"); EmitEof(); }
                else
                {
                    Error("unexpected-solidus-in-tag");
                    Reconsume(c, S.BeforeAttributeName);
                }
                break;

            case S.BogusComment:
                switch (c)
                {
                    case '>': State = S.Data; EmitComment(); break;
                    case Eof: EmitComment(); EmitEof(); break;
                    case 0: Error("unexpected-null-character"); _data.Append('\uFFFD'); break;
                    default: _data.Append((char)c); break;
                }
                break;

            case S.MarkupDeclarationOpen:
                Reconsume(c, S.MarkupDeclarationOpen);
                MarkupDeclarationOpen();
                break;

            case S.CommentStart:
                if (c == '-') State = S.CommentStartDash;
                else if (c == '>')
                {
                    Error("abrupt-closing-of-empty-comment");
                    State = S.Data;
                    EmitComment();
                }
                else Reconsume(c, S.Comment);
                break;

            case S.CommentStartDash:
                if (c == '-') State = S.CommentEnd;
                else if (c == '>')
                {
                    Error("abrupt-closing-of-empty-comment");
                    State = S.Data;
                    EmitComment();
                }
                else if (c == Eof) EofInComment();
                else
                {
                    _data.Append('-');
                    Reconsume(c, S.Comment);
                }
                break;

            case S.Comment:
                switch (c)
                {
                    case '<': _data.Append('<'); State = S.CommentLessThanSign; break;
                    case '-': State = S.CommentEndDash; break;
                    case 0: Error("unexpected-null-character"); _data.Append('\uFFFD'); break;
                    case Eof: EofInComment(); break;
                    default: _data.Append((char)c); break;
                }
                break;

            case S.CommentLessThanSign:
                if (c == '!') { _data.Append('!'); State = S.CommentLessThanSignBang; }
                else if (c == '<') _data.Append('<');
                else Reconsume(c, S.Comment);
                break;
            case S.CommentLessThanSignBang:
                if (c == '-') State = S.CommentLessThanSignBangDash;
                else Reconsume(c, S.Comment);
                break;
            case S.CommentLessThanSignBangDash:
                if (c == '-') State = S.CommentLessThanSignBangDashDash;
                else Reconsume(c, S.CommentEndDash);
                break;
            case S.CommentLessThanSignBangDashDash:
                if (c is not ('>' or Eof))
                    Error("nested-comment");
                Reconsume(c, S.CommentEnd);
                break;

            case S.CommentEndDash:
                if (c == '-') State = S.CommentEnd;
                else if (c == Eof) EofInComment();
                else
                {
                    _data.Append('-');
                    Reconsume(c, S.Comment);
                }
                break;

            case S.CommentEnd:
                switch (c)
                {
                    case '>': State = S.Data; EmitComment(); break;
                    case '!': State = S.CommentEndBang; break;
                    case '-': _data.Append('-'); break;
                    case Eof: EofInComment(); break;
                    default: _data.Append("--"); Reconsume(c, S.Comment); break;
                }
                break;

            case S.CommentEndBang:
                switch (c)
                {
                    case '-': _data.Append("--!"); State = S.CommentEndDash; break;
                    case '>': Error("incorrectly-closed-comment"); State = S.Data; EmitComment(); break;
                    case Eof: EofInComment(); break;
                    default: _data.Append("--!"); Reconsume(c, S.Comment); break;
                }
                break;

            case S.Doctype:
                if (IsWhitespace(c)) State = S.BeforeDoctypeName;
                else if (c == '>') Reconsume(c, S.BeforeDoctypeName);
                else if (c == Eof)
                {
                    Error("eof-in-doctype");
                    StartDoctype();
                    _forceQuirks = true;
                    EmitDoctype();
                    EmitEof();
                }
                else
                {
                    Error("missing-whitespace-before-doctype-name");
                    Reconsume(c, S.BeforeDoctypeName);
                }
                break;

            case S.BeforeDoctypeName:
                if (IsWhitespace(c))
                {
                }
                else if (c == '>')
                {
                    Error("missing-doctype-name");
                    StartDoctype();
                    _forceQuirks = true;
                    State = S.Data;
                    EmitDoctype();
                }
                else if (c == Eof)
                {
                    Error("eof-in-doctype");
                    StartDoctype();
                    _forceQuirks = true;
                    EmitDoctype();
                    EmitEof();
                }
                else
                {
                    StartDoctype();
                    _hasDoctypeName = true;
                    if (c == 0)
                    {
                        Error("unexpected-null-character");
                        _name.Append('\uFFFD');
                    }
                    else
                    {
                        _name.Append(Lower(c));
                    }
                    State = S.DoctypeName;
                }
                break;

            case S.DoctypeName:
                if (IsWhitespace(c)) State = S.AfterDoctypeName;
                else if (c == '>') { State = S.Data; EmitDoctype(); }
                else if (c == 0) { Error("unexpected-null-character"); _name.Append('\uFFFD'); }
                else if (c == Eof) EofInDoctype();
                else _name.Append(Lower(c));
                break;

            case S.AfterDoctypeName:
                if (IsWhitespace(c))
                {
                }
                else if (c == '>') { State = S.Data; EmitDoctype(); }
                else if (c == Eof) EofInDoctype();
                else if (MatchesIgnoreCase(_pos - 1, "PUBLIC"))
                {
                    _pos += 5;
                    State = S.AfterDoctypePublicKeyword;
                }
                else if (MatchesIgnoreCase(_pos - 1, "SYSTEM"))
                {
                    _pos += 5;
                    State = S.AfterDoctypeSystemKeyword;
                }
                else
                {
                    Error("invalid-character-sequence-after-doctype-name");
                    _forceQuirks = true;
                    Reconsume(c, S.BogusDoctype);
                }
                break;

            case S.AfterDoctypePublicKeyword:
                AfterDoctypeKeyword(c, isPublic: true);
                break;
            case S.BeforeDoctypePublicIdentifier:
                BeforeDoctypeIdentifier(c, isPublic: true);
                break;
            case S.DoctypePublicIdentifierDoubleQuoted:
                DoctypeIdentifier(c, '"', isPublic: true);
                break;
            case S.DoctypePublicIdentifierSingleQuoted:
                DoctypeIdentifier(c, '\'', isPublic: true);
                break;

            case S.AfterDoctypePublicIdentifier:
                if (IsWhitespace(c)) State = S.BetweenDoctypePublicAndSystemIdentifiers;
                else if (c == '>') { State = S.Data; EmitDoctype(); }
                else if (c is '"' or '\'')
                {
                    Error("missing-whitespace-between-doctype-public-and-system-identifiers");
                    StartSystemId(c);
                }
                else if (c == Eof) EofInDoctype();
                else MissingQuoteBeforeIdentifier(c, isPublic: false);
                break;

            case S.BetweenDoctypePublicAndSystemIdentifiers:
                if (IsWhitespace(c))
                {
                }
                else if (c == '>') { State = S.Data; EmitDoctype(); }
                else if (c is '"' or '\'') StartSystemId(c);
                else if (c == Eof) EofInDoctype();
                else MissingQuoteBeforeIdentifier(c, isPublic: false);
                break;

            case S.AfterDoctypeSystemKeyword:
                AfterDoctypeKeyword(c, isPublic: false);
                break;
            case S.BeforeDoctypeSystemIdentifier:
                BeforeDoctypeIdentifier(c, isPublic: false);
                break;
            case S.DoctypeSystemIdentifierDoubleQuoted:
                DoctypeIdentifier(c, '"', isPublic: false);
                break;
            case S.DoctypeSystemIdentifierSingleQuoted:
                DoctypeIdentifier(c, '\'', isPublic: false);
                break;

            case S.AfterDoctypeSystemIdentifier:
                if (IsWhitespace(c))
                {
                }
                else if (c == '>') { State = S.Data; EmitDoctype(); }
                else if (c == Eof) EofInDoctype();
                else
                {
                    Error("unexpected-character-after-doctype-system-identifier");
                    Reconsume(c, S.BogusDoctype);
                }
                break;

            case S.BogusDoctype:
                switch (c)
                {
                    case '>': State = S.Data; EmitDoctype(); break;
                    case 0: Error("unexpected-null-character"); break;
                    case Eof: EmitDoctype(); EmitEof(); break;
                }
                break;

            case S.CdataSection:
                switch (c)
                {
                    case ']': State = S.CdataSectionBracket; break;
                    case Eof: Error("eof-in-cdata"); EmitEof(); break;
                    default: Emit((char)c); break;
                }
                break;
            case S.CdataSectionBracket:
                if (c == ']') State = S.CdataSectionEnd;
                else
                {
                    Emit(']');
                    Reconsume(c, S.CdataSection);
                }
                break;
            case S.CdataSectionEnd:
                if (c == ']') Emit(']');
                else if (c == '>') State = S.Data;
                else
                {
                    Emit("]]");
                    Reconsume(c, S.CdataSection);
                }
                break;

            case S.CharacterReference:
                _temp.Clear().Append('&');
                if (IsAlphanumeric(c)) Reconsume(c, S.NamedCharacterReference);
                else if (c == '#')
                {
                    _temp.Append('#');
                    State = S.NumericCharacterReference;
                }
                else
                {
                    FlushCharacterReference();
                    Reconsume(c, _returnState);
                }
                break;

            case S.NamedCharacterReference:
                Reconsume(c, S.NamedCharacterReference);
                NamedCharacterReference();
                break;

            case S.AmbiguousAmpersand:
                if (IsAlphanumeric(c))
                {
                    if (ConsumedAsPartOfAttribute) _attrValue.Append((char)c);
                    else Emit((char)c);
                }
                else
                {
                    if (c == ';')
                        Error("unknown-named-character-reference");
                    Reconsume(c, _returnState);
                }
                break;

            case S.NumericCharacterReference:
                _charRefCode = 0;
                if (c is 'x' or 'X')
                {
                    _temp.Append((char)c);
                    State = S.HexadecimalCharacterReferenceStart;
                }
                else
                {
                    Reconsume(c, S.DecimalCharacterReferenceStart);
                }
                break;
            case S.HexadecimalCharacterReferenceStart:
                NumericReferenceStart(c, IsHexDigit(c), S.HexadecimalCharacterReference);
                break;
            case S.DecimalCharacterReferenceStart:
                NumericReferenceStart(c, IsDigit(c), S.DecimalCharacterReference);
                break;
            case S.HexadecimalCharacterReference:
                if (IsHexDigit(c)) AddDigit(16, c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10);
                else NumericReferenceDigitsEnd(c);
                break;
            case S.DecimalCharacterReference:
                if (IsDigit(c)) AddDigit(10, c - '0');
                else NumericReferenceDigitsEnd(c);
                break;
            // The numeric character reference end state consumes nothing; the digit states run it directly.
        }
    }

    // RAWTEXT and script data states differ only in where '<' leads.
    private void RawTextLike(int c, S lessThanSign)
    {
        switch (c)
        {
            case '<': State = lessThanSign; break;
            case 0: Error("unexpected-null-character"); Emit('\uFFFD'); break;
            case Eof: EmitEof(); break;
            default: Emit((char)c); break;
        }
    }

    // RCDATA and RAWTEXT less-than sign states.
    private void LessThanSign(int c, S endTagOpen, S text)
    {
        if (c == '/')
        {
            _temp.Clear();
            State = endTagOpen;
        }
        else
        {
            Emit('<');
            Reconsume(c, text);
        }
    }

    // RCDATA, RAWTEXT, script data and script data escaped end tag open states.
    private void EndTagOpen(int c, S endTagName, S text)
    {
        if (IsAlpha(c))
        {
            StartTag(isEnd: true);
            Reconsume(c, endTagName);
        }
        else
        {
            Emit("</");
            Reconsume(c, text);
        }
    }

    // RCDATA, RAWTEXT, script data and script data escaped end tag name states.
    private void EndTagName(int c, S text)
    {
        var appropriate = LastStartTagName is not null && _name.Equals(LastStartTagName.AsSpan());
        if (IsWhitespace(c) && appropriate)
        {
            State = S.BeforeAttributeName;
        }
        else if (c == '/' && appropriate)
        {
            State = S.SelfClosingStartTag;
        }
        else if (c == '>' && appropriate)
        {
            State = S.Data;
            EmitTag();
        }
        else if (IsAlpha(c))
        {
            _name.Append(Lower(c));
            _temp.Append((char)c);
        }
        else
        {
            Emit("</");
            Emit(_temp.ToString());
            Reconsume(c, text);
        }
    }

    // Script data double escape start and end states.
    private void DoubleEscapeBoundary(int c, S ifScript, S otherwise)
    {
        if (IsWhitespace(c) || c is '/' or '>')
        {
            State = _temp.Equals("script".AsSpan()) ? ifScript : otherwise;
            Emit((char)c);
        }
        else if (IsAlpha(c))
        {
            _temp.Append(Lower(c));
            Emit((char)c);
        }
        else
        {
            Reconsume(c, otherwise);
        }
    }

    private void QuotedAttributeValue(int c, char quote)
    {
        if (c == quote) State = S.AfterAttributeValueQuoted;
        else if (c == '&')
        {
            _returnState = State;
            State = S.CharacterReference;
        }
        else if (c == 0) { Error("unexpected-null-character"); _attrValue.Append('\uFFFD'); }
        else if (c == Eof) { Error("eof-in-tag"); EmitEof(); }
        else _attrValue.Append((char)c);
    }

    private void MarkupDeclarationOpen()
    {
        var rest = _input.AsSpan(_pos);
        if (rest.StartsWith("--"))
        {
            _pos += 2;
            _data.Clear();
            State = S.CommentStart;
        }
        else if (MatchesIgnoreCase(_pos, "DOCTYPE"))
        {
            _pos += 7;
            State = S.Doctype;
        }
        else if (rest.StartsWith("[CDATA["))
        {
            _pos += 7;
            if (CdataAllowed)
            {
                State = S.CdataSection;
            }
            else
            {
                Error("cdata-in-html-content");
                _data.Clear().Append("[CDATA[");
                State = S.BogusComment;
            }
        }
        else
        {
            Error("incorrectly-opened-comment");
            _data.Clear();
            State = S.BogusComment;
        }
    }

    private bool MatchesIgnoreCase(int start, string word) =>
        _input.AsSpan(start).StartsWith(word, StringComparison.OrdinalIgnoreCase);

    private void AfterDoctypeKeyword(int c, bool isPublic)
    {
        if (IsWhitespace(c))
        {
            State = isPublic ? S.BeforeDoctypePublicIdentifier : S.BeforeDoctypeSystemIdentifier;
        }
        else if (c is '"' or '\'')
        {
            Error(isPublic ? "missing-whitespace-after-doctype-public-keyword" : "missing-whitespace-after-doctype-system-keyword");
            StartIdentifier(c, isPublic);
        }
        else
        {
            BeforeDoctypeIdentifier(c, isPublic);
        }
    }

    private void BeforeDoctypeIdentifier(int c, bool isPublic)
    {
        if (IsWhitespace(c))
        {
        }
        else if (c is '"' or '\'')
        {
            StartIdentifier(c, isPublic);
        }
        else if (c == '>')
        {
            Error(isPublic ? "missing-doctype-public-identifier" : "missing-doctype-system-identifier");
            _forceQuirks = true;
            State = S.Data;
            EmitDoctype();
        }
        else if (c == Eof)
        {
            EofInDoctype();
        }
        else
        {
            MissingQuoteBeforeIdentifier(c, isPublic);
        }
    }

    private void DoctypeIdentifier(int c, char quote, bool isPublic)
    {
        var id = isPublic ? _publicId : _systemId;
        if (c == quote)
        {
            State = isPublic ? S.AfterDoctypePublicIdentifier : S.AfterDoctypeSystemIdentifier;
        }
        else if (c == 0)
        {
            Error("unexpected-null-character");
            id.Append('\uFFFD');
        }
        else if (c == '>')
        {
            Error(isPublic ? "abrupt-doctype-public-identifier" : "abrupt-doctype-system-identifier");
            _forceQuirks = true;
            State = S.Data;
            EmitDoctype();
        }
        else if (c == Eof)
        {
            EofInDoctype();
        }
        else
        {
            id.Append((char)c);
        }
    }

    private void StartIdentifier(int quote, bool isPublic)
    {
        if (isPublic)
        {
            _hasPublicId = true;
            _publicId.Clear();
            State = quote == '"' ? S.DoctypePublicIdentifierDoubleQuoted : S.DoctypePublicIdentifierSingleQuoted;
        }
        else
        {
            StartSystemId(quote);
        }
    }

    private void StartSystemId(int quote)
    {
        _hasSystemId = true;
        _systemId.Clear();
        State = quote == '"' ? S.DoctypeSystemIdentifierDoubleQuoted : S.DoctypeSystemIdentifierSingleQuoted;
    }

    private void MissingQuoteBeforeIdentifier(int c, bool isPublic)
    {
        Error(isPublic ? "missing-quote-before-doctype-public-identifier" : "missing-quote-before-doctype-system-identifier");
        _forceQuirks = true;
        Reconsume(c, S.BogusDoctype);
    }

    private void EofInDoctype()
    {
        Error("eof-in-doctype");
        _forceQuirks = true;
        EmitDoctype();
        EmitEof();
    }

    private void EofInComment()
    {
        Error("eof-in-comment");
        EmitComment();
        EmitEof();
    }

    private bool ConsumedAsPartOfAttribute =>
        _returnState is S.AttributeValueDoubleQuoted or S.AttributeValueSingleQuoted or S.AttributeValueUnquoted;

    private void FlushCharacterReference()
    {
        if (ConsumedAsPartOfAttribute)
            _attrValue.Append(_temp);
        else
            Emit(_temp.ToString());
    }

    private void NamedCharacterReference()
    {
        var length = EntityTable.Match(_input.AsSpan(_pos), out var value);
        if (length == 0)
        {
            FlushCharacterReference();
            State = S.AmbiguousAmpersand;
            return;
        }

        _temp.Append(_input, _pos, length);
        _pos += length;
        var endsWithSemicolon = _input[_pos - 1] == ';';
        var next = _pos < _input.Length ? _input[_pos] : Eof;
        if (ConsumedAsPartOfAttribute && !endsWithSemicolon && (next == '=' || IsAlphanumeric(next)))
        {
            // Historical: "&amp=" or "&notit" inside an attribute value stays as written.
            FlushCharacterReference();
        }
        else
        {
            if (!endsWithSemicolon)
                Error("missing-semicolon-after-character-reference");
            _temp.Clear().Append(value);
            FlushCharacterReference();
        }
        State = _returnState;
    }

    private void NumericReferenceStart(int c, bool isDigit, S digits)
    {
        if (isDigit)
        {
            Reconsume(c, digits);
        }
        else
        {
            Error("absence-of-digits-in-numeric-character-reference");
            FlushCharacterReference();
            Reconsume(c, _returnState);
        }
    }

    // Saturates above the Unicode range so long digit runs cannot overflow.
    private void AddDigit(int radix, int digit) =>
        _charRefCode = _charRefCode > 0x10FFFF ? _charRefCode : _charRefCode * radix + digit;

    private void NumericReferenceDigitsEnd(int c)
    {
        if (c != ';')
        {
            Error("missing-semicolon-after-character-reference");
            if (c != Eof)
                _pos--;
        }
        NumericCharacterReferenceEnd();
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#numeric-character-reference-end-state
    private void NumericCharacterReferenceEnd()
    {
        var code = _charRefCode;
        if (code == 0)
        {
            Error("null-character-reference");
            code = 0xFFFD;
        }
        else if (code > 0x10FFFF)
        {
            Error("character-reference-outside-unicode-range");
            code = 0xFFFD;
        }
        else if (code is >= 0xD800 and <= 0xDFFF)
        {
            Error("surrogate-character-reference");
            code = 0xFFFD;
        }
        else if (code is >= 0xFDD0 and <= 0xFDEF || (code & 0xFFFE) == 0xFFFE)
        {
            Error("noncharacter-character-reference");
        }
        else if (code == 0x0D || (IsControl(code) && !IsWhitespace(code)))
        {
            Error("control-character-reference");
            code = C1Replacement(code);
        }

        _temp.Clear().Append(char.ConvertFromUtf32(code));
        FlushCharacterReference();
        State = _returnState;
    }

    private static bool IsControl(int code) => code is <= 0x1F or (>= 0x7F and <= 0x9F);

    // The replacement table of the numeric character reference end state.
    private static int C1Replacement(int code) => code switch
    {
        0x80 => 0x20AC, 0x82 => 0x201A, 0x83 => 0x0192, 0x84 => 0x201E, 0x85 => 0x2026, 0x86 => 0x2020,
        0x87 => 0x2021, 0x88 => 0x02C6, 0x89 => 0x2030, 0x8A => 0x0160, 0x8B => 0x2039, 0x8C => 0x0152,
        0x8E => 0x017D, 0x91 => 0x2018, 0x92 => 0x2019, 0x93 => 0x201C, 0x94 => 0x201D, 0x95 => 0x2022,
        0x96 => 0x2013, 0x97 => 0x2014, 0x98 => 0x02DC, 0x99 => 0x2122, 0x9A => 0x0161, 0x9B => 0x203A,
        0x9C => 0x0153, 0x9E => 0x017E, 0x9F => 0x0178,
        _ => code,
    };

    private void StartTag(bool isEnd)
    {
        _isEndTag = isEnd;
        _selfClosing = false;
        _inAttribute = false;
        _name.Clear();
        _token.Attributes.Clear();
        _attrNames.Clear();
    }

    private void StartAttribute()
    {
        CommitAttribute();
        _inAttribute = true;
        _pendingAttrName = null;
        _attrName.Clear();
        _attrValue.Clear();
    }

    // "When the user agent leaves the attribute name state": drop duplicates.
    private void LeaveAttributeName()
    {
        var name = _attrName.ToString();
        if (!_attrNames.Add(name))
        {
            Error("duplicate-attribute");
            _pendingAttrName = null;
        }
        else
        {
            _pendingAttrName = name;
        }
    }

    private void CommitAttribute()
    {
        if (_inAttribute && _pendingAttrName is not null)
            _token.Attributes.Add(new TokenAttribute(_pendingAttrName, _attrValue.ToString()));
        _inAttribute = false;
    }

    private void StartDoctype()
    {
        _name.Clear();
        _hasDoctypeName = _hasPublicId = _hasSystemId = _forceQuirks = false;
    }

    private void Emit(char c) => _text.Append(c);

    private void Emit(string s) => _text.Append(s);

    private void FlushText()
    {
        if (_text.Length == 0)
            return;
        _token.Kind = TokenKind.Characters;
        _token.Data = _text.ToString();
        _text.Clear();
        _sink.Process(_token);
    }

    private void EmitTag()
    {
        CommitAttribute();
        FlushText();
        _token.Kind = _isEndTag ? TokenKind.EndTag : TokenKind.StartTag;
        _token.Name = _name.ToString();
        _token.SelfClosing = _selfClosing;
        if (_isEndTag)
        {
            if (_token.Attributes.Count > 0)
                Error("end-tag-with-attributes");
            if (_selfClosing)
                Error("end-tag-with-trailing-solidus");
        }
        else
        {
            LastStartTagName = _token.Name;
        }
        _sink.Process(_token);
        _token.Attributes.Clear();
    }

    private void EmitComment()
    {
        FlushText();
        _token.Kind = TokenKind.Comment;
        _token.Data = _data.ToString();
        _sink.Process(_token);
    }

    private void EmitDoctype()
    {
        FlushText();
        _token.Kind = TokenKind.Doctype;
        _token.DoctypeName = _hasDoctypeName ? _name.ToString() : null;
        _token.PublicId = _hasPublicId ? _publicId.ToString() : null;
        _token.SystemId = _hasSystemId ? _systemId.ToString() : null;
        _token.ForceQuirks = _forceQuirks;
        _sink.Process(_token);
    }

    private void EmitEof()
    {
        FlushText();
        _token.Kind = TokenKind.EndOfFile;
        _sink.Process(_token);
        _done = true;
    }
}

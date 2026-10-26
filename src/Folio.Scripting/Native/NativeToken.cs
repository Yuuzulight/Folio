namespace Folio.Scripting.Native;

public enum NativeTokenType
{
    Eof,
    // Literals
    Number,
    String,
    True,
    False,
    Null,
    Undefined,

    // Identifiers & Keywords
    Identifier,
    Var,
    Let,
    Const,
    Function,
    Return,
    If,
    Else,
    While,
    Do,
    For,
    Switch,
    Case,
    Default,
    Break,
    Continue,
    Throw,
    Try,
    Catch,
    Finally,
    New,
    This,
    TypeOf,
    Delete,

    // Punctuation & Operators
    Plus,            // +
    Minus,           // -
    Star,            // *
    Slash,           // /
    Percent,         // %
    DoubleStar,      // **
    PlusPlus,        // ++
    MinusMinus,      // --

    Equal,           // =
    PlusEqual,       // +=
    MinusEqual,      // -=
    StarEqual,       // *=
    SlashEqual,      // /=

    EqualEqual,      // ==
    EqualEqualEqual, // ===
    ExclamationEqual,// !=
    ExclamationDoubleEqual, // !==
    Less,            // <
    LessEqual,       // <=
    Greater,         // >
    GreaterEqual,    // >=

    AmpersandAmpersand, // &&
    PipePipe,           // ||
    Exclamation,        // !
    Question,           // ?
    Colon,              // :
    Arrow,              // =>

    Dot,             // .
    Comma,           // ,
    Semicolon,       // ;
    OpenParen,       // (
    CloseParen,      // )
    OpenBrace,       // {
    CloseBrace,      // }
    OpenBracket,     // [
    CloseBracket,    // ]
}

public readonly struct NativeToken(NativeTokenType type, string text, double numberValue, int line, int column)
{
    public NativeTokenType Type { get; } = type;
    public string Text { get; } = text;
    public double NumberValue { get; } = numberValue;
    public int Line { get; } = line;
    public int Column { get; } = column;

    public override string ToString() => $"{Type} '{Text}' ({Line}:{Column})";
}

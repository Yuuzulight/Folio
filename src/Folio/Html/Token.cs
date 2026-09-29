namespace Folio.Html;

internal enum TokenKind
{
    Doctype,
    StartTag,
    EndTag,
    Comment,
    Characters,
    EndOfFile,
}

internal readonly record struct TokenAttribute(string Name, string Value);

/// <summary>
/// One token, reused for every emission (https://html.spec.whatwg.org/multipage/parsing.html#tokenization);
/// sinks copy what they keep. Consecutive character tokens arrive as one <see cref="TokenKind.Characters"/> run.
/// </summary>
internal sealed class Token
{
    public TokenKind Kind { get; internal set; }

    /// <summary>Tag name for start and end tags.</summary>
    public string Name { get; internal set; } = "";

    public List<TokenAttribute> Attributes { get; } = [];
    public bool SelfClosing { get; internal set; }

    /// <summary>Comment text, or the characters of a <see cref="TokenKind.Characters"/> run.</summary>
    public string Data { get; internal set; } = "";

    /// <summary>DOCTYPE fields; null means missing, as distinct from empty.</summary>
    public string? DoctypeName { get; internal set; }
    public string? PublicId { get; internal set; }
    public string? SystemId { get; internal set; }
    public bool ForceQuirks { get; internal set; }
}

internal interface ITokenSink
{
    /// <summary>Called for each token as it is emitted; may change <see cref="Tokenizer.State"/> before the next one.</summary>
    void Process(Token token);
}

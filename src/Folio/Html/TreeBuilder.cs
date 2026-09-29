using Folio.Dom;

namespace Folio.Html;

/// <summary>Parser limits (docs/study/16-resources-and-security.md). Exceeding one is reported, never thrown.</summary>
internal sealed record ParserLimits(int MaxDepth = 512, int MaxNodes = 200_000)
{
    public static ParserLimits Default { get; } = new();
}

/// <summary>
/// Tree construction (https://html.spec.whatwg.org/multipage/parsing.html#tree-construction), iterative, with the
/// scripting flag off and without the frameset modes (docs/study/01-html-parsing.md).
/// </summary>
internal sealed partial class TreeBuilder : ITokenSink
{
    private enum Mode
    {
        Initial,
        BeforeHtml,
        BeforeHead,
        InHead,
        InHeadNoscript,
        AfterHead,
        InBody,
        Text,
        InTable,
        InTableText,
        InCaption,
        InColumnGroup,
        InTableBody,
        InRow,
        InCell,
        InTemplate,
        AfterBody,
        AfterAfterBody,
    }

    private readonly DocumentNode _document;
    private readonly Tokenizer _tokenizer;
    private readonly ParserLimits _limits;
    private readonly Action<string, int>? _parseError;

    private readonly List<ElementNode> _open = [];
    private readonly List<ElementNode?> _formatting = []; // null is a marker
    private readonly List<Mode> _templateModes = [];
    private readonly System.Text.StringBuilder _pendingTableText = new();

    private Mode _mode = Mode.Initial;
    private Mode _originalMode;
    private ElementNode? _head;
    private ElementNode? _form;
    private bool _fosterParenting;
    private bool _skipNextNewline;
    private bool _selfClosingAcknowledged;
    private int _nodes;
    private bool _depthReported;

    private TreeBuilder(string html, ParserLimits limits, Action<string, int>? parseError)
    {
        _document = new DocumentNode();
        _limits = limits;
        _parseError = parseError;
        _tokenizer = new Tokenizer(html, this) { ParseError = parseError };
    }

    /// <param name="parseError">Receives tokenizer error codes, "unexpected-token" for tree construction errors,
    /// and "nesting-depth-limit" / "node-count-limit" when a limit stops work; with the input offset.</param>
    public static DocumentNode Parse(string html, ParserLimits? limits = null, Action<string, int>? parseError = null)
    {
        var builder = new TreeBuilder(html, limits ?? ParserLimits.Default, parseError);
        builder._tokenizer.Run();
        return builder._document;
    }

    private void Error() => _parseError?.Invoke("unexpected-token", _tokenizer.Position);

    // ---------------------------------------------------------------- dispatch

    public void Process(Token token)
    {
        if (_skipNextNewline)
        {
            _skipNextNewline = false;
            if (token.Kind == TokenKind.Characters && token.Data.StartsWith('\n'))
            {
                if (token.Data.Length == 1)
                    return;
                token.Data = token.Data[1..];
            }
        }

        if (token.Kind == TokenKind.Characters)
        {
            var text = token.Data;
            while (text.Length > 0)
                text = InForeignContent(null) ? ForeignCharacters(text) : Characters(_mode, text);
        }
        else
        {
            _selfClosingAcknowledged = false;
            if (InForeignContent(token))
                ForeignToken(token);
            else
                Process(_mode, token);

            if (token.Kind == TokenKind.StartTag && token.SelfClosing && !_selfClosingAcknowledged)
                Error(); // non-void-html-element-start-tag-with-trailing-solidus
        }

        _tokenizer.CdataAllowed = _open.Count > 0 && !IsHtml(CurrentNode);
    }

    // https://html.spec.whatwg.org/multipage/parsing.html#tree-construction-dispatcher; null token = characters.
    private bool InForeignContent(Token? token)
    {
        if (_open.Count == 0 || token?.Kind == TokenKind.EndOfFile)
            return false;
        var node = CurrentNode;
        if (IsHtml(node))
            return false;
        var isStart = token?.Kind == TokenKind.StartTag;
        if (IsMathMLTextIntegrationPoint(node))
        {
            if (token is null || (isStart && token.Name is not ("mglyph" or "malignmark")))
                return false;
        }
        if (node.Name.Namespace == Namespaces.MathML && node.LocalName == "annotation-xml" && isStart && token!.Name == "svg")
            return false;
        if (IsHtmlIntegrationPoint(node) && (token is null || isStart))
            return false;
        return true;
    }

    private string Characters(Mode mode, string text)
    {
        switch (mode)
        {
            case Mode.Initial:
            case Mode.BeforeHtml:
            case Mode.BeforeHead:
            {
                var ws = LeadingWhitespace(text);
                if (ws > 0)
                    return text[ws..];
                AnythingElse(mode);
                return text;
            }

            case Mode.InHead:
            case Mode.InHeadNoscript:
            case Mode.AfterHead:
            {
                var ws = LeadingWhitespace(text);
                if (ws > 0)
                {
                    InsertCharacters(text[..ws]);
                    return text[ws..];
                }
                AnythingElse(mode);
                return text;
            }

            case Mode.InBody:
            case Mode.InCaption:
            case Mode.InCell:
            case Mode.InTemplate:
                BodyCharacters(text);
                return "";

            case Mode.Text:
                InsertCharacters(text);
                return "";

            case Mode.InTable:
            case Mode.InTableBody:
            case Mode.InRow:
                if (CurrentIsHtml("table", "tbody", "template", "tfoot", "thead", "tr"))
                {
                    _pendingTableText.Clear();
                    _originalMode = _mode;
                    _mode = Mode.InTableText;
                    return text;
                }
                FosterCharacters(text);
                return "";

            case Mode.InTableText:
                foreach (var c in text)
                {
                    if (c == '\0')
                        Error();
                    else
                        _pendingTableText.Append(c);
                }
                return "";

            case Mode.InColumnGroup:
            {
                var ws = LeadingWhitespace(text);
                if (ws > 0)
                {
                    InsertCharacters(text[..ws]);
                    return text[ws..];
                }
                if (!CurrentIsHtml("colgroup"))
                {
                    Error();
                    return text[1..];
                }
                Pop();
                _mode = Mode.InTable;
                return text;
            }

            case Mode.AfterBody:
            case Mode.AfterAfterBody:
            {
                var ws = LeadingWhitespace(text);
                if (ws > 0)
                {
                    BodyCharacters(text[..ws]);
                    return text[ws..];
                }
                Error();
                _mode = Mode.InBody;
                return text;
            }
        }
        return "";
    }

    // "in body" for character tokens.
    private void BodyCharacters(string text)
    {
        text = WithoutNulls(text);
        if (text.Length == 0)
            return;
        ReconstructActiveFormattingElements();
        InsertCharacters(text);
    }

    // "in table", anything else: foster-parented "in body" handling.
    private void FosterCharacters(string text)
    {
        Error();
        _fosterParenting = true;
        BodyCharacters(text);
        _fosterParenting = false;
    }

    private string WithoutNulls(string text)
    {
        if (!text.Contains('\0'))
            return text;
        foreach (var c in text)
        {
            if (c == '\0')
                Error();
        }
        return text.Replace("\0", "");
    }

    private void FlushPendingTableText()
    {
        var text = _pendingTableText.ToString();
        _pendingTableText.Clear();
        if (LeadingWhitespace(text) < text.Length)
            FosterCharacters(text);
        else if (text.Length > 0)
            InsertCharacters(text);
        _mode = _originalMode;
    }

    // The "anything else" branch of the modes before "in body" for a character: insert what is implied, switch mode.
    private void AnythingElse(Mode mode)
    {
        switch (mode)
        {
            case Mode.Initial:
                Error();
                _document.Mode = DocumentMode.Quirks;
                _mode = Mode.BeforeHtml;
                break;
            case Mode.BeforeHtml:
                InsertHtmlElementIntoDocument(null);
                _mode = Mode.BeforeHead;
                break;
            case Mode.BeforeHead:
                _head = InsertHtmlElement("head");
                _mode = Mode.InHead;
                break;
            case Mode.InHead:
                Pop();
                _mode = Mode.AfterHead;
                break;
            case Mode.InHeadNoscript:
                Error();
                Pop();
                _mode = Mode.InHead;
                break;
            case Mode.AfterHead:
                InsertHtmlElement("body");
                _mode = Mode.InBody;
                break;
        }
    }

    private static int LeadingWhitespace(string text)
    {
        var i = 0;
        while (i < text.Length && text[i] is '\t' or '\n' or '\f' or '\r' or ' ')
            i++;
        return i;
    }

    private static Token Tag(TokenKind kind, string name) => new() { Kind = kind, Name = name };
}

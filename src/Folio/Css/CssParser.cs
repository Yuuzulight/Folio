namespace Folio.Css;

/// <summary>A component value (https://www.w3.org/TR/css-syntax-3/#component-value); <see cref="Start"/>/<see cref="End"/> index the source.</summary>
internal abstract class ComponentValue
{
    public int Start { get; init; }
    public int End { get; set; }
}

internal sealed class PreservedToken(CssToken token) : ComponentValue
{
    public CssToken Token { get; } = token;
}

internal sealed class CssFunction(string name) : ComponentValue
{
    public string Name { get; } = name;
    public List<ComponentValue> Arguments { get; } = [];
}

/// <summary>A <c>[]</c>, <c>()</c> or <c>{}</c> block, named by its opening token.</summary>
internal sealed class SimpleBlock(CssTokenKind open) : ComponentValue
{
    public CssTokenKind Open { get; } = open;
    public List<ComponentValue> Contents { get; } = [];
}

internal sealed class Declaration(string name, List<ComponentValue> value, bool important)
{
    /// <summary>As written for custom properties (case-sensitive), ASCII-lowercased otherwise.</summary>
    public string Name { get; } = name;
    public List<ComponentValue> Value { get; } = value;
    public bool Important { get; } = important;
    public bool IsCustomProperty => Name.StartsWith("--", StringComparison.Ordinal);
}

internal abstract class CssRule;

/// <summary>A rule's block contents: the leading declarations, then child rules (declarations that follow a
/// child rule become <see cref="NestedDeclarations"/>, keeping their order).</summary>
internal abstract class BlockRule : CssRule
{
    public List<Declaration> Declarations { get; } = [];
    public List<CssRule> Rules { get; } = [];
}

/// <summary>A qualified rule; in a stylesheet or nested block, its prelude is a selector list.</summary>
internal sealed class StyleRule(List<ComponentValue> prelude) : BlockRule
{
    public List<ComponentValue> Prelude { get; } = prelude;
}

internal sealed class AtRule(string name, List<ComponentValue> prelude) : BlockRule
{
    public string Name { get; } = name;
    public List<ComponentValue> Prelude { get; } = prelude;

    /// <summary>False for statement at-rules such as <c>@import</c>.</summary>
    public bool HasBlock { get; set; }
}

/// <summary>https://drafts.csswg.org/css-nesting-1/#nested-declarations-rule</summary>
internal sealed class NestedDeclarations(List<Declaration> declarations) : CssRule
{
    public List<Declaration> Declarations { get; } = declarations;
}

internal sealed class CssStyleSheet(string source, List<CssRule> rules)
{
    /// <summary>The preprocessed source; component values index into it.</summary>
    public string Source { get; } = source;
    public List<CssRule> Rules { get; } = rules;
}

/// <summary>
/// The CSS parser (https://www.w3.org/TR/css-syntax-3/#parsing), producing rules, declarations and component values.
/// Grammar checks of selectors, at-rule preludes and property values happen later, against these structures.
/// </summary>
internal sealed class CssParser
{
    /// <summary>Blocks and functions nested deeper than this are skipped, so hostile input cannot exhaust the stack.</summary>
    public const int MaxNesting = 256;

    // ponytail: the whole token list is held (about 40 bytes a token) because block contents backtrack from a
    // declaration to a rule; a streaming buffer with marks would cut that if very large sheets matter.
    private readonly List<CssToken> _tokens;
    private readonly int _maxRules;
    private readonly Action<string>? _limitReached;
    private int _pos;
    private int _depth;
    private int _rules;
    private bool _stopped;

    private CssParser(CssTokenizer tokenizer, int maxRules, Action<string>? limitReached)
    {
        _tokens = tokenizer.TokenizeAll();
        _maxRules = maxRules;
        _limitReached = limitReached;
    }

    /// <param name="maxRules">Rules (at any depth) after which parsing stops, keeping what is done
    /// (docs/study/16-resources-and-security.md); <paramref name="limitReached"/> is told once.</param>
    public static CssStyleSheet ParseStyleSheet(string source, int maxRules = 100_000, Action<string>? limitReached = null)
    {
        var tokenizer = new CssTokenizer(source);
        var parser = new CssParser(tokenizer, maxRules, limitReached);
        return new CssStyleSheet(tokenizer.Source, parser.ConsumeStyleSheetContents());
    }

    /// <summary>Parses a <c>style</c> attribute: https://www.w3.org/TR/css-syntax-3/#parse-block-contents.</summary>
    public static (string Source, BlockRule Block) ParseBlockContents(string source)
    {
        var tokenizer = new CssTokenizer(source);
        var parser = new CssParser(tokenizer, int.MaxValue, null);
        var block = new StyleRule([]);
        parser.ConsumeBlockContents(block);
        return (tokenizer.Source, block);
    }

    /// <summary>Parses a list of component values, e.g. a media query or a value on its own.</summary>
    public static (string Source, List<ComponentValue> Values) ParseComponentValues(string source)
    {
        var tokenizer = new CssTokenizer(source);
        var parser = new CssParser(tokenizer, int.MaxValue, null);
        var values = new List<ComponentValue>();
        while (parser.Peek.Kind != CssTokenKind.EndOfFile)
            parser.AddComponentValue(values);
        return (tokenizer.Source, values);
    }

    private CssToken Peek => _tokens[_pos];

    private CssToken Consume()
    {
        var token = _tokens[_pos];
        if (token.Kind != CssTokenKind.EndOfFile)
            _pos++;
        return token;
    }

    private bool AtEnd => _stopped || Peek.Kind == CssTokenKind.EndOfFile;

    private bool CountRule()
    {
        if (++_rules <= _maxRules)
            return true;
        if (!_stopped)
        {
            _stopped = true;
            _limitReached?.Invoke("rule-count-limit");
        }
        return false;
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-stylesheet-contents
    private List<CssRule> ConsumeStyleSheetContents()
    {
        var rules = new List<CssRule>();
        while (!AtEnd)
        {
            switch (Peek.Kind)
            {
                case CssTokenKind.Whitespace or CssTokenKind.Cdo or CssTokenKind.Cdc:
                    Consume();
                    break;
                case CssTokenKind.AtKeyword:
                    AddRule(rules, ConsumeAtRule(nested: false));
                    break;
                default:
                    AddRule(rules, ConsumeQualifiedRule(nested: false, stopAtSemicolon: false));
                    break;
            }
        }
        return rules;
    }

    private void AddRule(List<CssRule> rules, CssRule? rule)
    {
        if (rule is not null && CountRule())
            rules.Add(rule);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-at-rule
    private AtRule ConsumeAtRule(bool nested)
    {
        var rule = new AtRule(Consume().Value.ToLowerInvariant(), []);
        while (true)
        {
            switch (Peek.Kind)
            {
                case CssTokenKind.Semicolon:
                    Consume();
                    return rule;
                case CssTokenKind.EndOfFile:
                    return rule;
                case CssTokenKind.RightBrace:
                    if (nested)
                        return rule;
                    rule.Prelude.Add(new PreservedToken(Peek) { Start = Peek.Start, End = Peek.End });
                    Consume();
                    break;
                case CssTokenKind.LeftBrace:
                    rule.HasBlock = true;
                    ConsumeBlock(rule);
                    return rule;
                default:
                    AddComponentValue(rule.Prelude);
                    break;
            }
        }
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-qualified-rule
    private StyleRule? ConsumeQualifiedRule(bool nested, bool stopAtSemicolon)
    {
        var prelude = new List<ComponentValue>();
        while (true)
        {
            switch (Peek.Kind)
            {
                case CssTokenKind.EndOfFile:
                    return null;
                case CssTokenKind.Semicolon when stopAtSemicolon:
                    return null;
                case CssTokenKind.RightBrace:
                    if (nested)
                        return null;
                    prelude.Add(new PreservedToken(Peek) { Start = Peek.Start, End = Peek.End });
                    Consume();
                    break;
                case CssTokenKind.LeftBrace:
                    if (LooksLikeCustomPropertyOrDeclaration(prelude))
                    {
                        if (nested)
                            ConsumeBadDeclarationRemnants(nested);
                        else
                            ConsumeBlock(new StyleRule([]));
                        return null;
                    }
                    var rule = new StyleRule(prelude);
                    ConsumeBlock(rule);
                    return rule;
                default:
                    AddComponentValue(prelude);
                    break;
            }
        }
    }

    // "If the first two non-whitespace values of rule's prelude are an <ident-token> whose value starts with "--"
    // followed by a <colon-token>".
    private static bool LooksLikeCustomPropertyOrDeclaration(List<ComponentValue> prelude)
    {
        var significant = prelude.Where(v => v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).Take(2).ToList();
        return significant is [PreservedToken { Token.Kind: CssTokenKind.Ident } name, PreservedToken { Token.Kind: CssTokenKind.Colon }]
            && name.Token.Value.StartsWith("--", StringComparison.Ordinal);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-block (the current token is the '{')
    private void ConsumeBlock(BlockRule rule)
    {
        Consume();
        if (_depth >= MaxNesting)
        {
            SkipToMatchingClose(CssTokenKind.RightBrace);
            return;
        }
        _depth++;
        ConsumeBlockContents(rule);
        _depth--;
        if (Peek.Kind == CssTokenKind.RightBrace)
            Consume();
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-block-contents
    private void ConsumeBlockContents(BlockRule rule)
    {
        List<Declaration>? pending = null; // declarations after the first child rule
        while (!AtEnd)
        {
            switch (Peek.Kind)
            {
                case CssTokenKind.Whitespace or CssTokenKind.Semicolon:
                    Consume();
                    break;
                case CssTokenKind.RightBrace:
                    FlushDeclarations(rule, ref pending);
                    return;
                case CssTokenKind.AtKeyword:
                    FlushDeclarations(rule, ref pending);
                    AddRule(rule.Rules, ConsumeAtRule(nested: true));
                    break;
                default:
                    var mark = _pos;
                    if (ConsumeDeclaration(nested: true) is { } declaration)
                    {
                        if (rule.Rules.Count == 0)
                            rule.Declarations.Add(declaration);
                        else
                            (pending ??= []).Add(declaration);
                        break;
                    }
                    _pos = mark;
                    if (ConsumeQualifiedRule(nested: true, stopAtSemicolon: true) is { } child)
                    {
                        FlushDeclarations(rule, ref pending);
                        AddRule(rule.Rules, child);
                    }
                    else if (Peek.Kind == CssTokenKind.Semicolon)
                    {
                        Consume();
                    }
                    break;
            }
        }
        FlushDeclarations(rule, ref pending);
    }

    private void FlushDeclarations(BlockRule rule, ref List<Declaration>? pending)
    {
        if (pending is { Count: > 0 })
            AddRule(rule.Rules, new NestedDeclarations(pending));
        pending = null;
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-declaration. The only caller restores the position and parses a
    // rule when this returns null, so a failure returns at once instead of consuming the remnants: same result,
    // without scanning ahead through following rules (which would make a block of rules quadratic).
    private Declaration? ConsumeDeclaration(bool nested)
    {
        if (Peek.Kind != CssTokenKind.Ident)
            return null;
        var nameToken = Consume();
        var isCustom = nameToken.Value.StartsWith("--", StringComparison.Ordinal);
        SkipWhitespace();
        if (Peek.Kind != CssTokenKind.Colon)
            return null;
        Consume();
        SkipWhitespace();

        var value = new List<ComponentValue>();
        bool hasBraceBlock = false, hasOther = false;
        while (!AtEnd && Peek.Kind != CssTokenKind.Semicolon && !(nested && Peek.Kind == CssTokenKind.RightBrace))
        {
            AddComponentValue(value);
            if (value[^1] is SimpleBlock { Open: CssTokenKind.LeftBrace })
                hasBraceBlock = true;
            else if (value[^1] is not PreservedToken { Token.Kind: CssTokenKind.Whitespace })
                hasOther = true;
            if (!isCustom && hasBraceBlock && hasOther)
                return null; // a {}-block mixed with other values is only valid in custom properties
        }

        TrimWhitespace(value);
        var important = false;
        var significant = value.Select((v, i) => (v, i)).Where(x => x.v is not PreservedToken { Token.Kind: CssTokenKind.Whitespace }).ToList();
        if (significant.Count >= 2
            && significant[^2].v is PreservedToken bang && bang.Token.IsDelim('!')
            && significant[^1].v is PreservedToken ident && ident.Token.IsIdent("important"))
        {
            value.RemoveRange(significant[^2].i, value.Count - significant[^2].i);
            TrimWhitespace(value);
            important = true;
        }

        return new Declaration(isCustom ? nameToken.Value : nameToken.Value.ToLowerInvariant(), value, important);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-the-remnants-of-a-bad-declaration
    private void ConsumeBadDeclarationRemnants(bool nested)
    {
        while (!AtEnd)
        {
            if (Peek.Kind == CssTokenKind.Semicolon)
            {
                Consume();
                return;
            }
            if (nested && Peek.Kind == CssTokenKind.RightBrace)
                return;
            AddComponentValue(null);
        }
    }

    private void SkipWhitespace()
    {
        while (Peek.Kind == CssTokenKind.Whitespace)
            Consume();
    }

    private static void TrimWhitespace(List<ComponentValue> values)
    {
        while (values.Count > 0 && values[^1] is PreservedToken { Token.Kind: CssTokenKind.Whitespace })
            values.RemoveAt(values.Count - 1);
        while (values.Count > 0 && values[0] is PreservedToken { Token.Kind: CssTokenKind.Whitespace })
            values.RemoveAt(0);
    }

    // https://www.w3.org/TR/css-syntax-3/#consume-component-value (null target: consume and drop)
    private void AddComponentValue(List<ComponentValue>? into)
    {
        var token = Consume();
        ComponentValue value;
        switch (token.Kind)
        {
            case CssTokenKind.LeftBrace or CssTokenKind.LeftBracket or CssTokenKind.LeftParen:
                var block = new SimpleBlock(token.Kind) { Start = token.Start };
                ConsumeNested(block.Contents, Mirror(token.Kind));
                block.End = _tokens[_pos - 1].End;
                value = block;
                break;
            case CssTokenKind.Function:
                var function = new CssFunction(token.Value) { Start = token.Start };
                ConsumeNested(function.Arguments, CssTokenKind.RightParen);
                function.End = _tokens[_pos - 1].End;
                value = function;
                break;
            default:
                value = new PreservedToken(token) { Start = token.Start, End = token.End };
                break;
        }
        into?.Add(value);
    }

    private static CssTokenKind Mirror(CssTokenKind open) => open switch
    {
        CssTokenKind.LeftBrace => CssTokenKind.RightBrace,
        CssTokenKind.LeftBracket => CssTokenKind.RightBracket,
        _ => CssTokenKind.RightParen,
    };

    // https://www.w3.org/TR/css-syntax-3/#consume-simple-block and #consume-function (the opener is consumed)
    private void ConsumeNested(List<ComponentValue> into, CssTokenKind close)
    {
        if (_depth >= MaxNesting)
        {
            SkipToMatchingClose(close);
            return;
        }
        _depth++;
        while (true)
        {
            if (Peek.Kind == close)
            {
                Consume();
                break;
            }
            if (Peek.Kind == CssTokenKind.EndOfFile)
                break; // parse error
            AddComponentValue(into);
        }
        _depth--;
    }

    // Past the nesting limit: skip, without recursion, to the token that closes the current block.
    private void SkipToMatchingClose(CssTokenKind close)
    {
        var stack = new Stack<CssTokenKind>();
        stack.Push(close);
        while (stack.Count > 0 && Peek.Kind != CssTokenKind.EndOfFile)
        {
            var token = Consume();
            switch (token.Kind)
            {
                case CssTokenKind.LeftBrace or CssTokenKind.LeftBracket or CssTokenKind.LeftParen:
                    stack.Push(Mirror(token.Kind));
                    break;
                case CssTokenKind.Function:
                    stack.Push(CssTokenKind.RightParen);
                    break;
                default:
                    if (token.Kind == stack.Peek())
                        stack.Pop();
                    break;
            }
        }
    }
}

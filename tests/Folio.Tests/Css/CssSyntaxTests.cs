using System.Globalization;
using System.Text;
using Folio.Css;

namespace Folio.Tests.Css;

public class CssSyntaxTests
{
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> TokenizerCases = new(() => CaseFiles.Load("Css", "Tokenizer"));
    private static readonly Lazy<Dictionary<string, CaseFiles.Case>> ParserCases = new(() => CaseFiles.Load("Css", "Parser"));

    public static TheoryData<string> TokenizerIds => new(TokenizerCases.Value.Keys.Order());
    public static TheoryData<string> ParserIds => new(ParserCases.Value.Keys.Order());

    [Theory]
    [MemberData(nameof(TokenizerIds))]
    public void Tokenizes(string id)
    {
        var test = TokenizerCases.Value[id];

        var tokens = new CssTokenizer(test.Input).TokenizeAll();

        Assert.Equal(test.Expected, tokens.Where(t => t.Kind != CssTokenKind.EndOfFile).Select(Dump));
    }

    [Theory]
    [MemberData(nameof(ParserIds))]
    public void Parses(string id)
    {
        var test = ParserCases.Value[id];

        var sheet = CssParser.ParseStyleSheet(test.Input);

        var lines = new List<string>();
        DumpRules(sheet.Source, sheet.Rules, 0, lines);
        Assert.Equal(test.Expected, lines);
    }

    [Fact]
    public void ParsesStyleAttributeContents()
    {
        var (source, block) = CssParser.ParseBlockContents("color: red; ; bad; margin: 0 !important");

        Assert.Equal(["color: red", "margin: 0 !important"], block.Declarations.Select(d => DumpDeclaration(source, d)));
    }

    [Fact]
    public void DeepNestingIsSkippedWithoutExhaustingTheStack()
    {
        var css = "p { x: " + new string('(', 100_000) + new string(')', 100_000) + " } q { color: red }";

        var sheet = CssParser.ParseStyleSheet(css);

        Assert.Contains(sheet.Rules, r => r is StyleRule s && Text(sheet.Source, s.Prelude) == "q");
    }

    [Fact]
    public void RuleLimitStopsParsing()
    {
        var reports = new List<string>();
        var css = string.Concat(Enumerable.Repeat("p { } ", 50));

        var sheet = CssParser.ParseStyleSheet(css, maxRules: 10, limitReached: reports.Add);

        Assert.Equal(10, sheet.Rules.Count);
        Assert.Equal(["rule-count-limit"], reports);
    }

    [Fact]
    public void LargeBlockOfRulesParsesInLinearTime()
    {
        var css = "@media screen { " + string.Concat(Enumerable.Range(0, 20_000).Select(i => $"div{i} {{ color: red }} a:hover{{}} ")) + "}";

        var media = (AtRule)CssParser.ParseStyleSheet(css).Rules.Single();

        Assert.Equal(40_000, media.Rules.Count);
    }

    private static string Dump(CssToken t) => t.Kind switch
    {
        CssTokenKind.Ident => $"ident {CaseFiles.Escape(t.Value)}",
        CssTokenKind.Function => $"function {t.Value}",
        CssTokenKind.AtKeyword => $"at-keyword {t.Value}",
        CssTokenKind.Hash => $"{(t.IsIdHash ? "hash-id" : "hash")} {t.Value}",
        CssTokenKind.String => $"string \"{CaseFiles.Escape(t.Value)}\"",
        CssTokenKind.BadString => "bad-string",
        CssTokenKind.Url => $"url \"{CaseFiles.Escape(t.Value)}\"",
        CssTokenKind.BadUrl => "bad-url",
        CssTokenKind.Delim => $"delim {t.Value}",
        CssTokenKind.Number => $"number {Number(t)}",
        CssTokenKind.Percentage => $"percentage {Number(t)}",
        CssTokenKind.Dimension => $"dimension {t.Number.ToString("R", CultureInfo.InvariantCulture)} {t.Value}{Flags(t)}",
        CssTokenKind.Whitespace => "whitespace",
        CssTokenKind.Cdo => "CDO",
        CssTokenKind.Cdc => "CDC",
        CssTokenKind.Colon => ":",
        CssTokenKind.Semicolon => ";",
        CssTokenKind.Comma => ",",
        CssTokenKind.LeftBracket => "[",
        CssTokenKind.RightBracket => "]",
        CssTokenKind.LeftParen => "(",
        CssTokenKind.RightParen => ")",
        CssTokenKind.LeftBrace => "{",
        CssTokenKind.RightBrace => "}",
        _ => t.Kind.ToString(),
    };

    private static string Number(CssToken t) => t.Number.ToString("R", CultureInfo.InvariantCulture) + Flags(t);

    private static string Flags(CssToken t) => (t.IsInteger ? " integer" : "") + (t.HasSign ? " signed" : "");

    private static void DumpRules(string source, List<CssRule> rules, int depth, List<string> lines)
    {
        var indent = new string(' ', depth * 2);
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case StyleRule style:
                    lines.Add($"{indent}style {Text(source, style.Prelude)}");
                    DumpBlock(source, style, depth + 1, lines);
                    break;
                case AtRule at:
                    var prelude = Text(source, at.Prelude);
                    lines.Add($"{indent}at {at.Name}{(prelude.Length > 0 ? " " + prelude : "")}{(at.HasBlock ? " {" : "")}");
                    DumpBlock(source, at, depth + 1, lines);
                    break;
                case NestedDeclarations nested:
                    lines.Add($"{indent}declarations");
                    lines.AddRange(nested.Declarations.Select(d => $"{indent}  {DumpDeclaration(source, d)}"));
                    break;
            }
        }
    }

    private static void DumpBlock(string source, BlockRule block, int depth, List<string> lines)
    {
        lines.AddRange(block.Declarations.Select(d => $"{new string(' ', depth * 2)}{DumpDeclaration(source, d)}"));
        DumpRules(source, block.Rules, depth, lines);
    }

    private static string DumpDeclaration(string source, Declaration d)
    {
        var value = Text(source, d.Value);
        return $"{d.Name}:{(value.Length > 0 ? " " + value : "")}{(d.Important ? " !important" : "")}";
    }

    // Component values as written, whitespace collapsed to single spaces.
    private static string Text(string source, List<ComponentValue> values)
    {
        var text = new StringBuilder();
        Append(source, values, text);
        return text.ToString().Trim();
    }

    private static void Append(string source, List<ComponentValue> values, StringBuilder text)
    {
        foreach (var value in values)
        {
            switch (value)
            {
                case PreservedToken { Token.Kind: CssTokenKind.Whitespace }:
                    text.Append(' ');
                    break;
                case PreservedToken token:
                    text.Append(source, token.Start, token.End - token.Start);
                    break;
                case CssFunction function:
                    text.Append(function.Name).Append('(');
                    Append(source, function.Arguments, text);
                    text.Append(')');
                    break;
                case SimpleBlock block:
                    var (open, close) = block.Open switch
                    {
                        CssTokenKind.LeftBrace => ('{', '}'),
                        CssTokenKind.LeftBracket => ('[', ']'),
                        _ => ('(', ')'),
                    };
                    text.Append(open);
                    Append(source, block.Contents, text);
                    text.Append(close);
                    break;
            }
        }
    }
}

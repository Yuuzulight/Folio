using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeJavaScriptLexerTests
{
    [Fact]
    public void SkipsCommentsAndPreservesTokenLocations()
    {
        var lexer = new JavaScriptLexer("// heading\n const answer = 0x2a; /* tail */");
        var tokens = Enumerable.Range(0, 6).Select(_ => lexer.Next()).ToArray();
        Assert.Equal(JavaScriptTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("const", tokens[0].Text);
        Assert.Equal((2, 2), (tokens[0].Line, tokens[0].Column));
        Assert.Equal("answer", tokens[1].Text);
        Assert.Equal(JavaScriptTokenKind.Number, tokens[3].Kind);
        Assert.Equal("0x2a", tokens[3].Text);
        Assert.Equal(JavaScriptTokenKind.End, tokens[5].Kind);
    }

    [Fact]
    public void DecodesStringEscapesWithoutExecutingCode()
    {
        var token = new JavaScriptLexer("'a\\n\\t\\\\b'").Next();
        Assert.Equal(JavaScriptTokenKind.String, token.Kind);
        Assert.Equal("a\n\t\\b", token.Text);
    }

    [Fact]
    public void RejectsUnterminatedStringsAndComments()
    {
        Assert.Throws<InvalidDataException>(() => new JavaScriptLexer("'unterminated").Next());
        Assert.Throws<InvalidDataException>(() => new JavaScriptLexer("/* unterminated").Next());
    }

    [Fact]
    public void EnforcesTokenBudget()
    {
        var lexer = new JavaScriptLexer("a b", maxTokens: 2);
        Assert.Equal("a", lexer.Next().Text);
        Assert.Equal("b", lexer.Next().Text);
        Assert.Throws<InvalidDataException>(() => lexer.Next());
    }

    [Fact]
    public void KeepsMultiCharacterOperatorsAsSingleTokens()
    {
        var lexer = new JavaScriptLexer("a===b && c=>d");
        Assert.Equal("a", lexer.Next().Text);
        Assert.Equal("===", lexer.Next().Text);
        Assert.Equal("b", lexer.Next().Text);
        Assert.Equal("&&", lexer.Next().Text);
        Assert.Equal("c", lexer.Next().Text);
        Assert.Equal("=>", lexer.Next().Text);
    }
}

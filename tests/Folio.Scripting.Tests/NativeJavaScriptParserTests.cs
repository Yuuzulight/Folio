using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeJavaScriptParserTests
{
    [Fact]
    public void ParsesPrecedenceMemberAccessAndCalls()
    {
        var expression = new JavaScriptParser("send(value + 2 * 3).done").ParseExpression();
        var member = Assert.IsType<JavaScriptMemberExpression>(expression);
        Assert.Equal("done", member.Name);
        var call = Assert.IsType<JavaScriptCallExpression>(member.Target);
        Assert.Single(call.Arguments);
        var add = Assert.IsType<JavaScriptBinaryExpression>(call.Arguments[0]);
        Assert.Equal("+", add.Operator);
        Assert.Equal("*", Assert.IsType<JavaScriptBinaryExpression>(add.Right).Operator);
    }

    [Fact]
    public void ParsesLiteralsAndUnaryOperators()
    {
        var expression = Assert.IsType<JavaScriptUnaryExpression>(new JavaScriptParser("!-42").ParseExpression());
        Assert.Equal("!", expression.Operator);
        var negative = Assert.IsType<JavaScriptUnaryExpression>(expression.Operand);
        Assert.Equal("-", negative.Operator);
        Assert.Equal("42", Assert.IsType<JavaScriptLiteralExpression>(negative.Operand).Text);
    }

    [Fact]
    public void EnforcesNodeBudgetAndRejectsTrailingTokens()
    {
        Assert.Throws<InvalidDataException>(() => new JavaScriptParser("a+b", maxNodes: 2).ParseExpression());
        Assert.Throws<InvalidDataException>(() => new JavaScriptParser("a b").ParseExpression());
    }

    [Fact]
    public void ParsesArrayLiteralsAndIndexing()
    {
        var expression = Assert.IsType<JavaScriptIndexExpression>(new JavaScriptParser("[1, 2, 3][1]").ParseExpression());
        Assert.IsType<JavaScriptArrayExpression>(expression.Target);
    }

    [Fact]
    public void ParsesObjectLiteralsAndMemberAccess()
    {
        var expression = Assert.IsType<JavaScriptMemberExpression>(new JavaScriptParser("{answer: 42}.answer").ParseExpression());
        Assert.Equal("answer", expression.Name);
        Assert.IsType<JavaScriptObjectExpression>(expression.Target);
    }

    [Fact]
    public void ParsesFunctionExpressions()
    {
        var expression = Assert.IsType<JavaScriptFunctionExpression>(new JavaScriptParser("function(a, b) { return a + b; }").ParseExpression());
        Assert.Equal(["a", "b"], expression.Parameters);
        Assert.Single(expression.Body);
    }

    [Fact]
    public void ParsesWithScopes()
    {
        var program = new JavaScriptParser("with (scope) { value = value + 1; }").ParseProgram();
        Assert.IsType<JavaScriptWithStatement>(program.Single());
    }

    [Fact]
    public void ParsesClassDeclarationsAndGetters()
    {
        var declaration = Assert.IsType<JavaScriptClassDeclarationStatement>(new JavaScriptParser("class Sample { constructor(value) { this.value = value; } get answer() { return this.value; } }").ParseProgram().Single());
        Assert.Equal("Sample", declaration.Name);
        Assert.Equal(2, declaration.Class.Methods.Count);
        Assert.True(declaration.Class.Methods[1].Getter);
    }
}

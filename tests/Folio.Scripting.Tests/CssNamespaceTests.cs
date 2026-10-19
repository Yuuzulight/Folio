using Folio.Html;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

/// <summary>The <c>CSS</c> namespace (issue #500): <c>CSS.escape</c> (CSSOM §2.1 serialize an identifier) and <c>CSS.supports</c>.</summary>
public class CssNamespaceTests
{
    private static string Eval(string script)
    {
        using var engine = new JintScriptEngine();
        DomBindings.Install(engine, TreeBuilder.Parse("<html><body></body></html>"));
        return engine.Evaluate(script).AsString()!;
    }

    [Fact]
    public void EscapeFollowsSerializeAnIdentifier()
    {
        // [input, expected]; the script sees the real characters, so these are JS literals.
        var mismatches = Eval("""
            const cases = [
                ['', ''],
                ['abc_-XYZ09', 'abc_-XYZ09'],
                ['é漢字', 'é漢字'],                  // from U+0080 up, left as they are
                ['\0', '�'],                    // NUL
                ['a\0b', 'a�b'],
                ['\x01', '\\1 '],                    // control characters: backslash, hex, space
                ['a\x1fb', 'a\\1f b'],
                ['\x7f', '\\7f '],
                ['0a', '\\30 a'],                    // a leading digit
                ['9', '\\39 '],
                ['-1', '-\\31 '],                    // a digit after a leading hyphen
                ['-a', '-a'],
                ['-', '\\-'],                        // a lone hyphen
                ['--', '--'],
                ['a b', 'a\\ b'],                    // anything else gets a backslash
                ['a.b#c:d', 'a\\.b\\#c\\:d'],
                ['\\', '\\\\'],
                ['"\'()', '\\"\\\'\\(\\)'],
                ['😀a', '😀a'],                       // astral characters are one code point
            ];
            cases.filter(([input, expected]) => CSS.escape(input) !== expected)
                 .map(([input, expected]) => JSON.stringify(input) + ' gave ' + JSON.stringify(CSS.escape(input)) + ', wanted ' + JSON.stringify(expected))
                 .join('; ')
            """);
        Assert.Equal("", mismatches);
    }

    [Fact]
    public void EscapeConvertsItsArgumentToAString() => Assert.Equal("\\31 2", Eval("CSS.escape(12)"));

    [Theory]
    [InlineData("display", "grid", true)]
    [InlineData("display", "nonsense", false)]
    [InlineData("not-a-property", "x", false)]
    [InlineData("color", "red", true)]
    [InlineData("color", "not-a-color", false)]
    [InlineData("--custom", "anything at all", true)]
    [InlineData("DISPLAY", "GRID", true)]
    [InlineData("all", "initial", true)]
    [InlineData("all", "inherit", true)]
    [InlineData("all", "unset", true)]
    [InlineData("all", "revert", true)]
    [InlineData("all", "revert-layer", true)]
    [InlineData("all", "red", false)]
    [InlineData("all", "initial unset", false)]
    public void TwoArgumentsAnswerLikeADeclarationInAnAtSupportsRule(string property, string value, bool expected) =>
        Assert.Equal(expected ? "true" : "false", Eval($"String(CSS.supports('{property}', '{value}'))"));

    [Theory]
    [InlineData("(display: grid)", true)]
    [InlineData("(display: nonsense)", false)]
    [InlineData("display: grid", true)]                                  // parentheses are added when the text is not a condition
    [InlineData("(display: grid) and (color: red)", true)]
    [InlineData("(display: grid) and (color: nope)", false)]
    [InlineData("(display: nope) or (color: red)", true)]
    [InlineData("not (display: nope)", true)]
    [InlineData("selector(a > b)", true)]
    [InlineData("selector(:no-such-pseudo)", false)]
    [InlineData("((display: grid))", true)]
    [InlineData("", false)]
    [InlineData("(", false)]
    public void OneArgumentIsAnAtSupportsCondition(string condition, bool expected) =>
        Assert.Equal(expected ? "true" : "false", Eval($"String(CSS.supports('{condition}'))"));

    [Fact]
    public void WithNoArgumentsItThrowsATypeError() =>
        Assert.Equal("TypeError", Eval("(() => { try { CSS.supports(); return 'no'; } catch (e) { return e.name; } })()"));
}

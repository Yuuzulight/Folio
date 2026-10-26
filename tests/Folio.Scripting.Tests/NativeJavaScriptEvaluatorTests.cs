using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeJavaScriptEvaluatorTests
{
    [Fact]
    public void EvaluatesArithmeticAndStringConcatenation()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(7d, evaluator.Evaluate(new JavaScriptParser("1 + 2 * 3").ParseExpression()));
        Assert.Equal("answer: 7", evaluator.Evaluate(new JavaScriptParser("'answer: ' + (1 + 2 * 3)").ParseExpression()));
    }

    [Fact]
    public void ReadsAndAssignsLexicalBindings()
    {
        var bindings = new Dictionary<string, object?> { ["value"] = 4d };
        var evaluator = new JavaScriptEvaluator(bindings);
        Assert.Equal(9d, evaluator.Evaluate(new JavaScriptParser("value = value + 5").ParseExpression()));
        Assert.Equal(9d, bindings["value"]);
    }

    [Fact]
    public void EnforcesStepLimitAndRejectsUnsupportedCalls()
    {
        var evaluator = new JavaScriptEvaluator(maxSteps: 1);
        Assert.Throws<InvalidOperationException>(() => evaluator.Evaluate(new JavaScriptParser("1 + 2").ParseExpression()));
        Assert.Throws<NotSupportedException>(() => new JavaScriptEvaluator().Evaluate(new JavaScriptParser("alert(1)").ParseExpression()));
    }

    [Fact]
    public void CallsOnlyExplicitlySuppliedHostFunctions()
    {
        var bindings = new Dictionary<string, object?>
        {
            ["add"] = (Func<IReadOnlyList<object?>, object?>)(args => (double)args[0]! + (double)args[1]!),
        };
        var result = new JavaScriptEvaluator(bindings).Evaluate(new JavaScriptParser("add(2, 3)").ParseExpression());
        Assert.Equal(5d, result);
    }

    [Fact]
    public void EvaluatesArrayElementsAndStringIndexes()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(2d, evaluator.Evaluate(new JavaScriptParser("[1, 2, 3][1]").ParseExpression()));
        Assert.Equal("b", evaluator.Evaluate(new JavaScriptParser("'abc'[1]").ParseExpression()));
    }

    [Fact]
    public void EvaluatesObjectPropertiesAndMissingMembers()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(42d, evaluator.Evaluate(new JavaScriptParser("{answer: 42}.answer").ParseExpression()));
        Assert.Null(evaluator.Evaluate(new JavaScriptParser("{answer: 42}.missing").ParseExpression()));
    }

    [Fact]
    public void MutatesBoundedObjectsAndArrays()
    {
        var bindings = new Dictionary<string, object?>
        {
            ["obj"] = new Dictionary<string, object?> { ["value"] = 1d },
            ["items"] = new object?[] { 1d, 2d },
        };
        var evaluator = new JavaScriptEvaluator(bindings);
        Assert.Equal(7d, evaluator.Evaluate(new JavaScriptParser("obj.value = 7").ParseExpression()));
        Assert.Equal(9d, evaluator.Evaluate(new JavaScriptParser("items[1] = 9").ParseExpression()));
        Assert.Equal(7d, ((Dictionary<string, object?>)bindings["obj"]!)["value"]);
        Assert.Equal(9d, ((object?[])bindings["items"]!)[1]);
    }

    [Fact]
    public void CallsFunctionExpressionWithCapturedBinding()
    {
        var bindings = new Dictionary<string, object?> { ["offset"] = 4d };
        var evaluator = new JavaScriptEvaluator(bindings);
        var function = evaluator.Evaluate(new JavaScriptParser("function(value) { return value + offset; }").ParseExpression());
        var call = (Func<IReadOnlyList<object?>, object?>)function!;
        Assert.Equal(9d, call([5d]));
    }

    [Fact]
    public void EvaluatesWithScopesAndCapturesThemInFunctions()
    {
        var scope = new Dictionary<string, object?> { ["value"] = 4d };
        var bindings = new Dictionary<string, object?> { ["scope"] = scope };
        var result = new JavaScriptEvaluator(bindings, preserveUndefined: true)
            .Execute(new JavaScriptParser("with (scope) { function read() { return value; } value = value + 1; } return read();").ParseProgram());
        Assert.Equal(5d, result);
    }

    [Fact]
    public void ClosureAssignmentUpdatesTheCapturedBinding()
    {
        var bindings = new Dictionary<string, object?> { ["count"] = 1d };
        var evaluator = new JavaScriptEvaluator(bindings);
        var function = evaluator.Evaluate(new JavaScriptParser("function() { count = count + 1; return count; }").ParseExpression());
        var call = (Func<IReadOnlyList<object?>, object?>)function!;
        Assert.Equal(2d, call([]));
        Assert.Equal(2d, bindings["count"]);
    }

    [Fact]
    public void NestedFunctionsResolveTransitiveCapturedBindings()
    {
        var bindings = new Dictionary<string, object?> { ["count"] = 1d };
        var evaluator = new JavaScriptEvaluator(bindings);
        var outer = Assert.IsType<Func<IReadOnlyList<object?>, object?>>(evaluator.Evaluate(new JavaScriptParser("function() { let inner = function() { count = count + 1; return count; }; return inner; }").ParseExpression()));
        var inner = Assert.IsType<Func<IReadOnlyList<object?>, object?>>(outer([]));

        Assert.Equal(2d, inner([]));
        Assert.Equal(2d, bindings["count"]);
    }

    [Fact]
    public void EvaluatesLogicalShortCircuitAndComparisons()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("1 < 2 && 3 >= 3").ParseExpression()));
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("1 == 2 || 3 != 4").ParseExpression()));
    }

    [Fact]
    public void EvaluatesConditionalExpressionsWithShortCircuiting()
    {
        var bindings = new Dictionary<string, object?>
        {
            ["left"] = (Func<IReadOnlyList<object?>, object?>)(_ => throw new InvalidOperationException()),
        };
        var evaluator = new JavaScriptEvaluator(bindings);
        Assert.Equal("yes", evaluator.Evaluate(new JavaScriptParser("true ? 'yes' : left()").ParseExpression()));
        Assert.Equal("no", evaluator.Evaluate(new JavaScriptParser("false ? left() : 'no'").ParseExpression()));
    }

    [Fact]
    public void EvaluatesTypeofAndVoidOperators()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal("undefined", evaluator.Evaluate(new JavaScriptParser("typeof missing").ParseExpression()));
        Assert.Equal("number", evaluator.Evaluate(new JavaScriptParser("typeof 1").ParseExpression()));
        Assert.Null(evaluator.Evaluate(new JavaScriptParser("void 7").ParseExpression()));
    }

    [Fact]
    public void EvaluatesBitwiseOperatorsAsSignedInt32Values()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(1d, evaluator.Evaluate(new JavaScriptParser("5 & 3").ParseExpression()));
        Assert.Equal(6d, evaluator.Evaluate(new JavaScriptParser("5 ^ 3").ParseExpression()));
        Assert.Equal(7d, evaluator.Evaluate(new JavaScriptParser("5 | 2").ParseExpression()));
        Assert.Equal(8d, evaluator.Evaluate(new JavaScriptParser("1 << 3").ParseExpression()));
        Assert.Equal(2d, evaluator.Evaluate(new JavaScriptParser("8 >>> 2").ParseExpression()));
        Assert.Equal(-6d, evaluator.Evaluate(new JavaScriptParser("~5").ParseExpression()));
    }

    [Fact]
    public void EvaluatesRightAssociativeExponentiation()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(512d, evaluator.Evaluate(new JavaScriptParser("2 ** 3 ** 2").ParseExpression()));
    }

    [Fact]
    public void DeletesObjectPropertiesAndArrayElements()
    {
        var bindings = new Dictionary<string, object?>
        {
            ["obj"] = new Dictionary<string, object?> { ["value"] = 1d },
            ["items"] = new object?[] { 1d, 2d },
        };
        var evaluator = new JavaScriptEvaluator(bindings);
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("delete obj.value").ParseExpression()));
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("delete items[0]").ParseExpression()));
        Assert.False(((Dictionary<string, object?>)bindings["obj"]!).ContainsKey("value"));
        Assert.Null(((object?[])bindings["items"]!)[0]);
    }

    [Fact]
    public void EvaluatesInAgainstBoundedObjectsAndArrays()
    {
        var bindings = new Dictionary<string, object?>
        {
            ["obj"] = new Dictionary<string, object?> { ["ready"] = true },
            ["items"] = new object?[] { 1d },
        };
        var evaluator = new JavaScriptEvaluator(bindings);
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("'ready' in obj").ParseExpression()));
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("0 in items").ParseExpression()));
        Assert.Equal(false, evaluator.Evaluate(new JavaScriptParser("'missing' in obj").ParseExpression()));
    }

    [Fact]
    public void EvaluatesBoundedInstanceofChecks()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("[] instanceof Array").ParseExpression()));
        Assert.Equal(true, evaluator.Evaluate(new JavaScriptParser("{} instanceof Object").ParseExpression()));
        Assert.Equal(false, evaluator.Evaluate(new JavaScriptParser("1 instanceof Array").ParseExpression()));
    }

    [Fact]
    public void ReadsArrayAndStringLength()
    {
        var evaluator = new JavaScriptEvaluator();
        Assert.Equal(3d, evaluator.Evaluate(new JavaScriptParser("[1, 2, 3].length").ParseExpression()));
        Assert.Equal(5d, evaluator.Evaluate(new JavaScriptParser("'Folio'.length").ParseExpression()));
    }
}

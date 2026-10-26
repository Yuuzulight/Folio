using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeJavaScriptProgramTests
{
    [Fact]
    public void ExecutesBindingsBranchesBlocksAndReturn()
    {
        var program = new JavaScriptParser("let value = 2; if (value < 3) { value = value + 5; } return value;").ParseProgram();
        var bindings = new Dictionary<string, object?>();
        var result = new JavaScriptEvaluator(bindings).Execute(program);
        Assert.Equal(7d, result);
        Assert.Equal(7d, bindings["value"]);
    }

    [Fact]
    public void ExecutesAllStatementsUntilReturnAndHonorsStepLimit()
    {
        var evaluator = new JavaScriptEvaluator(maxSteps: 3);
        Assert.Throws<InvalidOperationException>(() => evaluator.Execute(new JavaScriptParser("let a=1; a=a+1; a=a+1;").ParseProgram()));
    }

    [Fact]
    public void ExecutesBoundedWhileLoops()
    {
        var program = new JavaScriptParser("let value = 0; while (value < 3) { value = value + 1; } return value;").ParseProgram();
        var result = new JavaScriptEvaluator().Execute(program);
        Assert.Equal(3d, result);
    }

    [Fact]
    public void StopsRunawayWhileLoopsAtTheEvaluationBudget()
    {
        var program = new JavaScriptParser("while (true) { }").ParseProgram();
        Assert.Throws<InvalidOperationException>(() => new JavaScriptEvaluator(maxSteps: 32).Execute(program));
    }

    [Fact]
    public void HandlesBreakAndContinueInsideWhileLoops()
    {
        var program = new JavaScriptParser("let value = 0; while (value < 10) { value = value + 1; if (value < 3) continue; if (value == 5) break; } return value;").ParseProgram();
        Assert.Equal(5d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ExecutesForLoopsAndRunsUpdateAfterContinue()
    {
        var program = new JavaScriptParser("let total = 0; for (let i = 0; i < 5; i++) { if (i == 2) continue; total = total + i; } return total;").ParseProgram();
        Assert.Equal(8d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void SupportsPrefixAndPostfixUpdates()
    {
        var program = new JavaScriptParser("let value = 1; let old = value++; let next = ++value; return old + next + value;").ParseProgram();
        Assert.Equal(7d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ExecutesCompoundAssignments()
    {
        var program = new JavaScriptParser("let value = 2; value += 3; value *= 4; value &= 7; return value;").ParseProgram();
        Assert.Equal(4d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ShortCircuitsLogicalCompoundAssignments()
    {
        var program = new JavaScriptParser("let value = 0; value ||= 4; value &&= 3; value ??= 9; return value;").ParseProgram();
        Assert.Equal(3d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ExecutesThrowCatchAndFinally()
    {
        var program = new JavaScriptParser("let result = 0; try { throw 7; } catch (error) { result = error + 1; } finally { result = result + 2; } return result;").ParseProgram();
        Assert.Equal(10d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ExecutesDoWhileBodyBeforeCheckingCondition()
    {
        var program = new JavaScriptParser("let value = 0; do { value = value + 1; } while (false); return value;").ParseProgram();
        Assert.Equal(1d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ExecutesSwitchCasesWithFallthroughAndBreak()
    {
        var program = new JavaScriptParser("let value = 2; let result = 0; switch (value) { case 1: result = 1; break; case 2: result = 2; case 3: result = result + 3; break; default: result = 9; } return result;").ParseProgram();
        Assert.Equal(5d, new JavaScriptEvaluator().Execute(program));
    }

    [Fact]
    public void ExecutesForOfAndForInIterations()
    {
        var program = new JavaScriptParser("let total = 0; for (let item of [1, 2, 3]) { total += item; } let keys = 0; for (let key in {a: 1, b: 2}) { keys += 1; } return total + keys;").ParseProgram();
        Assert.Equal(8d, new JavaScriptEvaluator().Execute(program));
    }
}

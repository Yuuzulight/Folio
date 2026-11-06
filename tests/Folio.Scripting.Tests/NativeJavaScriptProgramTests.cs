using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeJavaScriptProgramTests
{
    [Fact]
    public void VarForEachHeaderInsideABlockBelongsToTheEnclosingFunction()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("3:3", engine.Evaluate("var read;{for(var n of [1,2,3]){}read=function(){return n;};}n+':'+read();").AsString());
    }

    [Fact]
    public void CommaDeclarationsShareTheirEnclosingScopeAndLoopIteration()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("3:0,2,4:undefined", engine.Evaluate("const a=1,b=2;var callbacks=[];for(let i=0,j=0;i<3;i++,j++){callbacks.push(function(){return i+j;});}(a+b)+':'+callbacks.map(function(f){return f();}).join(',')+':'+typeof j;").AsString());
    }

    [Theory]
    [InlineData("let", "0,1,2:undefined")]
    [InlineData("var", "3,3,3:number")]
    public void ForLoopClosuresRetainIterationBindings(string declaration, string expected)
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(expected, engine.Evaluate($"var callbacks=[];for({declaration} i=0;i<3;i++){{callbacks.push(function(){{return i;}});}}callbacks.map(function(f){{return f();}}).join(',')+':'+typeof i;").AsString());
    }

    [Fact]
    public void BlocksKeepLexicalBindingsPrivateAndVarBindingsInTheirFunction()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("outer:inner:7:undefined", engine.Evaluate("let name='outer';var read;{let name='inner';const hidden=2;var shared=7;read=function(){return name;};}name+':'+read()+':'+shared+':'+typeof hidden;").AsString());
    }

    [Fact]
    public void LexicalForHonorsContinueAndAggregatesTheEvaluationBudget()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(4d, engine.Evaluate("var total=0;for(let i=0;i<4;i++){if(i===2)continue;total+=i;}total;").AsNumber());
        Assert.Throws<InvalidOperationException>(() => new JavaScriptEvaluator(maxSteps: 100)
            .Execute(new JavaScriptParser("for(let i=0;i<1000;i++){let copy=i;}").ParseProgram()));
    }

    [Theory]
    [InlineData("const", "1,2,3")]
    [InlineData("let", "1,2,3")]
    [InlineData("var", "3,3,3")]
    public void ForEachClosuresRespectLexicalAndVarBindings(string declaration, string expected)
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(expected, engine.Evaluate($"var callbacks=[];for({declaration} n of [1,2,3]){{callbacks.push(function(){{return n;}});}}callbacks.map(function(f){{return f();}}).join(',');").AsString());
    }

    [Fact]
    public void ForEachBodyHasFreshLexicalBindingsAndFunctionScopedVars()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("1,2,3:3", engine.Evaluate("var callbacks=[];for(var n of [1,2,3]){const copy=n;var last=n;callbacks.push(function(){return copy;});}callbacks.map(function(f){return f();}).join(',')+':'+last;").AsString());
    }

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

    // return, break and continue are completions on the evaluator, not exceptions: each reaches the statement it
    // belongs to through every kind of loop, switch, label, scope and finally, and no further.
    [Theory]
    [InlineData("function f(){for(var i=0;i<5;i++){for(var j=0;j<5;j++){if(i*j===6)return i+','+j;}}return 'none';}f()", "2,3")]
    [InlineData("var out=[];outer:for(var i=0;i<3;i++){for(var j=0;j<3;j++){if(j===1)continue outer;out.push(i+''+j);}}out.join(',')", "00,10,20")]
    [InlineData("var out=[];outer:for(var i=0;i<3;i++){for(var j=0;j<3;j++){if(i===1&&j===1)break outer;out.push(i+''+j);}}out.join(',')", "00,01,02,10")]
    [InlineData("var out=[];for(var i=0;i<4;i++){switch(i){case 1:out.push('one');break;case 2:continue;default:out.push(i);}out.push('/');}out.join('')", "0/one/3/")]
    [InlineData("var s='';var i=0;while(true){i++;if(i%2)continue;if(i>6)break;s+=i;}var j=0;do{j++;if(j===2)continue;s+='d'+j;}while(j<3);s", "246d1d3")]
    [InlineData("function f(){const fs=[];for(const x of [1,2,3]){fs.push(()=>x);if(x===2)return fs.map(g=>g()).join(',');}}f()", "1,2")]
    [InlineData("function f(){for(let i=0;i<9;i++){let k=i*2;if(k>5)return String(k);}}f()", "6")]
    [InlineData("var o={a:1,b:2,c:3},k='';for(var p in o){if(p==='c')break;k+=p;}k", "ab")]
    [InlineData("var r='start';blk:{r+='-in';break blk;r+='-never';}r", "start-in")]
    [InlineData("function f(){{let a=1;{let b=2;return String(a+b);}}}f()", "3")]
    [InlineData("var log=[];function f(){try{return 'try';}finally{log.push('fin');}}f()+':'+log.join(',')", "try:fin")]
    [InlineData("function f(){try{return 'try';}finally{return 'fin';}}f()", "fin")]
    [InlineData("function f(){try{throw new Error('x');}finally{return 'kept';}}f()", "kept")]
    [InlineData("var n=0;for(var i=0;i<3;i++){try{throw 1;}finally{n++;break;}}String(n)", "1")]
    [InlineData("var log=[];try{(function(){try{throw new Error('boom');}finally{log.push('fin');}})();}catch(e){log.push(e.message);}log.join(',')", "fin,boom")]
    [InlineData("var s='';for(var i=0;i<3;i++){try{if(i===1)continue;s+=i;}finally{s+='f';}}s", "0ff2f")]
    [InlineData("function f(){try{throw 2;}catch(e){return 'c'+e;}finally{}}f()", "c2")]
    [InlineData("function f(x){switch(x){case 1:case 2:return 'low';case 3:break;default:return 'other';}return 'three';}[f(1),f(2),f(3),f(9)].join(',')", "low,low,three,other")]
    [InlineData("function inner(){return 1;}function outer(){var t=0;for(var i=0;i<3;i++){t+=inner();}return t;}String(outer())", "3")]
    [InlineData("var s=[];[1,2,3].forEach(function(x){if(x===2)return;s.push(x);});s.join(',')", "1,3")]
    public void ControlFlowCompletesWhereItBelongs(string script, string expected)
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(expected, engine.Evaluate(script).AsString());
    }
}

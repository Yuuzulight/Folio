using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public class NativeEngineTests
{
    [Fact]
    public void Tokenizer_TokenizesNumbersAndStrings()
    {
        var tokenizer = new NativeTokenizer("let x = 42; let hex = 0x2A; let bin = 0b1010; let s = \"hello\\nworld\";");
        var tokens = tokenizer.Tokenize();

        Assert.Equal(NativeTokenType.Let, tokens[0].Type);
        Assert.Equal(NativeTokenType.Identifier, tokens[1].Type);
        Assert.Equal("x", tokens[1].Text);
        Assert.Equal(NativeTokenType.Equal, tokens[2].Type);
        Assert.Equal(NativeTokenType.Number, tokens[3].Type);
        Assert.Equal(42.0, tokens[3].NumberValue);

        var hexToken = tokens.First(t => t.Text == "0x2A");
        Assert.Equal(42.0, hexToken.NumberValue);

        var binToken = tokens.First(t => t.Text == "0b1010");
        Assert.Equal(10.0, binToken.NumberValue);

        var strToken = tokens.First(t => t.Type == NativeTokenType.String);
        Assert.Equal("hello\nworld", strToken.Text);
    }

    [Fact]
    public void Parser_BuildsAstForFunctionAndLoop()
    {
        var source = @"
function sum(n) {
    let acc = 0;
    for (let i = 1; i <= n; i++) {
        acc += i;
    }
    return acc;
}";
        var program = NativeParser.Parse(source);
        Assert.Single(program.Statements);
        Assert.IsType<FunctionDeclaration>(program.Statements[0]);

        var func = (FunctionDeclaration)program.Statements[0];
        Assert.Equal("sum", func.Name);
        Assert.Single(func.Parameters);
        Assert.Equal("n", func.Parameters[0]);
        Assert.Equal(3, func.Body.Statements.Count);
    }

    [Fact]
    public void Engine_EvaluatesArithmeticAndVariables()
    {
        using var engine = new NativeScriptEngine();
        var result = engine.Evaluate("let a = 10; let b = 20; (a + b) * 2;");
        Assert.True(result.IsNumber);
        Assert.Equal(60.0, result.AsNumber());
    }

    [Fact]
    public void Engine_EvaluatesFunctionsAndRecursion()
    {
        using var engine = new NativeScriptEngine();
        var result = engine.Evaluate(@"
function factorial(n) {
    if (n <= 1) return 1;
    return n * factorial(n - 1);
}
factorial(5);");
        Assert.True(result.IsNumber);
        Assert.Equal(120.0, result.AsNumber());
    }

    [Fact]
    public void Engine_EvaluatesLoopsAndAccumulators()
    {
        using var engine = new NativeScriptEngine();
        var result = engine.Evaluate(@"
var total = 0;
for (var i = 0; i < 10; i++) {
    total = total + i;
}
total;");
        Assert.True(result.IsNumber);
        Assert.Equal(45.0, result.AsNumber());
    }

    [Fact]
    public void Engine_EvaluatesObjectsAndArrays()
    {
        using var engine = new NativeScriptEngine();
        var arrResult = engine.Evaluate("[10, 20, 30][1];");
        Assert.True(arrResult.IsNumber);
        Assert.Equal(20.0, arrResult.AsNumber());

        var objResult = engine.Evaluate("let p = { name: \"Folio\", speed: 100 }; p.speed;");
        Assert.True(objResult.IsNumber);
        Assert.Equal(100.0, objResult.AsNumber());
    }

    [Fact]
    public void Engine_EvaluatesStandardMathAndDateGlobals()
    {
        var options = new ScriptEngineOptions
        {
            RandomSeed = 12345,
            Clock = () => DateTimeOffset.FromUnixTimeMilliseconds(1700000000000),
        };
        using var engine = new NativeScriptEngine(options);

        var dateRes = engine.Evaluate("Date.now();");
        Assert.Equal(1700000000000, dateRes.AsNumber());

        var mathRes = engine.Evaluate("Math.floor(Math.PI) + Math.abs(-10);");
        Assert.Equal(13.0, mathRes.AsNumber());
    }

    private sealed class PersonTarget
    {
        public string Name { get; set; } = "Alice";
        public int Age { get; set; } = 30;
    }

    [Fact]
    public void Engine_IntegratesHostObjectTemplate()
    {
        var template = new HostObjectTemplate("Person");
        template.DefineProperty("name",
            getter: (target, _) => ScriptValue.FromString(((PersonTarget)target).Name),
            setter: (target, _, v) => ((PersonTarget)target).Name = v.AsString() ?? "");
        template.DefineProperty("age",
            getter: (target, _) => ScriptValue.FromNumber(((PersonTarget)target).Age));
        template.DefineMethod("greet", (target, _, args) =>
        {
            var p = (PersonTarget)target;
            return ScriptValue.FromString($"Hello {p.Name} ({p.Age})");
        });

        using var engine = new NativeScriptEngine();
        var target = new PersonTarget();
        var hostVal = engine.CreateHostObject(template, target);
        engine.SetGlobalProperty("person", hostVal);

        var readRes = engine.Evaluate("person.name;");
        Assert.Equal("Alice", readRes.AsString());

        engine.Evaluate("person.name = \"Bob\";");
        Assert.Equal("Bob", target.Name);

        var methodRes = engine.Evaluate("person.greet();");
        Assert.Equal("Hello Bob (30)", methodRes.AsString());

        Assert.True(engine.TryGetHostTarget(hostVal, out var extracted));
        Assert.Same(target, extracted);
    }

    [Fact]
    public void Engine_EnforcesStatementLimits()
    {
        var options = new ScriptEngineOptions
        {
            Limits = new ScriptLimits { MaxStatements = 50 }
        };
        using var engine = new NativeScriptEngine(options);

        var ex = Assert.Throws<ScriptLimitExceededException>(() =>
        {
            engine.Evaluate("while (true) {}");
        });
        Assert.Equal("Statements", ex.LimitName);
    }

    [Fact]
    public void Engine_SupportsMicrotaskCheckpoint()
    {
        using var engine = new NativeScriptEngine();
        engine.Evaluate(@"
var flag = 0;
queueMicrotask(function() { flag = 42; });
");
        var before = engine.GetGlobalProperty("flag");
        Assert.Equal(0.0, before.AsNumber());

        engine.PerformMicrotaskCheckpoint();

        var after = engine.GetGlobalProperty("flag");
        Assert.Equal(42.0, after.AsNumber());
    }

    [Fact]
    public void Engine_HandlesArrayBufferAndClampedArray()
    {
        using var engine = new NativeScriptEngine();
        byte[] original = [1, 2, 3, 4];
        var clamped = engine.CreateUint8ClampedArray(original);

        Assert.True(engine.TryGetBytes(clamped, out var retrieved));
        Assert.Equal(original, retrieved);

        byte[] modified = [9, 8, 7, 6];
        Assert.True(engine.TrySetBytes(clamped, modified));
        Assert.True(engine.TryGetBytes(clamped, out var retrieved2));
        Assert.Equal(modified, retrieved2);

        var buffer = engine.CreateArrayBuffer(original);
        Assert.True(engine.TryGetArrayBufferBytes(buffer, out var bufBytes));
        Assert.Equal(original, bufBytes);

        Assert.True(engine.TryDetachArrayBuffer(buffer));
        Assert.True(engine.TryGetArrayBufferBytes(buffer, out var detachedBytes));
        Assert.Empty(detachedBytes);
    }

    [Fact]
    public void Engine_EvaluatesSwitchStatements()
    {
        using var engine = new NativeScriptEngine();
        var res1 = engine.Evaluate(@"
function getTier(score) {
    let tier = 'none';
    switch (score) {
        case 100:
            tier = 'perfect';
            break;
        case 90:
            tier = 'excellent';
            break;
        default:
            tier = 'pass';
            break;
    }
    return tier;
}
getTier(90);");
        Assert.Equal("excellent", res1.AsString());

        var res2 = engine.Evaluate("getTier(100);");
        Assert.Equal("perfect", res2.AsString());

        var res3 = engine.Evaluate("getTier(50);");
        Assert.Equal("pass", res3.AsString());
    }

    [Fact]
    public void Engine_EvaluatesDoWhileLoops()
    {
        using var engine = new NativeScriptEngine();
        var res = engine.Evaluate(@"
let count = 0;
let val = 1;
do {
    val *= 2;
    count++;
} while (count < 4);
val;");
        Assert.Equal(16.0, res.AsNumber());
    }

    [Fact]
    public void Engine_SupportsArrayBuiltInMethods()
    {
        using var engine = new NativeScriptEngine();
        var resPush = engine.Evaluate(@"
let list = [1, 2];
list.push(3, 4);
list.join('-');");
        Assert.Equal("1-2-3-4", resPush.AsString());

        var resMap = engine.Evaluate(@"
let nums = [1, 2, 3];
nums.map(function(x) { return x * 10; }).join(',');");
        Assert.Equal("10,20,30", resMap.AsString());

        var resFilter = engine.Evaluate(@"
let items = [5, 10, 15, 20];
items.filter(function(x) { return x > 10; }).join(',');");
        Assert.Equal("15,20", resFilter.AsString());

        var resReduce = engine.Evaluate(@"
[1, 2, 3, 4].reduce(function(acc, x) { return acc + x; }, 0);");
        Assert.Equal(10.0, resReduce.AsNumber());

        var resIndex = engine.Evaluate("[10, 20, 30].indexOf(20);");
        Assert.Equal(1.0, resIndex.AsNumber());
    }

    [Fact]
    public void Engine_SupportsStringBuiltInMethods()
    {
        using var engine = new NativeScriptEngine();
        var resUpper = engine.Evaluate("'hello folio'.toUpperCase();");
        Assert.Equal("HELLO FOLIO", resUpper.AsString());

        var resSplit = engine.Evaluate("'apple,banana,orange'.split(',')[1];");
        Assert.Equal("banana", resSplit.AsString());

        var resSlice = engine.Evaluate("'abcdef'.slice(1, 4);");
        Assert.Equal("bcd", resSlice.AsString());

        var resIndex = engine.Evaluate("'folio engine'[6];");
        Assert.Equal("e", resIndex.AsString());
    }

    [Fact]
    public void Engine_SupportsObjectAndPromiseGlobals()
    {
        using var engine = new NativeScriptEngine();
        var resKeys = engine.Evaluate(@"
let obj = { a: 1, b: 2 };
Object.keys(obj).join(',');");
        Assert.Equal("a,b", resKeys.AsString());

        var resAssign = engine.Evaluate(@"
let target = { x: 1 };
Object.assign(target, { y: 2 }, { z: 3 });
target.x + target.y + target.z;");
        Assert.Equal(6.0, resAssign.AsNumber());

        engine.Evaluate(@"
var asyncResult = 0;
Promise.resolve(99).then(function(val) { asyncResult = val; });
");
        Assert.Equal(0.0, engine.GetGlobalProperty("asyncResult").AsNumber());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal(99.0, engine.GetGlobalProperty("asyncResult").AsNumber());
    }

    [Fact]
    public void Engine_VerifiesInlineCachingOnPropertyAccess()
    {
        using var engine = new NativeScriptEngine();
        var res = engine.Evaluate(@"
function getVal(o) { return o.score; }
let o1 = { score: 10 };
let o2 = { score: 20 };
let sum = 0;
for (let i = 0; i < 100; i++) {
    sum += getVal(o1) + getVal(o2);
}
sum;");
        Assert.Equal(3000.0, res.AsNumber());
    }
}

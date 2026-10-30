using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

/// <summary>Small differential gate for the Folio-owned evaluator while it remains in replacement development.</summary>
public sealed class NativeDifferentialParityTests
{
    public static IEnumerable<object[]> CorePrograms()
    {
        yield return ["arithmetic", "1 + 2 * 3", "7"];
        yield return ["arrays", "[1, 2, 3].map(x => x * 2).join(',')", "2,4,6"];
        yield return ["objects", "const x={a:1,b:2}; Object.keys(x).sort().join('') + ':' + (x.a+x.b)", "ab:3"];
        yield return ["regex", "'a1b2'.replace(/\\d/g, '#')", "a#b#"];
        yield return ["map-set", "const m=new Map([['a',1],['b',2]]); const s=new Set([1,2,2]); m.get('b')+':'+s.size", "2:2"];
        yield return ["classes", "class Box { constructor(v){this.v=v;} value(){return this.v*2;} } new Box(4).value()", "8"];
        yield return ["destructuring", "const [a,...rest]=[1,2,3]; a+rest.join('')", "123"];
        yield return ["object-rest", "const {a,...rest}={a:1,b:2}; a+':'+Object.keys(rest).join('')", "1:b"];
        yield return ["function-rest", "((a,...rest)=>a+rest.join(''))(1,2,3)", "123"];
        yield return ["nested-destructuring", "const [[a],{b}]=[[1],{b:2}]; a+b", "3"];
        yield return ["optional-chain", "const x={nested:{value:4}}; (x.missing?.value ?? x.nested.value)", "4"];
    }

    [Theory]
    [MemberData(nameof(CorePrograms))]
    public void NativeEvaluatorMatchesJintForCorePrograms(string name, string source, string expected)
    {
        using var jint = new JintScriptEngine();
        using var native = new NativeJavaScriptEngine();

        Assert.Equal(expected, jint.Evaluate(source).ToString());
        var nativeResult = native.Evaluate(source).ToString();
        Assert.True(expected == nativeResult, $"{name}: expected '{expected}', got '{nativeResult}'");
    }
}

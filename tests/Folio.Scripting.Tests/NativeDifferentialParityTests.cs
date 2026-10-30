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
        yield return ["array-spread", "[...[1,2],3].join(',')", "1,2,3"];
        yield return ["object-spread", "const x={a:1,...{b:2}}; x.a+x.b", "3"];
        yield return ["typed-array", "const x=new Uint8Array([1,255,256]); Array.from(x).join(',')", "1,255,0"];
        yield return ["clamped-typed-array", "Array.from(new Uint8ClampedArray([1.5,1.6,-1,300])).join(',')", "2,2,0,255"];
        yield return ["signed-typed-array", "Array.from(new Int8Array([128,-129])).join(',')", "-128,127"];
        yield return ["wide-typed-array", "Array.from(new Uint16Array([65536,-1])).join(',')", "0,65535"];
        yield return ["float-typed-array", "Array.from(new Float32Array([1.5, -2.25])).join(',')", "1.5,-2.25"];
        yield return ["bigint-typed-array", "Array.from(new BigInt64Array([1n,-2n])).map(String).join(',')", "1,-2"];
        yield return ["bigint-type", "typeof BigInt(2) + ':' + typeof 2n + ':' + String(2n)", "bigint:bigint:2"];
        yield return ["date-json", "JSON.stringify({d:new Date('2020-01-02T03:04:05Z').toISOString()})", "{\"d\":\"2020-01-02T03:04:05.000Z\"}"];
        yield return ["promise", "var result='pending'; Promise.resolve(4).then(v => result=String(v+1)); result", "pending"];
        yield return ["bitwise", "((0xff & 15) << 1) | 1", "31"];
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

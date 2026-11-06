using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeNumberDifferentialTests
{
    [Theory]
    [InlineData("1/0")]
    [InlineData("-1/0")]
    [InlineData("1/-0")]
    [InlineData("0/0")]
    [InlineData("1/'0'")]
    [InlineData("var value=1;value/=0;value")]
    [InlineData("var value=-1;value/=0;value")]
    [InlineData("var value=1;value/=-0;value")]
    [InlineData("var value=0;value/=0;value")]
    public void DivisionPreservesIeeeZeroInfinityAndNaNSemantics(string script)
    {
        using var native = new NativeJavaScriptEngine();
        using var oracle = new JintScriptEngine();
        var expected = oracle.Evaluate(script).AsNumber();
        var actual = native.Evaluate(script).AsNumber();
        if (double.IsNaN(expected)) Assert.True(double.IsNaN(actual));
        else Assert.Equal(BitConverter.DoubleToInt64Bits(expected), BitConverter.DoubleToInt64Bits(actual));
    }
}

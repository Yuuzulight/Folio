using Folio.Gen;
using Folio.Scripting.Bindings;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public class WebIdlBindingGeneratorTests
{
    private sealed class MockTestElement : ITestElementTarget
    {
        public string NodeName { get; set; } = "DIV";
        public string? TextContent { get; set; }
        public bool Active { get; set; }
        public double Score { get; set; }
        public int Priority { get; set; }
        public byte Level { get; set; }
        public string UnicodeText { get; set; } = "";

        public string Greet(string name, string title) => $"Hello {title} {name}";
        public int Compute(int x) => x * 2;
        public int Compute(int x, int y) => x + y;
        public void Reset()
        {
            Active = false;
            Score = 0;
            Priority = 0;
            Level = 0;
            UnicodeText = "";
        }
    }

    [Fact]
    public void PrototypeChainAndConstantBinding()
    {
        using var engine = new JintScriptEngine();

        var nodeProto = TestNodeBinding.CreateTemplate();
        var elTemplate = TestElementBinding.CreateTemplate(nodeProto);

        var mock = new MockTestElement();
        var elVal = engine.CreateHostObject(elTemplate, mock);
        engine.SetGlobalProperty("el", elVal);

        // Constant from TestNode
        Assert.Equal(1, engine.Evaluate("el.ELEMENT_NODE").AsNumber());
        // Read property from base interface
        Assert.Equal("DIV", engine.Evaluate("el.nodeName").AsString());
    }

    [Fact]
    public void TypeConversionsAndAttributes()
    {
        using var engine = new JintScriptEngine();

        var nodeProto = TestNodeBinding.CreateTemplate();
        var elTemplate = TestElementBinding.CreateTemplate(nodeProto);

        var mock = new MockTestElement();
        var elVal = engine.CreateHostObject(elTemplate, mock);
        engine.SetGlobalProperty("el", elVal);

        // Boolean conversion
        engine.Evaluate("el.active = 1;");
        Assert.True(mock.Active);
        engine.Evaluate("el.active = '';");
        Assert.False(mock.Active);

        // Double conversion
        engine.Evaluate("el.score = 42.5;");
        Assert.Equal(42.5, mock.Score);

        // [EnforceRange] throws on out-of-range or NaN
        engine.Evaluate("el.priority = 100;");
        Assert.Equal(100, mock.Priority);
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("el.priority = NaN;"));
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("el.priority = 1e15;"));

        // [Clamp] clamps without throwing
        engine.Evaluate("el.level = 300;");
        Assert.Equal(255, mock.Level); // clamped to byte.MaxValue
        engine.Evaluate("el.level = -50;");
        Assert.Equal(0, mock.Level);

        // USVString replaces lone surrogate with U+FFFD
        engine.Evaluate("el.unicodeText = 'a\\uD800b';");
        Assert.Equal("a\uFFFDb", mock.UnicodeText);

        // Nullable vs non-nullable
        engine.Evaluate("el.textContent = null;");
        Assert.Null(mock.TextContent);
        engine.Evaluate("el.textContent = 'hello';");
        Assert.Equal("hello", mock.TextContent);
    }

    [Fact]
    public void MethodOperationsAndOverloadResolution()
    {
        using var engine = new JintScriptEngine();

        var nodeProto = TestNodeBinding.CreateTemplate();
        var elTemplate = TestElementBinding.CreateTemplate(nodeProto);

        var mock = new MockTestElement();
        var elVal = engine.CreateHostObject(elTemplate, mock);
        engine.SetGlobalProperty("el", elVal);

        // Optional parameter with default
        Assert.Equal("Hello Mx. Alice", engine.Evaluate("el.greet('Alice')").AsString());
        Assert.Equal("Hello Dr. Alice", engine.Evaluate("el.greet('Alice', 'Dr.')").AsString());

        // Overloads (compute with 1 vs 2 arguments)
        Assert.Equal(10, engine.Evaluate("el.compute(5)").AsNumber());
        Assert.Equal(15, engine.Evaluate("el.compute(5, 10)").AsNumber());

        // Void method
        mock.Active = true;
        engine.Evaluate("el.reset();");
        Assert.False(mock.Active);
    }

    [Fact]
    public void IllegalInvocationThrowsTypeError()
    {
        using var engine = new JintScriptEngine();

        var nodeProto = TestNodeBinding.CreateTemplate();
        var elTemplate = TestElementBinding.CreateTemplate(nodeProto);

        var mock = new MockTestElement();
        var elVal = engine.CreateHostObject(elTemplate, mock);
        engine.SetGlobalProperty("el", elVal);

        // Calling method with wrong this (e.g. empty object)
        Assert.Throws<ScriptRuntimeException>(() =>
        {
            engine.Evaluate("el.greet.call({}, 'Alice')");
        });
    }

    [Fact]
    public void WrapperCacheEnsuresIdentityAcrossCalls()
    {
        using var engine = new JintScriptEngine();
        var cache = new ScriptWrapperCache();

        var template = TestNodeBinding.CreateTemplate();
        var mock = new MockTestElement();

        var wrapper1 = cache.GetOrCreate(engine, template, mock);
        var wrapper2 = cache.GetOrCreate(engine, template, mock);

        Assert.Equal(wrapper1, wrapper2);

        engine.SetGlobalProperty("w1", wrapper1);
        engine.SetGlobalProperty("w2", wrapper2);
        Assert.True(engine.Evaluate("w1 === w2").AsBoolean());
    }

    [Fact]
    public void DomExceptionPropertiesAndCodes()
    {
        var ex = WebIdlException.DomException(DomExceptionNames.HierarchyRequestError, "Node insertion violates hierarchy.");
        Assert.Equal("HierarchyRequestError", ex.Name);
        Assert.Equal(3, ex.LegacyCode);
        Assert.Equal(WebIdlExceptionKind.DOMException, ex.Kind);
    }

    [Fact]
    public void ParserFailsOnUnrecognizedConstruct()
    {
        var invalidIdl = "invalid_keyword Foo { };";
        Assert.Throws<FormatException>(() => IdlParser.Parse(invalidIdl));
    }
}

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

    private sealed class MockConversionProbe : IConversionProbeTarget
    {
        public MockConversionProbe()
        {
            First = new MockConversionProbe(initializeChildren: false);
            Second = new MockConversionProbe(initializeChildren: false);
        }

        private MockConversionProbe(bool initializeChildren)
        {
            if (initializeChildren)
            {
                First = new MockConversionProbe(initializeChildren: false);
                Second = new MockConversionProbe(initializeChildren: false);
            }
        }

        public int ChangingReads { get; private set; }
        public int? Changing { get { ChangingReads++; return 11; } }
        public IConversionProbeTarget First { get; } = null!;
        public IConversionProbeTarget Second { get; } = null!;
        public IConversionProbeTarget? EchoResult { get; set; }
        public IConversionProbeTarget Echo(IConversionProbeTarget value) => EchoResult ?? value;
        public int ConsumeOptions(ConversionOptions options) => options.RequiredValue + options.Label.Length;
        public ConversionOptions EchoOptions(ConversionOptions options) => options;
        public bool ConsumeBoolean(bool value) => value;
        public IReadOnlyList<float> ConsumeFloats(IReadOnlyList<float> values) => values;
        public ScriptBufferSource ConsumeBuffer(ScriptBufferSource value) => value;
        public ScriptBufferSource ConsumeFloat32(ScriptBufferSource value) => value;
        public int Upload(uint target, int size, uint usage) => 1000 + size;
        public int Upload(uint target, ScriptBufferSource? data, uint usage) => data is null ? 2000 : 2000 + data.Value.Info.ByteLength;
        public int RequiredOptional(int requiredValue, int optionalValue) => requiredValue + optionalValue;
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

    [Fact]
    public void ParserPreservesTypedefsAndWebIdlBufferTypes()
    {
        var doc = IdlParser.Parse("typedef unsigned long Handle; interface BufferUser { attribute BufferSource data; Handle getHandle(); };");
        Assert.Single(doc.Typedefs!);
        Assert.Equal("Handle", doc.Typedefs![0].Name);
        Assert.Equal(IdlTypeKind.BufferSource, doc.Interfaces[0].Attributes[0].Type.Kind);
        Assert.Equal(IdlTypeKind.Interface, doc.Interfaces[0].Operations[0].ReturnType.Kind);
    }

    [Fact]
    public void WebIdlNumericConversionsRespectFloatRules()
    {
        Assert.Equal(1.5f, WebIdlConversions.ToFloat(ScriptValue.FromNumber(1.5)));
        Assert.Throws<WebIdlException>(() => WebIdlConversions.ToFloat(ScriptValue.FromNumber(double.PositiveInfinity)));
        Assert.Equal(sbyte.MaxValue, WebIdlConversions.ToByte(ScriptValue.FromNumber(300), clamp: true));
        Assert.Equal(-1, WebIdlConversions.ToByte(ScriptValue.FromNumber(255)));
        Assert.True(float.IsInfinity(WebIdlConversions.ToFloat(ScriptValue.FromNumber(1e100), unrestricted: true)));
        Assert.Throws<WebIdlException>(() => WebIdlConversions.ToUint64(ScriptValue.FromNumber(-1), enforceRange: true));
        Assert.Equal(ulong.MaxValue, WebIdlConversions.ToUint64(ScriptValue.FromNumber(-1)));
    }

    [Fact]
    public void GeneratedConversionProbeUsesRuntimeConversionsAndIdentity()
    {
        using var engine = new JintScriptEngine();
        var target = new MockConversionProbe();
        var value = ConversionProbeBinding.CreateObject(engine, target);
        var repeated = ConversionProbeBinding.CreateObject(engine, target);
        Assert.Equal(value, repeated);
        engine.SetGlobalProperty("probe", value);
        Assert.Equal(11, engine.Evaluate("probe.changing").AsNumber());
        Assert.Equal(1, target.ChangingReads);
        Assert.Equal(17, engine.Evaluate("probe.consumeOptions({ requiredValue: 9 })").AsNumber());
        engine.Evaluate("Object.prototype.label = 99;");
        Assert.Equal(9, engine.Evaluate("probe.echoOptions({ requiredValue: 9, label: 'safe' }).requiredValue").AsNumber());
        Assert.Equal("safe", engine.Evaluate("probe.echoOptions({ requiredValue: 9, label: 'safe' }).label").AsString());
        Assert.Equal(2, engine.Evaluate("probe.consumeFloats([1, 3]).length").AsNumber());
        Assert.Equal(2, engine.Evaluate("probe.consumeFloats(new Set([1, 3])).length").AsNumber());
        engine.Evaluate("globalThis.closed = 0; globalThis.iterable = { [Symbol.iterator]() { let i = 0; return { next() { return i++ < 2 ? { value: i, done: false } : { done: true }; }, return() { globalThis.closed++; return { done: true }; } }; } }");
        Assert.Equal(2, engine.Evaluate("probe.consumeFloats(iterable).length").AsNumber());
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.consumeFloats({ [Symbol.iterator]() { return { next() { return { value: 'bad', done: false }; }, return() { globalThis.closed++; return { done: true }; } }; } })"));
        Assert.Equal(1, engine.Evaluate("closed").AsNumber());
        Assert.Equal(12, engine.Evaluate("probe.requiredOptional(5)").AsNumber());
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.requiredOptional()"));
        Assert.Equal(23, engine.Evaluate("probe.requiredOptional({ valueOf() { return '0x10'; } })").AsNumber());
        Assert.Equal(23, engine.Evaluate("probe.requiredOptional({ [Symbol.toPrimitive]() { return '0x10'; } })").AsNumber());
        Assert.Equal(23, engine.Evaluate("globalThis.reads = 0; const coercible = { get valueOf() { reads++; return () => '0x10'; } }; probe.requiredOptional(coercible)").AsNumber());
        Assert.Equal(1, engine.Evaluate("reads").AsNumber());
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.requiredOptional({ get [Symbol.toPrimitive]() { throw new Error('coercion'); } })"));
        Assert.Equal(7, engine.Evaluate("probe.requiredOptional('')").AsNumber());
        Assert.False(engine.Evaluate("probe.consumeBoolean(0n)").AsBoolean());
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.requiredOptional(Symbol('x'))"));
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.requiredOptional(1n)"));
        Assert.True(engine.Evaluate("probe.echo(probe) === probe").AsBoolean());
        Assert.True(engine.Evaluate("probe.first === probe.first").AsBoolean());
        Assert.True(engine.Evaluate("probe.second === probe.second").AsBoolean());
        Assert.True(engine.Evaluate("probe.first !== probe.second").AsBoolean());
        Assert.Equal(2, engine.Evaluate("probe.consumeFloat32(new Float32Array(2)).length").AsNumber());
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.consumeFloat32(new Int16Array(2))"));
        Assert.Equal(1012, engine.Evaluate("probe.upload('34962', 12, 35044, 'ignored')").AsNumber());
        Assert.Equal(2000, engine.Evaluate("probe.upload(34962, null, 35044)").AsNumber());
        Assert.Throws<ScriptRuntimeException>(() => engine.Evaluate("probe.echo({})"));
    }
}

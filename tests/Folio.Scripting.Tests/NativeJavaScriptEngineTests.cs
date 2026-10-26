using Folio.Scripting.Native;
using Folio.Scripting.Dom;
using Folio.Dom;
using Folio.Html;

namespace Folio.Scripting.Tests;

public sealed class NativeJavaScriptEngineTests
{
    [Fact]
    public void ExecutesThroughTheScriptEngineInterface()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(7d, engine.Evaluate("let value = 2; value += 5; return value;").AsNumber());
    }

    [Fact]
    public void EvaluatesParenthesizedSequenceExpressions()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(3d, engine.Evaluate("let value = 0; return (value = 1, value += 2, value);").AsNumber());
    }

    [Fact]
    public void EvaluatesSequenceExpressionsInForInIterables()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("b", engine.Evaluate("let seen = ''; for (let key in (seen = 'a', { b: 1 })) { return key; }").AsString());
    }

    [Fact]
    public void EvaluatesBareForInSequenceIterables()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("b", engine.Evaluate("let result = ''; for (result in (result = 'a', { b: 1 })) { return result; }").AsString());
    }

    [Fact]
    public void EvaluatesReactForInFragment()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("key", engine.Evaluate("function run(b){var m,d={},c=null,h=null;if(null!=b)for(m in void 0!==b.ref&&(h=b.ref),void 0!==b.key&&(c='' + b.key),b)if(m==='x')return 'key'; return 'none';} return run({ref:1,key:2,x:3});").AsString());
    }

    [Fact]
    public void ExposesArgumentsLengthToNativeFunctions()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(2d, engine.Evaluate("function count(a){ return arguments.length; } return count(1, 2);").AsNumber());
    }

    [Fact]
    public void ParsesReactDomErrorDecoderFunction()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("Minified React error #1", engine.Evaluate("function m(a){for(var b=\"https://reactjs.org/docs/error-decoder.html?invariant=\"+a,c=1;c<arguments.length;c++)b+=\"&args[]=\"+encodeURIComponent(arguments[c]);return\"Minified React error #\"+a;} return m(1, 2);").AsString());
    }

    [Fact]
    public void EvaluatesSequenceExpressionsAsStatements()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(2d, engine.Evaluate("let value = 0; value = 1, value = 2; return value;").AsNumber());
    }

    [Fact]
    public void AllowsMinifiedBareReturns()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(1d, engine.Evaluate("function done(value){if(value)return;return 1;} return done(false);").AsNumber());
    }

    [Fact]
    public void ProvidesSymbolForToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("Symbol.for:react.element", engine.Evaluate("return Symbol.for('react.element');").AsString());
    }

    [Fact]
    public void ProvidesObjectAssignToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("ok", engine.Evaluate("return Object.assign({}, { value: 'ok' }).value;").AsString());
    }

    [Fact]
    public void ProvidesStringFromCharCodeToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("AB", engine.Evaluate("return String.fromCharCode(65, 66);").AsString());
    }

    [Fact]
    public void ProvidesDictionaryHasOwnPropertyToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("return ({ value: 1 }).hasOwnProperty('value');").AsBoolean());
    }

    [Fact]
    public void ProvidesMathRandomToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("var value = Math.random(); return value >= 0 && value < 1;").AsBoolean());
    }

    [Fact]
    public void ProvidesNumberToStringToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("42", engine.Evaluate("return (42).toString();").AsString());
    }

    [Fact]
    public void PreservesNativeMapBindingsAcrossFunctions()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(2d, engine.Evaluate("var fh = new Map(); function put(){ fh.set(1, 2); } put(); return fh.get(1);").AsNumber());
    }

    [Fact]
    public void PreservesNativeMapBindingsInFactoryClosures()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(2d, engine.Evaluate("function factory(){var fh, value; fh=new Map(), value=1; function put(){fh.set(1, 2);} put(); return fh.get(1);} return factory();").AsNumber());
    }

    [Fact]
    public void PreservesMapFromMinifiedDeclaratorLists()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(2d, engine.Evaluate("function factory(){var jh=1,kh=2,mh=3,fh=new Map,Zi='x'; function put(){fh.set(1,2);} put(); return fh.get(1);} return factory();").AsNumber());
    }

    [Fact]
    public void ConstructsPrototypeObjectsWithoutParentheses()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("function Base(){} function Child(){} var value = Child.prototype = new Base; value.constructor = Child; return value.constructor === Child;").AsBoolean());
    }


    [Fact]
    public void RoutesHostBootstrapThroughTheNativeEvaluator()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.EvaluateHostScript("globalThis.bootstrapValue = 40 + 2;", "folio:test-bootstrap");
        Assert.Equal(42d, engine.GetGlobalProperty("bootstrapValue").AsNumber());
    }

    [Fact]
    public void ProvidesNativeDomExceptionConstructorAndCodes()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.InstallDomException();
        Assert.Equal(36d, engine.Evaluate("let error = new DOMException('denied', 'SecurityError'); return error.code + DOMException.SECURITY_ERR;").AsNumber());
    }

    [Fact]
    public void ProvidesIdentityWeakCollectionsAndSymbols()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("let key = {}; let map = new WeakMap(); map.set(key, 7); let set = new WeakSet(); set.add(key); Reflect.apply(WeakSet.prototype.add, set, [key]); return map.get(key) + (map.has(key) ? 1 : 0) + (set.has(key) ? 1 : 0);").AsNumber() == 9d);
        Assert.Equal("Symbol.iterator", engine.Evaluate("Symbol.iterator").AsString());
    }

    [Fact]
    public void ProvidesReceiverAwareMapAndSetPrototypes()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(15d, engine.Evaluate("let key = {}; let map = new Map(); Reflect.apply(Map.prototype.set, map, [key, 11]); let set = new Set(); Reflect.apply(Set.prototype.add, set, [key]); return map.get(key) + (set.has(key) ? 4 : 0);").AsNumber());
    }

    [Fact]
    public void NativeTypedArraysSupportConstructionAndMemberChains()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(2d, engine.Evaluate("const bytes = new Uint8Array([1, 2, 3]); return bytes.slice(1)[0];").AsNumber());
        Assert.Equal(3d, engine.Evaluate("const bytes = new Uint8Array([1, 2, 3]); return bytes.byteLength;").AsNumber());
        Assert.Equal(3d, engine.Evaluate("const bytes = new Uint8Array([1, 2, 3]); return bytes.buffer.byteLength;").AsNumber());
        Assert.Equal(15d, engine.Evaluate("const bytes = new Uint8Array(3); bytes.set([4, 5, 6]); return bytes[0] + bytes[1] + bytes[2];").AsNumber());
        Assert.True(engine.Evaluate("return new Uint8Array(1) instanceof Uint8Array;").AsBoolean());
    }

    [Fact]
    public void NativeArraysGrowWhenIndexedPastTheirCurrentLength()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("3:7:null", engine.Evaluate("const values = []; values[0] = 3; values[2] = 7; return values.length + ':' + values[2] + ':' + values[1];").AsString());
    }

    [Fact]
    public void ProvidesObjectDescriptorAndErrorIntrinsics()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("ok", engine.Evaluate("let base = Object.create(null); Object.defineProperty(base, 'value', { value: 'ok' }); return base.value;").AsString());
        Assert.Equal("TypeError", engine.Evaluate("let error = new TypeError('bad'); return error.name;").AsString());
    }

    [Fact]
    public void ExecutesNativeClassConstructorsMethodsAndGetters()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(12d, engine.Evaluate("class Sample { constructor(value) { this.value = value; } get answer() { return this.value * 2; } } let sample = new Sample(6); return sample.answer;").AsNumber());
    }

    [Fact]
    public void NativeClassGettersRemainLiveAfterConstruction()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(14d, engine.Evaluate("class Sample { get answer() { return this.value * 2; } } let sample = new Sample(); sample.value = 7; return sample.answer;").AsNumber());
    }

    [Fact]
    public void NativeSubclassInheritsParentGettersAndMethods()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("7:ok", engine.Evaluate("class Base { get value() { return this.number; } ping() { return 'ok'; } } class Child extends Base { } let child = new Child(); child.number = 7; return child.value + ':' + child.ping();").AsString());
    }

    [Fact]
    public void ExecutesBootstrapSyntaxAndFunctionProperties()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("42:true:yes", engine.Evaluate("const value = 'item-12'; const box = { get answer() { return 42; }, add(x) { return 42 + x; } }; const fn = x => x + 1; fn.extra = 'yes'; return [box.add(0), /item-/.test(value), fn.extra].join(':');").AsString());
    }

    [Fact]
    public void BindsThisForObjectMethods()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(12d, engine.Evaluate("const box = { value: 7, add(x) { return this.value + x; } }; return box.add(5);").AsNumber());
    }

    [Fact]
    public void ResolvesComputedHostMethods()
    {
        using var engine = new NativeJavaScriptEngine();
        var template = new HostObjectTemplate("ComputedHost")
            .DefineMethod("open", (_, _, _) => ScriptValue.FromNumber(7));
        engine.SetGlobalProperty("host", engine.CreateHostObject(template, new object()));
        Assert.Equal(7d, engine.Evaluate("const name = 'open'; return host[name]();").AsNumber());
    }

    [Fact]
    public void ProbesNativeDomEventBootstrap()
    {
        using var engine = new NativeJavaScriptEngine();
        using var loop = new HtmlEventLoop(engine);
        _ = DomEvents.Install(engine, loop);
    }

    [Fact]
    public void InstallsNativeDomBindingsForARealDocument()
    {
        using var engine = new NativeJavaScriptEngine();
        var document = TreeBuilder.Parse("<!doctype html><html><body><button id='go'>Go</button></body></html>");
        using var loop = new HtmlEventLoop(engine);
        _ = DomBindings.Install(engine, document, eventLoop: loop);
    }

    [Fact]
    public void ExecutesScopedWorkerBootstrapShape()
    {
        using var engine = new NativeJavaScriptEngine();
        var registrations = new List<object?>();
        var workerSelf = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["addEventListener"] = (Func<IReadOnlyList<object?>, object?>)(args => { registrations.Add(args[1]); return null; }),
        };
        engine.SetGlobalProperty("self", ScriptValue.FromObject(workerSelf));
        engine.EvaluateHostScript("(function(self) { with (self) { addEventListener('fetch', function(event) { return 7; }); } })(self);", "folio:worker-bootstrap");
        Assert.Single(registrations);
        var handler = Assert.IsType<Func<IReadOnlyList<object?>, object?>>(registrations[0]);
        Assert.Equal(7d, handler([new Dictionary<string, object?>()]));
    }

    [Fact]
    public void ExecutesDynamicFunctionBootstrapWrapper()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(42d, engine.Evaluate("Function('return 42;')()").AsNumber());
        Assert.Equal(42d, engine.Evaluate("Function('scope', 'return scope.value + 2;')({ value: 40 })").AsNumber());
        Assert.Equal(42d, engine.Evaluate("new Function('scope', 'with (scope) { return value + 2; }')({ value: 40 })").AsNumber());
        Assert.Equal(9d, engine.Evaluate("Function('first', 'return arguments[0] + arguments[1];')(4, 5)").AsNumber());
    }

    [Fact]
    public void PreservesGlobalsAndCreatesIndependentIsolatedEngines()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.SetGlobalProperty("answer", ScriptValue.FromNumber(4));
        Assert.Equal(9d, engine.Evaluate("answer + 5").AsNumber());
        using var isolated = Assert.IsAssignableFrom<IScriptEngineFactory>(engine).CreateIsolated();
        Assert.True(isolated.GetGlobalProperty("answer").IsUndefined);
    }

    [Fact]
    public void CarriesExecutionBudgetIntoIsolatedEngines()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(new ScriptLimits { MaxStatements = 8 });
        using var isolated = Assert.IsAssignableFrom<IScriptEngineFactory>(engine).CreateIsolated();
        Assert.Throws<InvalidOperationException>(() => isolated.Evaluate("let value = 0; while (value < 100) value++;"));
    }

    [Fact]
    public void EnforcesTimeoutOnNativeInfiniteLoops()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Configure(new ScriptLimits { Timeout = TimeSpan.FromMilliseconds(1), MaxStatements = null });
        var error = Assert.Throws<ScriptLimitExceededException>(() => engine.Evaluate("while (true) {}"));
        Assert.Equal("Timeout", error.LimitName);
    }

    [Fact]
    public void CallsNativeFunctionValues()
    {
        using var engine = new NativeJavaScriptEngine();
        var function = engine.Evaluate("function(value) { return value * 2; }");
        Assert.Equal(8d, engine.Call(function, ScriptValue.Undefined, [ScriptValue.FromNumber(4)]).AsNumber());
    }

    [Fact]
    public void RoutesHostPropertiesMethodsAndIndexedAccess()
    {
        using var engine = new NativeJavaScriptEngine();
        var state = new HostState { Value = 3 };
        var template = new HostObjectTemplate("TestHost")
            .DefineProperty("value", (target, _) => ScriptValue.FromNumber(((HostState)target).Value),
                (target, _, value) => ((HostState)target).Value = (int)value.AsNumber())
            .DefineMethod("add", (target, _, args) =>
            {
                var host = (HostState)target;
                host.Value += (int)args[0].AsNumber();
                return ScriptValue.FromNumber(host.Value);
            })
            .DefineIndexedProperty((target, _, index) => ScriptValue.FromNumber(((HostState)target).Items[index]));
        engine.SetGlobalProperty("host", engine.CreateHostObject(template, state));

        Assert.Equal(9d, engine.Evaluate("host.value = host.add(2); return host[1] + host.value;").AsNumber());
        Assert.Equal(5, state.Value);
    }

    [Fact]
    public void ImplementsBufferSourceOwnershipAndDetachment()
    {
        using var engine = new NativeJavaScriptEngine();
        var storage = new byte[] { 1, 2, 3, 4 };
        var buffer = engine.CreateArrayBufferOwned(storage);
        Assert.True(engine.TryGetArrayBufferBytesOwned(buffer, out var owned));
        Assert.Same(storage, owned);
        Assert.True(engine.TryWriteBufferSource(buffer, 1, [9, 8]));
        Assert.Equal(new byte[] { 1, 9, 8, 4 }, storage);
        Assert.True(engine.TryGetBufferSourceInfo(buffer, out var info));
        Assert.Equal((ScriptBufferSourceKind.ArrayBuffer, 4, 0, 1, 4), (info.Kind, info.ByteLength, info.ByteOffset, info.ElementSize, info.Length));
        Assert.True(engine.TryDetachArrayBuffer(buffer));
        Assert.False(engine.TryGetArrayBufferBytes(buffer, out _));
    }

    [Fact]
    public void PreservesNullAndUndefinedAtTheEngineBoundary()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("null").IsNull);
        Assert.True(engine.Evaluate("void 0").IsUndefined);
        Assert.True(engine.GetProperty(engine.CreatePlainObject(new Dictionary<string, ScriptValue>()), "missing").IsUndefined);
    }

    [Fact]
    public void ExposesTypedBufferPropertiesAndIndexedMutationToScripts()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.SetGlobalProperty("bytes", engine.CreateTypedArray(ScriptBufferSourceKind.Uint8, [1, 2, 3]));
        Assert.Equal(15d, engine.Evaluate("bytes[1] = 9; return bytes[1] + bytes.length + bytes.byteLength;").AsNumber());
        Assert.True(engine.TryGetBytes(engine.GetGlobalProperty("bytes"), out var bytes));
        Assert.Equal(new byte[] { 1, 9, 3 }, bytes);
    }

    [Fact]
    public void ExecutesFunctionDeclarationsAndCallsThem()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(12d, engine.Evaluate("function twice(value) { return value * 2; } return twice(6);").AsNumber());
    }

    [Fact]
    public void ExecutesArrowFunctionClosures()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(15d, engine.Evaluate("let offset = 3; let add = value => value + offset; return add(12);").AsNumber());
        Assert.Equal(9d, engine.Evaluate("let add = (left, right) => left + right; return add(4, 5);").AsNumber());
    }

    [Fact]
    public void ProvidesBoundedStandardBuiltins()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("Array.isArray([1, 2])").AsBoolean());
        Assert.Equal(6d, engine.Evaluate("Math.max(1, 6, 3)").AsNumber());
        Assert.Equal(2d, engine.Evaluate("Object.keys({ first: 1, second: 2 }).length").AsNumber());
    }

    [Fact]
    public void ProvidesCoercionParsingAndConsoleGlobals()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("42", engine.Evaluate("String(42)").AsString());
        Assert.Equal(7d, engine.Evaluate("Number('7')").AsNumber());
        Assert.True(engine.Evaluate("Boolean('yes')").AsBoolean());
        Assert.Equal(12d, engine.Evaluate("parseInt('12px')").AsNumber());
        Assert.True(engine.Evaluate("isNaN(Number('x'))").AsBoolean());
        Assert.True(engine.Evaluate("console.log('ok'); return true;").AsBoolean());
    }

    [Fact]
    public void ProvidesBoundedArrayAndStringMethods()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("a-b-c", engine.Evaluate("['a', 'b', 'c'].join('-')").AsString());
        Assert.Equal(3d, engine.Evaluate("let values = [1, 2]; values.push(3); return values.length").AsNumber());
        Assert.Equal("1,9", engine.Evaluate("let values = [1, 2, 3]; let removed = values.splice(1, 1, 9); values.pop(); values.unshift(0); values.shift(); return values.join(',')").AsString());
        Assert.Equal(3d, engine.Evaluate("let values = [4, 5]; let first = values.shift(); values.unshift(1, 2); return values.length").AsNumber());
        Assert.True(engine.Evaluate("[1, 2, 3].includes(2)").AsBoolean());
        Assert.Equal(2d, engine.Evaluate("[1, 2, 3].find(value => value > 1)").AsNumber());
        Assert.True(engine.Evaluate("[1, 2, 3].some(value => value == 3)").AsBoolean());
        Assert.True(engine.Evaluate("[1, 2, 3].every(value => value > 0)").AsBoolean());
        Assert.Equal(1d, engine.Evaluate("[4, 8, 9].indexOf(8)").AsNumber());
        Assert.Equal("BC", engine.Evaluate("'abc'.toUpperCase().slice(1)").AsString());
        Assert.True(engine.Evaluate("'folio'.startsWith('fol')").AsBoolean());
    }

    [Fact]
    public void DispatchesBoundedArrayCallbacks()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(12d, engine.Evaluate("[1, 2, 3].map(value => value * 2).reduce((total, value) => total + value, 0)").AsNumber());
        Assert.Equal(2d, engine.Evaluate("[1, 2, 3].filter(value => value > 1).length").AsNumber());
        Assert.Equal(3d, engine.Evaluate("Object.keys([7, 8, 9]).length").AsNumber());
    }

    [Fact]
    public void ProvidesJsonRoundTripGlobals()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("{\"value\":7}", engine.Evaluate("JSON.stringify({ value: 7 })").AsString());
        Assert.Equal(7d, engine.Evaluate("JSON.parse('{\"value\":7}').value").AsNumber());
    }

    [Fact]
    public void DrainsQueuedMicrotasksAtExplicitCheckpoints()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Evaluate("let done = false; queueMicrotask(() => done = true);");
        Assert.False(engine.Evaluate("done").AsBoolean());
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("done").AsBoolean());
    }

    [Fact]
    public void ResolvesAndChainsPromisesAtCheckpoints()
    {
        using var engine = new NativeJavaScriptEngine();
        engine.Evaluate("let result = 0; Promise.resolve(4).then(value => result = value + 1);");
        Assert.Equal(0d, engine.Evaluate("result").AsNumber());
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal(5d, engine.Evaluate("result").AsNumber());
    }

    [Fact]
    public void ParsesBoundedNewExpressionsThroughHostCalls()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("7", engine.Evaluate("new String(7)").AsString());
    }

    private sealed class HostState
    {
        public int Value { get; set; }
        public int[] Items { get; } = [3, 4];
    }
}

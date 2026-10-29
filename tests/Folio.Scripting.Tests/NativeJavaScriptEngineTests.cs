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
    public void ForAssignmentConditionStopsOnNull()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("0:null", engine.Evaluate("var source={ lastChild:null }; var value='main'; var count=0; for (; value=source.lastChild;) count++; return count + ':' + value;").AsString());
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
    public void EvaluatesDefaultFunctionAndArrowParameters()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(15d, engine.Evaluate("function add(a=4,b=a+1){return a+b;} const scale=(value=3)=>value*2; return add() + scale();").AsNumber());
    }

    [Fact]
    public void EvaluatesDestructuredArrowParameters()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(5d, engine.Evaluate("const sum=([a,b])=>a+b; return sum([2,3]);").AsNumber());
        Assert.Equal(7d, engine.Evaluate("const third=([,,value])=>value; return third([1,2,7]);").AsNumber());
    }

    [Fact]
    public void EvaluatesGeneratorYieldsAsAnIterableSnapshot()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("1|2", engine.Evaluate("function* values(){yield 1; yield 2;} return Array.from(values()).join('|');").AsString());
        Assert.Equal("1|2", engine.Evaluate("function* values(){yield*[1,2];} return Array.from(values()).join('|');").AsString());
    }

    [Fact]
    public void EvaluatesNestedTemplateLiteralInInterpolation()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("x1", engine.Evaluate("return `${true ? `x${1}` : 'y'}`;").AsString());
    }

    [Fact]
    public void HoistsFunctionDeclarationsWithinAFunctionBody()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(3d, engine.Evaluate("return run(); function run(){return 3;}").AsNumber());
    }

    [Fact]
    public void ExposesNativeMathFunctions()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(3d + Math.PI / 2, engine.Evaluate("return Math.sqrt(9) + Math.asin(1) + Math.acos(1);").AsNumber(), 10);
    }

    [Fact]
    public void SupportsCallAndApplyOnNativeFunctions()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("ab", engine.Evaluate("return String.fromCharCode.call(null,97,98);").AsString());
        Assert.Equal("2|4", engine.Evaluate("return Array.prototype.map.call([1,2], x => x * 2).join('|');").AsString());
        Assert.Equal("1|2|2|3", engine.Evaluate("return [1,2].flatMap(x => [x,x+1]).join('|');").AsString());
        Assert.Equal(20d, engine.Evaluate("return Math.max(...[{values:[1,2],scale:10},{values:[3],scale:1}].flatMap(s => s.values.map(v => v * (s.scale || 1))));").AsNumber());
        Assert.Equal("2|1|2", engine.Evaluate("class X extends Array {}; const x = new X(); x.push(1,2); return [x.length,x[0],x[1]].join('|');").AsString());
        Assert.Equal("Start|invalid", engine.Evaluate("return 'Start : invalid'.split(/\\s*:\\s*/).join('|');").AsString());
        Assert.Equal("1|2|3", engine.Evaluate("return [[1],[2,[3]]].flat(Infinity).join('|');").AsString());
        Assert.Equal("Symbol:x|Symbol.iterator", engine.Evaluate("return Symbol('x')+'|'+Symbol.iterator;").AsString());
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
        Assert.Equal("function|object", engine.Evaluate("return typeof Object + '|' + typeof new Object();").AsString());
        Assert.Equal("value|0|length", engine.Evaluate("const names = Object.getOwnPropertyNames({ value: 1 }); const arrayNames = Object.getOwnPropertyNames([1]); return names[0] + '|' + Object.getOwnPropertySymbols({}).length + '|' + arrayNames[1];").AsString());
    }

    [Fact]
    public void ProvidesObjectEntriesAndValuesForArrays()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("0:a|1:b", engine.Evaluate("return Object.entries(['a', 'b']).map(pair => pair[0] + ':' + pair[1]).join('|');").AsString());
        Assert.Equal("a|b", engine.Evaluate("return Object.values(['a', 'b']).join('|');").AsString());
    }

    [Fact]
    public void ImplementsArrayNegativeSliceAndEmptyJoinFields()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("b,c", engine.Evaluate("return ['a', 'b', 'c'].slice(-2).join(',');").AsString());
        Assert.Equal("a||c", engine.Evaluate("return ['a', null, 'c'].join('|');").AsString());
        Assert.Equal("b", engine.Evaluate("return Array.prototype.slice.call({0:'a', 1:'b', length:2}, -1).join('');").AsString());
    }

    [Fact]
    public void ImplementsArrayFromForStringsArrayLikesAndMapping()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("a|𝄞", engine.Evaluate("return Array.from('a𝄞').join('|');").AsString());
        Assert.Equal("x2|y4", engine.Evaluate("return Array.from({0:'x', 1:'y', length:2}, value => value + value === 'xx' ? 'x2' : 'y4').join('|');").AsString());
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
    public void ProvidesMathBuiltinsUsedByNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(0d, engine.Evaluate("return Math.log(1);").AsNumber());
        Assert.Equal(32d, engine.Evaluate("return Math.pow(2, 5);").AsNumber());
        Assert.Equal(26d, engine.Evaluate("return Math.clz32(1) - 5;").AsNumber());
        Assert.True(engine.Evaluate("return Number.isSafeInteger(9007199254740991);").AsBoolean());
        Assert.False(engine.Evaluate("return Number.isSafeInteger(9007199254740992);").AsBoolean());
    }

    [Fact]
    public void ProxyGetTrapReadsThroughTheNativeTarget()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("value", engine.Evaluate("var p = new Proxy({ value: 42 }, { get: function(target, name) { return name; } }); return p.value;").AsString());
        Assert.Equal(42d, engine.Evaluate("var p = new Proxy({ value: 42 }, { get: function(target, name) { return target.value; } }); return p.value;").AsNumber());
        Assert.Equal(43d, engine.Evaluate("var p = new Proxy({ value: 42 }, { get: function(target, name) { return target[name] + 1; } }); return p.value;").AsNumber());
    }

    [Fact]
    public void ProvidesNumberToStringToNativeBundles()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("42", engine.Evaluate("return (42).toString();").AsString());
        Assert.True(engine.Evaluate("return Date.now() > 0;").AsBoolean());
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
    public void PreservesBoundArgumentsAndReceiver()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("x:7:", engine.Evaluate("const box={ value: 7, read(prefix, suffix){ return prefix + this.value + suffix; } }; const bound=box.read.bind(box, 'x:'); return bound(':');").AsString());
        Assert.Equal(9d, engine.Evaluate("function add(a,b){return a+b;} const bound=add.bind(null, 4); return bound(5);").AsNumber());
        Assert.Equal("x:7", engine.Evaluate("const read = function(prefix){ return prefix + this.value; }; const bound = read.bind({ value: 7 }, 'x:'); return bound();").AsString());
    }

    [Fact]
    public void ConstructsPrototypeObjectsWithoutParentheses()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("function Base(){} function Child(){} var value = Child.prototype = new Base; value.constructor = Child; return value.constructor === Child;").AsBoolean());
    }

    [Fact]
    public void AppliesFunctionPrototypeMethodsToConstructedObjects()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("function", engine.Evaluate("function Box(value){this.value=value;} Box.prototype.read=function(){return this.value;}; return typeof Box.prototype.read;").AsString());
        Assert.Equal(2d, engine.Evaluate("function Box(value){this.value=value;} Box.prototype.read=function(){return this.value;}; return (new Box(2)).read();").AsNumber());
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
        Assert.True(engine.Evaluate("const bytes = new Uint8Array([1, 2, 3, 4]); const view = bytes.subarray(1, 3); view[0] = 9; return bytes[1] == 9 && view.byteOffset == 1 && view.byteLength == 2;").AsBoolean());
        Assert.Equal(4d, engine.Evaluate("const bytes = new Uint8Array([1, 2, 3, 4]); return bytes.subarray(-1)[0];").AsNumber());
        Assert.Equal(4660d, engine.Evaluate("const values = new Uint16Array(2); values[0] = 4660; return values[0];").AsNumber());
        Assert.Equal(1.5d, engine.Evaluate("const values = new Float32Array(1); values[0] = 1.5; return values[0];").AsNumber(), 5);
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
        Assert.Equal("ok|1|3", engine.Evaluate("const object = { value: 'ok' }; const array = [1, 2, 3]; const a = Object.getOwnPropertyDescriptor(object, 'value'); const b = Object.getOwnPropertyDescriptor(array, '0'); const c = Object.getOwnPropertyDescriptor(array, 'length'); return a.value + '|' + b.value + '|' + c.value;").AsString());
    }

    [Fact]
    public void ObjectCreatePreservesTheSuppliedPrototype()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("inherited", engine.Evaluate("const proto = { value: 'inherited' }; const object = Object.create(proto); return object.value;").AsString());
    }

    [Fact]
    public void ArrayPrototypeSliceSupportsCallWithArrayLikeArguments()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("c,d", engine.Evaluate("return Array.prototype.slice.call({ 0: 'a', 1: 'b', 2: 'c', 3: 'd', length: 4 }, 2).join(',');").AsString());
    }

    [Fact]
    public void ObjectPrototypeToStringSupportsCall()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("[object Object]|[object Array]|[object Null]", engine.Evaluate("return [Object.prototype.toString.call({}), Object.prototype.toString.call([]), Object.prototype.toString.call(null)].join('|');").AsString());
    }

    [Fact]
    public void ObjectPrototypeHasOwnPropertySupportsCall()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("return Object.prototype.hasOwnProperty.call({ value: 1 }, 'value');").AsBoolean());
        Assert.False(engine.Evaluate("return Object.prototype.hasOwnProperty.call({ value: 1 }, 'missing');").AsBoolean());
    }

    [Fact]
    public void ObjectGetPrototypeOfReadsNativePrototypeLinks()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("inherited", engine.Evaluate("const proto = { value: 'inherited' }; const object = Object.create(proto); return Object.getPrototypeOf(object).value;").AsString());
    }

    [Fact]
    public void ExecutesNativeClassConstructorsMethodsAndGetters()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(12d, engine.Evaluate("class Sample { constructor(value) { this.value = value; } get answer() { return this.value * 2; } } let sample = new Sample(6); return sample.answer;").AsNumber());
    }

    [Fact]
    public void NativeClassInstancesPreserveInstanceofIdentity()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("class Event { constructor(type) { this.type = type; } } const event = new Event('click'); return event instanceof Event;").AsBoolean());
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
    public void NativeDomConstructorsRecognizeTheirHostPrototypeChain()
    {
        using var engine = new NativeJavaScriptEngine();
        var document = TreeBuilder.Parse("<!doctype html><html><body><div id='root'></div></body></html>");
        using var loop = new HtmlEventLoop(engine);
        _ = DomBindings.Install(engine, document, eventLoop: loop);
        Assert.Equal("X", engine.Evaluate("var f=function(){}; Object.defineProperty(f, 'name', { value: 'X' }); f.name").AsString());
        Assert.Equal("Node", engine.Evaluate("Object.defineProperty(Node, 'name', { value: 'Node' }); Node.name").AsString());
        Assert.Equal("function:Node:true", engine.Evaluate("var probe=document.createElement('div'); typeof Node + ':' + Node.name + ':' + (probe instanceof Node)").AsString());
        Assert.Equal("true:true:true", engine.Evaluate("(probe instanceof Element) + ':' + (probe instanceof HTMLElement) + ':' + (probe instanceof HTMLDivElement)").AsString());
    }

    [Fact]
    public void NativeDomMutationMaintainsHostOwnership()
    {
        using var engine = new NativeJavaScriptEngine();
        var document = TreeBuilder.Parse("<div id='root'></div>");
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        Assert.True(engine.Evaluate("const root=document.getElementById('root'); const button=document.createElement('button'); root.appendChild(button); root.firstChild===button && button.parentNode===root && root.removeChild(button)===button && root.firstChild===null").AsBoolean());
        Assert.True(engine.Evaluate("const fragment=document.createDocumentFragment(); const main=document.createElement('main'); fragment.appendChild(main); root.appendChild(fragment); root.firstChild===main && main.parentNode===root && root.removeChild(main)===main && root.firstChild===null").AsBoolean());
        Assert.Equal("overridden", engine.Evaluate("Node.prototype.removeChild=function(){return 'overridden'}; document.getElementById('root').removeChild(document.createElement('button'))").AsString());
    }

    [Fact]
    public void NativeDomMatrixSupportsCanvasTransformOperations()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<div></div>");
        DomBindings.Install(engine, document);
        Assert.Equal("2,0,0,3,10,14,true", engine.Evaluate("var m = new DOMMatrix([2,0,0,3,4,5]).translate(3, 3); var n = m.multiply(new DOMMatrix().scale(1, 1)); [n.a,n.b,n.c,n.d,n.e,n.f,n.is2D].join(',')").AsString());
        Assert.Equal("1,0,0,1,-7,-8", engine.Evaluate("var i = new DOMMatrix([1,0,0,1,7,8]).inverse(); [i.a,i.b,i.c,i.d,i.e,i.f].join(',')").AsString());
        Assert.Equal("3,8,0,1", engine.Evaluate("var p = new DOMMatrix([2,0,0,3,1,2]).transformPoint({x:1,y:2}); [p.x,p.y,p.z,p.w].join(',')").AsString());
        Assert.Equal("-1,1", engine.Evaluate("var q = new DOMMatrix().flipX().skewY(45); [q.a,q.b].join(',')").AsString());
    }

    [Fact]
    public void NativeDomTextContentConcatenationKeepsStringValues()
    {
        using var engine = new NativeJavaScriptEngine();
        var document = TreeBuilder.Parse("<p id='status'>Wasm image filter</p>");
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        Assert.Equal("Wasm image filter ready", engine.Evaluate("document.getElementById('status').textContent += true ? ' ready' : ' failed'; document.getElementById('status').textContent").AsString());
    }

    [Fact]
    public void NativeIframeSrcdocCreatesLiveChildDocument()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<html><body><iframe id='preview' srcdoc=\"<p id='child'>child</p><output id='message'>pending</output><script>document.getElementById('child').onclick=function(){document.getElementById('message').textContent='clicked';};</script>\" sandbox='allow-scripts'></iframe></body></html>", new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess,
            Capabilities = new FeatureCapabilities { Iframes = true }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        document.Paint(800, 600);
        Assert.IsType<Document>(document.GetElementById("preview")!.Node.FramePresentation!.ChildDocument);
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

        var typed = engine.CreateTypedArray(ScriptBufferSourceKind.Uint8, [5, 6, 7]);
        var backing = engine.GetProperty(typed, "buffer");
        Assert.True(engine.TryDetachArrayBuffer(backing));
        Assert.Equal(0d, engine.GetProperty(typed, "byteLength").AsNumber());
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
        Assert.True(engine.Evaluate("Object.is(0 / 0, 0 / 0)").AsBoolean());
        Assert.False(engine.Evaluate("Object.is(0, -0)").AsBoolean());
        Assert.False(engine.Evaluate("Number.isNaN('x')").AsBoolean());
        Assert.False(engine.Evaluate("Number.isNaN()").AsBoolean());
        Assert.False(engine.Evaluate("Number.isInteger('2')").AsBoolean());
        Assert.True(engine.Evaluate("Number.isInteger(2)").AsBoolean());
        Assert.False(engine.Evaluate("Number.isFinite('2')").AsBoolean());
        Assert.Equal(7d, engine.Evaluate("Math.max('7', 3)").AsNumber());
        Assert.True(engine.Evaluate("Number.isNaN(Math.min(1, 'x'))").AsBoolean());
        Assert.Equal(-2d, engine.Evaluate("Math.round(-2.4)").AsNumber());
        Assert.Equal(6d, engine.Evaluate("Math.imul(2, 3)").AsNumber());
        Assert.Equal(2d, engine.Evaluate("Math.log10(100)").AsNumber());
        Assert.Equal(5d, engine.Evaluate("Math.hypot(3, 4)").AsNumber());
        Assert.Equal("😀", engine.Evaluate("String.fromCodePoint(0x1f600)").AsString());
        Assert.Equal(12d, engine.Evaluate("Number.parseInt('12px')").AsNumber());
        Assert.Equal(10d, engine.Evaluate("parseInt('1010', 2)").AsNumber());
        Assert.Equal(255d, engine.Evaluate("Number.parseInt('0xff')").AsNumber());
        Assert.Equal(3.14d, engine.Evaluate("parseFloat('3.14px')").AsNumber(), 2);
        Assert.Throws<WebIdlException>(() => engine.Evaluate("String.fromCodePoint(0x110000)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("queueMicrotask(1)"));
        Assert.Equal("1970-01-01T00:00:00.000Z", engine.Evaluate("new Date(0).toISOString()").AsString());
        Assert.Equal(0d, engine.Evaluate("Date.parse('1970-01-01T00:00:00Z')").AsNumber());
        Assert.True(engine.Evaluate("console.log('ok'); return true;").AsBoolean());
    }

    [Fact]
    public void ProvidesBoundedArrayAndStringMethods()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("a-b-c", engine.Evaluate("['a', 'b', 'c'].join('-')").AsString());
        Assert.Equal(3d, engine.Evaluate("let values = [1, 2]; values.push(3); return values.length").AsNumber());
        Assert.Equal("1,9", engine.Evaluate("let values = [1, 2, 3]; let removed = values.splice(1, 1, 9); values.pop(); values.unshift(0); values.shift(); return values.join(',')").AsString());
        Assert.Equal("3,2,1", engine.Evaluate("[1, 2, 3].toReversed().join(',')").AsString());
        Assert.Equal("1,2,3", engine.Evaluate("[3, 1, 2].toSorted().join(',')").AsString());
        Assert.Equal(3d, engine.Evaluate("let values = [4, 5]; let first = values.shift(); values.unshift(1, 2); return values.length").AsNumber());
        Assert.True(engine.Evaluate("[1, 2, 3].includes(2)").AsBoolean());
        Assert.True(engine.Evaluate("[1, 2, 3].includes(1, -3)").AsBoolean());
        Assert.True(engine.Evaluate("[0 / 0].includes(0 / 0)").AsBoolean());
        Assert.Equal(2d, engine.Evaluate("[1, 2, 3].find(value => value > 1)").AsNumber());
        Assert.Equal(1d, engine.Evaluate("[1, 2, 3].findIndex(value => value > 1)").AsNumber());
        Assert.Equal(3d, engine.Evaluate("[1, 2, 3].findLast(value => value > 1)").AsNumber());
        Assert.Equal(2d, engine.Evaluate("[1, 2, 3].findLastIndex(value => value > 1)").AsNumber());
        Assert.True(engine.Evaluate("[1, 2, 3].some(value => value == 3)").AsBoolean());
        Assert.True(engine.Evaluate("[1, 2, 3].every(value => value > 0)").AsBoolean());
        Assert.Equal(1d, engine.Evaluate("[4, 8, 9].indexOf(8)").AsNumber());
        Assert.Equal(2d, engine.Evaluate("[4, 8, 9].indexOf(9, -1)").AsNumber());
        Assert.Equal("BC", engine.Evaluate("'abc'.toUpperCase().slice(1)").AsString());
        Assert.True(engine.Evaluate("'folio'.startsWith('fol')").AsBoolean());
        Assert.True(engine.Evaluate("'folio'.includes('li', 2)").AsBoolean());
        Assert.True(engine.Evaluate("'folio'.startsWith('li', 2)").AsBoolean());
        Assert.True(engine.Evaluate("'folio'.endsWith('fo', 2)").AsBoolean());
        Assert.Equal("bc", engine.Evaluate("'abcdef'.slice(1, -3)").AsString());
        Assert.Equal("bcd", engine.Evaluate("'abcdef'.substring(4, 1)").AsString());
        Assert.Equal("cdef", engine.Evaluate("'abcdef'.substring(2)").AsString());
        Assert.Equal("ababab", engine.Evaluate("'ab'.repeat(3)").AsString());
        Assert.Equal("00abc", engine.Evaluate("'abc'.padStart(5, '0')").AsString());
        Assert.Equal("abc!!", engine.Evaluate("'abc'.padEnd(5, '!')").AsString());
        Assert.Equal(3d, engine.Evaluate("[1, 2, 3].at(-1)").AsNumber());
        Assert.True(engine.Evaluate("[].pop()").IsUndefined);
        Assert.True(engine.Evaluate("[].shift()").IsUndefined);
        Assert.Equal("c", engine.Evaluate("[...['a', 'b', 'c'].splice(-1, 1)][0]").AsString());
        Assert.Equal("cba", engine.Evaluate("[...['a', 'b', 'c'].reverse()].join('')").AsString());
        Assert.Equal("x,x,c", engine.Evaluate("['a', 'b', 'c'].fill('x', 0, 2).join(',')").AsString());
        Assert.Equal("aXc", engine.Evaluate("['a', 'b', 'c'].with(1, 'X').join('')").AsString());
        Assert.Equal("ac", engine.Evaluate("['a', 'b', 'c'].toSpliced(1, 1).join('')").AsString());
        Assert.Equal("ababe", engine.Evaluate("['a', 'b', 'c', 'd', 'e'].copyWithin(2, 0, 2).join('')").AsString());
        Assert.Equal("i", engine.Evaluate("'folio'.at(-2)").AsString());
        Assert.True(engine.Evaluate("'folio'.at(99)").IsUndefined);
        Assert.Equal(128512d, engine.Evaluate("'😀'.codePointAt(0)").AsNumber());
        Assert.Equal(3d, engine.Evaluate("'folio'.indexOf('i', 2)").AsNumber());
        Assert.Equal(3d, engine.Evaluate("'folio'.lastIndexOf('i')").AsNumber());
        Assert.Equal("x_x_x", engine.Evaluate("'x-x-x'.replaceAll('-', '_')").AsString());
        Assert.Equal("x-x-x", engine.Evaluate("'a-a-a'.replaceAll(new RegExp('a', 'g'), 'x')").AsString());
        Assert.Equal(2d, engine.Evaluate("'folio'.search(new RegExp('l'))").AsNumber());
        Assert.Equal(2d, engine.Evaluate("'a1a2'.matchAll(new RegExp('a.', 'g')).length").AsNumber());
        Assert.False(engine.Evaluate("new RegExp('(a+)+$').test('a'.repeat(10000) + '!')").AsBoolean());
        Assert.Equal("a+", engine.Evaluate("new RegExp('a+', 'gi').source").AsString());
        Assert.True(engine.Evaluate("new RegExp('a+', 'gi').global").AsBoolean());
        Assert.True(engine.Evaluate("new RegExp('a+', 'gi').ignoreCase").AsBoolean());
        Assert.Equal("x-a-a", engine.Evaluate("'a-a-a'.replace(new RegExp('a'), 'x')").AsString());
        Assert.Equal("x-x-x", engine.Evaluate("'a-a-a'.replace(new RegExp('a', 'g'), 'x')").AsString());
        Assert.Equal("x-a-a", engine.Evaluate("'a-a-a'.replace('a', 'x')").AsString());
        Assert.Throws<WebIdlException>(() => engine.Evaluate("'a-a'.replaceAll(new RegExp('a'), 'x')"));
    }

    [Fact]
    public void DispatchesBoundedArrayCallbacks()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(12d, engine.Evaluate("[1, 2, 3].map(value => value * 2).reduce((total, value) => total + value, 0)").AsNumber());
        Assert.Equal("cba", engine.Evaluate("['a', 'b', 'c'].reduceRight((result, value) => result + value, '')").AsString());
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[].reduce((total, value) => total + value)"));
        Assert.Equal(2d, engine.Evaluate("[1, 2, 3].filter(value => value > 1).length").AsNumber());
        Assert.Equal(3d, engine.Evaluate("Object.keys([7, 8, 9]).length").AsNumber());
    }

    [Fact]
    public void ArrayCallbackMethodsRejectNonCallableCallbacks()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].map(1)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].filter(null)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].find(undefined)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].findLast({})"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].some('callback')"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].every(false)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("[1].flatMap(0)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("Array.from([1], 0)"));
        Assert.Equal("0:1|1:2", engine.Evaluate("Array.from([1, 2], (value, index, source) => index + ':' + source[index]).join('|')").AsString());
        Assert.Throws<WebIdlException>(() => engine.Evaluate("new Set([1]).forEach(0)"));
        Assert.Throws<WebIdlException>(() => engine.Evaluate("new Map([['a', 1]]).forEach(null)"));
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

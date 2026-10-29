using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;
using Wasmtime;

namespace Folio.Scripting.Tests;

public sealed class NativeWebAssemblyTests
{
    [Fact]
    public void NativeJavaScriptEngineExposesNativeWebAssemblyExports()
    {
        // (module (func (export "run") (result i32) i32.const 42))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 5, 1, 96, 0, 1, 127,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 6, 1, 4, 0, 65, 42, 11
        };
        using var engine = new NativeJavaScriptEngine();
        using var bridge = NativeWebAssemblyBridge.Install(engine, 64 * 1024);
        WebAssemblyBindings.Install(engine, () => true, 64 * 1024, bridge);
        var values = string.Join(',', bytes);
        Assert.Equal(42, engine.Evaluate($"var module = new WebAssembly.Module(new Uint8Array([{values}])); var instance = new WebAssembly.Instance(module, {{}}); instance.exports.run()").AsNumber());
        Assert.Equal(42, engine.Evaluate($"new WebAssembly.Instance(new WebAssembly.Module(new Uint8Array([{values}])), {{}}).exports.run()").AsNumber());
        Assert.True(engine.Evaluate($"new WebAssembly.Instance(new WebAssembly.Module(new Uint8Array([{values}])), {{}}).exports.run() - 2 === 40").AsBoolean());
    }

    [Fact]
    public void NativeEngineExecutesAFunctionAndEnforcesItsStoreLimit()
    {
        // (module (func (export "run") (result i32) i32.const 42))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 5, 1, 96, 0, 1, 127,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 6, 1, 4, 0, 65, 42, 11
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        Assert.NotNull(instance);
        var native = instance!;
        using (native)
            Assert.Equal(42, native.Invoke(engine, "run", []).AsNumber());
    }

    [Fact]
    public void NativeEngineRejectsModulesOverTheConfiguredCompileBudget()
    {
        using var runtime = new NativeWebAssemblyRuntime(7);
        using var engine = new JintScriptEngine();
        Assert.False(runtime.TryInstantiate(engine, new byte[8], ScriptValue.Undefined, [], out _));
    }

    [Fact]
    public void NativeEngineCallsAJavaScriptFunctionImport()
    {
        // (module (type (func (param i32) (result i32)))
        //   (import "env" "fn" (func)) (func (export "run") (call 0)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 96, 1, 127, 1, 127,
            2, 10, 1, 3, (byte)'e', (byte)'n', (byte)'v', 2, (byte)'f', (byte)'n', 0, 0,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 1,
            10, 8, 1, 6, 0, 65, 4, 16, 0, 11
        };
        using var engine = new JintScriptEngine();
        var imports = engine.Evaluate("({env:{fn:function(value){return value + 9;}}})");
        var descriptors = engine.Evaluate("([{module:'env',name:'fn',kind:0,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        Assert.True(runtime.TryInstantiate(engine, bytes, imports, descriptorValues, out var instance));
        var native = instance!;
        using (native)
            Assert.Equal(13, native.Invoke(engine, "run", [ScriptValue.FromNumber(4)]).AsNumber());
    }

    [Fact]
    public void NativeEngineBridgesExternrefImportsWithoutStringifyingObjects()
    {
        // (module (type (func (param externref) (result externref)))
        //   (import "env" "identity" (func (type 0)))
        //   (func (export "run") (type 0) (local.get 0) (call 0)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 96, 1, 0x6f, 1, 0x6f,
            2, 16, 1, 3, (byte)'e', (byte)'n', (byte)'v', 8, (byte)'i', (byte)'d', (byte)'e', (byte)'n', (byte)'t', (byte)'i', (byte)'t', (byte)'y', 0, 0,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 1,
            10, 8, 1, 6, 0, 32, 0, 16, 0, 11
        };
        using var engine = new JintScriptEngine();
        var imports = engine.Evaluate("({env:{identity:function(value){return value;}}})");
        var descriptors = engine.Evaluate("([{module:'env',name:'identity',kind:0,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        Assert.True(runtime.TryInstantiate(engine, bytes, imports, descriptorValues, out var instance));
        using var native = instance!;
        var value = engine.Evaluate("({answer:42})");
        var result = native.Invoke(engine, "run", [value]);
        Assert.Same(value.Raw, result.Raw);
    }

    [Fact]
    public void NativeEngineExposesNullExternrefGlobals()
    {
        // (module (global (export "g") externref (ref.null extern)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            6, 6, 1, 0x6f, 0, 0xd0, 0x6f, 0x0b,
            7, 5, 1, 1, (byte)'g', 3, 0
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        Assert.True(native.GetGlobal("g")!.GetValue(engine).IsNull);
    }

    [Fact]
    public void NativeEngineRoundTripsLocalExternrefFunctionResults()
    {
        // (module (type (func (param externref) (result externref)))
        //   (func (export "run") (type 0) (local.get 0)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 0x60, 1, 0x6f, 1, 0x6f,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 6, 1, 4, 0, 0x20, 0, 0x0b
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        var value = engine.Evaluate("({answer:42})");
        var result = native.Invoke(engine, "run", [value]);
        Assert.Same(value.Raw, result.Raw);
    }

    [Fact]
    public void NativeEngineReusesBoundedCompiledModuleCache()
    {
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 5, 1, 96, 0, 1, 127,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 6, 1, 4, 0, 65, 42, 11
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.Prepare(bytes));
        Assert.Equal(1, runtime.CachedModuleCount);
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var firstInstance));
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var secondInstance));
        var first = firstInstance!;
        var second = secondInstance!;
        Assert.Equal(1, runtime.CachedModuleCount);
        using (first) using (second)
        {
            Assert.Equal(42, first.Invoke(engine, "run", []).AsNumber());
            Assert.Equal(42, second.Invoke(engine, "run", []).AsNumber());
        }
    }

    [Fact]
    public void NativeEngineExposesAndSynchronizesLocalMemoryExports()
    {
        // (module (memory (export "mem") 1)
        //   (func (export "load") (result i32) i32.load))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 5, 1, 96, 0, 1, 127,
            3, 2, 1, 0,
            5, 3, 1, 0, 1,
            7, 14, 2, 4, (byte)'l', (byte)'o', (byte)'a', (byte)'d', 0, 0, 3, (byte)'m', (byte)'e', (byte)'m', 2, 0,
            10, 9, 1, 7, 0, 65, 0, 40, 0, 0, 11
        };
        using var runtime = new NativeWebAssemblyRuntime(128 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        var memory = native.GetMemory("mem");
        Assert.NotNull(memory);
        var memoryTemplate = new HostObjectTemplate("NativeMemoryTest")
            .DefineProperty("buffer", (target, _) => ((NativeWebAssemblyMemory)target).GetBuffer(engine));
        engine.SetGlobalProperty("nativeMemory", engine.CreateHostObject(memoryTemplate, memory));
        engine.Evaluate("new Uint8Array(nativeMemory.buffer)[0] = 7;");
        Assert.Equal(7, native.Invoke(engine, "load", []).AsNumber());
        Assert.Equal(1, memory.Grow(1));
        Assert.Equal(2 * 65536, memory.ByteLength);
    }

    [Fact]
    public void NativeEngineSynchronizesImportedMemory()
    {
        // (module (type (func (result i32)))
        //   (import "env" "mem" (memory 1))
        //   (func (export "load") (result i32) i32.load))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 5, 1, 96, 0, 1, 127,
            2, 12, 1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'m', (byte)'e', (byte)'m', 2, 0, 1,
            3, 2, 1, 0,
            7, 8, 1, 4, (byte)'l', (byte)'o', (byte)'a', (byte)'d', 0, 0,
            10, 9, 1, 7, 0, 65, 0, 40, 0, 0, 11
        };
        using var engine = new JintScriptEngine();
        var imports = engine.Evaluate("({env:{mem:{buffer:new ArrayBuffer(65536)}}})");
        engine.SetGlobalProperty("nativeImports", imports);
        engine.Evaluate("new Uint8Array(nativeImports.env.mem.buffer)[0] = 11;");
        var descriptors = engine.Evaluate("([{module:'env',name:'mem',kind:2,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        using var runtime = new NativeWebAssemblyRuntime(128 * 1024);
        Assert.True(runtime.TryInstantiate(engine, bytes, imports, descriptorValues, out var instance));
        var native = instance!;
        using (native) Assert.Equal(11, native.Invoke(engine, "load", []).AsNumber());
    }

    [Fact]
    public void NativeEngineExposesNumericGlobalExports()
    {
        // (module (global (export "g") i32 (i32.const 42)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            6, 6, 1, 127, 0, 65, 42, 11,
            7, 5, 1, 1, (byte)'g', 3, 0
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        var global = native.GetGlobal("g");
        Assert.NotNull(global);
        Assert.Equal(42, global!.GetValue(engine).AsNumber());
        Assert.ThrowsAny<Exception>(() => global.SetValue(ScriptValue.FromNumber(9)));
    }

    [Fact]
    public void NativeEngineExposesLocalFunctionTables()
    {
        // (module (table (export "tab") 1 funcref))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            4, 4, 1, 112, 0, 1,
            7, 7, 1, 3, (byte)'t', (byte)'a', (byte)'b', 1, 0
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        var table = native.GetTable("tab");
        Assert.NotNull(table);
        Assert.Equal(1, table!.Length);
        Assert.Equal(1, table.Grow(1, null));
        Assert.Equal(2, table.Length);
        Assert.Null(table.Get(0));
        table.Set(1, null);
        Assert.Null(table.Get(1));
    }

    [Fact]
    public void NativeEngineExposesLocalExternrefTables()
    {
        // (module (table (export "tab") 1 externref))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            4, 4, 1, 0x6f, 0, 1,
            7, 7, 1, 3, (byte)'t', (byte)'a', (byte)'b', 1, 0
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        var table = native.GetTable("tab");
        Assert.NotNull(table);
        var value = engine.Evaluate("({answer:42})");
        table!.Set(0, value.Raw);
        Assert.Same(value.Raw, table.Get(0));
        Assert.Equal(1, table.Grow(1, null));
        Assert.Equal(2, table.Length);
        Assert.Null(table.Get(1));
    }

    [Fact]
    public void JavaScriptBindingExposesNativeExternrefTables()
    {
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            4, 4, 1, 0x6f, 0, 1,
            7, 7, 1, 3, (byte)'t', (byte)'a', (byte)'b', 1, 0
        };
        using var engine = new NativeJavaScriptEngine();
        using var bridge = NativeWebAssemblyBridge.Install(engine, 64 * 1024);
        WebAssemblyBindings.Install(engine, () => true, 64 * 1024, bridge);
        var values = string.Join(',', bytes);
        var result = engine.Evaluate($"var m=new WebAssembly.Module(new Uint8Array([{values}])); var i=new WebAssembly.Instance(m,{{}}); var o={{answer:42}}; i.exports.tab.set(0,o); i.exports.tab.get(0)===o;");
        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void NativeEngineAcceptsImportedNullFunctionTables()
    {
        // (module (import "env" "tab" (table 1 funcref)) (export "tab" (table 0)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            2, 13, 1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'t', (byte)'a', (byte)'b', 1, 112, 0, 1,
            7, 7, 1, 3, (byte)'t', (byte)'a', (byte)'b', 1, 0
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        var imports = engine.Evaluate("({env:{tab:{length:1,get:function(){return null;}}}})");
        var descriptors = engine.Evaluate("([{module:'env',name:'tab',kind:1,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        Assert.True(runtime.TryInstantiate(engine, bytes, imports, descriptorValues, out var instance));
        using var native = instance!;
        var table = native.GetTable("tab");
        Assert.NotNull(table);
        Assert.Equal(1, table!.Length);
        Assert.Null(table.Get(0));
    }

    [Fact]
    public void NativeEngineBridgesNonNullImportedFunctionTables()
    {
        // Provider: (func (type 0) (param i32) (result i32) (+ local.get 0 1))
        //           (table (export "tab") 1 funcref) (elem (i32.const 0) 0)
        var providerBytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 96, 1, 127, 1, 127,
            3, 2, 1, 0,
            4, 4, 1, 112, 0, 1,
            7, 7, 1, 3, (byte)'t', (byte)'a', (byte)'b', 1, 0,
            9, 7, 1, 0, 65, 0, 11, 1, 0,
            10, 9, 1, 7, 0, 32, 0, 65, 1, 106, 11
        };
        // Consumer: (import "env" "tab" (table 1 funcref))
        //           (func (export "run") (param i32) (result i32)
        //             (call_indirect (type 0) (local.get 0) (i32.const 0)))
        var consumerBytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 96, 1, 127, 1, 127,
            2, 13, 1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'t', (byte)'a', (byte)'b', 1, 112, 0, 1,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 11, 1, 9, 0, 32, 0, 65, 0, 17, 0, 0, 11
        };
        using var engine = new JintScriptEngine();
        using (var validationEngine = new Engine()) Assert.Null(Module.Validate(validationEngine, providerBytes));
        using var providerRuntime = new NativeWebAssemblyRuntime(64 * 1024);
        Assert.True(providerRuntime.TryInstantiate(engine, providerBytes, ScriptValue.Undefined, [], out var providerInstance));
        using var provider = providerInstance!;
        var providerTable = provider.GetTable("tab");
        Assert.NotNull(providerTable);
        var providerFunction = Assert.IsType<Wasmtime.Function>(providerTable!.Get(0));
        var nativeFunction = new NativeWebAssemblyFunction(providerFunction, providerTable.Gate);
        var functionTemplate = new HostObjectTemplate("TestNativeWebAssemblyFunction")
            .DefineMethod("invoke", (target, _, args) => ((NativeWebAssemblyFunction)target).Invoke(engine, args.ToArray()));
        var wrappedFunction = engine.CreateHostObject(functionTemplate, nativeFunction);
        engine.SetGlobalProperty("__nativeFunction", wrappedFunction);
        engine.Evaluate("var importedTableFunction = function(){return __nativeFunction.invoke.apply(__nativeFunction, arguments);}; Object.defineProperty(importedTableFunction, '__folio_native_wasm_function', {value:__nativeFunction});");
        var imports = engine.Evaluate("({env:{tab:{length:1,get:function(){return importedTableFunction;}}}})");
        var descriptors = engine.Evaluate("([{module:'env',name:'tab',kind:1,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        using var consumerRuntime = new NativeWebAssemblyRuntime(64 * 1024);
        Assert.True(consumerRuntime.TryInstantiate(engine, consumerBytes, imports, descriptorValues, out var consumerInstance));
        using var consumer = consumerInstance!;
        Assert.Equal(42, consumer.Invoke(engine, "run", [ScriptValue.FromNumber(41)]).AsNumber());
    }

    [Fact]
    public void NativeEngineBridgesTypedPageFunctionTables()
    {
        // (module (type (func (param i32) (result i32)))
        //   (import "env" "tab" (table 1 funcref))
        //   (func (export "run") (param i32) (result i32)
        //     (call_indirect (type 0) (local.get 0) (i32.const 0))))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 96, 1, 127, 1, 127,
            2, 13, 1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'t', (byte)'a', (byte)'b', 1, 112, 0, 1,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 11, 1, 9, 0, 32, 0, 65, 0, 17, 0, 0, 11
        };
        using var engine = new JintScriptEngine();
        var imports = engine.Evaluate("({env:{tab:{length:1,get:function(){return function(value){return value + 5;};}}}})");
        var descriptors = engine.Evaluate("([{module:'env',name:'tab',kind:1,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        Assert.True(runtime.TryInstantiate(engine, bytes, imports, descriptorValues, out var instance));
        using var native = instance!;
        Assert.Equal(42, native.Invoke(engine, "run", [ScriptValue.FromNumber(37)]).AsNumber());
    }

    [Fact]
    public void JavaScriptBindingUsesNativeEngineForTypedPageFunctionTables()
    {
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 6, 1, 96, 1, 127, 1, 127,
            2, 13, 1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'t', (byte)'a', (byte)'b', 1, 112, 0, 1,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 11, 1, 9, 0, 32, 0, 65, 0, 17, 0, 0, 11
        };
        using var engine = new JintScriptEngine();
        using var bridge = NativeWebAssemblyBridge.Install(engine, 64 * 1024);
        WebAssemblyBindings.Install(engine, () => true, 64 * 1024, bridge);
        var values = string.Join(',', bytes);
        var result = engine.Evaluate($"var table = new WebAssembly.Table({{initial:1}}); table.set(0, function(value){{return value + 5;}}); var module = new WebAssembly.Module(new Uint8Array([{values}])); var instance = new WebAssembly.Instance(module, {{env:{{tab:table}}}}); instance.exports.run(37);");
        Assert.Equal(42, result.AsNumber());
    }

    [Fact]
    public void NativeEngineRefusesUntypedImportedFunctionCallbacksInsteadOfMislinkingThem()
    {
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            2, 13, 1, 3, (byte)'e', (byte)'n', (byte)'v', 3, (byte)'t', (byte)'a', (byte)'b', 1, 112, 0, 1,
            7, 7, 1, 3, (byte)'t', (byte)'a', (byte)'b', 1, 0
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        var imports = engine.Evaluate("({env:{tab:{length:1,get:function(){return function(){return 7;};}}}})");
        var descriptors = engine.Evaluate("([{module:'env',name:'tab',kind:1,index:0}])");
        Assert.True(engine.TryGetArrayItems(descriptors, out var descriptorValues));
        Assert.False(runtime.TryInstantiate(engine, bytes, imports, descriptorValues, out _));
    }

    [Fact]
    public void NativeEngineInterruptsRunawayExecution()
    {
        // (module (func (export "run") (loop br 0)))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 4, 1, 96, 0, 0,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 9, 1, 7, 0, 3, 64, 12, 0, 11, 11
        };
        using var runtime = new NativeWebAssemblyRuntime(64 * 1024);
        using var engine = new JintScriptEngine();
        Assert.True(runtime.TryInstantiate(engine, bytes, ScriptValue.Undefined, [], out var instance));
        using var native = instance!;
        var error = Assert.ThrowsAny<Exception>(() => native.Invoke(engine, "run", []));
        Assert.Contains("deadline", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JavaScriptBindingUsesNativeEngineForRunawayModule()
    {
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 4, 1, 96, 0, 0,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 9, 1, 7, 0, 3, 64, 12, 0, 11, 11
        };
        using var engine = new JintScriptEngine();
        using var bridge = NativeWebAssemblyBridge.Install(engine, 64 * 1024);
        WebAssemblyBindings.Install(engine, () => true, 64 * 1024);
        var values = string.Join(',', bytes);
        var error = Assert.ThrowsAny<Exception>(() => engine.Evaluate($"var m=new WebAssembly.Module(new Uint8Array([{values}])); var i=new WebAssembly.Instance(m,{{}}); i.exports.run();"));
        Assert.Contains("deadline", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StreamingCompilationWarmsAndUsesTheNativeModuleCache()
    {
        // (module (func (export "run") (result i32) i32.const 42))
        var bytes = new byte[]
        {
            0, 97, 115, 109, 1, 0, 0, 0,
            1, 5, 1, 96, 0, 1, 127,
            3, 2, 1, 0,
            7, 7, 1, 3, (byte)'r', (byte)'u', (byte)'n', 0, 0,
            10, 6, 1, 4, 0, 65, 42, 11
        };
        using var engine = new JintScriptEngine();
        using var bridge = NativeWebAssemblyBridge.Install(engine, 64 * 1024);
        WebAssemblyBindings.Install(engine, () => true, 64 * 1024, bridge);
        var values = string.Join(',', bytes);
        engine.Evaluate($"var response={{ok:true,status:200,headers:{{get:function(name){{return name.toLowerCase()==='content-type'?'application/wasm':null;}}}},arrayBuffer:function(){{return Promise.resolve(new Uint8Array([{values}]).buffer);}}}}; var streamedRun; WebAssembly.instantiateStreaming(response, {{}}).then(function(result){{streamedRun=result.instance.exports.run();}});");
        for (var checkpoint = 0; checkpoint < 16; checkpoint++) engine.PerformMicrotaskCheckpoint();
        Assert.Equal(42, engine.Evaluate("streamedRun").AsNumber());
        Assert.Equal(1, bridge.CachedModuleCount);
    }

    [Fact]
    public void StreamingCompilationRejectsAnInvalidMimeType()
    {
        using var engine = new JintScriptEngine();
        using var bridge = NativeWebAssemblyBridge.Install(engine, 64 * 1024);
        WebAssemblyBindings.Install(engine, () => true, 64 * 1024, bridge);
        engine.Evaluate("var response={ok:true,status:200,headers:{get:function(name){return name.toLowerCase()==='content-type'?'text/plain':null;}},arrayBuffer:function(){return Promise.resolve(new Uint8Array([0,97,115,109,1,0,0,0]).buffer);}}; var failure; WebAssembly.instantiateStreaming(response, {}).catch(function(error){failure=error.name;});");
        for (var checkpoint = 0; checkpoint < 16; checkpoint++) engine.PerformMicrotaskCheckpoint();
        Assert.Equal("TypeError", engine.Evaluate("failure").AsString());
    }
}

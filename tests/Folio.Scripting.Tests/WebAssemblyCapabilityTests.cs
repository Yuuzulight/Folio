using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class WebAssemblyCapabilityTests
{
    private const string EmptyModule = "new Uint8Array([0,97,115,109,1,0,0,0])";
    private const string AddModule = "new Uint8Array([0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,97,100,100,0,0,10,9,1,7,0,32,0,32,1,106,11])";

    [Theory]
    [InlineData("new WebAssembly.Memory({initial:1})")]
    [InlineData("new WebAssembly.Table({initial:1})")]
    [InlineData("new WebAssembly.Global({value:'i32'},0)")]
    [InlineData("new WebAssembly.Module(bytes)")]
    public void PageCannotGrantItselfWebAssembly(string operation)
    {
        using var engine = Create(false);
        engine.Evaluate($"var bytes = {EmptyModule}; __folio_wasm_enabled = true;");
        AssertDenied(engine, operation);
        Assert.False(engine.Evaluate("WebAssembly.validate(bytes)").AsBoolean());
    }

    [Theory]
    [InlineData("new saved.Memory({initial:1})")]
    [InlineData("new saved.Table({initial:1})")]
    [InlineData("new saved.Global({value:'i32'},0)")]
    [InlineData("new saved.Module(bytes)")]
    [InlineData("new saved.Instance(module,{})")]
    [InlineData("memory.grow(0)")]
    [InlineData("table.grow(0)")]
    [InlineData("table.get(0)")]
    [InlineData("table.set(0,null)")]
    [InlineData("instance.exports.add(2,3)")]
    public void RetainedOperationsObserveHostRevocationAndRegrant(string operation)
    {
        using var engine = Create(true);
        engine.Evaluate($"var saved=WebAssembly, bytes={AddModule}; var module=new saved.Module(bytes); var instance=new saved.Instance(module,{{}}); var memory=new saved.Memory({{initial:1}}); var table=new saved.Table({{initial:1}});");
        Assert.Equal(5, engine.Evaluate("instance.exports.add(2,3)").AsNumber());
        Assert.True(DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default));
        engine.Evaluate("__folio_wasm_enabled=true;");
        AssertDenied(engine, operation);
        Assert.True(DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebAssembly = true }));
        engine.Evaluate(operation);
        Assert.Equal(5, engine.Evaluate("instance.exports.add(2,3)").AsNumber());
    }

    [Fact]
    public void HostCanGrantWebAssemblyAfterInstallation()
    {
        using var engine = Create(false);
        engine.Evaluate($"var bytes={EmptyModule}; var retained=WebAssembly;");
        Assert.False(engine.Evaluate("retained.validate(bytes)").AsBoolean());
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebAssembly = true });
        Assert.True(engine.Evaluate("retained.validate(bytes)").AsBoolean());
        engine.Evaluate("new retained.Module(bytes)");
    }

    [Fact]
    public void FailedBindingReplacementRetiresThePreviousPermissionContext()
    {
        using var engine = Create(true);
        engine.Evaluate("var retained=WebAssembly;");
        using var replacement = Document.Parse("<p>replacement</p>");
        // Reinstalling the full bootstrap currently fails on its non-configurable globals.
        // That failure must not leave the old document's granted facade usable.
        Assert.Throws<ScriptRuntimeException>(() => DomBindings.Install(engine, replacement));
        AssertDenied(engine, "new retained.Memory({initial:1})");
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebAssembly = true });
        AssertDenied(engine, "new retained.Memory({initial:1})");
    }

    [Theory]
    [InlineData("__folio_wasm_max_pages=100; new WebAssembly.Memory({initial:2})",65536)]
    [InlineData("__folio_wasm_max_table_elements=100; new WebAssembly.Table({initial:5})",16)]
    [InlineData("new WebAssembly.Global({value:'i32'},0); __folio_wasm_global_count=0; __folio_wasm_max_globals=100; new WebAssembly.Global({value:'i32'},0)",16)]
    public void PageCannotRaiseHostLimits(string operation, int limit)
    {
        using var engine = Create(true, limit);
        Assert.Equal("RangeError", engine.Evaluate($"try {{ {operation}; 'allowed'; }} catch(error) {{ error.name; }}").AsString());
    }

    [Fact]
    public void PendingStreamingCompilationRechecksPermission()
    {
        using var engine = Create(true);
        engine.Evaluate($"var release; var response={{ok:true,headers:{{get:()=> 'application/wasm'}},arrayBuffer:()=>new Promise(resolve=>release=resolve)}}; var outcome='pending'; WebAssembly.compileStreaming(response).then(()=>outcome='allowed',error=>outcome=error.name); var bytes={EmptyModule};");
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        engine.Evaluate("__folio_wasm_enabled=true; release(bytes.buffer);");
        engine.PerformMicrotaskCheckpoint();
        Assert.Equal("NotSupportedError", engine.Evaluate("outcome").AsString());
    }

    [Fact]
    public void PrivateHelpersAndConstructorsIgnorePageReplacements()
    {
        using var engine = Create(true);
        engine.Evaluate($"var retained=WebAssembly, bytes={AddModule}; __folio_wasm_validate_bytes=()=>false; __folio_wasm_call=()=>99; globalThis.WebAssembly={{Module:function(){{throw 'page constructor';}}}};");
        Assert.Equal(5, engine.Evaluate("new retained.Instance(new retained.Module(bytes),{}).exports.add(2,3)").AsNumber());
        Assert.True(engine.Evaluate("typeof __folio_wasm_host === 'undefined'").AsBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedApiKeepsItsIntrinsicFunctions(bool replaceMethods)
    {
        using var engine = Create(true);
        engine.Evaluate($"var retained=WebAssembly, bytes={AddModule};");
        engine.Evaluate(replaceMethods
            ? "Math.min=()=>{throw 'page min';}; Number.isInteger=()=>false; String.fromCharCode=()=>''; Object.defineProperty=()=>{throw 'page define';}; Promise.resolve=()=>{throw 'page promise';};"
            : "globalThis.Math={}; globalThis.Array={}; globalThis.String=()=> 'page string'; globalThis.Object={};");
        Assert.Equal(5, engine.Evaluate("new retained.Instance(new retained.Module(bytes),{}).exports.add(2,3)").AsNumber());
        engine.Evaluate("var compiled=false; retained.compile(bytes).then(()=>compiled=true); var global=new retained.Global({value:'i32',mutable:true},4); global.value=7;");
        engine.PerformMicrotaskCheckpoint();
        Assert.True(engine.Evaluate("compiled && global.value === 7").AsBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RevocationInsideAnImportStopsTheEnclosingExport(bool directImport)
    {
        using var engine = Create(true);
        var host = new HostObjectTemplate("Revoker").DefineMethod("stop", (_, _, _) =>
        {
            DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
            return ScriptValue.Undefined;
        });
        engine.SetGlobalProperty("revoker", engine.CreateHostObject(host, this));
        // Both modules import env.stop. One exports that import; the other calls it from a local function.
        var bytes = directImport
            ? "[0,97,115,109,1,0,0,0,1,4,1,96,0,0,2,12,1,3,101,110,118,4,115,116,111,112,0,0,7,7,1,3,114,117,110,0,0]"
            : "[0,97,115,109,1,0,0,0,1,8,2,96,0,0,96,0,1,127,2,12,1,3,101,110,118,4,115,116,111,112,0,0,3,2,1,1,7,7,1,3,114,117,110,0,1,10,8,1,6,0,16,0,65,7,11]";
        engine.Evaluate($"var instance=new WebAssembly.Instance(new WebAssembly.Module(new Uint8Array({bytes})),{{env:{{stop:()=>revoker.stop()}}}});");
        AssertDenied(engine, "instance.exports.run()");
    }

    [Fact]
    public void DenialIsReportedOncePerDocumentAndDisposalRetiresRetainedApi()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<p>capabilities</p>");
        DomBindings.Install(engine, document);
        var diagnostics = new List<Diagnostic>();
        using var runner = new ScriptRunner(document, engine, new HtmlEventLoop(engine), diagnostics: diagnostics);
        engine.Evaluate($"var retained=WebAssembly, bytes={EmptyModule}; retained.validate(bytes); retained.validate(bytes);");
        AssertDenied(engine, "new retained.Memory({initial:1})");
        var diagnostic = Assert.Single(diagnostics, d => d.Code == DiagnosticCode.CapabilityDenied);
        Assert.Equal("WebAssembly", diagnostic.Feature);
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebAssembly = true });
        engine.Evaluate("new retained.Memory({initial:1})");
        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        AssertDenied(engine, "new retained.Memory({initial:1})");
        Assert.Single(diagnostics, d => d.Code == DiagnosticCode.CapabilityDenied);
        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebAssembly = true });
        Assert.True(DomBindings.Dispose(engine));
        AssertDenied(engine, "new retained.Memory({initial:1})");
    }

    private static JintScriptEngine Create(bool granted, int limit = 65536)
    {
        var engine = new JintScriptEngine();
        DomBindings.Install(engine, Document.Parse("<p>capability probe</p>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebAssembly = granted },
            Limits = ResourceLimits.Default with { MaxWebAssemblyBytes = limit }
        }));
        return engine;
    }

    private static void AssertDenied(JintScriptEngine engine, string operation) =>
        Assert.Equal("NotSupportedError", engine.Evaluate($"try {{ {operation}; 'allowed'; }} catch(error) {{ error.name; }}").AsString());
}

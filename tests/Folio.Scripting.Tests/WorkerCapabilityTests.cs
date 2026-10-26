using System.Text.Json;
using System.Text;
using Folio.Resources;
using Folio.Scripting.Dom;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

[Collection("Worker execution deadlines")]
public sealed class WorkerCapabilityTests
{
    private static FeatureCapabilities Grants(bool wasm = true) => new() { Workers = true, WebAssembly = wasm };
    private const string AddBytes = "[0,97,115,109,1,0,0,0,1,7,1,96,2,127,127,1,127,3,2,1,0,7,7,1,3,97,100,100,0,0,10,9,1,7,0,32,0,32,1,106,11]";

    [Fact]
    public void NativeEngineWorkerRunsAnIsolatedScript()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>worker</p>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        engine.Evaluate("var response=''; var error=''; var worker=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(e){postMessage(e.data+1);}')); worker.onmessage=function(e){response=e.data;}; worker.onerror=function(e){error=e.message;}; worker.postMessage(41);");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("response").AsNumber() == 42 || engine.GetGlobalProperty("error").AsString() != ""; }, TimeSpan.FromSeconds(2)), engine.GetGlobalProperty("error").AsString());
        Assert.Equal("", engine.GetGlobalProperty("error").AsString());
    }

    [Fact]
    public void NativeEngineWorkerRoundTripsObjectsAndArrays()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>worker</p>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        engine.Evaluate("var response=''; var error=''; var worker=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(e){postMessage({n:e.data.n+1,second:e.data.values[1]});}')); worker.onmessage=function(e){response=e.data.n+':'+e.data.second;}; worker.onerror=function(e){error=e.message;}; worker.onmessageerror=function(e){error='messageerror';}; worker.postMessage({n:41,values:[7,42]});");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("response").AsString() != "" || engine.GetGlobalProperty("error").AsString() != ""; }, TimeSpan.FromSeconds(2)), engine.GetGlobalProperty("error").AsString());
        Assert.Equal("42:42", engine.GetGlobalProperty("response").AsString());
        Assert.Equal("", engine.GetGlobalProperty("error").AsString());
    }

    [Fact]
    public void NativeEngineWorkerRoundTripsCyclesMapsSetsAndAliases()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>worker</p>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        engine.Evaluate("var response=''; var error=''; var worker=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(e){var same=e.data.self===e.data && e.data.left===e.data.right; postMessage([same,e.data.map.get(\"answer\"),e.data.set.has(7)].join(\":\"));}')); worker.onmessage=function(e){response=e.data;}; worker.onerror=function(e){error=e.message;}; worker.onmessageerror=function(){error='messageerror';}; var root={}; root.self=root; root.map=new Map([[\"answer\",42]]); root.set=new Set([7]); root.left={value:9}; root.right=root.left; worker.postMessage(root);");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("response").AsString() != "" || engine.GetGlobalProperty("error").AsString() != ""; }, TimeSpan.FromSeconds(2)), engine.GetGlobalProperty("error").AsString());
        Assert.Equal("true:42:true", engine.GetGlobalProperty("response").AsString());
        Assert.Equal("", engine.GetGlobalProperty("error").AsString());
    }

    [Fact]
    public void NativeEngineStructuredCloneEncodesObjectsAndArrays()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("({n:41,values:[7,42]})");
        var payload = StructuredCloneCodec.Encode(engine, value, ScriptValue.Undefined, 1024 * 1024, static (_, _) => null);
        Assert.True(payload.Json.Contains("records", StringComparison.Ordinal), payload.Json);
        Assert.False(payload.Json.Contains("\"t\":\"port\"", StringComparison.Ordinal), payload.Json);
        using var destination = new NativeJavaScriptEngine();
        var decoded = StructuredCloneCodec.Decode(destination, payload, static (_, _) => ScriptValue.Undefined);
        Assert.Equal(41, destination.GetProperty(decoded.Value, "n").AsNumber());
    }

    [Fact]
    public void NativeEngineStructuredCloneRoundTripsRegularExpressions()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("/folio/i");
        var payload = StructuredCloneCodec.Encode(engine, value, ScriptValue.Undefined, 1024 * 1024, static (_, _) => null);
        using var destination = new NativeJavaScriptEngine();
        var decoded = StructuredCloneCodec.Decode(destination, payload, static (_, _) => ScriptValue.Undefined);
        var test = destination.GetProperty(decoded.Value, "test");
        Assert.True(destination.Call(test, decoded.Value, [ScriptValue.FromString("FOLIO")]).AsBoolean());
    }

    [Fact]
    public void NativeEngineStructuredCloneRoundTripsCyclesMapsAndSets()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("var root={}; root.self=root; root.map=new Map([['answer',42]]); root.set=new Set([7]); root");
        var payload = StructuredCloneCodec.Encode(engine, value, ScriptValue.Undefined, 1024 * 1024, static (_, _) => null);
        using var destination = new NativeJavaScriptEngine();
        var decoded = StructuredCloneCodec.Decode(destination, payload, static (_, _) => ScriptValue.Undefined);
        Assert.Same(decoded.Value.Raw, destination.GetProperty(decoded.Value, "self").Raw);
        var map = destination.GetProperty(decoded.Value, "map");
        var get = destination.GetProperty(map, "get");
        Assert.Equal(42, destination.Call(get, map, [ScriptValue.FromString("answer")]).AsNumber());
        var set = destination.GetProperty(decoded.Value, "set");
        var has = destination.GetProperty(set, "has");
        Assert.True(destination.Call(has, set, [ScriptValue.FromNumber(7)]).AsBoolean());
    }

    [Fact]
    public void NativeEngineStructuredCloneRoundTripsDates()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("new Date(123456789)");
        var payload = StructuredCloneCodec.Encode(engine, value, ScriptValue.Undefined, 1024 * 1024, static (_, _) => null);
        using var destination = new NativeJavaScriptEngine();
        var decoded = StructuredCloneCodec.Decode(destination, payload, static (_, _) => ScriptValue.Undefined);
        var getTime = destination.GetProperty(decoded.Value, "getTime");
        Assert.Equal(123456789, destination.Call(getTime, decoded.Value, []).AsNumber());
    }

    [Fact]
    public void NativeEngineStructuredCloneTransfersArrayBuffersAndTypedArrays()
    {
        using var engine = new NativeJavaScriptEngine();
        var buffer = engine.Evaluate("var b=new ArrayBuffer(3); var v=new Uint8Array(b); v[0]=4; v[1]=8; v[2]=15; b");
        var view = engine.Evaluate("v");
        var payload = StructuredCloneCodec.Encode(engine, view, engine.CreateArray([buffer]), 1024 * 1024, static (_, _) => null);
        Assert.False(engine.TryGetArrayBufferBytes(buffer, out _));
        using var destination = new NativeJavaScriptEngine();
        var decoded = StructuredCloneCodec.Decode(destination, payload, static (_, _) => ScriptValue.Undefined);
        Assert.True(destination.TryGetBytes(decoded.Value, out var bytes));
        Assert.Equal(new byte[] { 4, 8, 15 }, bytes);
    }

    [Fact]
    public void NativeEngineStructuredCloneRoundTripsErrors()
    {
        using var engine = new NativeJavaScriptEngine();
        var value = engine.Evaluate("new TypeError('bad worker data')");
        var payload = StructuredCloneCodec.Encode(engine, value, ScriptValue.Undefined, 1024 * 1024, static (_, _) => null);
        using var destination = new NativeJavaScriptEngine();
        var decoded = StructuredCloneCodec.Decode(destination, payload, static (_, _) => ScriptValue.Undefined);
        Assert.Equal("TypeError", destination.GetProperty(decoded.Value, "name").AsString());
        Assert.Equal("bad worker data", destination.GetProperty(decoded.Value, "message").AsString());
    }

    [Fact]
    public void NativeEngineWorkerTransfersArrayBuffersEndToEnd()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>worker</p>", new FolioOptions { Capabilities = new FeatureCapabilities { Workers = true } });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        engine.Evaluate("var response=''; var error=''; var worker=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(e){postMessage(e.data.byteLength);}')); worker.onmessage=function(e){response=''+e.data;}; worker.onerror=function(e){error=e.message;}; var buffer=new ArrayBuffer(16); worker.postMessage(buffer,{transfer:[buffer]});");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("response").AsString() != "" || engine.GetGlobalProperty("error").AsString() != ""; }, TimeSpan.FromSeconds(2)), engine.GetGlobalProperty("error").AsString());
        Assert.Equal("16", engine.GetGlobalProperty("response").AsString());
        Assert.Equal("", engine.GetGlobalProperty("error").AsString());
        Assert.False(engine.TryGetArrayBufferBytes(engine.GetGlobalProperty("buffer"), out _));
    }

    [Fact]
    public void NativeEngineWorkerWatchdogInterruptsBusyLoopWithoutBlockingThePage()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>worker</p>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = TimeSpan.FromMilliseconds(50) }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        engine.Evaluate("var error=''; var worker=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(){while(true){}}')); worker.onerror=function(e){error=e.message;}; worker.postMessage(0);");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("error").AsString() != ""; }, TimeSpan.FromSeconds(2)), "native worker watchdog did not report an error");
        Assert.NotEmpty(engine.GetGlobalProperty("error").AsString() ?? "");
        Assert.Equal(2, engine.Evaluate("1 + 1").AsNumber());
    }

    [Fact]
    public void NativeEngineWorkerWebAssemblyHonorsExecutionDeadline()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>worker</p>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true, WebAssembly = true },
            Limits = ResourceLimits.Default with { MaxWorkerExecutionTime = TimeSpan.FromMilliseconds(100) }
        });
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        const string runaway = "[0,97,115,109,1,0,0,0,1,4,1,96,0,0,3,2,1,0,7,7,1,3,114,117,110,0,0,10,9,1,7,0,3,64,12,0,11,11]";
        engine.Evaluate($"var error=''; var worker=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=()=>{{var m=new WebAssembly.Module(new Uint8Array({runaway})); var i=new WebAssembly.Instance(m,{{}}); i.exports.run(); postMessage(\"unexpected\");}}')); worker.onerror=function(e){{error=e.message;}}; worker.postMessage(0);");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("error").AsString() != ""; }, TimeSpan.FromSeconds(2)), "native WebAssembly worker watchdog did not report an error");
        Assert.Contains("execution", engine.GetGlobalProperty("error").AsString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeEngineMessageChannelRoundTripsStructuredData()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Document.Parse("<p>message channel</p>", new FolioOptions());
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        ScriptGlobalsInstaller.Install(loop, engine);
        engine.Evaluate("var response=''; var channel=new MessageChannel(); channel.port1.addEventListener('message',function(e){response=e.data.n+':'+e.data.values[1];}); channel.port1.start(); channel.port2.postMessage({n:41,values:[7,42]});");
        Assert.True(SpinWait.SpinUntil(() => { loop.SpinUntilIdle(); return engine.GetGlobalProperty("response").AsString() != ""; }, TimeSpan.FromSeconds(2)));
        Assert.Equal("41:42", engine.GetGlobalProperty("response").AsString());
    }



    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerFetchWorksIndependentlyOfWebAssemblyGrant(bool wasm)
    {
        using var fixture = new Installed(Grants(wasm), parentLoop: true);
        fixture.Start("onmessage=()=>fetch('data:text/plain,%E2%98%83').then(r=>r.text()).then(postMessage);");
        Assert.Equal("☃", fixture.Post());
    }

    [Fact]
    public void WorkerMessagesUseEventTargetOnceAbortAndTrustedMessageEvents()
    {
        using var fixture = new Installed(Grants(false), parentLoop: true);
        fixture.Start("var seen=0,aborted=new AbortController();addEventListener('message',()=>seen+=100,{signal:aborted.signal});aborted.abort();addEventListener('message',()=>seen++,{once:true});onmessage=e=>postMessage([seen,e instanceof MessageEvent,e.isTrusted,e.target===self,e.currentTarget===self,e.data].join(':'));");
        Assert.Equal("1:true:true:true:true:0", fixture.Post());
        Assert.Equal("1:true:true:true:true:0", fixture.Post());
    }

    [Fact]
    public void WorkerCanDispatchEventsAndAbortOrdinaryEventTargets()
    {
        using var fixture = new Installed(Grants(false));
        fixture.Start("onmessage=()=>{var target=new EventTarget(),controller=new AbortController(),count=0;target.addEventListener('x',()=>count++,{signal:controller.signal});target.dispatchEvent(new Event('x'));controller.abort();target.dispatchEvent(new Event('x'));var selfCount=0;addEventListener('custom',()=>selfCount++,{once:true});dispatchEvent(new Event('custom'));dispatchEvent(new Event('custom'));postMessage(count+':'+selfCount);};");
        Assert.Equal("1:1", fixture.Post());
    }

    [Fact]
    public void WorkerNavigatorExposesMetadataWithoutWindowDeviceApis()
    {
        using var fixture = new Installed(Grants(false));
        fixture.Start("onmessage=()=>postMessage([Object.prototype.toString.call(navigator),navigator.userAgent,navigator.hardwareConcurrency>=1,typeof navigator.language==='string',Object.isFrozen(navigator.languages),typeof navigator.mediaDevices].join(':'));");
        Assert.Equal("[object WorkerNavigator]:Folio:true:true:true:undefined", fixture.Post());
    }

    [Fact]
    public void WorkerConsoleReachesDocumentDiagnostics()
    {
        using var fixture = new Installed(Grants(false), parentLoop: true);
        fixture.Start("onmessage=()=>{console.warn('worker',3);postMessage('sent');};");
        Assert.Equal("sent", fixture.Post());
        var diagnostic = Assert.Single(fixture.Runner.Diagnostics, d => d.Code == DiagnosticCode.ScriptConsole);
        Assert.StartsWith("Worker (data:text/javascript,", diagnostic.Message);
        Assert.EndsWith(": worker 3", diagnostic.Message);
        Assert.Equal(Severity.Warning, diagnostic.Severity);
    }

    [Fact]
    public void WorkerClosingItselfDoesNotDiscardAlreadyReportedConsoleMessages()
    {
        using var fixture = new Installed(Grants(false), parentLoop: true);
        fixture.Start("onmessage=()=>{console.warn('before close');close();};");
        fixture.Engine.Evaluate("worker.postMessage(0);");
        fixture.Until(() => fixture.Worker().ResourcesReleased);
        fixture.Loop.SpinUntilIdle();
        Assert.Contains(fixture.Runner.Diagnostics, d => d.Code == DiagnosticCode.ScriptConsole && d.Message.EndsWith(": before close", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkerCanImportAndRevokeItsOwnBlobScript()
    {
        using var fixture = new Installed(Grants(false));
        fixture.Start("var url=URL.createObjectURL(new Blob(['globalThis.loaded=\"☃\";']));importScripts(url);URL.revokeObjectURL(url);onmessage=()=>{var revoked=false;try{importScripts(url);}catch(e){revoked=true;}postMessage(loaded+':'+revoked);};");
        Assert.Equal("☃:true", fixture.Post());
    }

    [Fact]
    public void WorkerCanStartNestedWorkerFromItsOwnBlob()
    {
        using var fixture = new Installed(Grants(false), parentLoop: true);
        fixture.Start("var url=URL.createObjectURL(new Blob(['onmessage=()=>postMessage(\"☃\");']));var nested=new Worker(url);nested.onmessage=e=>postMessage(e.data);onmessage=()=>nested.postMessage(0);");
        Assert.Equal("☃", fixture.Post());
    }

    [Fact]
    public void WorkerFetchUsesHostBrokerAndWorkerScriptBase()
    {
        var loader = new FetchLoader();
        using var fixture = new Installed(Grants(false), parentLoop: true, loader: loader);
        fixture.StartUrl("https://worker.example/scripts/start.js");
        Assert.Equal("host reply", fixture.Post());
        var request = Assert.Single(loader.Requests, r => r.Kind == ResourceKind.Fetch);
        Assert.Equal("https://worker.example/data", request.Url);
    }

    [Fact]
    public void WorkerFetchCannotReadLocalFilesAndReportsOnParentThread()
    {
        using var fixture = new Installed(Grants(false), parentLoop: true);
        fixture.Start("onmessage=()=>fetch('file:///C:/secret').then(()=>postMessage('allowed'),e=>postMessage(e.name));");
        Assert.Equal("TypeError", fixture.Post());
        Assert.Contains(fixture.Runner.Diagnostics, d => d.Code == DiagnosticCode.ResourceNotLoaded && d.Feature == "fetch");
    }

    private sealed class FetchLoader : IResourceLoader
    {
        public System.Collections.Concurrent.ConcurrentQueue<ResourceRequest> Requests { get; } = new();
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(request.Kind == ResourceKind.Script
                ? "onmessage=()=>fetch('../data').then(r=>r.text()).then(postMessage);" : "host reply"), "text/plain"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WebAssemblyGrantDoesNotInstallWindowApisInWorker(bool wasm)
    {
        using var fixture = new Installed(Grants(wasm));
        fixture.Start("onmessage=()=>postMessage([typeof WebAssembly,typeof document,typeof window,typeof RTCPeerConnection,typeof navigator==='undefined'||typeof navigator.mediaDevices==='undefined',typeof MouseEvent,typeof UIEvent,typeof onbeforeunload].join(':'));");
        Assert.Equal("object:undefined:undefined:undefined:true:undefined:undefined:undefined", fixture.Post());
    }

    [Fact]
    public void ExistingInitiallyDeniedWorkerReceivesLaterGrant()
    {
        using var fixture = new Installed(Grants(false));
        fixture.Start("var retained=typeof WebAssembly==='undefined'?null:WebAssembly; onmessage=()=>{try{postMessage(new retained.Memory({initial:1}).buffer.byteLength);}catch(error){postMessage(error.name);}};");
        DomBindings.UpdateCapabilities(fixture.Engine, Grants());
        Assert.Equal("65536", fixture.Post());
    }

    [Theory]
    [InlineData(false, "NotSupportedError")]
    [InlineData(true, "RangeError")]
    public void WorkerCannotGrantItselfAccessOrRaiseItsMemoryLimit(bool wasm, string expected)
    {
        using var fixture = new Installed(Grants(wasm));
        fixture.Start("__folio_wasm_enabled=true;__folio_wasm_max_pages=100;onmessage=()=>{try{new WebAssembly.Memory({initial:2});postMessage('allowed');}catch(error){postMessage(error.name);}};");
        Assert.Equal(expected, fixture.Post());
    }

    [Fact]
    public void DeniedWorkerConstructionReportsOneFeatureDiagnostic()
    {
        using var fixture = new Installed(FeatureCapabilities.Default);
        for (var i = 0; i < 2; i++)
            Assert.Equal("NotSupportedError", fixture.Engine.Evaluate("try{new Worker('data:text/javascript,');'allowed';}catch(error){error.name;}").AsString());
        Assert.Equal("Workers", Assert.Single(fixture.Runner.Diagnostics, d => d.Code == DiagnosticCode.CapabilityDenied).Feature);
    }

    [Fact]
    public void RetainedWorkerConstructorCannotCreateWorkAfterDocumentDisposal()
    {
        using var fixture = new Installed(Grants());
        fixture.Engine.Evaluate("var retainedWorker=Worker;");
        fixture.Runner.Dispose();
        Assert.Equal("NotSupportedError", fixture.Engine.Evaluate("try{new retainedWorker('data:text/javascript,');'allowed';}catch(error){error.name;}").AsString());
    }

    [Fact]
    public void ReplacingDocumentBindingsRetiresExistingWorkersEvenIfReinstallFails()
    {
        using var fixture = new Installed(Grants());
        fixture.Start("onmessage=()=>postMessage('old');");
        var worker = fixture.Worker();
        using var replacement = Document.Parse("<p>replacement</p>");
        try { DomBindings.Install(fixture.Engine, replacement); }
        catch (ScriptRuntimeException) { /* Same-engine bootstrap may reject nonconfigurable Window properties. */ }
        Assert.True(worker.Terminated);
        Assert.True(SpinWait.SpinUntil(() => worker.ResourcesReleased, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void MissingRemoteLoaderFailsInsteadOfEchoingWorkerMessages()
    {
        using var fixture = new Installed(Grants(false), parentLoop: true);
        fixture.StartUrl("https://worker.example/missing.js");
        fixture.Until(() => fixture.Worker().Terminated && fixture.Engine.Evaluate("errors.length>0").AsBoolean());
        fixture.Engine.Evaluate("worker.postMessage('never echoed');");
        fixture.Loop.SpinUntilIdle();
        Assert.Equal(0, fixture.Engine.Evaluate("responses.length").AsNumber());
    }

    [Theory]
    [InlineData("new retained.Memory({initial:1})")]
    [InlineData("memory.grow(0)")]
    [InlineData("table.grow(0)")]
    [InlineData("table.get(0)")]
    [InlineData("table.set(0,null)")]
    [InlineData("new retained.Module(bytes)")]
    [InlineData("instance.exports.add(2,3)")]
    public void RetainedWorkerApiObservesRevocationAndRegrant(string operation)
    {
        using var fixture = new Installed(Grants());
        fixture.Start($"var retained=WebAssembly,bytes=new Uint8Array({AddBytes}); var memory=new retained.Memory({{initial:1}}),table=new retained.Table({{initial:1}}),module=new retained.Module(bytes),instance=new retained.Instance(module,{{}}); onmessage=()=>{{try{{{operation};postMessage('allowed');}}catch(error){{postMessage(error.name);}}}};");
        Assert.Equal("allowed", fixture.Post());
        DomBindings.UpdateCapabilities(fixture.Engine, Grants(false));
        Assert.Equal("NotSupportedError", fixture.Post());
        Assert.False(fixture.Worker().Terminated);
        DomBindings.UpdateCapabilities(fixture.Engine, Grants());
        Assert.Equal("allowed", fixture.Post());
    }

    [Fact]
    public void UpdateReachesEveryExistingWorker()
    {
        using var fixture = new Installed(Grants());
        const string source = "var retained=WebAssembly; onmessage=()=>{try{new retained.Memory({initial:1});postMessage('allowed');}catch(error){postMessage(error.name);}};";
        fixture.Start(source, "first");
        fixture.Start(source, "second");
        DomBindings.UpdateCapabilities(fixture.Engine, Grants(false));
        Assert.Equal("NotSupportedError", fixture.Post("first"));
        Assert.Equal("NotSupportedError", fixture.Post("second"));
    }

    [Fact]
    public void RevokingWorkersTerminatesExistingWorkerAndReleasesItsSlot()
    {
        using var fixture = new Installed(Grants(), maxWorkers: 1);
        fixture.Start("onmessage=()=>postMessage('alive');");
        var retired = fixture.Worker();
        DomBindings.UpdateCapabilities(fixture.Engine, FeatureCapabilities.Default);
        Assert.True(retired.Terminated);
        Assert.True(retired.ResourcesReleased);
        DomBindings.UpdateCapabilities(fixture.Engine, Grants());
        fixture.Start("onmessage=()=>postMessage('fresh');", "replacement");
        Assert.Equal("fresh", fixture.Post("replacement"));
        Assert.True(retired.Terminated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkerDenialsAreDeduplicatedAndDeliveredOnDocumentOwner(bool parentLoop)
    {
        using var fixture = new Installed(Grants(false), parentLoop);
        var ownerThread = Environment.CurrentManagedThreadId;
        var original = fixture.Document.Node.DiagnosticSink;
        fixture.Document.Node.DiagnosticSink = diagnostic =>
        {
            Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
            original!(diagnostic);
        };
        const string source = "onmessage=()=>{try{new WebAssembly.Memory({initial:1});}catch(error){postMessage(error.name);}};";
        fixture.Start(source, "first");
        fixture.Start(source, "second");
        Assert.Equal("NotSupportedError", fixture.Post("first"));
        Assert.Equal("NotSupportedError", fixture.Post("first"));
        Assert.Equal("NotSupportedError", fixture.Post("second"));
        var diagnostic = Assert.Single(fixture.Runner.Diagnostics, d => d.Code == DiagnosticCode.CapabilityDenied);
        Assert.Equal("WebAssembly", diagnostic.Feature);
    }

    [Fact]
    public void ActiveWorkerExportObservesRevocationWithoutStoppingWorker()
    {
        using var fixture = new Installed(Grants(), parentLoop: true);
        // A valid module re-exports env.run. Its import remains active until the live grant changes.
        const string importedModule = "[0,97,115,109,1,0,0,0,1,4,1,96,0,0,2,11,1,3,101,110,118,3,114,117,110,0,0,7,7,1,3,114,117,110,0,0]";
        fixture.Start($"var empty=new Uint8Array([0,97,115,109,1,0,0,0]); var retained=WebAssembly; var instance=new retained.Instance(new retained.Module(new Uint8Array({importedModule})),{{env:{{run:()=>{{postMessage('started');while(retained.validate(empty)){{}}}}}}}}); setTimeout(()=>{{try{{instance.exports.run();postMessage('unexpected return');}}catch(error){{postMessage(error.name);}}}},100); onmessage=()=>{{try{{new retained.Memory({{initial:1}});postMessage('alive');}}catch(error){{postMessage(error.name);}}}};");
        fixture.Until(() => fixture.Engine.Evaluate("responses.includes('started')").AsBoolean());
        DomBindings.UpdateCapabilities(fixture.Engine, Grants(false));
        fixture.Until(() => fixture.Engine.Evaluate("responses.includes('NotSupportedError')").AsBoolean());
        Assert.False(fixture.Worker().Terminated);
        DomBindings.UpdateCapabilities(fixture.Engine, Grants());
        Assert.Equal("alive", fixture.Post());
        Assert.DoesNotContain("unexpected return", fixture.Engine.Evaluate("responses.join('|')").AsString());
    }

    [Fact]
    public void NativeWebAssemblyWorkerHonorsExecutionDeadline()
    {
        using var fixture = new Installed(Grants(), parentLoop: true, maxWorkerExecution: TimeSpan.FromMilliseconds(100));
        const string runaway = "[0,97,115,109,1,0,0,0,1,4,1,96,0,0,3,2,1,0,7,7,1,3,114,117,110,0,0,10,9,1,7,0,3,64,12,0,11,11]";
        fixture.Start($"onmessage=()=>{{var m=new WebAssembly.Module(new Uint8Array({runaway})); var i=new WebAssembly.Instance(m,{{}}); i.exports.run(); postMessage('unexpected');}};");
        fixture.Engine.Evaluate("worker.postMessage(0);");
        fixture.Until(() => fixture.Engine.Evaluate("errors.length > 0").AsBoolean());
        Assert.Contains("execution", fixture.Engine.Evaluate("errors[0]").AsString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CapabilityChangeDuringStartupIsNotLost(bool useDuringStartup)
    {
        var loader = new StartupLoader();
        var installed = new TaskCompletionSource<Installed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = Task.Run(() =>
        {
            using var fixture = new Installed(Grants(), loader: loader);
            installed.SetResult(fixture);
            fixture.StartUrl("https://worker.example/start.js");
            return fixture.Post();
        }, TestContext.Current.CancellationToken);
        try
        {
            await loader.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            DomBindings.UpdateCapabilities((await installed.Task).Engine, Grants(false));
            const string probe = "try{new WebAssembly.Memory({initial:1});result='allowed';}catch(error){result=error.name;}";
            var source = useDuringStartup
                ? "var result;" + probe + "onmessage=()=>postMessage(result);"
                : "onmessage=()=>{var result;" + probe + "postMessage(result);};";
            loader.Release(source);
            Assert.Equal("NotSupportedError", await scenario.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            loader.Release("onmessage=()=>postMessage('cleanup');");
            await scenario.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerRevocationCancelsItsPendingStartupLoad(bool module)
    {
        var loader = new StartupLoader();
        var installed = new TaskCompletionSource<Installed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = Task.Run(() =>
        {
            using var fixture = new Installed(Grants(), loader: loader);
            installed.SetResult(fixture);
            fixture.StartUrl("https://worker.example/start.js", module);
            return fixture.Worker().Terminated;
        }, TestContext.Current.CancellationToken);
        try
        {
            await loader.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            DomBindings.UpdateCapabilities((await installed.Task).Engine, FeatureCapabilities.Default);
            await loader.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(await scenario.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            loader.Release("");
            await scenario.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    private sealed class StartupLoader : IResourceLoader
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ResourceResponse?> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release(string source) => _response.TrySetResult(ResourceResponse.Loaded(Encoding.UTF8.GetBytes(source), "application/javascript"));
        public async Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            Requested.TrySetResult();
            try { return await _response.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Canceled.TrySetResult();
                throw;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStartupReleasesWorkerSlotAndReportsError(bool module)
    {
        using var fixture = new Installed(Grants(), parentLoop: true, maxWorkers: 1, loader: new MissingLoader(module));
        fixture.StartUrl("https://worker.example/start.js", module);
        fixture.Until(() => fixture.Worker().Terminated && fixture.Engine.Evaluate("errors.length > 0").AsBoolean());
        Assert.True(SpinWait.SpinUntil(() => fixture.Worker().ResourcesReleased, TimeSpan.FromSeconds(2)));
        Assert.Equal("dropped", fixture.Engine.Evaluate("worker.postMessage(0);'dropped';").AsString());
        fixture.Start("onmessage=()=>postMessage('fresh');", "replacement");
        Assert.Equal("fresh", fixture.Post("replacement"));
    }

    [Fact]
    public void InsufficientWorkerMemoryReportsErrorAndReleasesSlot()
    {
        using var fixture = new Installed(Grants(), parentLoop: true, maxWorkers: 1, maxWorkerMemory: 1);
        fixture.Start("onmessage=()=>postMessage('unreachable');");
        fixture.Until(() => fixture.Worker().Terminated && fixture.Engine.Evaluate("errors.length > 0").AsBoolean());
        Assert.True(SpinWait.SpinUntil(() => fixture.Worker().ResourcesReleased, TimeSpan.FromSeconds(2)));
        Assert.Throws<ObjectDisposedException>(() => fixture.Worker().WorkerEngine!.Evaluate("1"));
        fixture.Start("onmessage=()=>postMessage('unreachable');", "replacement");
        fixture.Until(() => fixture.Worker("replacement").Terminated);
    }

    [Fact]
    public void AggregateWorkerMemoryBudgetRejectsAndReleasesWholeWorkerReservations()
    {
        using var fixture = new Installed(Grants(), maxWorkers: 2, maxWorkerMemory: 1 * 1024 * 1024, maxAggregateWorkerMemory: 1 * 1024 * 1024);
        fixture.Start("onmessage=()=>postMessage('first');", "first");
        fixture.Engine.Evaluate("var secondError=''; try { second = new Worker('data:text/javascript,'+encodeURIComponent(\"onmessage=()=>postMessage('second')\")); } catch(e) { secondError=e.name; }");
        Assert.Equal("QuotaExceededError", fixture.Engine.Evaluate("secondError").AsString());
        fixture.Engine.Evaluate("first.terminate();");
        Assert.True(SpinWait.SpinUntil(() => fixture.Worker("first").ResourcesReleased, TimeSpan.FromSeconds(2)));
        fixture.Start("onmessage=()=>postMessage('second');", "second");
        Assert.False(fixture.Worker("second").Terminated);
    }

    private sealed class MissingLoader(bool module) : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(module && request.Url.EndsWith("/start.js", StringComparison.Ordinal)
                ? ResourceResponse.Loaded(Encoding.UTF8.GetBytes("import './missing.js'; onmessage=()=>postMessage('should not run');"), "application/javascript")
                : null);
    }

    private sealed class Installed : IDisposable
    {
        public JintScriptEngine Engine { get; } = new();
        public Document Document { get; }
        public HtmlEventLoop Loop { get; }
        public ScriptRunner Runner { get; }
        private readonly bool _parentLoop;

        public Installed(FeatureCapabilities capabilities, bool parentLoop = false, int maxWorkers = 8, IResourceLoader? loader = null, long maxWorkerMemory = 64L * 1024 * 1024, long? maxAggregateWorkerMemory = null, TimeSpan? maxWorkerExecution = null)
        {
            _parentLoop = parentLoop;
            var options = new FolioOptions
            {
                Capabilities = capabilities,
                ResourceLoader = loader,
                Limits = ResourceLimits.Default with { MaxWorkers = maxWorkers, MaxWebAssemblyBytes = 65536, MaxWorkerMemoryBytes = maxWorkerMemory, MaxAggregateWorkerMemoryBytes = maxAggregateWorkerMemory ?? ResourceLimits.Default.MaxAggregateWorkerMemoryBytes, MaxWorkerExecutionTime = maxWorkerExecution ?? ResourceLimits.Default.MaxWorkerExecutionTime }
            };
            Document = Document.Parse("<p>worker capability test</p>", options);
            Loop = new HtmlEventLoop(Engine);
            DomBindings.Install(Engine, Document, eventLoop: parentLoop ? Loop : null);
            if (parentLoop) ScriptGlobalsInstaller.Install(Loop, Engine);
            Runner = new ScriptRunner(Document, Engine, Loop, options);
            Engine.Evaluate("var responses=[],errors=[];");
        }

        public void Start(string source, string name = "worker") => Engine.Evaluate(
            $"var {name}=new Worker('data:text/javascript,'+encodeURIComponent({JsonSerializer.Serialize(source)})); {name}.onmessage=e=>responses.push(String(e.data)); {name}.onerror=e=>errors.push(e.message);");

        public void StartUrl(string url, bool module = false) => Engine.Evaluate(
            $"var worker=new Worker({JsonSerializer.Serialize(url)},{{type:'{(module ? "module" : "classic")}'}}); worker.onmessage=e=>responses.push(String(e.data)); worker.onerror=e=>errors.push(e.message);");

        public string Post(string name = "worker")
        {
            var previous = Engine.Evaluate("responses.length").AsNumber();
            Engine.Evaluate($"{name}.postMessage(0)");
            Until(() => Engine.Evaluate("responses.length").AsNumber() > previous);
            return Engine.Evaluate("responses[responses.length-1]").AsString()!;
        }

        public WorkerState Worker(string name = "worker")
        {
            Assert.True(Engine.TryGetHostTarget(Engine.Evaluate(name), out var target));
            return Assert.IsType<WorkerState>(target);
        }

        public void Until(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!condition() && DateTime.UtcNow < deadline)
            {
                if (_parentLoop) Loop.SpinUntilIdle();
                Thread.Yield();
            }
            Assert.True(condition(), Engine.Evaluate("errors.join('|')").AsString());
        }

        public void Dispose()
        {
            Runner.Dispose();
            Loop.Dispose();
            Engine.Dispose();
            Document.Dispose();
        }
    }
}

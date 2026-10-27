using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>Shared-worker construction and connection routing.</summary>
internal static class SharedWorkerBindings
{
    internal static void Install(IScriptEngine engine, DomContext context, IHostObjectTemplate messageEventTemplate,
        IHostObjectTemplate messagePortTemplate, IResourceLoader? resourceLoader, Uri? baseUri,
        IReadOnlyDictionary<string, string>? blobSources, HtmlEventLoop? eventLoop)
    {
        var owner = new WeakReference<DomContext>(context);
        var endpointTemplate = new HostObjectTemplate("SharedWorker")
            .DefineProperty("port", (target, _) => ((SharedWorkerEndpoint)target).Port)
            .DefineProperty("scriptURL", (target, _) => ScriptValue.FromString(((SharedWorkerEndpoint)target).ScriptUrl))
            .DefineProperty("name", (target, _) => ScriptValue.FromString(((SharedWorkerEndpoint)target).Name));
        var host = new HostObjectTemplate("SharedWorkerHost")
            .DefineMethod("create", (_, _, args) =>
            {
                if (!owner.TryGetTarget(out var root))
                    throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Shared workers are disabled by the host.");
                root.EnterDocumentTurn(engine);
                if (!root.CheckCapability("Workers", root.Capabilities.Workers))
                    throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Shared workers are disabled by the host.");
                var url = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : string.Empty;
                var name = string.Empty;
                var type = "classic";
                if (args.Length > 1 && args[1].IsObject)
                {
                    name = engine.GetProperty(args[1], "name").AsString() ?? string.Empty;
                    type = engine.GetProperty(args[1], "type").AsString() ?? "classic";
                }
                else if (args.Length > 1 && !args[1].IsNullOrUndefined)
                    name = WebIdlConversions.ToDOMString(args[1]);
                if (type is not ("classic" or "module"))
                    throw WebIdlException.TypeError("SharedWorker options.type must be 'classic' or 'module'.");
                if (baseUri is not null && Uri.TryCreate(baseUri, url, out var resolved)) url = resolved.AbsoluteUri;
                var key = url + "\0" + name;
                if (!root.TryGetSharedWorker(key, out var worker))
                {
                    if (!root.TryAcquireWorker())
                        throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The worker limit has been exceeded.");
                    if (!root.TryAcquireWorkerMemory())
                    {
                        root.ReleaseWorker();
                        throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The aggregate worker memory limit has been exceeded.");
                    }
                    IScriptEngine? isolated = null;
                    var released = 0;
                    var memoryReleased = 0;
                    void ReleaseSlot() { if (Interlocked.Exchange(ref released, 1) == 0) root.ReleaseWorker(); }
                    void ReleaseMemory() { if (Interlocked.Exchange(ref memoryReleased, 1) == 0) root.ReleaseWorkerMemory(); }
                    try
                    {
                        isolated = (engine as IScriptEngineFactory)?.CreateIsolated()
                            ?? throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "This engine cannot create an isolated shared worker.");
                        worker = new WorkerState(engine, messageEventTemplate, messagePortTemplate, url, true,
                            root.MaxWorkerMessageBytes, isolated, resourceLoader, root.MaxWorkerMemoryBytes,
                            root.MaxWebAssemblyBytes, root.Capabilities, root.MaxWorkerExecutionTime, ReleaseSlot, ReleaseMemory,
                            type == "module", blobSources, eventLoop, root,
                            state => root.RegisterWorker(state), state => { root.UnregisterWorker(state); root.UnregisterSharedWorker(state); });
                        root.RegisterSharedWorker(key, worker);
                    }
                    catch
                    {
                        worker?.Terminate();
                        if (worker is null) { ReleaseSlot(); ReleaseMemory(); isolated?.Dispose(); }
                        throw;
                    }
                }

                var pair = MessagePortFactory.Create(messageEventTemplate, messagePortTemplate, engine, eventLoop,
                    root.MaxWorkerMessageBytes, root.MaxWorkerMemoryBytes);
                var transfer = StructuredCloneCodec.NormalizeTransfer(engine,
                    engine.CreateArray([pair.Second.GetWrapper()]), postMessage: false);
                var prepared = StructuredCloneCodec.Prepare(engine, ScriptValue.Undefined, transfer,
                    root.MaxWorkerMessageBytes, MessagePortEndpoint.TryGetPort);
                var payload = prepared.Commit();
                try { worker.ConnectSharedPort(payload); }
                catch { payload.AbortTransfers(); pair.First.Close(); throw; }
                return engine.CreateHostObject(endpointTemplate, new SharedWorkerEndpoint(
                    pair.First.GetWrapper(), url, name));
            });
        engine.SetGlobalProperty("__folio_shared_worker_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript("""
            (function(host) {
                const TypeErrorCtor = TypeError;
                globalThis.SharedWorker = function(scriptURL, options) {
                    if (!new.target) throw new TypeErrorCtor('SharedWorker requires new');
                    const worker = host.create(scriptURL, options);
                    Object.defineProperty(worker, Symbol.toStringTag, { value: 'SharedWorker', configurable: true });
                    return worker;
                };
            })(__folio_shared_worker_host);
            delete globalThis.__folio_shared_worker_host;
            """, "folio:shared-worker-constructor");
    }

    private sealed class SharedWorkerEndpoint(ScriptValue port, string scriptUrl, string name)
    {
        public ScriptValue Port { get; } = port;
        public string ScriptUrl { get; } = scriptUrl;
        public string Name { get; } = name;
    }
}

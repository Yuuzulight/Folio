using Folio.Resources;

namespace Folio.Scripting.Dom;

/// <summary>Dedicated-worker construction shared by documents and worker scopes.</summary>
internal static class WorkerBindings
{
    internal static void Install(IScriptEngine engine, DomContext rootContext, IHostObjectTemplate messageEventTemplate,
        IHostObjectTemplate messagePortTemplate, IResourceLoader? resourceLoader, Uri? baseUri,
        IReadOnlyDictionary<string,string>? blobSources, HtmlEventLoop? eventLoop, Action<WorkerState>? registerChild = null,
        Action<WorkerState>? unregisterChild = null)
    {
        var owner = new WeakReference<DomContext>(rootContext);
        var workerTemplate = new HostObjectTemplate("Worker")
            .DefineProperty("onmessage", (target, _) => ((WorkerState)target).OnMessage, (target, _, value) => ((WorkerState)target).OnMessage = value)
            .DefineProperty("onmessageerror", (target, _) => ((WorkerState)target).OnMessageError, (target, _, value) => ((WorkerState)target).OnMessageError = value)
            .DefineProperty("onerror", (target, _) => ((WorkerState)target).OnError, (target, _, value) => ((WorkerState)target).OnError = value)
            .DefineProperty("scriptURL", (target, _) => ScriptValue.FromString(((WorkerState)target).ScriptUrl))
            .DefineMethod("addEventListener", (target, _, args) => { if (args.Length > 1) ((WorkerState)target).AddEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; })
            .DefineMethod("removeEventListener", (target, _, args) => { if (args.Length > 1) ((WorkerState)target).RemoveEventListener(WebIdlConversions.ToDOMString(args[0]), args[1]); return ScriptValue.Undefined; })
            .DefineMethod("postMessage", (target, _, args) =>
            {
                if (((WorkerState)target).Terminated && !((WorkerState)target).HasFailure)
                    throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The worker has been terminated.");
                if (args.Length > 0 && args[0].IsFunction)
                    throw WebIdlException.DomException("DataCloneError", "Functions cannot be cloned into a worker.");
                if (args.Length > 0 && !((WorkerState)target).PostMessage(args[0], args.Length > 1 ? args[1] : ScriptValue.Undefined))
                    throw WebIdlException.DomException("DataCloneError", "The worker message exceeds the host limit.");
                return ScriptValue.Undefined;
            })
            .DefineMethod("terminate", (target, _, _) => { ((WorkerState)target).Terminate(); return ScriptValue.Undefined; });
        var host = new HostObjectTemplate("WorkerHost")
            .DefineMethod("create", (_, _, args) =>
            {
                if (!owner.TryGetTarget(out var context))
                    throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Workers are disabled by the host.");
                context.EnterDocumentTurn(engine);
                if (!context.CheckCapability("Workers", context.Capabilities.Workers))
                    throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "Workers are disabled by the host.");
                context.DrainWorkerDiagnostics();
                var url = args.Length > 0 ? WebIdlConversions.ToDOMString(args[0]) : "";
                var workerType = args.Length > 1 && args[1].IsObject ? engine.GetProperty(args[1], "type").AsString() ?? "classic" : "classic";
                if (workerType is not ("classic" or "module")) throw WebIdlException.TypeError("Worker options.type must be 'classic' or 'module'.");
                if (baseUri is not null && Uri.TryCreate(baseUri, url, out var resolved)) url = resolved.AbsoluteUri;
                if (!context.TryAcquireWorker()) throw WebIdlException.DomException(DomExceptionNames.QuotaExceededError, "The worker limit has been exceeded.");
                IScriptEngine? isolated = null;
                WorkerState? state = null;
                var released = 0;
                void ReleaseSlot() { if (Interlocked.Exchange(ref released, 1) == 0) context.ReleaseWorker(); }
                try
                {
                    isolated = (engine as IScriptEngineFactory)?.CreateIsolated()
                        ?? throw WebIdlException.DomException(DomExceptionNames.NotSupportedError, "This engine cannot create an isolated worker.");
                    void Register(WorkerState state) { context.RegisterWorker(state); registerChild?.Invoke(state); }
                    state = new WorkerState(engine, messageEventTemplate, messagePortTemplate, url, true, context.MaxWorkerMessageBytes,
                        isolated, resourceLoader, context.MaxWorkerMemoryBytes, context.MaxWebAssemblyBytes, context.Capabilities,
                        context.MaxWorkerExecutionTime, ReleaseSlot, workerType == "module", blobSources, eventLoop, context, Register, unregisterChild);
                    var wrapper = engine.CreateHostObject(workerTemplate, state);
                    state.Source = wrapper;
                    return wrapper;
                }
                catch
                {
                    if (state is not null) state.Terminate();
                    else { ReleaseSlot(); isolated?.Dispose(); }
                    throw;
                }
            });
        engine.SetGlobalProperty("__folio_worker_host", engine.CreateHostObject(host, new object()));
        engine.EvaluateHostScript("(function(host){globalThis.Worker=function(scriptURL,options){return host.create(scriptURL,options);};})(__folio_worker_host);delete globalThis.__folio_worker_host;", "folio:worker-constructor");
    }
}

namespace Folio.Scripting.Dom;

internal static class MessagePortBindings
{
    private static MessagePortEndpoint Port(object target) => target as MessagePortEndpoint
        ?? throw WebIdlException.TypeError("Illegal MessagePort invocation.");

    internal static IHostObjectTemplate Install(IScriptEngine engine, IHostObjectTemplate messageEventTemplate,
        HtmlEventLoop? eventLoop, int maxMessageBytes, long maxPendingBytes)
    {
        MessagePortEndpoint.Initialize(engine, maxPendingBytes);
        StructuredCloneCodec.EnsureHelpers(engine);
        var template = new HostObjectTemplate("MessagePort")
            .DefineProperty("onmessage", (target, _) => Port(target).OnMessage,
                (target, _, value) => Port(target).SetOnMessage(value))
            .DefineProperty("onmessageerror", (target, _) => Port(target).OnMessageError,
                (target, _, value) => Port(target).SetOnMessageError(value))
            .DefineProperty("closed", (target, _) => ScriptValue.FromBoolean(Port(target).Closed))
            .DefineMethod("start", (target, _, _) => { Port(target).Start(); return ScriptValue.Undefined; })
            .DefineMethod("close", (target, _, _) => { Port(target).Close(); return ScriptValue.Undefined; })
            .DefineMethod("addEventListener", (target, _, args) =>
            {
                if (args.Length > 1)
                    Port(target).AddListener(WebIdlConversions.ToDOMString(args[0]), args[1]);
                return ScriptValue.Undefined;
            })
            .DefineMethod("removeEventListener", (target, _, args) =>
            {
                if (args.Length > 1)
                    Port(target).RemoveListener(WebIdlConversions.ToDOMString(args[0]), args[1]);
                return ScriptValue.Undefined;
            })
            .DefineMethod("postMessage", (target, _, args) =>
            {
                if (args.Length == 0) throw WebIdlException.TypeError("MessagePort.postMessage requires a message.");
                Port(target).PostMessage(args[0], args.Length > 1 ? args[1] : ScriptValue.Undefined);
                return ScriptValue.Undefined;
            });
        var factory = new HostObjectTemplate("MessagePortFactory")
            .DefineMethod("clone", (_, _, args) =>
            {
                var transfer = ScriptValue.Undefined;
                if (args.Length > 1 && !args[1].IsNullOrUndefined)
                {
                    if (!args[1].IsObject) throw WebIdlException.TypeError("structuredClone options must be an object.");
                    transfer = engine.GetProperty(args[1], "transfer");
                }
                transfer = StructuredCloneCodec.NormalizeTransfer(engine, transfer, postMessage: false);
                var prepared = StructuredCloneCodec.Prepare(engine, args[0], transfer,
                    (int)Math.Min(int.MaxValue, Math.Max(0, maxPendingBytes)), MessagePortEndpoint.TryGetPort);
                using var reservation = ScriptMessageBudget.For(engine, maxPendingBytes).Reserve(prepared.Payload.MemoryCost);
                var payload = prepared.Commit();
                return StructuredCloneCodec.Decode(engine, payload,
                    (port, destination) => port.ClaimTransfer(destination, eventLoop, maxMessageBytes).GetWrapper()).Value;
            })
            .DefineMethod("create", (_, _, _) =>
            {
                var pair = MessagePortFactory.Create(messageEventTemplate, template, engine, eventLoop, maxMessageBytes, maxPendingBytes);
                return engine.CreateArray([
                    pair.First.GetWrapper(),
                    pair.Second.GetWrapper()
                ]);
            });
        engine.SetGlobalProperty("__folio_message_port_factory", engine.CreateHostObject(factory, new object()));
        engine.EvaluateHostScript("""
            (function(host) {
                const defineProperty = Object.defineProperty;
                const create = Object.create;
                function property(value) { const descriptor = create(null); descriptor.value = value; descriptor.enumerable = true; return descriptor; }
                const TypeErrorCtor = TypeError;
                function MessagePort() { throw new TypeErrorCtor('Illegal constructor'); }
                function MessageChannel() {
                    if (!new.target) throw new TypeErrorCtor('MessageChannel requires new');
                    const pair = host.create();
                    defineProperty(this, 'port1', property(pair[0]));
                    defineProperty(this, 'port2', property(pair[1]));
                }
                defineProperty(MessagePort.prototype, Symbol.toStringTag, { value: 'MessagePort', configurable: true });
                defineProperty(MessageChannel.prototype, Symbol.toStringTag, { value: 'MessageChannel', configurable: true });
                globalThis.structuredClone = function(value, options) {
                    if (arguments.length === 0) throw new TypeErrorCtor('structuredClone requires a value');
                    return host.clone(value, options);
                };
                globalThis.MessagePort = MessagePort;
                globalThis.MessageChannel = MessageChannel;
            })(__folio_message_port_factory);
            delete globalThis.__folio_message_port_factory;
            """, "folio:message-ports");
        return template;
    }
}

using System.Runtime.CompilerServices;

namespace Folio.Scripting.Dom;

internal sealed class MessagePortEndpoint
{
    // Mailboxes and entanglement survive transfer; script wrappers never change engines.
    private sealed class ChannelSide
    {
        internal readonly object Gate = new();
        internal readonly Queue<StructuredClonePayload> Mailbox = new();
        internal ChannelSide? Peer;
        internal MessagePortEndpoint? Owner;
        internal ScriptMessageBudget? Budget;
        internal long Reserved;
        internal long Generation;
        internal bool Closed;
        internal bool DeliveryQueued;
        internal bool MessageErrorPending;
    }

    private sealed class PortRegistry
    {
        internal readonly object Gate = new();
        internal readonly List<WeakReference<MessagePortEndpoint>> Ports = [];
        internal readonly Queue<(MessagePortEndpoint Port, long Generation)> LegacyTasks = new();
        internal readonly int OwnerThread = Environment.CurrentManagedThreadId;
        internal bool Draining;
        internal bool Disposed;
    }

    private static readonly ConditionalWeakTable<IScriptEngine, PortRegistry> Registries = new();
    private readonly ChannelSide _side;
    private readonly IHostObjectTemplate _messageEventTemplate;
    private readonly IHostObjectTemplate _messagePortTemplate;
    private readonly List<ScriptValue> _listeners = [];
    private readonly List<ScriptValue> _errorListeners = [];
    private IScriptEngine? _engine;
    private HtmlEventLoop? _eventLoop;
    private int _ownerThread;
    private int _maxBytes;
    private long _maxPendingBytes;
    private bool _started;
    private bool _closed;
    private bool _detached;
    private bool _transferPending;
    private ScriptValue _wrapper;
    private ScriptValue _onMessage = ScriptValue.Null;
    private ScriptValue _onMessageError = ScriptValue.Null;

    public MessagePortEndpoint(IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate)
        : this(messageEventTemplate, messagePortTemplate, new ChannelSide()) { }

    private MessagePortEndpoint(IHostObjectTemplate messageEventTemplate, IHostObjectTemplate messagePortTemplate, ChannelSide side)
    {
        _messageEventTemplate = messageEventTemplate;
        _messagePortTemplate = messagePortTemplate;
        _side = side;
    }

    internal static void Initialize(IScriptEngine engine, long maxPendingBytes)
    {
        if (Registries.TryGetValue(engine, out var previous) && previous.Disposed)
        {
            // A host may reuse an engine for a new document after teardown/reconfiguration.
            Registries.Remove(engine);
            ScriptMessageBudget.Reset(engine);
            StructuredCloneCodec.ResetHelpers(engine);
        }
        ScriptMessageBudget.For(engine, maxPendingBytes);
        Registries.GetValue(engine, _ => new PortRegistry());
    }

    public ScriptValue OnMessage { get { lock (_side.Gate) return _onMessage; } }
    public ScriptValue OnMessageError { get { lock (_side.Gate) return _onMessageError; } }
    public bool Closed { get { lock (_side.Gate) return _closed || _detached; } }
    internal ScriptValue GetWrapper() => _wrapper;

    public void Attach(IScriptEngine engine, HtmlEventLoop? eventLoop, int maxBytes, long maxPendingBytes)
    {
        InitializeOwner(engine, eventLoop, maxBytes, maxPendingBytes);
        var budget = ScriptMessageBudget.For(engine, maxPendingBytes);
        lock (_side.Gate)
        {
            _side.Budget = budget;
            _side.Owner = this;
            _side.Generation++;
        }
    }

    private void InitializeOwner(IScriptEngine engine, HtmlEventLoop? loop, int maxBytes, long maxPendingBytes)
    {
        _engine = engine;
        _eventLoop = loop;
        _ownerThread = Environment.CurrentManagedThreadId;
        _maxBytes = Math.Max(0, maxBytes);
        _maxPendingBytes = Math.Max(0, maxPendingBytes);
        _wrapper = engine.CreateHostObject(_messagePortTemplate, this);
        var registry = Registries.GetValue(engine, _ => new PortRegistry());
        lock (registry.Gate)
        {
            if (registry.Disposed) throw WebIdlException.DomException(DomExceptionNames.InvalidStateError, "The document was closed.");
            if (registry.Ports.Count >= 64) registry.Ports.RemoveAll(reference => !reference.TryGetTarget(out _));
            registry.Ports.Add(new WeakReference<MessagePortEndpoint>(this));
        }
    }

    public void SetPeer(MessagePortEndpoint peer) { lock (_side.Gate) _side.Peer = peer._side; }

    public void SetOnMessage(ScriptValue handler)
    {
        lock (_side.Gate) { _onMessage = handler.IsFunction ? handler : ScriptValue.Null; _started = true; }
        ScheduleDelivery();
    }

    public void SetOnMessageError(ScriptValue handler)
    {
        lock (_side.Gate) _onMessageError = handler.IsFunction ? handler : ScriptValue.Null;
    }

    public void AddListener(string type, ScriptValue handler)
    {
        if (!handler.IsFunction || type is not ("message" or "messageerror")) return;
        lock (_side.Gate)
        {
            var listeners = type == "message" ? _listeners : _errorListeners;
            if (!listeners.Contains(handler)) listeners.Add(handler);
        }
    }

    public void RemoveListener(string type, ScriptValue handler)
    {
        if (type is not ("message" or "messageerror")) return;
        lock (_side.Gate) (type == "message" ? _listeners : _errorListeners).Remove(handler);
    }

    public void Start()
    {
        lock (_side.Gate) _started = true;
        ScheduleDelivery();
    }

    public void Close()
    {
        lock (_side.Gate)
        {
            _closed = true;
            _onMessage = _onMessageError = ScriptValue.Null;
            _listeners.Clear();
            _errorListeners.Clear();
            if (_detached || !ReferenceEquals(_side.Owner, this)) return;
        }
        CloseSide(_side);
    }

    private static void CloseSide(ChannelSide side)
    {
        // Abandoned messages can own arbitrarily deep chains of transferred ports.
        // Drain them iteratively rather than recursing through the host stack.
        var pending = new Stack<ChannelSide>();
        pending.Push(side);
        while (pending.TryPop(out var current))
        {
            StructuredClonePayload[] abandoned;
            ChannelSide? peer;
            lock (current.Gate)
            {
                if (current.Closed) continue;
                current.Closed = true;
                peer = current.Peer;
                current.Peer = null;
                current.Owner = null;
                current.Generation++;
                current.DeliveryQueued = false;
                current.MessageErrorPending = false;
                abandoned = current.Mailbox.ToArray();
                current.Mailbox.Clear();
                current.Budget?.Release(current.Reserved);
                current.Reserved = 0;
            }
            if (peer is not null)
                lock (peer.Gate)
                    if (ReferenceEquals(peer.Peer, current)) peer.Peer = null;
            foreach (var payload in abandoned)
                foreach (var port in payload.Ports)
                    lock (port._side.Gate)
                    {
                        if (!port._transferPending) continue;
                        port._transferPending = false;
                        pending.Push(port._side);
                    }
        }
    }

    public void PostMessage(ScriptValue data, ScriptValue transfer = default)
    {
        var engine = _engine ?? throw new InvalidOperationException("The port has no owner.");
        AssertOwner();
        transfer = StructuredCloneCodec.NormalizeTransfer(engine, transfer, postMessage: true);
        var items = engine.TryGetArrayItems(transfer, out var transfers) ? transfers : [];
        if (items.Any(value => ReferenceEquals(TryGetPort(engine, value), this)))
            throw WebIdlException.DomException("DataCloneError", "A message port cannot transfer itself.");
        ChannelSide? peer;
        lock (_side.Gate) peer = _closed || _detached ? null : _side.Peer;
        var doomed = peer is not null && items.Any(value => TryGetPort(engine, value) is { } port && ReferenceEquals(port._side, peer));
        var prepared = StructuredCloneCodec.Prepare(engine, data, transfer, _maxBytes, TryGetPort);
        if (peer is null || doomed)
        {
            prepared.Commit().AbortTransfers();
            return;
        }
        ScriptMessageBudget? destinationBudget;
        lock (peer.Gate) destinationBudget = peer.Closed ? null : peer.Budget;
        if (destinationBudget is null)
        {
            prepared.Commit().AbortTransfers();
            return;
        }
        using var reservation = destinationBudget.Reserve(prepared.Payload.MemoryCost);
        var payload = prepared.Commit();
        Enqueue(peer, payload, reservation);
    }

    internal void ValidateTransfer()
    {
        AssertOwner();
        lock (_side.Gate)
            if (_closed || _detached || !ReferenceEquals(_side.Owner, this))
                throw WebIdlException.DomException("DataCloneError", "The message port is not transferable.");
    }

    internal void PrepareTransfer()
    {
        ValidateTransfer();
        lock (_side.Gate)
        {
            _detached = _transferPending = true;
            _started = false;
            _onMessage = _onMessageError = ScriptValue.Null;
            _listeners.Clear();
            _errorListeners.Clear();
            _wrapper = ScriptValue.Undefined;
            _side.Owner = null;
            _side.Generation++;
            _side.DeliveryQueued = false;
        }
    }

    internal MessagePortEndpoint ClaimTransfer(IScriptEngine engine, HtmlEventLoop? loop, int maxBytes)
    {
        lock (_side.Gate)
        {
            if (!_transferPending || _side.Closed)
                throw WebIdlException.DomException("DataCloneError", "The message port transfer is unavailable.");
            _transferPending = false;
        }
        var replacement = new MessagePortEndpoint(_messageEventTemplate, _messagePortTemplate, _side);
        try
        {
            replacement.InitializeOwner(engine, loop, maxBytes, _maxPendingBytes);
            var destinationBudget = ScriptMessageBudget.For(engine, _maxPendingBytes);
            lock (_side.Gate)
            {
                if (_side.Closed) throw WebIdlException.DomException("DataCloneError", "The message port was closed.");
                if (!ReferenceEquals(_side.Budget, destinationBudget))
                {
                    using var reservation = destinationBudget.Reserve(_side.Reserved);
                    _side.Budget?.Release(_side.Reserved);
                    _side.Budget = destinationBudget;
                    reservation.Adopt();
                }
                _side.Owner = replacement;
                _side.Generation++;
                _side.DeliveryQueued = false;
            }
            return replacement;
        }
        catch
        {
            CloseSide(_side);
            throw;
        }
    }

    internal void AbortTransfer()
    {
        lock (_side.Gate)
        {
            if (!_transferPending) return;
            _transferPending = false;
        }
        CloseSide(_side);
    }

    private static void Enqueue(ChannelSide side, StructuredClonePayload payload, ScriptMessageBudget.Reservation reservation)
    {
        MessagePortEndpoint? owner;
        var accepted = false;
        lock (side.Gate)
        {
            owner = side.Owner;
            if (!side.Closed && side.Budget is { } currentBudget)
            {
                if (ReferenceEquals(currentBudget, reservation.Budget))
                {
                    reservation.Adopt();
                    accepted = true;
                }
                else
                {
                    try
                    {
                        using var moved = currentBudget.Reserve(payload.MemoryCost);
                        moved.Adopt();
                        accepted = true;
                    }
                    catch (WebIdlException)
                    {
                        // The target moved to a more constrained engine during serialization.
                        side.MessageErrorPending = true;
                    }
                }
                if (accepted)
                {
                    side.Reserved += payload.MemoryCost;
                    side.Mailbox.Enqueue(payload);
                }
            }
        }
        if (!accepted) payload.AbortTransfers();
        owner?.ScheduleDelivery();
    }

    private void ScheduleDelivery()
    {
        long generation;
        lock (_side.Gate)
        {
            if (!CanDeliver() || _side.DeliveryQueued || (_side.Mailbox.Count == 0 && !_side.MessageErrorPending)) return;
            _side.DeliveryQueued = true;
            generation = _side.Generation;
        }
        if (_eventLoop is { } loop)
        {
            try { loop.QueueTask(TaskSource.Generic, () => DeliverOne(generation), "message port"); }
            catch (ObjectDisposedException) { Close(); }
        }
        else if (_engine is { } engine)
        {
            var registry = Registries.GetValue(engine, _ => new PortRegistry());
            lock (registry.Gate) registry.LegacyTasks.Enqueue((this, generation));
            DrainAll(engine);
        }
    }

    private bool CanDeliver() => !_closed && !_detached && _started && !_side.Closed && ReferenceEquals(_side.Owner, this);

    private void DeliverOne(long generation)
    {
        AssertOwner();
        StructuredClonePayload? payload = null;
        lock (_side.Gate)
        {
            if (_side.Generation != generation || !CanDeliver()) return;
            _side.DeliveryQueued = false;
            if (_side.Mailbox.Count > 0)
            {
                payload = _side.Mailbox.Dequeue();
                _side.Reserved -= payload.MemoryCost;
                _side.Budget!.Release(payload.MemoryCost);
            }
            else if (_side.MessageErrorPending) _side.MessageErrorPending = false;
            else return;
        }
        var engine = _engine!;
        ScriptValue message;
        var messageError = payload is null;
        StructuredCloneResult? decoded = null;
        try
        {
            if (payload is null)
                message = CreateMessage(ScriptValue.Undefined, [], "messageerror");
            else
            {
                decoded = StructuredCloneCodec.Decode(engine, payload,
                    (port, destination) => port.ClaimTransfer(destination, _eventLoop, _maxBytes).GetWrapper());
                message = CreateMessage(decoded.Value, decoded.Ports, "message");
            }
        }
        catch
        {
            decoded?.AbortPorts(engine);
            messageError = true;
            message = CreateMessage(ScriptValue.Undefined, [], "messageerror");
        }
        ScriptValue handler;
        ScriptValue[] listeners;
        lock (_side.Gate)
        {
            handler = messageError ? _onMessageError : _onMessage;
            listeners = (messageError ? _errorListeners : _listeners).ToArray();
        }
        Invoke(handler, message);
        foreach (var listener in listeners)
        {
            lock (_side.Gate)
                if (!(messageError ? _errorListeners : _listeners).Contains(listener)) continue;
            Invoke(listener, message);
        }
        ScheduleDelivery();
    }

    private ScriptValue CreateMessage(ScriptValue value, IReadOnlyList<ScriptValue> ports, string type) =>
        _engine!.CreateHostObject(_messageEventTemplate,
            new MessageEventState(value, string.Empty, ScriptValue.Null, type: type, target: _wrapper,
                ports: StructuredCloneCodec.CreatePorts(_engine, ports)));

    private void Invoke(ScriptValue handler, ScriptValue message)
    {
        if (!handler.IsFunction) return;
        try { _engine!.Call(handler, _wrapper, [message]); }
        catch (Exception error) { ReportException(error); }
    }

    private void ReportException(Exception error) => _eventLoop?.ReportException(error);

    private void AssertOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("A message port was used outside its engine owner thread.");
    }

    internal static MessagePortEndpoint? TryGetPort(IScriptEngine engine, ScriptValue value) =>
        engine.TryGetHostTarget(value, out var target) && target is MessagePortEndpoint port && ReferenceEquals(port._engine, engine)
            ? port : null;

    internal static void DisposeAll(IScriptEngine engine)
    {
        if (!Registries.TryGetValue(engine, out var registry)) return;
        MessagePortEndpoint[] ports;
        lock (registry.Gate)
        {
            registry.Disposed = true;
            registry.LegacyTasks.Clear();
            ports = registry.Ports.Select(reference => reference.TryGetTarget(out var port) ? port : null)
                .Where(port => port is not null).Cast<MessagePortEndpoint>().ToArray();
            registry.Ports.Clear();
        }
        foreach (var port in ports)
        {
            bool ownsSide;
            lock (port._side.Gate) ownsSide = ReferenceEquals(port._side.Owner, port);
            if (ownsSide) port.Close();
        }
    }

    internal static void DrainAll(IScriptEngine engine)
    {
        if (!Registries.TryGetValue(engine, out var registry) || Environment.CurrentManagedThreadId != registry.OwnerThread) return;
        lock (registry.Gate)
        {
            if (registry.Draining || registry.Disposed) return;
            registry.Draining = true;
        }
        try
        {
            for (var turn = 0; turn < 1000; turn++)
            {
                (MessagePortEndpoint Port, long Generation) task;
                lock (registry.Gate)
                {
                    if (registry.Disposed || registry.LegacyTasks.Count == 0) break;
                    task = registry.LegacyTasks.Dequeue();
                }
                task.Port.DeliverOne(task.Generation);
            }
        }
        finally { lock (registry.Gate) registry.Draining = false; }
    }
}

internal static class MessagePortFactory
{
    public static (MessagePortEndpoint First, MessagePortEndpoint Second) Create(IHostObjectTemplate messageEventTemplate,
        IHostObjectTemplate messagePortTemplate, IScriptEngine engine, HtmlEventLoop? eventLoop = null,
        int maxBytes = 1024 * 1024, long maxPendingBytes = 64L * 1024 * 1024)
    {
        var first = new MessagePortEndpoint(messageEventTemplate, messagePortTemplate);
        var second = new MessagePortEndpoint(messageEventTemplate, messagePortTemplate);
        first.SetPeer(second);
        second.SetPeer(first);
        try
        {
            first.Attach(engine, eventLoop, maxBytes, maxPendingBytes);
            second.Attach(engine, eventLoop, maxBytes, maxPendingBytes);
            return (first, second);
        }
        catch
        {
            first.Close();
            second.Close();
            throw;
        }
    }
}

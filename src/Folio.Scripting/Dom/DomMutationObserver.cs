using Folio.Dom;

namespace Folio.Scripting.Dom;

/// <summary>https://dom.spec.whatwg.org/#mutationobserverinit, already validated and with its implied members filled in.</summary>
internal sealed record MutationObserverOptions(bool ChildList, bool Subtree, bool Attributes, bool AttributeOldValue, bool CharacterData, bool CharacterDataOldValue, IReadOnlyList<string>? AttributeFilter);

/// <summary>A node's registered observer; a transient one (with a source) keeps a removed subtree observed until the next notification.</summary>
internal sealed class MutationRegistration(MutationObserverState observer, MutationObserverOptions options, MutationRegistration? source)
{
    public MutationObserverState Observer { get; } = observer;
    public MutationObserverOptions Options { get; set; } = options;
    public MutationRegistration? Source { get; } = source;
}

internal sealed class MutationObserverState(ScriptValue callback, ScriptValue wrapper)
{
    public ScriptValue Callback { get; } = callback;

    /// <summary>The script's MutationObserver object, which its callback gets as the second argument.</summary>
    public ScriptValue Wrapper { get; } = wrapper;

    public List<MutationRecordState> Queue { get; } = [];
}

internal sealed record MutationRecordState(Node Target, IReadOnlyList<Node> AddedNodes, IReadOnlyList<Node> RemovedNodes, string Type = "childList",
    string? AttributeName = null, string? OldValue = null, Node? PreviousSibling = null, Node? NextSibling = null);

/// <summary>
/// DOM §4.3 mutation observers: the registered observers on nodes, the records the tree, attribute and character data
/// algorithms queue (through <see cref="IMutationRecorder"/>) and their delivery at a microtask, in observer creation order.
/// </summary>
internal sealed class MutationObserverRegistry(DomContext context) : IMutationRecorder
{
    private readonly Dictionary<Node, List<MutationRegistration>> _registrations = new(ReferenceEqualityComparer.Instance);
    private readonly List<MutationObserverState> _observers = [];
    private bool _notifyQueued;

    public MutationObserverState Create(ScriptValue callback, ScriptValue wrapper)
    {
        var observer = new MutationObserverState(callback, wrapper);
        _observers.Add(observer);
        context.Document.MutationRecorder = this;
        return observer;
    }

    /// <summary>observe(): registers the observer on the target, or replaces its options when it already is.</summary>
    public void Observe(MutationObserverState observer, Node target, MutationObserverOptions options)
    {
        if (!_registrations.TryGetValue(target, out var list))
            _registrations[target] = list = [];

        if (list.FirstOrDefault(r => r.Observer == observer && r.Source is null) is { } existing)
        {
            RemoveTransient(source: existing);
            existing.Options = options;
            return;
        }
        list.Add(new MutationRegistration(observer, options, null));
    }

    public void Disconnect(MutationObserverState observer)
    {
        foreach (var list in _registrations.Values)
            list.RemoveAll(r => r.Observer == observer);
        observer.Queue.Clear();
    }

    public IReadOnlyList<MutationRecordState> Take(MutationObserverState observer)
    {
        var records = observer.Queue.ToArray();
        observer.Queue.Clear();
        return records;
    }

    public void ChildList(Node target, IReadOnlyList<Node> added, IReadOnlyList<Node> removed, Node? previousSibling, Node? nextSibling)
    {
        if (removed.Count > 0)
            RegisterTransient(target, removed);
        Queue("childList", target, null, null, added, removed, previousSibling, nextSibling);
    }

    public void Attribute(ElementNode element, string name, string? oldValue) =>
        Queue("attributes", element, name, oldValue, [], [], null, null);

    public void CharacterData(Node node, string oldValue) =>
        Queue("characterData", node, null, oldValue, [], [], null, null);

    // https://dom.spec.whatwg.org/#queue-a-mutation-record
    private void Queue(string type, Node target, string? attributeName, string? oldValue, IReadOnlyList<Node> added, IReadOnlyList<Node> removed, Node? previous, Node? next)
    {
        var interested = new List<(MutationObserverState Observer, string? OldValue)>();
        for (Node? node = target; node is not null; node = node.Parent)
        {
            if (!_registrations.TryGetValue(node, out var list))
                continue;
            foreach (var registration in list.ToArray())
            {
                var options = registration.Options;
                if (!ReferenceEquals(node, target) && !options.Subtree)
                    continue;
                if (type == "attributes" && (!options.Attributes || options.AttributeFilter is { } filter && !filter.Contains(attributeName!)))
                    continue;
                if (type == "characterData" && !options.CharacterData)
                    continue;
                if (type == "childList" && !options.ChildList)
                    continue;

                var index = interested.FindIndex(i => i.Observer == registration.Observer);
                if (index < 0)
                {
                    interested.Add((registration.Observer, null));
                    index = interested.Count - 1;
                }
                if (type == "attributes" && options.AttributeOldValue || type == "characterData" && options.CharacterDataOldValue)
                    interested[index] = (registration.Observer, oldValue);
            }
        }

        foreach (var (observer, mappedOldValue) in interested)
            observer.Queue.Add(new MutationRecordState(target, added, removed, type, attributeName, mappedOldValue, previous, next));
        if (interested.Count > 0)
            QueueNotify();
    }

    // https://dom.spec.whatwg.org/#concept-node-remove: a removed subtree stays observed by the observers that watched
    // its parent's subtree, until they are next notified.
    private void RegisterTransient(Node parent, IReadOnlyList<Node> removed)
    {
        for (Node? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!_registrations.TryGetValue(ancestor, out var list))
                continue;
            foreach (var registration in list.ToArray())
            {
                if (!registration.Options.Subtree)
                    continue;
                foreach (var node in removed)
                {
                    if (!_registrations.TryGetValue(node, out var own))
                        _registrations[node] = own = [];
                    own.Add(new MutationRegistration(registration.Observer, registration.Options, registration));
                }
            }
        }
    }

    private void RemoveTransient(MutationRegistration? source = null, MutationObserverState? observer = null)
    {
        foreach (var list in _registrations.Values)
            list.RemoveAll(r => r.Source is not null && (source is null || ReferenceEquals(r.Source, source)) && (observer is null || r.Observer == observer));
    }

    // The mutation observer microtask is a promise job, so it runs in order with the script's own promise callbacks.
    private void QueueNotify()
    {
        if (_notifyQueued)
            return;
        _notifyQueued = true;
        context.Engine.Call(context.Engine.GetGlobalProperty("__folio_mo_queue"), ScriptValue.Undefined, ReadOnlySpan<ScriptValue>.Empty);
    }

    // https://dom.spec.whatwg.org/#notify-mutation-observers
    public void Notify()
    {
        _notifyQueued = false;
        foreach (var observer in _observers.ToArray())
        {
            var records = observer.Queue.ToArray();
            observer.Queue.Clear();
            RemoveTransient(observer: observer);
            if (records.Length == 0)
                continue;
            try
            {
                var wrapped = records.Select(context.WrapMutationRecord).ToArray();
                context.Engine.Call(observer.Callback, observer.Wrapper, [context.Engine.CreateArray(wrapped), observer.Wrapper]);
            }
            catch (Exception)
            {
                // A callback that throws is reported by the host; the other observers are still notified.
            }
        }
    }
}

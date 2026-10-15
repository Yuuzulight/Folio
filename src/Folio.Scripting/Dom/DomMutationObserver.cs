using Folio.Dom;

namespace Folio.Scripting.Dom;

internal sealed class MutationObserverState(DomContext context, ScriptValue callback)
{
    public DomContext Context { get; } = context;
    public ScriptValue Callback { get; } = callback;
    public HashSet<Node> Targets { get; } = new(ReferenceEqualityComparer.Instance);
    public List<MutationRecordState> Records { get; } = [];
}

internal sealed record MutationRecordState(Node Target, IReadOnlyList<Node> AddedNodes, IReadOnlyList<Node> RemovedNodes);

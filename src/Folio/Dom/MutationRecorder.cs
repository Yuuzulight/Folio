namespace Folio.Dom;

/// <summary>
/// Where the tree, attribute and character data algorithms queue mutation records
/// (https://dom.spec.whatwg.org/#queue-a-mutation-record). A document has one only while something observes it.
/// </summary>
internal interface IMutationRecorder
{
    /// <summary>Nodes were inserted into and/or removed from <paramref name="target"/>; the siblings are those around the change.</summary>
    void ChildList(Node target, IReadOnlyList<Node> added, IReadOnlyList<Node> removed, Node? previousSibling, Node? nextSibling);

    /// <summary>An attribute was set or removed; <paramref name="oldValue"/> is null when it was not there.</summary>
    void Attribute(ElementNode element, string name, string? oldValue);

    /// <summary>A text or comment node's data was replaced.</summary>
    void CharacterData(Node node, string oldValue);
}

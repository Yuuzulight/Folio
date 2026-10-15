namespace Folio.Scripting.Dom;

internal sealed class MessageEventState(ScriptValue data, string origin, ScriptValue source, string type = "message", ScriptValue target = default)
{
    public ScriptValue Data { get; } = data;
    public string Origin { get; } = origin;
    public ScriptValue Source { get; } = source;
    public string Type { get; } = type;
    public ScriptValue Target { get; } = target.IsUndefined ? source : target;
}

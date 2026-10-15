namespace Folio.Scripting.Dom;

internal sealed class MessageEventState(ScriptValue data, string origin, ScriptValue source)
{
    public ScriptValue Data { get; } = data;
    public string Origin { get; } = origin;
    public ScriptValue Source { get; } = source;
}

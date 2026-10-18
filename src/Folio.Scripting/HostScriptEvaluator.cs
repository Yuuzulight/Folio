namespace Folio.Scripting;

/// <summary>Optional preparation of Folio-owned bootstrap code; never selected by an artifact URL.</summary>
internal interface IHostScriptEvaluator
{
    ScriptValue EvaluateHostScript(string source, string url);
}

internal static class HostScriptEvaluator
{
    internal static ScriptValue EvaluateHostScript(this IScriptEngine engine, string source, string url) =>
        engine is IHostScriptEvaluator host ? host.EvaluateHostScript(source, url) : engine.Evaluate(source, url);
}

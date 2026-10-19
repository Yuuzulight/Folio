namespace Folio.Scripting;

/// <summary>Native object brands needed by cloning; artifact prototypes cannot supply them.</summary>
internal interface IStructuredCloneEngine
{
    string ClassifyCloneObject(ScriptValue value);
    (string Source, string Flags) GetCloneRegExp(ScriptValue value);
}

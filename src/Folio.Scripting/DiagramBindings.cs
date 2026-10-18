using Folio.Diagrams;

namespace Folio.Scripting;

/// <summary>Small host bridge used by the Mermaid-compatible diagram stand-in.</summary>
internal static class DiagramBindings
{
    public static void Install(IScriptEngine engine)
    {
        var template = new HostObjectTemplate("FolioDiagramHost")
            .DefineMethod("render", (_, _, args) =>
            {
                var source = args.Length == 0 ? "" : WebIdlConversions.ToDOMString(args[0]);
                return DiagramRenderer.TryRender(source, out var svg) ? ScriptValue.FromString(svg) : ScriptValue.Null;
            });
        engine.SetGlobalProperty("__folio_diagrams", engine.CreateHostObject(template, new object()));
        engine.EvaluateHostScript(@"
            globalThis.mermaid = {
                initialize: function() {},
                run: function(options) {
                    var nodes = options && options.nodes;
                    if (!nodes) {
                        nodes = document.querySelectorAll('.mermaid');
                    }
                    for (var i = 0; i < nodes.length; i++) {
                        var svg = __folio_diagrams.render(nodes[i].textContent || '');
                        if (svg === null || svg === undefined) {
                            throw new Error('Folio.Diagrams does not support this diagram syntax.');
                        }
                        nodes[i].innerHTML = svg;
                    }
                    return Promise.resolve();
                }
            };
        ", "folio:diagram-stand-in");
    }
}

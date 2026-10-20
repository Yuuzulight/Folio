using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

public sealed class WebGlCapabilityTests
{
    [Fact]
    public void RetainedContextAndRestoreExtensionCannotBypassRevocationOrDisposal()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);
        engine.Evaluate("var gl=document.getElementById('canvas').getContext('webgl2');var extension=gl.getExtension('WEBGL_lose_context');var buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buffer);gl.bufferData(gl.ARRAY_BUFFER,16,gl.STATIC_DRAW);");
        Assert.True(engine.TryGetHostTarget(engine.Evaluate("gl"), out var target));
        var state = Assert.IsType<WebGlContextState>(target);
        Assert.Equal(16, state.AllocatedBytes);

        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        engine.Evaluate("extension.restoreContext();gl.bufferData(gl.ARRAY_BUFFER,4096,gl.STATIC_DRAW);gl.drawArrays(gl.TRIANGLES,0,3);");
        Assert.True(state.Lost);
        Assert.Equal(16, state.AllocatedBytes);
        Assert.Equal(0, state.DrawCalls);
        Assert.True(engine.Evaluate("gl.createBuffer() === null").AsBoolean());

        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true });
        Assert.False(state.Lost);
        Assert.False(engine.Evaluate("gl.createBuffer() === null").AsBoolean());
        Assert.True(DomBindings.Dispose(engine));
        engine.Evaluate("extension.restoreContext();gl.drawArrays(gl.TRIANGLES,0,3);");
        Assert.True(state.Lost);
        Assert.Equal(0, state.DrawCalls);
        Assert.True(engine.Evaluate("gl.createBuffer() === null").AsBoolean());
    }
}

using Folio.Scripting.Dom;
using Folio.Scripting.Jint;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class WebGlCapabilityTests
{
    [Fact]
    public void RetainedContextAndRestoreExtensionCannotBypassRevocationOrDisposal()
    {
        using var engine = new JintScriptEngine();
        using var document = Document.Parse("<canvas id='canvas' width='4' height='4'></canvas>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { WebGl = true }
        });
        DomBindings.Install(engine, document);
        engine.Evaluate("var gl=document.getElementById('canvas').getContext('webgl2',{antialias:false,depth:false});");
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(engine.Evaluate("gl === null").AsBoolean());
            DomBindings.Dispose(engine);
            return;
        }
        engine.Evaluate("var extension=gl.getExtension('WEBGL_lose_context');var buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,buffer);gl.bufferData(gl.ARRAY_BUFFER,16,gl.STATIC_DRAW);");
        Assert.True(DomBindings.TryGetGpuMetrics(engine, out var reserved, out _, out _));
        Assert.Equal(80, reserved);

        DomBindings.UpdateCapabilities(engine, FeatureCapabilities.Default);
        engine.Evaluate("extension.restoreContext();gl.bufferData(gl.ARRAY_BUFFER,4096,gl.STATIC_DRAW);gl.drawArrays(gl.TRIANGLES,0,3);");
        Assert.True(engine.Evaluate("gl.isContextLost()").AsBoolean());
        Assert.False(DomBindings.TryGetGpuMetrics(engine, out _, out _, out _));
        Assert.True(engine.Evaluate("gl.createBuffer() === null").AsBoolean());

        DomBindings.UpdateCapabilities(engine, new FeatureCapabilities { WebGl = true });
        Assert.False(engine.Evaluate("gl.isContextLost()").AsBoolean());
        Assert.False(engine.Evaluate("gl.createBuffer() === null").AsBoolean());
        Assert.False(engine.Evaluate("gl.isBuffer(buffer)").AsBoolean());
        Assert.True(DomBindings.Dispose(engine));
        engine.Evaluate("extension.restoreContext();gl.drawArrays(gl.TRIANGLES,0,3);");
        Assert.True(engine.Evaluate("gl.isContextLost()").AsBoolean());
        Assert.False(DomBindings.TryGetGpuMetrics(engine, out _, out _, out _));
        Assert.True(engine.Evaluate("gl.createBuffer() === null").AsBoolean());
    }
}

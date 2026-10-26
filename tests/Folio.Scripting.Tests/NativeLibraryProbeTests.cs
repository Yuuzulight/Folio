using Folio.Scripting.Native;
using Folio.Scripting.Dom;

namespace Folio.Scripting.Tests;

public sealed class NativeLibraryProbeTests
{
    [Fact]
    public void NativeCallHonorsThisReceiver()
    {
        using var engine = new NativeJavaScriptEngine();
        var function = engine.Evaluate("(function() { return this.value; })");
        var receiver = engine.Evaluate("({ value: 42 })");

        Assert.Equal(42, engine.Call(function, receiver, []).AsNumber());
    }

    [Fact]
    public void NativeHostMethodsHonorCallReceiver()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Folio.Document.Parse("<div id='source'></div><div id='target'></div>");
        DomBindings.Install(engine, document);

        var append = engine.Evaluate("document.getElementById('source').appendChild");
        var target = engine.Evaluate("document.getElementById('target')");
        var child = engine.Evaluate("document.createElement('span')");

        engine.Call(append, target, [child]);

        Assert.Single(document.GetElementById("target")!.Children);
        Assert.Empty(document.GetElementById("source")!.Children);
    }

    [Fact]
    public void ProbeReactLibrary()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "tests", "conformance-cache", "files"));
        var source = File.ReadAllText(Path.Combine(root, "react.production.min.js"));
        var domSource = File.ReadAllText(Path.Combine(root, "react-dom.production.min.js"));
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal("object", engine.Evaluate("typeof this").AsString());
        Assert.Equal("object", engine.Evaluate("(function(c) { return typeof c; })(this)").AsString());
        try
        {
            engine.Evaluate(source, "react.production.min.js");
        }
        catch (Exception error)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
        Assert.Equal("object", engine.Evaluate("typeof React").AsString());
        engine.Evaluate(domSource, "react-dom.production.min.js");
        Assert.Equal("object", engine.Evaluate("typeof ReactDOM").AsString());

    }
}

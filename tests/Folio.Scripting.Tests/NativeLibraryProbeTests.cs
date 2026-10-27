using Folio.Scripting.Native;
using Folio.Scripting.Dom;

namespace Folio.Scripting.Tests;

public sealed class NativeLibraryProbeTests
{
    [Fact]
    public void ProbeD3Library()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../..", "tests", "conformance-cache", "files"));
        using var engine = new NativeJavaScriptEngine();
        engine.Evaluate(File.ReadAllText(Path.Combine(root, "d3.min.js")), "d3.min.js");
        Assert.Equal("object", engine.Evaluate("typeof d3").AsString());
    }
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

        Assert.True(engine.Evaluate("document.getElementById('source').appendChild === document.getElementById('source').appendChild").AsBoolean());

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

    [Fact]
    public void NativeNodePrototypeMutationMethodsOperateOnTheReceiver()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Folio.Document.Parse("<div id='root'></div>");
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        engine.Evaluate("var p=document.createElement('i'); Node.prototype.appendChild.call(document.getElementById('root'),p); var attached=document.getElementById('root').children.length; Node.prototype.removeChild.call(document.getElementById('root'),p); var detached=document.getElementById('root').children.length;");
        Assert.Equal(1, engine.Evaluate("attached").AsNumber());
        Assert.Equal(0, engine.Evaluate("detached").AsNumber());
    }

    [Fact]
    public void NativeDocumentAndElementPrototypesExposeDomMethods()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Folio.Document.Parse("<div id='root'></div>");
        DomBindings.Install(engine, document);
        Assert.Equal("function", engine.Evaluate("typeof Document.prototype.createElement").AsString());
        Assert.Equal("function", engine.Evaluate("typeof Element.prototype.setAttribute").AsString());
        engine.Evaluate("var made=Document.prototype.createElement.call(document,'span'); Element.prototype.setAttribute.call(made,'data-x','ok');");
        Assert.Equal("ok", engine.Evaluate("made.getAttribute('data-x')").AsString());
    }

    [Fact]
    public void ProbeNativeMessageChannelDelivery()
    {
        using var engine = new NativeJavaScriptEngine();
        using var document = Folio.Document.Parse("<div></div>");
        using var loop = new HtmlEventLoop(engine);
        DomBindings.Install(engine, document, eventLoop: loop);
        engine.Evaluate("var hit = 0; var mc = new MessageChannel(); mc.port1.onmessage = function() { hit++; }; mc.port2.start(); mc.port1.start(); mc.port2.postMessage('x');");
        for (var i = 0; i < 8 && engine.Evaluate("hit").AsNumber() == 0; i++) loop.RunOneTurn();
        Assert.Equal(1, engine.Evaluate("hit").AsNumber());
    }
}

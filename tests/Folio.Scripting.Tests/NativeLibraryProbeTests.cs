using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public sealed class NativeLibraryProbeTests
{
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

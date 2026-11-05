using System.Text.Json;
using Folio.Scripting;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Scripting.Native;

namespace Folio.Scripting.Tests;

public class NativeJsonTests
{
    public static TheoryData<object?> WireValues => new()
    {
        (object?)null, "quoted \" and non-ASCII \u00E9 <script>", new byte[] { 0, 1, 255 },
        new Dictionary<string, object?> { ["id"] = 4, ["data"] = new byte[] { 7, 8 }, ["samples"] = new float[] { .25f, -.5f }, ["optional"] = null },
        new Dictionary<string, string> { ["content-type"] = "text/plain" },
        new ContentCacheBroker.Request("names", "context"),
        new CacheStorageState.Snapshot(1, [new("cache", [new(new("https://example.test/", "GET", []),
            200, "OK", [], [1, 2], false, "https://example.test/", "basic")])]),
        new[] { new CacheStorageState.Request("https://example.test/", "GET", []) },
    };

    [Theory]
    [MemberData(nameof(WireValues))]
    public void ExplicitWireShapesPreserveTheExistingJsonContract(object? value) =>
        Assert.Equal(JsonSerializer.Serialize(value), ScriptJson.Serialize(value));

    [Fact]
    public void StorageContractsRoundTripWithoutChangingTheirFieldNames()
    {
        var request = new ContentCacheBroker.Request("names", "abc");
        var bytes = ScriptJson.SerializeToUtf8Bytes(request);
        Assert.Equal(request, ScriptJson.Deserialize<ContentCacheBroker.Request>(bytes));
        Assert.Contains("\"Operation\"", System.Text.Encoding.UTF8.GetString(bytes));
        // Capability parsing is independently case-insensitive; persisted cache contracts keep their old casing.
        Assert.Null(ScriptJson.Deserialize<ContentCacheBroker.Request>("{\"operation\":\"names\",\"context\":\"abc\"}")!.Operation);
    }

    [Theory]
    [InlineData("JSON.stringify({value:7})", "{\"value\":7}")]
    [InlineData("JSON.stringify({value:undefined, kept:null})", "{\"kept\":null}")]
    [InlineData("JSON.stringify([undefined,NaN,Infinity,-0])", "[null,null,null,0]")]
    [InlineData("JSON.stringify('\u00E9 <script>')", "\"\u00E9 <script>\"")]
    [InlineData("typeof JSON.stringify()", "undefined")]
    [InlineData("typeof JSON.stringify(function(){})", "undefined")]
    public void NativeStringifyHandlesScriptValues(string script, string expected)
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.Equal(expected, engine.Evaluate(script).AsString());
    }

    [Fact]
    public void NativeStringifyRejectsCyclesWithoutPoisoningLaterCalls()
    {
        using var engine = new NativeJavaScriptEngine();
        Assert.True(engine.Evaluate("var value={};value.self=value;var rejected=false;try{JSON.stringify(value)}catch(e){rejected=true}rejected").AsBoolean());
        Assert.Equal("[1,2]", engine.Evaluate("JSON.stringify([1,2])").AsString());
    }
}


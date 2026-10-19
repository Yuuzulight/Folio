using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class WebAssemblyContentProcessTests
{
    [Fact]
    public async Task RealContentProcessAppliesGrantsRevocationAndRegrantToRetainedApi()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            documentLimits: ResourceLimits.Default with { MaxWebAssemblyBytes = 65536 });
        var granted = new FeatureCapabilities { WebAssembly = true };
        var initial = await Exchange(client, ContentMessageKind.Load, new
        {
            html = "<script>var retained=WebAssembly; var bytes=new Uint8Array([0,97,115,109,1,0,0,0]);</script>",
            script = "retained.validate(bytes)", capabilities = granted
        });
        Assert.Equal("true", Value(initial));

        var revoked = await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = FeatureCapabilities.Default });
        Assert.True(revoked.TryGetCapabilities(out var revokedCapabilities));
        Assert.False(revokedCapabilities.WebAssembly);
        const string attempt = "__folio_wasm_enabled=true; try {new retained.Memory({initial:1}); 'allowed';} catch(error) {error.name;}";
        var denied = await Exchange(client, ContentMessageKind.Load, new { script = attempt });
        Assert.Equal("NotSupportedError", Value(denied));
        var repeated = await Exchange(client, ContentMessageKind.Load, new { script = attempt });
        Assert.Equal("NotSupportedError", Value(repeated));
        Assert.Single(repeated.Payload.GetProperty("diagnostics").EnumerateArray(),
            item => item.GetString()!.Contains("WebAssembly is disabled by the host.", StringComparison.Ordinal));

        var restored = await Exchange(client, ContentMessageKind.Capabilities, new { capabilities = granted });
        Assert.True(restored.TryGetCapabilities(out var restoredCapabilities));
        Assert.True(restoredCapabilities.WebAssembly);
        Assert.Equal("65536", Value(await Exchange(client, ContentMessageKind.Load,
            new { script = "new retained.Memory({initial:1}).buffer.byteLength" })));
        Assert.Equal("RangeError", Value(await Exchange(client, ContentMessageKind.Load, new
        {
            script = "__folio_wasm_max_pages=100; try {new retained.Memory({initial:2}); 'allowed';} catch(error) {error.name;}"
        })));

        // A fresh document's denied load must not inherit the previous grant.
        Assert.Equal("false", Value(await Exchange(client, ContentMessageKind.Load, new
        {
            html = "<p>fresh denied document</p>", capabilities = FeatureCapabilities.Default,
            script = "__folio_wasm_enabled=true; WebAssembly.validate(new Uint8Array([0,97,115,109,1,0,0,0]))"
        })));
    }

    private static string? Value(ContentMessage message)
    {
        Assert.Equal(ContentMessageKind.Frame, message.Kind);
        return message.Payload.GetProperty("value").GetString();
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, ContentMessageKind kind, object payload)
    {
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Capabilities or ContentMessageKind.Diagnostic)
                received.TrySetResult(message);
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(kind, payload, TestContext.Current.CancellationToken);
            var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.NotEqual(ContentMessageKind.Diagnostic, result.Kind);
            Assert.Equal(kind == ContentMessageKind.Capabilities ? ContentMessageKind.Capabilities : ContentMessageKind.Frame, result.Kind);
            return result;
        }
        finally { client.MessageReceived -= Handler; }
    }
}

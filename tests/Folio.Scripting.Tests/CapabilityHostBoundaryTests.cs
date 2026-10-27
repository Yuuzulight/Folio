using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class CapabilityHostBoundaryTests
{
    [Theory]
    [InlineData("dedicated")]
    [InlineData("observation")]
    [InlineData("observation-case")]
    [InlineData("replacement")]
    public async Task InvalidOrOmittedGrantsCannotRetainPreviousArtifactAuthority(string path)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            documentLimits: ResourceLimits.Default with { MaxWebAssemblyBytes = 65536 });
        var initial = await Exchange(ContentMessageKind.Load, new
        {
            html = "<script>var retained=WebAssembly;</script>",
            capabilities = new FeatureCapabilities { WebAssembly = true },
            script = "new retained.Memory({initial:1}).buffer.byteLength"
        });
        Assert.Equal("65536", initial.Payload.GetProperty("value").GetString());

        // An observation without a grant field must leave the current document alone.
        Assert.Equal("65536", (await Exchange(ContentMessageKind.Load, new
        {
            script = "new retained.Memory({initial:1}).buffer.byteLength"
        })).Payload.GetProperty("value").GetString());

        const string probe = "try { new retained.Memory({initial:1}); 'allowed'; } catch(e) { e.name; }";
        ContentMessage result;
        if (path == "dedicated")
        {
            var acknowledgement = await Exchange(ContentMessageKind.Capabilities, new { capabilities = new { webAssembly = "yes" } });
            Assert.False(acknowledgement.Payload.GetProperty("accepted").GetBoolean());
            Assert.True(acknowledgement.TryGetCapabilities(out var denied));
            Assert.Equal(FeatureCapabilities.Default, denied);
            result = await Exchange(ContentMessageKind.Load, new { script = probe });
        }
        else if (path.StartsWith("observation", StringComparison.Ordinal))
        {
            object payload = path == "observation-case"
                ? new { Capabilities = new { webAssembly = 1 }, script = probe }
                : new { capabilities = new { webAssembly = 1 }, script = probe };
            result = await Exchange(ContentMessageKind.Load, payload);
        }
        else
            result = await Exchange(ContentMessageKind.Load, new { html = "<script>var retained=WebAssembly;</script>", script = probe });
        Assert.Equal("NotSupportedError", result.Payload.GetProperty("value").GetString());

        async Task<ContentMessage> Exchange(ContentMessageKind kind, object payload)
        {
            var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Handler(ContentMessage message, CancellationToken _)
            {
                if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Capabilities)
                    completion.TrySetResult(message);
                else if (message.Kind == ContentMessageKind.Diagnostic)
                    completion.TrySetException(new InvalidOperationException(message.Payload.ToString()));
                return ValueTask.CompletedTask;
            }
            client.MessageReceived += Handler;
            try
            {
                await client.SendAsync(kind, payload, TestContext.Current.CancellationToken);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            }
            finally { client.MessageReceived -= Handler; }
        }
    }
}

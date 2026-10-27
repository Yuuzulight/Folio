using System.Text.Json;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class CapabilityIpcTests
{
    [Fact]
    public void NewAndDefaultCapabilitySetsDenyEveryFeature()
    {
        foreach (var capabilities in new[] { new FeatureCapabilities(), FeatureCapabilities.Default })
            Assert.All(typeof(FeatureCapabilities).GetProperties().Where(property => property.PropertyType == typeof(bool)),
                property => Assert.False((bool)property.GetValue(capabilities)!));
    }

    [Fact]
    public async Task PublicUpdateApiChangesRetainedArtifactWithoutReloadingHtml()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            documentLimits: ResourceLimits.Default with { MaxWebAssemblyBytes = 65536 });
        var granted = new FeatureCapabilities
        {
            WebGl = true, Media = true, Sound = true, Camera = true, Microphone = true,
            AllowSoundAutoplay = true, Workers = true, WebAssembly = true, Iframes = true,
            WebRtc = true, ServiceWorkers = true
        };
        var loaded = await Exchange(client, ContentMessageKind.Load, new
        {
            html = "<script>var retained=WebAssembly;</script>",
            script = "typeof retained",
            capabilities = granted
        });
        Assert.Equal("object", loaded.Payload.GetProperty("value").GetString());

        await UpdateAndAwait(client, FeatureCapabilities.Default);
        Assert.Equal("NotSupportedError", (await Exchange(client, ContentMessageKind.Load, new
        {
            script = "try { new retained.Memory({initial:1}); } catch (error) { error.name; }"
        })).Payload.GetProperty("value").GetString());

        await UpdateAndAwait(client, granted);
        Assert.Equal("65536", (await Exchange(client, ContentMessageKind.Load, new
        {
            script = "new retained.Memory({initial:1}).buffer.byteLength"
        })).Payload.GetProperty("value").GetString());

        static async Task UpdateAndAwait(ContentProcessClient client, FeatureCapabilities capabilities)
        {
            var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Handler(ContentMessage message, CancellationToken _) { if (message.Kind == ContentMessageKind.Capabilities) completion.TrySetResult(message); return ValueTask.CompletedTask; }
            client.MessageReceived += Handler;
            try
            {
                await client.UpdateCapabilitiesAsync(capabilities, TestContext.Current.CancellationToken);
                var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.True(reply.TryGetCapabilities(out var echoed));
                Assert.Equal(capabilities, echoed);
            }
            finally { client.MessageReceived -= Handler; }
        }

        static async Task<ContentMessage> Exchange(ContentProcessClient client, ContentMessageKind kind, object payload)
        {
            var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Handler(ContentMessage message, CancellationToken _)
            {
                if (message.Kind is ContentMessageKind.Frame or ContentMessageKind.Capabilities) completion.TrySetResult(message);
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

    [Fact]
    public void DecodesAllKnownCapabilityBits()
    {
        using var document = JsonDocument.Parse("""
            {"capabilities":{"webGl":true,"media":true,"sound":true,"camera":true,"microphone":true,
            "allowSoundAutoplay":true,"workers":true,"webAssembly":true,"iframes":true,"webRtc":true,"serviceWorkers":true}}
            """);
        var message = new ContentMessage(ContentMessageKind.Capabilities, 1, document.RootElement.Clone());

        Assert.True(message.TryGetCapabilities(out var capabilities));
        Assert.All(typeof(FeatureCapabilities).GetProperties()
            .Where(property => property.PropertyType == typeof(bool)), property => Assert.True((bool)property.GetValue(capabilities)!));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"capabilities\":null}")]
    [InlineData("{\"capabilities\":[]}")]
    [InlineData("{\"capabilities\":{\"webGl\":\"yes\"}}")]
    [InlineData("{\"capabilities\":{\"webGl\":1}}")]
    [InlineData("{}")]
    [InlineData("{\"capabilities\":{\"webGl\":true,\"WebGL\":false}}")]
    [InlineData("{\"capabilities\":null,\"Capabilities\":{\"webGl\":true}}")]
    [InlineData("{\"CAPABILITIES\":{\"webGl\":true},\"capabilities\":null}")]
    [InlineData("{\"capabilities\":{\"webGl\":false},\"Capabilities\":{\"webGl\":true}}")]
    public void MalformedCapabilityPayloadFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);
        var message = new ContentMessage(ContentMessageKind.Capabilities, 1, document.RootElement.Clone());

        Assert.False(message.TryGetCapabilities(out var capabilities));
        Assert.Equal(FeatureCapabilities.Default, capabilities);
    }

    [Fact]
    public void WrongMessageKindDoesNotDecodeCapabilities()
    {
        using var document = JsonDocument.Parse("{\"capabilities\":{\"webGl\":true}}");
        var message = new ContentMessage(ContentMessageKind.Frame, 1, document.RootElement.Clone());

        Assert.False(message.TryGetCapabilities(out var capabilities));
        Assert.Equal(FeatureCapabilities.Default, capabilities);
    }

    [Fact]
    public void UndefinedPayloadFailsClosedWithoutThrowing()
    {
        var message = new ContentMessage(ContentMessageKind.Capabilities, 1, default);

        Assert.False(message.TryGetCapabilities(out var capabilities));
        Assert.Equal(FeatureCapabilities.Default, capabilities);
    }

    [Fact]
    public void UnknownCapabilityFieldsCannotGrantKnownBits()
    {
        using var document = JsonDocument.Parse("{\"capabilities\":{\"futurePower\":true}}");
        var message = new ContentMessage(ContentMessageKind.Capabilities, 1, document.RootElement.Clone());

        Assert.True(message.TryGetCapabilities(out var capabilities));
        Assert.Equal(FeatureCapabilities.Default, capabilities);
    }
}

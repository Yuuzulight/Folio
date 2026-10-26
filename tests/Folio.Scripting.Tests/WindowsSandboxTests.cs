using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Folio;
using Folio.Resources;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

[Collection("Content process round trips")]
public sealed class WindowsSandboxTests
{
    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public async Task ServiceWorkerCapabilityRowRunsThroughTheRealContentProcess()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        if (row is not ("service-workers" or "all")) return;
        var host = ContentHostPath();
        Assert.True(File.Exists(host), $"Content host was not built: {host}");
        var storage = new MatrixStorage();
        await using var client = ContentProcessClient.Start("dotnet", $"\"{host}\"",
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)),
            resourceLoader: new MatrixServiceWorkerLoader(), artifactStorage: storage, artifactId: "matrix",
            documentLimits: ResourceLimits.Default with { MaxServiceWorkerCacheBytes = 64 * 1024 });
        var frame = await Exchange(client, new
        {
            html = "<script>var result='unfinished';(async()=>{await navigator.serviceWorker.register('/sw.js');result=await (await fetch('/offline')).text();})().catch(e=>result=e.name+':'+e.message);</script>",
            script = "result",
            url = "https://matrix.example/",
            artifactId = "matrix",
            storageEnabled = true,
            capabilities = CapabilityMatrixTests.CapabilitiesFor(row),
            limits = ResourceLimits.Default with { MaxServiceWorkerCacheBytes = 64 * 1024 }
        });
        Assert.Equal("offline", frame.Payload.GetProperty("value").GetString());
        Assert.Contains(storage.Keys("matrix:service-worker-registration/"), key => key.EndsWith("%2F", StringComparison.Ordinal));
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public async Task NativeSandboxProbeDeniesFileNetworkAndChildProcessForEveryCapabilityRow()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        var capabilities = CapabilityMatrixTests.CapabilitiesFor(row);
        var host = ProbePath();
        Assert.True(File.Exists(host), $"Sandbox probe was not built: {host}");
        var canary = Path.Combine(Path.GetTempPath(), "folio-sandbox-canary-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(canary, "host-canary");
        var environmentKey = "FOLIO_PROBE_SENTINEL_" + Guid.NewGuid().ToString("N");
        var previousEnvironmentValue = Environment.GetEnvironmentVariable(environmentKey);
        Environment.SetEnvironmentVariable(environmentKey, "host-secret");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var exercisesWorker = row is "workers" or "all";
        var exercisesWebAssembly = row is "webassembly" or "all";
        var exercisesWebRtc = row is "webrtc" or "all";
        var script = exercisesWorker
            ? "var w=new Worker('data:text/javascript,'+encodeURIComponent('onmessage=function(e){postMessage(String(e.data+1));}'));w.onmessage=e=>__folio_worker_result=e.data;w.onerror=e=>__folio_worker_error=e.message;w.postMessage(41);typeof document"
            : exercisesWebAssembly
                ? "try{var m=new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0,1,5,1,96,0,1,127,3,2,1,0,7,7,1,3,114,117,110,0,0,10,6,1,4,0,65,42,11]));__folio_wasm_result=String(new WebAssembly.Instance(m,{}).exports.run());}catch(e){__folio_wasm_error=e.name+': '+e.message;}typeof document"
                : exercisesWebRtc
                    ? "try{var pc=new RTCPeerConnection();pc.createDataChannel('matrix');pc.close();__folio_webrtc_result='ok';}catch(e){__folio_webrtc_error=e.name+': '+e.message;}typeof document"
                    : "typeof document";
        // A timeout against an unreachable endpoint is not sandbox evidence.
        using (var control = new TcpClient())
        {
            await control.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port,
                TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        }
        await using var client = ContentProcessClient.Start(host, null,
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        try
        {
            var result = await Exchange(client, new
            {
                probe = "sandbox",
                filePath = canary,
                networkPort = ((IPEndPoint)listener.LocalEndpoint).Port,
                environmentKey,
                capabilities,
                html = "<html><body></body></html>",
                script,
            });
            var payload = result.Payload.GetProperty("payload");
            Assert.Equal(capabilities, System.Text.Json.JsonSerializer.Deserialize<FeatureCapabilities>(payload.GetProperty("appliedCapabilities").GetRawText()));
            Assert.True(payload.GetProperty("runtimeReady").GetBoolean());
            Assert.Equal("object", payload.GetProperty("runtimeValue").GetString());
            if (exercisesWorker)
            {
                Assert.Equal("42", payload.GetProperty("workerResult").GetString());
                Assert.Null(payload.GetProperty("workerError").GetString());
            }
            if (exercisesWebAssembly)
            {
                Assert.Equal("42", payload.GetProperty("wasmResult").GetString());
                Assert.Null(payload.GetProperty("wasmError").GetString());
            }
            if (exercisesWebRtc)
            {
                Assert.Equal("ok", payload.GetProperty("webrtcResult").GetString());
                Assert.Null(payload.GetProperty("webrtcError").GetString());
            }
            Assert.True(payload.GetProperty("isAppContainer").GetBoolean());
            Assert.True(payload.GetProperty("isLowIntegrity").GetBoolean());
            Assert.Equal(0, payload.GetProperty("capabilityCount").GetInt32());
            Assert.True(payload.GetProperty("environmentAbsent").GetBoolean());
            Assert.Equal(256L * 1024 * 1024, payload.GetProperty("jobMemoryBytes").GetInt64());
            Assert.Equal(1, payload.GetProperty("activeProcessLimit").GetInt32());
            Assert.Equal(25, payload.GetProperty("cpuRatePercent").GetInt32());
            Assert.Equal(0xff, payload.GetProperty("uiRestrictions").GetInt32());
            Assert.True(payload.GetProperty("fileReadDenied").GetBoolean());
            Assert.True(payload.GetProperty("fileWriteDenied").GetBoolean());
            Assert.True(payload.GetProperty("networkDenied").GetBoolean());
            Assert.True(payload.GetProperty("childDenied").GetBoolean());
            Assert.True(File.Exists(canary));
            Assert.Equal("host-canary", File.ReadAllText(canary));
            Assert.False(listener.Pending());
        }
        finally
        {
            try { File.Delete(canary); } catch { }
            Environment.SetEnvironmentVariable(environmentKey, previousEnvironmentValue);
        }
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public async Task NativeSandboxProbePublishesFrameThenMemoryPressureKillsTheChild()
    {
        var host = ProbePath();
        Assert.True(File.Exists(host), $"Sandbox probe was not built: {host}");
        var capabilities = CapabilityMatrixTests.CapabilitiesFor(Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none");
        await using var client = ContentProcessClient.Start(host, null,
            new ContentProcessLimits(MemoryBytes: 256L * 1024 * 1024, HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        var frame = await Exchange(client, new { probe = "frame", html = "<html><body></body></html>", script = "typeof document", capabilities });
        Assert.Equal(1280, frame.Payload.GetProperty("canvasFrame").GetProperty("width").GetInt32());
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ScriptStopped += (_, args) => stopped.TrySetResult(args.Reason);
        await client.SendAsync(ContentMessageKind.Load, new { probe = "memory", bytes = 320 * 1024 * 1024 }, TestContext.Current.CancellationToken);
        var reason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Contains("memory", reason, StringComparison.OrdinalIgnoreCase);
        await client.Process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(client.Process.HasExited);
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public async Task NativeSandboxProbeKeepsLastFrameWhenWatchdogKillsHungChild()
    {
        var host = ProbePath();
        Assert.True(File.Exists(host), $"Sandbox probe was not built: {host}");
        var capabilities = CapabilityMatrixTests.CapabilitiesFor(Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none");
        // Exercise the production watchdog contract, including cold document
        // bootstrap, rather than imposing a separate shorter startup deadline.
        await using var client = ContentProcessClient.Start(host);
        var frame = await Exchange(client, new { probe = "frame", html = "<html><body></body></html>", script = "typeof document", capabilities });
        var descriptor = frame.Payload.GetProperty("canvasFrame");
        var framePath = descriptor.GetProperty("path").GetString()!;
        var width = descriptor.GetProperty("width").GetInt32();
        var height = descriptor.GetProperty("height").GetInt32();
        var stride = descriptor.GetProperty("stride").GetInt32();
        byte[] before;
        long beforeSequence;
        using (var store = new CanvasFrameStore(framePath, width, height, stride))
            before = store.ReadLatest(out beforeSequence);
        Assert.True(beforeSequence > 0);
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ScriptStopped += (_, args) => stopped.TrySetResult(args.Reason);
        await client.SendAsync(ContentMessageKind.Load, new { probe = "hang" }, TestContext.Current.CancellationToken);
        var reason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Contains("heartbeat", reason, StringComparison.OrdinalIgnoreCase);
        await client.Process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(client.Process.HasExited);
        using var reopened = new CanvasFrameStore(framePath, width, height, stride);
        var after = reopened.ReadLatest(out var afterSequence);
        Assert.Equal(beforeSequence, afterSequence);
        Assert.Equal(before, after);
    }

    [Fact(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    public async Task RejectsForgedFrameDescriptorWithoutDispatchingOrTouchingHostFile()
    {
        var host = ProbePath();
        Assert.True(File.Exists(host), $"Sandbox probe was not built: {host}");
        var capabilities = CapabilityMatrixTests.CapabilitiesFor(Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none");
        var canary = Path.Combine(Path.GetTempPath(), "folio-frame-canary-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(canary, "host-frame-canary");
        await using var client = ContentProcessClient.Start(host, null,
            new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        try
        {
            var valid = await Exchange(client, new { probe = "frame", html = "<html><body></body></html>", script = "typeof document", capabilities });
            Assert.True(valid.Payload.GetProperty("canvasFrame").GetProperty("sequence").GetInt64() > 0);
            var dispatched = 0;
            var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ValueTask Handler(ContentMessage message, CancellationToken _)
            {
                if (message.Kind == ContentMessageKind.Frame) Interlocked.Increment(ref dispatched);
                return ValueTask.CompletedTask;
            }
            client.MessageReceived += Handler;
            client.ScriptStopped += (_, args) => stopped.TrySetResult(args.Reason);
            try
            {
                await client.SendAsync(ContentMessageKind.Load, new { probe = "forge", framePath = canary }, TestContext.Current.CancellationToken);
                var reason = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.Contains("frame descriptor", reason, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, Volatile.Read(ref dispatched));
                Assert.Equal("host-frame-canary", File.ReadAllText(canary));
            }
            finally { client.MessageReceived -= Handler; }
        }
        finally
        {
            try { File.Delete(canary); } catch { }
        }
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, object payload)
    {
        var completion = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? stopReason = null;
        string? stage = null;
        void Stopped(object? sender, ContentProcessStoppedEventArgs args) => stopReason = args.Reason;
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame) completion.TrySetResult(message);
            else if (message.Kind == ContentMessageKind.Hello) stage = message.Payload.ToString();
            else if (message.Kind == ContentMessageKind.Diagnostic)
                completion.TrySetException(new IOException($"Native probe failed: {message.Payload}"));
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        client.ScriptStopped += Stopped;
        try
        {
            try { await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken); }
            catch (IOException error)
            {
                await client.Process.WaitForExitAsync(TestContext.Current.CancellationToken);
                throw new IOException($"Probe exited ({client.Process.ExitCode}): {client.StandardError}", error);
            }
            try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken); }
            catch (TimeoutException error)
            {
                throw new TimeoutException($"Probe response timed out; stage={stage}, exit={(client.Process.HasExited ? client.Process.ExitCode : null)}, stopped={stopReason}, stderr={client.StandardError}", error);
            }
        }
        finally { client.MessageReceived -= Handler; client.ScriptStopped -= Stopped; }
    }

    private static string ProbePath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.SandboxProbe/bin", configuration,
            OperatingSystem.IsWindows() ? "net10.0/Folio.SandboxProbe.exe" : "net10.0/Folio.SandboxProbe.dll"));
    }

    private static string ContentHostPath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
    }

    private sealed class MatrixServiceWorkerLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(request.Kind == ResourceKind.Script && request.Url == "https://matrix.example/sw.js"
                ? ResourceResponse.Loaded(System.Text.Encoding.UTF8.GetBytes("self.addEventListener('fetch', e => e.respondWith(new Response('offline')));"), "application/javascript")
                : ResourceResponse.Refused("matrix host refusal"));
    }

    private sealed class MatrixStorage : IArtifactStorage
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Read(string key) => _values.GetValueOrDefault(key);
        public void Write(string key, string value) => _values[key] = value;
        public void Delete(string key) => _values.TryRemove(key, out _);
        public IReadOnlyList<string> Keys(string prefix) => _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
}

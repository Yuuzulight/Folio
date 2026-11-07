using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Folio.Scripting.Dom;
using Folio.Scripting.Ipc;
using Folio.Scripting.Jint;
using Folio.Scripting.Native;
using Microsoft.Win32.SafeHandles;

namespace Folio.Scripting.Tests;

/// <summary>Original #539/#541 acceptance: interrupt the active worker, not its page.</summary>
[Collection("Worker execution deadlines")]
public sealed class WorkerIsolationAcceptanceTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("jint")]
    public void ExplicitTerminateInterruptsAnActiveCallbackAndReleasesItsSlot(string implementation)
    {
        using var fixture = new WorkerIsolationFixture(implementation, maxWorkers: 1);
        using var entered = new ManualResetEventSlim();
        fixture.Engine.Evaluate("var worker = new Worker('data:text/javascript,' + encodeURIComponent(\"onmessage=function(){setTimeout(function(){busyProbe.enter();while(true){}},100);};\"));");
        var worker = fixture.Worker("worker");

        // Native Configure updates limits in place. Jint Configure rebuilds its
        // realm, so calling it after startup would delete the worker's globals
        // and make this a broken-startup test instead of an interruption test.
        if (implementation == "native")
            worker.WorkerEngine!.Configure(ScriptLimits.Default with
            {
                MaxStatements = null, MemoryLimitBytes = null, Timeout = TimeSpan.FromSeconds(30)
            });
        var probe = new HostObjectTemplate("BusyWorkerProbe")
            .DefineMethod("enter", (_, _, _) => { entered.Set(); return ScriptValue.Undefined; });
        worker.WorkerEngine!.SetGlobalProperty("busyProbe", worker.WorkerEngine.CreateHostObject(probe, entered));
        fixture.Engine.Evaluate("worker.postMessage('start');");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
            $"{implementation}: worker never entered its busy callback");
        Assert.False(worker.Terminated);
        Assert.False(worker.ResourcesReleased);

        var started = Stopwatch.GetTimestamp();
        fixture.Engine.Evaluate("worker.terminate();");
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2),
            $"{implementation}: explicit terminate did not return before the engine/worker deadline");
        fixture.AssertReleased(worker);
        Assert.Equal(42d, fixture.Engine.Evaluate("40+2").AsNumber());
        fixture.AssertReplacementReplies();
    }

    [Theory]
    [InlineData("new ArrayBuffer(32*1024*1024)", false)]
    [InlineData("new Array(8*1024*1024)", false)]
    [InlineData("new ArrayBuffer(32*1024*1024)", true)]
    [InlineData("new Array(8*1024*1024)", true)]
    public void NativeAllocationFailureAfterStartupReleasesOnlyTheFailingWorker(string allocation, bool timer)
    {
        using var fixture = new WorkerIsolationFixture("native");
        var fail = allocation + ";postMessage('unexpected');";
        var source = "onmessage=function(e){if(e.data==='allocate'){"
            + (timer ? "setTimeout(function(){" + fail + "},0);" : fail)
            + "}else postMessage('ready');};";
        fixture.Engine.Evaluate("var workerError='',workerErrorCount=0,workerReply='',siblingReply='';var worker=new Worker('data:text/javascript,'+encodeURIComponent("
            + JsonSerializer.Serialize(source) + "));worker.onmessage=e=>workerReply=e.data;worker.onerror=function(e){workerErrorCount++;workerError=e.message;};"
            + "var sibling=new Worker('data:text/javascript,'+encodeURIComponent(\"onmessage=e=>postMessage(e.data);\"));sibling.onmessage=e=>siblingReply=e.data;worker.postMessage('ready');sibling.postMessage('before');");
        fixture.Until(() => fixture.String("workerReply") == "ready" && fixture.String("siblingReply") == "before");
        var worker = fixture.Worker("worker");
        var sibling = fixture.Worker("sibling");
        Assert.False(worker.Terminated); // Failure must happen after successful startup/delivery.
        fixture.Engine.Evaluate("worker.postMessage('allocate');");
        fixture.Until(() => fixture.String("workerError").Length != 0);
        Assert.Contains("memory", fixture.String("workerError"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ready", fixture.String("workerReply"));
        Assert.Equal(42d, fixture.Engine.Evaluate("40+2").AsNumber());
        fixture.Engine.Evaluate("sibling.postMessage('after');");
        fixture.Until(() => fixture.String("siblingReply") == "after");
        Assert.False(sibling.Terminated);

        // Do not call terminate() here: #541 requires the over-limit worker to
        // fail alone and return its slot/reservation without help from the page.
        fixture.AssertReleased(worker);
        fixture.AssertReplacementReplies();
        fixture.DrainParentTasks();
        Assert.Equal(1d, fixture.Engine.GetGlobalProperty("workerErrorCount").AsNumber());
        Assert.Contains("memory", fixture.String("workerError"), StringComparison.OrdinalIgnoreCase);
        Assert.False(sibling.Terminated);
    }

    [Theory]
    [InlineData("native", false)]
    [InlineData("jint", false)]
    [InlineData("native", true)]
    [InlineData("jint", true)]
    public void OrdinaryCallbackErrorDoesNotRetireTheWorker(string implementation, bool timer)
    {
        using var fixture = new WorkerIsolationFixture(implementation, maxWorkers: 1);
        var fail = "throw new Error('ordinary worker error');";
        var source = "onmessage=function(e){if(e.data==='throw'){"
            + (timer ? "setTimeout(function(){" + fail + "},0);" : fail)
            + "}else postMessage('still alive');};";
        fixture.Engine.Evaluate("var workerError='',workerReply='';var worker=new Worker('data:text/javascript,'+encodeURIComponent("
            + JsonSerializer.Serialize(source) + "));worker.onerror=e=>workerError=e.message;worker.onmessage=e=>workerReply=e.data;worker.postMessage('throw');");
        fixture.Until(() => fixture.String("workerError").Length != 0);
        Assert.Contains("ordinary worker error", fixture.String("workerError"));
        var worker = fixture.Worker("worker");
        Assert.False(worker.Terminated);
        Assert.False(worker.ResourcesReleased);
        fixture.Engine.Evaluate("worker.postMessage('recover');");
        fixture.Until(() => fixture.String("workerReply") == "still alive");
        Assert.Equal(42d, fixture.Engine.Evaluate("40+2").AsNumber());
        fixture.Engine.Evaluate("worker.terminate();");
        fixture.AssertReleased(worker);
        fixture.AssertReplacementReplies();
    }
}

/// <summary>
/// The suffix deliberately matches CI's existing '*CapabilityMatrixTests' filter.
/// #541 failure cases execute in the selected real host, inside its Windows job/AppContainer.
/// </summary>
[Collection("Content process round trips")]
public sealed class WorkerIsolationCapabilityMatrixTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Skip = "Only supported on Windows.", SkipUnless = nameof(IsWindows))]
    [InlineData("workers", false)]
    [InlineData("all", false)]
    [InlineData("workers", true)]
    [InlineData("all", true)]
    public async Task RestrictedProcessKeepsPageAndSiblingAliveAndReclaimsTheFailedWorker(string row, bool allocationFailure)
    {
        var selectedRow = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX");
        if (!string.IsNullOrEmpty(selectedRow) && selectedRow != row) return;
        // Post-start native memory accounting is the gap this case targets.
        if (allocationFailure && string.Equals(Environment.GetEnvironmentVariable("FOLIO_SCRIPT_ENGINE"), "jint", StringComparison.OrdinalIgnoreCase)) return;
        Assert.NotEqual("1", Environment.GetEnvironmentVariable("FOLIO_UNSANDBOXED"));
        var host = ContentHostTestPaths.Resolve();
        Assert.True(File.Exists(host), $"{row}: selected content host does not exist: {host}");
        var limits = WorkerIsolationFixture.Limits;
        await using var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(15)), documentLimits: limits);
        AssertRestricted(client);
        var stopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? sender, ContentProcessStoppedEventArgs args) => stopped.TrySetResult(args.Reason);
        client.ScriptStopped += OnStopped;
        try
        {
            await Exchange(client, new
            {
                html = "<p>worker isolation acceptance</p>", url = "https://worker-isolation.example/",
                capabilities = CapabilityMatrixTests.CapabilitiesFor(row), limits,
                script = Scenario(allocationFailure)
            }, row, stopped.Task);
            var deadline = Stopwatch.StartNew();
            JsonElement report = default;
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                var frame = await Exchange(client, new { script = "JSON.stringify({result:result,error:failure,memoryError:memoryError,ticks:pageTicks,native:globalThis.__folio_native_content_host===true})" }, row, stopped.Task);
                using var parsed = JsonDocument.Parse(frame.Payload.GetProperty("value").GetString()!);
                report = parsed.RootElement.Clone();
                Assert.Equal("", report.GetProperty("error").GetString());
                if (report.GetProperty("result").GetString() == "complete") break;
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            Assert.Equal("complete", report.GetProperty("result").GetString());
            Assert.True(report.GetProperty("ticks").GetInt32() > 0, $"{row}: page timers did not progress");
            if (allocationFailure)
            {
                Assert.True(report.GetProperty("native").GetBoolean(), $"{row}: memory case did not use the native host");
                Assert.Contains("memory", report.GetProperty("memoryError").GetString(), StringComparison.OrdinalIgnoreCase);
            }
            Assert.False(stopped.Task.IsCompleted, $"{row}: host watchdog stopped the page");
            Assert.False(client.Process.HasExited);
            AssertRestricted(client);
        }
        finally { client.ScriptStopped -= OnStopped; }
    }

    private static string Scenario(bool allocationFailure)
    {
        var victim = allocationFailure
            ? "onmessage=function(e){if(e.data==='fail'){new ArrayBuffer(32*1024*1024);postMessage('unexpected');}else postMessage('ready');};"
            : "onmessage=function(e){if(e.data==='fail'){setTimeout(function(){postMessage('busy');while(true){}},100);}else postMessage('ready');};";
        return """
            var result='pending',failure='',memoryError='',pageTicks=0,ticksAfterFailure=0,phase='before',replacementAttempts=0;
            setInterval(function(){pageTicks++;},1);
            var sibling=new Worker('data:text/javascript,'+encodeURIComponent("onmessage=function(e){postMessage(e.data);};"));
            var worker=new Worker('data:text/javascript,'+encodeURIComponent(
            """ + JsonSerializer.Serialize(victim) + """
            ));
            function replaceWorker(){
                try{
                    var replacement=new Worker('data:text/javascript,'+encodeURIComponent("onmessage=function(){postMessage('replacement');};"));
                    replacement.onerror=function(e){failure='replacement: '+e.message;};
                    replacement.onmessage=function(e){
                        if(e.data!=='replacement'){failure='wrong replacement reply';return;}
                        phase='final';sibling.postMessage('final');
                    };
                    replacement.postMessage('probe');
                }catch(e){
                    if(e.name!=='QuotaExceededError'){failure=e.name+': '+e.message;return;}
                    if(++replacementAttempts>100){failure='failed worker did not release its slot/reservation';return;}
                    setTimeout(replaceWorker,10);
                }
            }
            sibling.onerror=function(e){failure='sibling: '+e.message;};
            sibling.onmessage=function(e){
                if(e.data!==phase){failure='wrong sibling reply';return;}
                if(phase==='before'){worker.postMessage('fail');}
                else if(phase==='after'){replaceWorker();}
                else if(phase==='final'){
                    function finish(){if(pageTicks>ticksAfterFailure){if(40+2===42)result='complete';}else setTimeout(finish,1);}
                    finish();
                }
            };
            worker.onmessage=function(e){
                if(e.data==='ready'){sibling.postMessage('before');return;}
            """ + (allocationFailure
                ? "failure='allocation returned without a memory error';"
                : "if(e.data!=='busy'){failure='wrong busy worker reply';return;}worker.terminate();ticksAfterFailure=pageTicks;phase='after';sibling.postMessage('after');") + """
            };
            worker.onerror=function(e){
            """ + (allocationFailure
                ? "memoryError=e.message;if(String(e.message).toLowerCase().indexOf('memory')<0){failure='wrong worker failure: '+e.message;return;}ticksAfterFailure=pageTicks;phase='after';sibling.postMessage('after');"
                : "failure='worker hit a limit before explicit terminate: '+e.message;") + """
            };
            // Both slots are occupied: replacement success later must establish recovery.
            try{new Worker('data:text/javascript,');failure='worker count cap was not applied';}
            catch(e){if(e.name!=='QuotaExceededError')failure='unexpected cap error: '+e.name;}
            worker.postMessage('ready');'started';
            """;
    }

    private static async Task<ContentMessage> Exchange(ContentProcessClient client, object payload, string row, Task<string> stopped)
    {
        var received = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask Handler(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame && message.Payload.GetProperty("sequence").GetInt64() > 0) received.TrySetResult(message);
            if (message.Kind == ContentMessageKind.Diagnostic)
                received.TrySetException(new IOException($"{row}: {message.Payload}"));
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += Handler;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, payload, TestContext.Current.CancellationToken);
            var first = await Task.WhenAny(received.Task, stopped).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.True(first == received.Task, $"{row}: content process stopped: {(stopped.IsCompletedSuccessfully ? stopped.Result : client.StandardError)}");
            return await received.Task;
        }
        finally { client.MessageReceived -= Handler; }
    }

    private static void AssertRestricted(ContentProcessClient client) => AssertRestricted(client.Process);

    /// <summary>The process runs in an AppContainer and inside a job.</summary>
    internal static void AssertRestricted(System.Diagnostics.Process process)
    {
        Assert.True(OpenProcessToken(process.Handle, 0x0008, out var token), $"OpenProcessToken: {Marshal.GetLastWin32Error()}");
        using (token)
        {
            Assert.True(GetTokenInformation(token, 29 /* TokenIsAppContainer */, out var appContainer, sizeof(int), out _),
                $"GetTokenInformation: {Marshal.GetLastWin32Error()}");
            Assert.Equal(1, appContainer);
        }
        Assert.True(IsProcessInJob(process.Handle, IntPtr.Zero, out var inJob), $"IsProcessInJob: {Marshal.GetLastWin32Error()}");
        Assert.True(inJob);
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out int information, int length, out int returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool inJob);
}

internal sealed class WorkerIsolationFixture : IDisposable
{
    internal static ResourceLimits Limits => ResourceLimits.Default with
    {
        MaxWorkers = 2, MaxWorkerMemoryBytes = 8L * 1024 * 1024,
        MaxAggregateWorkerMemoryBytes = 16L * 1024 * 1024,
        MaxWorkerExecutionTime = TimeSpan.FromSeconds(30)
    };

    internal IScriptEngine Engine { get; }
    private readonly Document _document;
    private readonly HtmlEventLoop _loop;

    internal WorkerIsolationFixture(string implementation, int maxWorkers = 2)
    {
        Engine = implementation == "native" ? new NativeJavaScriptEngine() : new JintScriptEngine();
        _document = Document.Parse("<p>worker isolation</p>", new FolioOptions
        {
            Capabilities = new FeatureCapabilities { Workers = true },
            Limits = Limits with { MaxWorkers = maxWorkers, MaxAggregateWorkerMemoryBytes = maxWorkers * Limits.MaxWorkerMemoryBytes }
        });
        _loop = new HtmlEventLoop(Engine);
        DomBindings.Install(Engine, _document, eventLoop: _loop);
        ScriptGlobalsInstaller.Install(_loop, Engine);
    }

    internal WorkerState Worker(string name)
    {
        Assert.True(Engine.TryGetHostTarget(Engine.GetGlobalProperty(name), out var target));
        return Assert.IsType<WorkerState>(target);
    }

    internal string String(string name) => Engine.GetGlobalProperty(name).AsString() ?? "";

    internal void DrainParentTasks() => _loop.SpinUntilIdle();

    internal void Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            _loop.SpinUntilIdle();
            Thread.Sleep(1);
        }
        Assert.True(condition(), "Worker isolation condition was not reached within five seconds.");
    }

    internal void AssertReleased(WorkerState worker)
    {
        Until(() => worker.ResourcesReleased);
        Assert.True(worker.Terminated);
        Assert.Throws<ObjectDisposedException>(() => worker.WorkerEngine!.Evaluate("1"));
    }

    internal void AssertReplacementReplies()
    {
        Engine.Evaluate("var replacementReply='';var replacement=new Worker('data:text/javascript,'+encodeURIComponent(\"onmessage=function(){postMessage('replacement');};\"));replacement.onmessage=e=>replacementReply=e.data;replacement.postMessage('probe');");
        Until(() => String("replacementReply") == "replacement");
    }

    public void Dispose()
    {
        try { DomBindings.Dispose(Engine); }
        finally { _loop.Dispose(); _document.Dispose(); Engine.Dispose(); }
    }
}

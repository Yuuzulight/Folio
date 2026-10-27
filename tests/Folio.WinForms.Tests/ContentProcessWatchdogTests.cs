#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text;
using System.Windows.Forms;
using Folio.Hosting;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

public sealed class ContentProcessWatchdogTests
{
    [Fact]
    public Task ContentFrameRemainsVisibleWhenBrokerHangStopsChild() => RunStaAsync(async () =>
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        var (row, capabilities) = MatrixCapabilities();
        var attached = false;
        using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        using var view = new FolioView { Dock = DockStyle.Fill, Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();
        var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(5)),
            resourceLoader: new CapabilityResourceLoader());
        try { view.AttachContentProcess(client); attached = true; }
        catch { await client.DisposeAsync(); attached = true; throw; }
        var probe = new TaskCompletionSource<ContentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask OnMessage(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame) probe.TrySetResult(message);
            return ValueTask.CompletedTask;
        }

        try
        {
            client.MessageReceived += OnMessage;
            var clientStopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClientStopped(object? _, ContentProcessStoppedEventArgs args) => clientStopped.TrySetResult(args.Reason);
            client.ScriptStopped += OnClientStopped;
            try
            {
                await client.SendAsync(ContentMessageKind.Load, new
                {
                    html = "<canvas id='c' style='position:absolute;left:0;top:0' width='220' height='80'></canvas><audio id='media' style='display:none'></audio><audio id='sound' style='display:none' src='data:application/octet-stream;base64,AQID'></audio><script>var soundResult='pending',cameraDeniedResult='pending',microphoneDeniedResult='pending',serviceWorkerResult=false;try{document.getElementById('sound').play().then(()=>soundResult='allowed',e=>soundResult=e.name);}catch(e){soundResult=e.name;}try{navigator.mediaDevices.getUserMedia({video:true}).then(()=>cameraDeniedResult='allowed',e=>cameraDeniedResult=e.name);}catch(e){cameraDeniedResult=e.name;}try{navigator.mediaDevices.getUserMedia({audio:true}).then(()=>microphoneDeniedResult='allowed',e=>microphoneDeniedResult=e.name);}catch(e){microphoneDeniedResult=e.name;}try{if(typeof navigator.serviceWorker!=='undefined')navigator.serviceWorker.register('/matrix-sw.js').then(()=>serviceWorkerResult=true);}catch(e){}</script>",
                    script = CapabilityProbeScript(row),
                    url = "https://matrix.example/",
                    capabilities,
                    framePath = client.FramePath, viewportWidth = 220, viewportHeight = 80
                });
                ContentMessage probeMessage;
                Task completed;
                try { completed = await Task.WhenAny(probe.Task, clientStopped.Task).WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (TimeoutException error)
                {
                    throw new TimeoutException($"Capability matrix row '{row}' timed out waiting for its first frame; exited={HasExited(client)}; supervisorStopped={client.Supervisor.IsStopped}", error);
                }
                if (completed == clientStopped.Task)
                    throw new InvalidOperationException($"Capability matrix row '{row}' content process stopped before its first frame: {clientStopped.Task.Result}; exited={HasExited(client)}; supervisorStopped={client.Supervisor.IsStopped}");
                probeMessage = await probe.Task;
                Assert.Equal(capabilities, probeMessage.Payload.GetProperty("appliedCapabilities").Deserialize<FeatureCapabilities>());
                var expected = $"row:{row}:webgl={Bool(capabilities.WebGl)}:workers={Bool(capabilities.Workers)}:wasm={Bool(capabilities.WebAssembly)}:iframe={Bool(capabilities.Iframes)}:rtc={Bool(capabilities.WebRtc)}:media={Bool(capabilities.Media)}";
                var observed = probeMessage.Payload.GetProperty("value").GetString()!;
                Assert.StartsWith(expected, observed, StringComparison.Ordinal);
                Assert.Contains(":soundResult=NotSupportedError:", observed, StringComparison.Ordinal);
                // Capability grants are necessary but not sufficient for capture. This watchdog matrix does not
                // inject an IMediaCaptureProvider, so the content process must still deny both device requests even
                // on the camera/microphone/all capability rows. Provider-backed approval is covered separately by
                // ContentCaptureBridgeTests.
                Assert.Contains(":cameraDenied=true", observed, StringComparison.Ordinal);
                Assert.Contains(":microphoneDenied=true", observed, StringComparison.Ordinal);
                Assert.Contains($":serviceWorkers={Bool(capabilities.ServiceWorkers)}", observed, StringComparison.Ordinal);
            }
            finally
            {
                client.ScriptStopped -= OnClientStopped;
                client.MessageReceived -= OnMessage;
            }
            await WaitForBlueAsync(view);
            using var before = Capture(view);
            Assert.True(IsBlue(before.GetPixel(100, 70)), "The initial content frame was not painted by the control.");

            var stopped = new TaskCompletionSource<ScriptStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStopped(object? sender, ScriptStoppedEventArgs args) => stopped.TrySetResult(args);
            view.ScriptStopped += OnStopped;
            try
            {
                await client.SendAsync(ContentMessageKind.Load, new
                {
                    html = "<script src='https://hang.example/script.js'></script>",
                    script = "'never reached'",
                    capabilities,
                    framePath = client.FramePath, viewportWidth = 220, viewportHeight = 80
                });
                var stop = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(stop.Reason.Contains("heartbeat", StringComparison.OrdinalIgnoreCase), $"Capability matrix row '{row}' did not report watchdog termination: {stop.Reason}");
                using var after = Capture(view);
                Assert.True(IsBlue(after.GetPixel(100, 70)), "The last published canvas frame was not retained.");
                Assert.True(HasStoppedNotice(after), "The stopped notice was not painted over the retained frame.");
            }
            finally { view.ScriptStopped -= OnStopped; }
        }
        finally
        {
            if (!attached) await client.DisposeAsync();
        }
    });

    [Fact]
    public Task StoppedChildBeforeFirstFrameShowsNotice() => RunStaAsync(async () =>
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        var (row, capabilities) = MatrixCapabilities();
        using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        using var view = new FolioView { Dock = DockStyle.Fill, Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();
        var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(5)),
            resourceLoader: new CapabilityResourceLoader());
        try { view.AttachContentProcess(client); }
        catch { await client.DisposeAsync(); throw; }
        var stopped = new TaskCompletionSource<ScriptStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.ScriptStopped += OnStopped;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                html = "<script src='https://hang.example/script.js'></script>",
                script = "'never reached'",
                capabilities,
                framePath = client.FramePath, viewportWidth = 220, viewportHeight = 80
            });
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var bitmap = Capture(view);
            Assert.True(HasStoppedNotice(bitmap), $"The stopped notice was not painted before any frame arrived for capability matrix row '{row}'.");
        }
        finally { view.ScriptStopped -= OnStopped; }
        void OnStopped(object? sender, ScriptStoppedEventArgs args) => stopped.TrySetResult(args);
    });

    private static (string Row, FeatureCapabilities Capabilities) MatrixCapabilities()
    {
        var row = Environment.GetEnvironmentVariable("FOLIO_CAPABILITY_MATRIX") ?? "none";
        return (row, row switch
        {
            "all" => new FeatureCapabilities { Sound = true, Camera = true, Microphone = true, WebGl = true, Media = true, Workers = true, WebAssembly = true, Iframes = true, WebRtc = true, ServiceWorkers = true },
            "webgl" => new FeatureCapabilities { WebGl = true },
            "media" => new FeatureCapabilities { Media = true },
            "sound" => new FeatureCapabilities { Sound = true },
            "camera" => new FeatureCapabilities { Camera = true },
            "microphone" => new FeatureCapabilities { Microphone = true },
            "workers" => new FeatureCapabilities { Workers = true },
            "webassembly" => new FeatureCapabilities { WebAssembly = true },
            "iframes" => new FeatureCapabilities { Iframes = true },
            "webrtc" => new FeatureCapabilities { WebRtc = true },
            "service-workers" => new FeatureCapabilities { ServiceWorkers = true },
            "none" => FeatureCapabilities.Default,
            _ => throw new InvalidOperationException($"Unknown capability matrix row '{row}'.")
        });
    }

    private static string CapabilityProbeScript(string row) => $"var c=document.getElementById('c'),x=c.getContext('2d');x.fillStyle='rgb(0,0,255)';x.fillRect(0,0,220,80);var p=document.createElement('canvas').getContext('webgl')!==null,w=false,m=false,i=false,r=false,a=document.getElementById('media');try{{var worker=new Worker('data:text/javascript,');worker.terminate();w=true;}}catch(_){{}}try{{new WebAssembly.Module(new Uint8Array([0,97,115,109,1,0,0,0]));m=true;}}catch(_){{}}try{{var frame=document.createElement('iframe');document.body.appendChild(frame);i=frame.contentDocument!==null;frame.remove();}}catch(_){{}}try{{var peer=new RTCPeerConnection();peer.close();r=true;}}catch(_){{}}'row:'+{JsonSerializer.Serialize(row)}+':webgl='+p+':workers='+w+':wasm='+m+':iframe='+i+':rtc='+r+':media='+(a.error===null&&a.readyState===0)+':soundResult='+soundResult+':cameraDenied='+(cameraDeniedResult==='NotAllowedError')+':microphoneDenied='+(microphoneDeniedResult==='NotAllowedError')+':serviceWorkers='+serviceWorkerResult";

    private static string Bool(bool value) => value ? "true" : "false";

    private static async Task WaitForBlueAsync(Control view)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            using var bitmap = Capture(view);
            if (IsBlue(bitmap.GetPixel(100, 70))) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The content process did not publish a blue frame.");
    }

    private static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format32bppPArgb);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap;
    }

    private static bool IsBlue(Color color) => color.B > 180 && color.R < 40 && color.G < 40;

    private static bool HasExited(ContentProcessClient client)
    {
        try { return client.Process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }

    private static bool HasStoppedNotice(Bitmap bitmap)
    {
        for (var y = 12; y < Math.Min(bitmap.Height, 56); y++)
        for (var x = 12; x < Math.Min(bitmap.Width, 220); x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (color.R > 70 && color.R > color.G * 1.15 && color.R > color.B * 1.15)
                return true;
        }
        return false;
    }

    private sealed class CapabilityResourceLoader : IResourceLoader
    {
        private readonly TaskCompletionSource<ResourceResponse?> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken)
        {
            if (request.Url.Contains("hang.example", StringComparison.OrdinalIgnoreCase))
                return _never.Task.WaitAsync(cancellationToken);
            if (request.Url.EndsWith("/matrix-sw.js", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<ResourceResponse?>(ResourceResponse.Loaded(Encoding.UTF8.GetBytes("self.oninstall=()=>{};"), "text/javascript"));
            return Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("matrix test resource denied"));
        }
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Idle += Start;
            Application.Run();

            void Start(object? sender, EventArgs args)
            {
                Application.Idle -= Start;
                _ = ExecuteAsync();
            }

            async Task ExecuteAsync()
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Application.ExitThread(); }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return AwaitCompletionAsync();

        async Task AwaitCompletionAsync()
        {
            await completion.Task.ConfigureAwait(false);
            thread.Join();
        }
    }
}
#else
namespace Folio.WinForms.Tests;

public sealed class ContentProcessWatchdogTests
{
    [Fact(Skip = "The content-process UI watchdog test requires Windows Forms.")]
    public void ContentFrameRemainsVisibleWhenBrokerHangStopsChild() { }
}
#endif

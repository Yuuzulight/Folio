#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Folio.Hosting;
using Folio.Resources;
using Folio.Scripting;
using Folio.Scripting.Ipc;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

public sealed class PreHandleContentLifecycleTests
{
    [Fact]
    public Task FrameArrivingBeforeHandleIsAppliedAfterHandleCreation() => RunStaAsync(async () =>
    {
        var client = StartClient();
        using var view = new FolioView { Dock = DockStyle.Fill, Size = new Size(220, 80), Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        try { view.AttachContentProcess(client); }
        catch { await client.DisposeAsync(); throw; }
        var frameArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask OnMessage(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame) frameArrived.TrySetResult();
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += OnMessage;
        try
        {
            Assert.False(view.IsHandleCreated);
            await client.SendAsync(ContentMessageKind.Load, new
            {
                html = "<canvas id='c' width='220' height='80'></canvas>",
                script = "var c=document.getElementById('c'),x=c.getContext('2d');x.fillStyle='rgb(0,0,255)';x.fillRect(0,0,220,80);true",
                capabilities = FeatureCapabilities.Default,
                framePath = client.FramePath, viewportWidth = 220, viewportHeight = 80
            });
            await frameArrived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
            form.Controls.Add(view);
            form.Show();
            view.CreateControl();
            await WaitForBlueAsync(view);
            using var bitmap = Capture(view);
            Assert.True(IsBlue(bitmap.GetPixel(100, 70)), "The frame delivered before handle creation was not retained.");
        }
        finally { client.MessageReceived -= OnMessage; }
    });

    [Fact]
    public Task StopArrivingBeforeHandleIsMarshaledAndPaintedAfterHandleCreation() => RunStaAsync(async () =>
    {
        var client = StartClient();
        using var view = new FolioView { Dock = DockStyle.Fill, Size = new Size(220, 80), Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        try { view.AttachContentProcess(client); }
        catch { await client.DisposeAsync(); throw; }
        var stopped = new TaskCompletionSource<ScriptStoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerThread = Environment.CurrentManagedThreadId;
        var eventThread = 0;
        void OnStopped(object? _, ScriptStoppedEventArgs args)
        {
            eventThread = Environment.CurrentManagedThreadId;
            stopped.TrySetResult(args);
        }
        view.ScriptStopped += OnStopped;
        try
        {
            Assert.False(view.IsHandleCreated);
            var processStopped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClientStopped(object? _, ContentProcessStoppedEventArgs args) => processStopped.TrySetResult(args.Reason);
            client.ScriptStopped += OnClientStopped;
            string processStopReason;
            try
            {
                await client.SendAsync(ContentMessageKind.Load, new
                {
                    html = "<script src='https://hang.example/script.js'></script>",
                    script = "'never reached'",
                    capabilities = FeatureCapabilities.Default,
                    framePath = client.FramePath, viewportWidth = 220, viewportHeight = 80
                });
                processStopReason = await processStopped.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { client.ScriptStopped -= OnClientStopped; }
            Assert.Contains("heartbeat", processStopReason, StringComparison.OrdinalIgnoreCase);
            Assert.False(stopped.Task.IsCompleted);
            using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
            form.Controls.Add(view);
            form.Show();
            view.CreateControl();
            var notice = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ownerThread, eventThread);
            Assert.Contains("heartbeat", notice.Reason, StringComparison.OrdinalIgnoreCase);
            using var bitmap = Capture(view);
            Assert.True(HasStoppedNotice(bitmap), "The pre-handle stop notice was not painted.");
        }
        finally { view.ScriptStopped -= OnStopped; }
    });

    private static ContentProcessClient StartClient()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        return ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(5)),
            resourceLoader: new NeverCompletingLoader());
    }

    private static async Task WaitForBlueAsync(Control view)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            using var bitmap = Capture(view);
            if (IsBlue(bitmap.GetPixel(100, 70))) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The pre-handle frame was not painted.");
    }

    private static Bitmap Capture(Control control)
    {
        var bitmap = new Bitmap(control.ClientSize.Width, control.ClientSize.Height, PixelFormat.Format32bppPArgb);
        control.DrawToBitmap(bitmap, control.ClientRectangle);
        return bitmap;
    }

    private static bool IsBlue(Color color) => color.B > 180 && color.R < 40 && color.G < 40;

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

    private sealed class NeverCompletingLoader : IResourceLoader
    {
        private readonly TaskCompletionSource<ResourceResponse?> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) => _never.Task.WaitAsync(cancellationToken);
    }

    private static Task RunStaAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Idle += Start;
            Application.Run();
            void Start(object? _, EventArgs __) { Application.Idle -= Start; _ = ExecuteAsync(); }
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
        async Task AwaitCompletionAsync() { await completion.Task.ConfigureAwait(false); thread.Join(); }
    }
}
#else
namespace Folio.WinForms.Tests;

public sealed class PreHandleContentLifecycleTests
{
    [Fact(Skip = "The pre-handle content lifecycle tests require Windows Forms.")]
    public void WindowsOnlyPlaceholder() { }
}
#endif

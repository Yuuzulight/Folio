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

public sealed class ContentProcessOwnershipTests
{
    [Fact]
    public Task ReplacingProcessRetiresOldClientAndKeepsNewClientUsable() => RunStaAsync(async () =>
    {
        using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        var view = new FolioView { Size = new Size(220, 80), Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();
        var first = StartClient();
        var firstCompletion = first.Completion;
        var firstFramePath = first.FramePath;
        view.AttachContentProcess(first);
        var second = StartClient();
        var secondFramePath = second.FramePath;
        try
        {
            await WaitForFrameAsync(first);
            await WaitForBlueAsync(view);
            view.AttachContentProcess(second);
            Assert.True(firstCompletion.IsCompleted, "Replacing the attached process did not retire the previous client.");
            Assert.False(File.Exists(firstFramePath), "The retired client's frame mapping was not cleaned up.");
            Assert.True(File.Exists(secondFramePath), "The replacement client's frame mapping was not retained.");
            using var retained = Capture(view);
            Assert.True(IsBlue(retained.GetPixel(100, 70)), "Replacing the process discarded the last published canvas frame.");
            await WaitForFrameAsync(second);
        }
        finally
        {
            view.Dispose();
            Assert.False(File.Exists(secondFramePath), "Disposing the view did not clean up the owned replacement client.");
        }
    });

    [Fact]
    public Task ReattachingSameClientDoesNotDisposeIt() => RunStaAsync(async () =>
    {
        var view = new FolioView { Size = new Size(220, 80), Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        var client = StartClient();
        var framePath = client.FramePath;
        var completion = client.Completion;
        try
        {
            view.AttachContentProcess(client);
            await WaitForFrameAsync(client);
            view.AttachContentProcess(client);
            Assert.False(completion.IsCompleted, "Reattaching the same client disposed its live process.");
            Assert.True(File.Exists(framePath), "Reattaching the same client lost its frame mapping.");
            using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
            form.Controls.Add(view);
            form.Show();
            view.CreateControl();
            await WaitForBlueAsync(view);
            using var retained = Capture(view);
            Assert.True(IsBlue(retained.GetPixel(100, 70)), "Reattaching the same client discarded the queued pre-handle frame.");
        }
        finally
        {
            view.Dispose();
            Assert.False(File.Exists(framePath), "The view did not clean up the same client on disposal.");
        }
    });

    [Fact]
    public Task SwitchingAwayFromContentProcessRetiresOwnedClient() => RunStaAsync(async () =>
    {
        var view = new FolioView { Size = new Size(220, 80), Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        var client = StartClient();
        var framePath = client.FramePath;
        var completion = client.Completion;
        try
        {
            view.AttachContentProcess(client);
            view.Options = new FolioOptions { Scripting = ScriptingPolicy.Disabled };
            view.LoadHtml("<p>static document</p>");
            Assert.True(completion.IsCompleted, "Leaving content-process mode did not retire the attached client.");
            Assert.False(File.Exists(framePath), "Leaving content-process mode left the frame mapping behind.");
            Assert.NotNull(view.Document);
        }
        finally { view.Dispose(); }
    });

    [Fact]
    public Task SwitchingStoppedProcessToStaticClearsStoppedNotice() => RunStaAsync(async () =>
    {
        using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        var view = new FolioView { Dock = DockStyle.Fill, Options = new FolioOptions { Scripting = ScriptingPolicy.ContentProcess } };
        form.Controls.Add(view);
        form.Show();
        view.CreateControl();
        var client = StartClient();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStopped(object? _, ScriptStoppedEventArgs __) => stopped.TrySetResult();
        view.ScriptStopped += OnStopped;
        try
        {
            view.AttachContentProcess(client);
            client.Supervisor.Stop("ownership regression stop");
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            view.Options = new FolioOptions { Scripting = ScriptingPolicy.Disabled };
            view.LoadHtml("<p>static document</p>");
            using var bitmap = Capture(view);
            Assert.False(HasStoppedNotice(bitmap), "The stopped content-process notice remained over the static document.");
        }
        finally
        {
            view.ScriptStopped -= OnStopped;
            view.Dispose();
        }
    });

    private static ContentProcessClient StartClient()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        return ContentProcessClient.Start(host, limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(5)), resourceLoader: new TestResourceLoader());
    }

    private static async Task WaitForFrameAsync(ContentProcessClient client)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask OnMessage(ContentMessage message, CancellationToken _) { if (message.Kind == ContentMessageKind.Frame) frame.TrySetResult(); return ValueTask.CompletedTask; }
        client.MessageReceived += OnMessage;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                html = "<canvas id='c' width='220' height='80'></canvas>",
                script = "var c=document.getElementById('c'),x=c.getContext('2d');x.fillStyle='rgb(0,0,255)';x.fillRect(0,0,220,80);true",
                capabilities = FeatureCapabilities.Default,
                framePath = client.FramePath,
                viewportWidth = 220,
                viewportHeight = 80,
            });
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { client.MessageReceived -= OnMessage; }
    }

    private static async Task WaitForBlueAsync(Control view)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            using var bitmap = Capture(view);
            if (IsBlue(bitmap.GetPixel(100, 70))) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("The content frame was not painted.");
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

    private sealed class TestResourceLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("ownership test resource denied"));
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

public sealed class ContentProcessOwnershipTests
{
    [Fact(Skip = "The content-process ownership tests require Windows Forms.")]
    public void WindowsOnlyPlaceholder() { }
}
#endif

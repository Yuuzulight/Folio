#if WINDOWS
using System.Drawing;
using System.Windows.Forms;
using Folio.Resources;
using Folio.WinForms;

namespace Folio.WinForms.Tests;

public sealed class IframeHostDispatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NavigationUsesTheUiThreadAndCanRetireItsOwnPage(bool dispose) => RunSta(async () =>
    {
        var owner = Environment.CurrentManagedThreadId;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var form = new Form { ClientSize = new Size(220, 80), ShowInTaskbar = false, Opacity = 0, Location = new Point(-32000, -32000) };
        using var view = new FolioView { Dock = DockStyle.Fill };
        view.Options = new FolioOptions
        {
            Scripting = ScriptingPolicy.ContentProcess, ContentProcessPath = ContentHostTestPaths.Resolve(),
            Capabilities = new FeatureCapabilities { Iframes = true }, ResourceLoader = new DeniedLoader(),
            IframeNavigationRequested = uri =>
            {
                try
                {
                    Assert.Equal(owner, Environment.CurrentManagedThreadId);
                    Assert.Equal("https://frame.example/child.html", uri.AbsoluteUri);
                    if (dispose) view.Dispose();
                    else { view.Options = new FolioOptions(); view.LoadHtml("<body style='background:blue'>Replacement</body>"); }
                    delivered.TrySetResult();
                }
                catch (Exception error) { delivered.TrySetException(error); }
            }
        };
        form.Controls.Add(view);
        form.Show();
        view.LoadHtml("<iframe src='child.html'></iframe>", new Uri("https://frame.example/"));
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    });

    private sealed class DeniedLoader : IResourceLoader
    {
        public Task<ResourceResponse?> LoadAsync(ResourceRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ResourceResponse?>(ResourceResponse.Refused("Test navigation does not fetch the network."));
    }

    private static Task RunSta(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Application.Idle += Start;
            Application.Run();
            void Start(object? sender, EventArgs args) { Application.Idle -= Start; _ = Execute(); }
            async Task Execute()
            {
                try { await action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { Application.ExitThread(); }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return Join();
        async Task Join() { await completion.Task.ConfigureAwait(false); thread.Join(); }
    }
}
#endif

using System.Diagnostics;
using Folio.Scripting.Ipc;

namespace Folio.Scripting.Tests;

public sealed class ContentProcessClientCleanupTests
{
    [Fact]
    public async Task DisposeCleansProcessAndFrameAfterMessageCallbackFaults()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Parent!.Name;
        var host = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../tools/Folio.ContentHost/bin", configuration, "net10.0/Folio.ContentHost.dll"));
        Assert.True(File.Exists(host), $"Content host was not built: {host}");

        var client = ContentProcessClient.Start(host,
            limits: new ContentProcessLimits(HeartbeatTimeout: TimeSpan.FromSeconds(10)));
        using var observer = Process.GetProcessById(client.Process.Id);
        var framePath = client.FramePath;
        var frameDirectory = Path.GetDirectoryName(framePath)!;
        ValueTask ThrowOnFrame(ContentMessage message, CancellationToken _)
        {
            if (message.Kind == ContentMessageKind.Frame)
                throw new InvalidOperationException("intentional callback fault");
            return ValueTask.CompletedTask;
        }
        client.MessageReceived += ThrowOnFrame;
        try
        {
            await client.SendAsync(ContentMessageKind.Load, new
            {
                html = "<html><body></body></html>",
                script = "1"
            }, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await client.Completion.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        }
        finally
        {
            client.MessageReceived -= ThrowOnFrame;
            Exception? disposeFailure = null;
            try { await client.DisposeAsync(); }
            catch (Exception error) { disposeFailure = error; }
            await client.DisposeAsync();
            if (disposeFailure is not null)
                Assert.IsType<InvalidOperationException>(disposeFailure);
        }

        observer.WaitForExit(5000);
        Assert.True(observer.HasExited);
        Assert.False(File.Exists(framePath));
        Assert.False(Directory.Exists(frameDirectory));
    }
}

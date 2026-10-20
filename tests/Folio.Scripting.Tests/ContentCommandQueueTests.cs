using Folio.WinForms;

namespace Folio.Scripting.Tests;

public sealed class ContentCommandQueueTests
{
    [Fact]
    public async Task CapabilityWriteWaitsForPendingDocumentWrite()
    {
        var queue = new ContentCommandQueue();
        var load = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var first = queue.Enqueue(async () =>
        {
            events.Add("load-start");
            await load.Task;
            events.Add("load-end");
        });
        var second = queue.Enqueue(() =>
        {
            events.Add("capabilities");
            return ValueTask.CompletedTask;
        });
        Assert.Equal(["load-start"], events);
        Assert.False(second.IsCompleted);
        load.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["load-start", "load-end", "capabilities"], events);
    }

    [Fact]
    public async Task FailedDocumentWriteDoesNotPoisonSubsequentCommands()
    {
        var queue = new ContentCommandQueue();
        var first = queue.Enqueue(() => ValueTask.FromException(new IOException("old document closed")));
        var executed = false;
        var second = queue.Enqueue(() => { executed = true; return ValueTask.CompletedTask; });
        await Assert.ThrowsAsync<IOException>(() => first);
        await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(executed);
    }
}
